using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using RDM.Data.Repositories;
using RDM.Shared.Models;
using RDM.Shared.Network;
using RDM.Shared.Protocol;

namespace RDM.Data.Services;

public enum PresenceAvailability { NotConfigured, Connecting, Connected, Unavailable, Rejected }

/// <summary>
/// Trạng thái Online/Offline thật lấy từ Relay. Khi mất Relay thì trả về "chưa rõ", không suy đoán là Offline.
/// Thông tin thiết bị do Relay chuyển chỉ để hiển thị: Relay không được tin cậy về định danh (Giai đoạn 4 mới xác thực đầu cuối).
/// </summary>
public sealed class PresenceService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly SettingsService _settings;
    private readonly ClientRepository _clients;
    private readonly AuditLogger _audit;
    private readonly ConcurrentDictionary<string, PresenceEntry> _entries = new ConcurrentDictionary<string, PresenceEntry>();
    private PresenceRelayClient _client;

    public event Action Changed;

    /// <summary>Thời gian chờ trước khi nối lại; kiểm thử có thể giảm xuống.</summary>
    public TimeSpan? InitialBackoff { get; set; }

    public PresenceService(SettingsService settings, ClientRepository clients, AuditLogger audit)
    {
        _settings = settings; _clients = clients; _audit = audit;
    }

    public string LastError => _client?.LastError;

    public PresenceAvailability Availability
    {
        get
        {
            var c = _client;
            if (c == null) return PresenceAvailability.NotConfigured;
            return c.State switch
            {
                RelayState.Online => PresenceAvailability.Connected,
                RelayState.Rejected => PresenceAvailability.Rejected,
                RelayState.Connecting or RelayState.Stopped => PresenceAvailability.Connecting,
                _ => PresenceAvailability.Unavailable
            };
        }
    }

    public void Start()
    {
        if (_client != null) return;
        var host = _settings.Get(SettingKeys.RelayHost);
        if (string.IsNullOrWhiteSpace(host)) { Changed?.Invoke(); return; }
        var port = int.TryParse(_settings.Get(SettingKeys.RelayPort), out var p) ? p : ProtocolInfo.DefaultRelayPort;
        var client = new PresenceRelayClient(new RelayEndpoint { Host = host, Port = port, PinnedKeySha256 = _settings.Get(SettingKeys.RelayPin) }, _settings.Get(SettingKeys.RelayToken));
        if (InitialBackoff.HasValue) client.InitialBackoff = InitialBackoff.Value;
        client.StateChanged += s =>
        {
            if (s != RelayState.Online) _entries.Clear();
            Changed?.Invoke();
        };
        client.EntriesReceived += OnEntries;
        _client = client;
        client.Start();
    }

    public async Task StopAsync()
    {
        var c = _client;
        _client = null;
        if (c != null) await c.StopAsync().ConfigureAwait(false);
        _entries.Clear();
        Changed?.Invoke();
    }

    /// <summary>Áp dụng cấu hình mới hoặc làm mới trạng thái: kết nối lại sẽ nhận ảnh chụp đầy đủ.</summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Start();
    }

    public void Dispose() => _client?.Dispose();

    public PresenceEntry Find(string remoteId) => remoteId != null && _entries.TryGetValue(remoteId, out var e) ? e : null;

    /// <summary>Thiết bị có đang Online thật theo Relay hay không (để hiển thị đèn xanh/xám).</summary>
    public bool IsOnline(ClientDevice device)
    {
        if (device == null || !device.IsEnabled || Availability != PresenceAvailability.Connected) return false;
        var e = Find(device.RemoteId);
        return e != null && e.IsOnline;
    }

    public string StatusText(ClientDevice device)
    {
        if (!device.IsEnabled) return "Disabled";
        if (Availability != PresenceAvailability.Connected) return "Unknown";
        var e = Find(device.RemoteId);
        if (e == null) return "Not registered";
        if (!e.IsOnline) return "Offline";
        return e.AccessEnabled ? "Online" : "Online (access off)";
    }

    /// <summary>Đếm trong danh sách thiết bị của Controller. Thiết bị chưa đăng ký với Relay tính là Offline.</summary>
    public (int online, int offline)? Count(IEnumerable<string> remoteIds)
    {
        if (Availability != PresenceAvailability.Connected) return null;
        var online = 0; var offline = 0;
        foreach (var id in remoteIds)
        {
            if (Find(id)?.IsOnline == true) online++; else offline++;
        }
        return (online, offline);
    }

    public string DescribeAvailability() => RDM.Shared.Common.ProductMode.Managed ? (Availability == PresenceAvailability.Connected ? "Live" : "Not available") : Availability switch
    {
        PresenceAvailability.NotConfigured => "Relay not configured",
        PresenceAvailability.Connected => "From Relay",
        PresenceAvailability.Connecting => "Connecting to Relay",
        _ => LastError ?? "Cannot reach Relay"
    };

    /// <summary>Áp lại dữ liệu Relay đang có vào Database, dùng sau khi thêm thiết bị đã Online từ trước.</summary>
    public void ReapplyCached()
    {
        var entries = _entries.Values.ToList();
        if (entries.Count > 0) _clients.ApplyPresence(entries, false);
    }

    private void OnEntries(IReadOnlyList<PresenceEntry> entries, bool isSnapshot)
    {
        if (isSnapshot) _entries.Clear();
        foreach (var e in entries) _entries[e.RemoteId] = e;
        try
        {
            var autoRegister = _settings.Get(SettingKeys.AutoRegister, "false") == "true";
            var added = _clients.ApplyPresence(entries, autoRegister);
            if (added > 0) _audit.Write(AuditEvents.ClientCreated, null, details: $"Automatically registered {added} devices");
        }
        catch (Exception ex) when (ex is System.Data.SQLite.SQLiteException || ex is InvalidOperationException)
        {
            Log.Error(ex, "Could not save device status");
        }
        Changed?.Invoke();
    }
}
