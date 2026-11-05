using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Win32;
using NLog;
using RDM.Shared.Common;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Client;

/// <summary>
/// Kết nối Client với Relay. Trùng Remote ID ở lần đăng ký đầu thì đổi ID mới.
/// Đã từng đăng ký mà bị báo trùng là bất thường, nên báo cho chủ máy chứ không tự đổi ID.
/// </summary>
public sealed class ConnectionManager : IDisposable, IRelayBroker
{
    private const int MaxIdRetries = 5;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly ClientStateStore _store;
    private readonly ClientState _state;
    private readonly DeviceInfo _info;
    private readonly object _gate = new object();
    private DeviceRelayClient _client;
    private int _idRetries;

    public event Action Changed;

    /// <summary>Có yêu cầu phiên qua Relay; đối số là địa chỉ Relay và vé.</summary>
    public event Action<RelayEndpoint, byte[]> IncomingSession;
    public bool IsOnline => _client?.IsOnline == true;
    public RelayEndpoint Endpoint => new RelayEndpoint { Host = _state.RelayHost, Port = _state.RelayPort, PinnedKeySha256 = _state.RelayPin };

    public Task<RelayTicket> RequestSessionAsync(string remoteId, bool wantRelay, System.Threading.CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to the Relay.");
        return client.RequestSessionAsync(remoteId, wantRelay, ct);
    }

    public RelayState State => _client?.State ?? RelayState.Stopped;
    public string LastError => _client?.LastError;
    public string RemoteId => _state.RemoteId;
    public TimeSpan? InitialBackoff { get; set; }

    public ConnectionManager(ClientStateStore store, ClientState state, DeviceInfo info)
    {
        _store = store; _state = state; _info = info;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_client != null || string.IsNullOrWhiteSpace(_state.RelayHost)) { Changed?.Invoke(); return; }
            var identity = DeviceIdentity.FromSeed(_state.IdentitySeed);
            var client = new DeviceRelayClient(new RelayEndpoint { Host = _state.RelayHost, Port = _state.RelayPort, PinnedKeySha256 = _state.RelayPin },
                identity, _state.RemoteId, _info, _state.AccessEnabled, _state.EnrollmentToken);
            if (InitialBackoff.HasValue) client.InitialBackoff = InitialBackoff.Value;
            client.StateChanged += s => OnState(client, s);
            client.IncomingSession += token => IncomingSession?.Invoke(client.Endpoint, token);
            _client = client;
            client.Start();
        }
    }

    public async Task RestartAsync()
    {
        DeviceRelayClient old;
        lock (_gate) { old = _client; _client = null; }
        if (old != null) await old.StopAsync().ConfigureAwait(false);
        _idRetries = 0;
        Start();
    }

    public void NotifyAccessChanged(bool enabled) => _client?.SetAccessEnabled(enabled);

    private void OnState(DeviceRelayClient client, RelayState state)
    {
        if (state == RelayState.Online && !_state.RegisteredOnce)
        {
            _state.RegisteredOnce = true;
            Try(() => _store.Save(_state));
        }
        if (state == RelayState.Rejected && client.LastRejection == AuthCode.RemoteIdConflict && !_state.RegisteredOnce && _idRetries < MaxIdRetries)
        {
            _idRetries++;
            _state.RemoteId = RemoteIdGenerator.Generate();
            Try(() => _store.Save(_state));
            Log.Info("Remote ID was already taken at first registration; generated a new ID.");
            var retries = _idRetries;
            _ = Task.Run(async () =>
            {
                DeviceRelayClient old;
                lock (_gate) { old = _client; _client = null; }
                if (old != null) await old.StopAsync().ConfigureAwait(false);
                _idRetries = retries;
                Start();
            });
        }
        Changed?.Invoke();
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (IOException ex) { Log.Error(ex, "Could not save the computer state"); }
    }

    public void Dispose() => _client?.Dispose();
}

/// <summary>Thông tin thiết bị để hiển thị. Không thu thập mật khẩu hay dữ liệu cá nhân.</summary>
public static class DeviceInfoProvider
{
    public static DeviceInfo Collect()
    {
        var (ip, mac) = PrimaryAddress();
        // Giới hạn lắng nghe vào một địa chỉ thì chỉ báo địa chỉ đó, vì các địa chỉ khác sẽ không kết nối được.
        var bind = SessionManager.BindAddress();
        if (!bind.Equals(System.Net.IPAddress.Any)) ip = bind.ToString();
        // Ghi đè địa chỉ công bố: dùng khi cổng được chuyển tiếp qua router, hoặc khi kiểm thử.
        var advertised = Environment.GetEnvironmentVariable("RDM_ADVERTISE_ADDRESS");
        if (!string.IsNullOrWhiteSpace(advertised)) ip = advertised.Trim();
        return new DeviceInfo { Name = Environment.MachineName, OperatingSystem = OsName(), Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86", LocalIp = ip, MacAddress = mac };
    }

    public static string OsName()
    {
        var version = Environment.OSVersion.Version;
        try
        {
            // Bản x86 chạy trên Windows x64 sẽ bị chuyển hướng sang WOW6432Node, nơi có thể ghi sai edition; phải đọc nhánh gốc.
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            if (string.IsNullOrEmpty(product)) return "Windows " + version;
            // ProductName của Windows 11 vẫn ghi "Windows 10", phải phân biệt bằng số build.
            if (version.Major == 10 && version.Build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return product;
        }
        catch (System.Security.SecurityException) { return "Windows " + version; }
    }

    /// <summary>Card mạng đang hoạt động có gateway; nếu không có thì lấy card đang hoạt động đầu tiên.</summary>
    private static (string ip, string mac) PrimaryAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0);
            foreach (var n in candidates)
            {
                var address = n.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (address == null) continue;
                var bytes = n.GetPhysicalAddress().GetAddressBytes();
                return (address.Address.ToString(), string.Join(":", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
            }
        }
        catch (NetworkInformationException ex) { LogManager.GetCurrentClassLogger().Warn("Could not read network information: {0}", ex.GetType().Name); }
        return (null, null);
    }
}

/// <summary>Khởi động cùng Windows chỉ khi người dùng cho phép. Dùng khóa HKCU nên không cần quyền quản trị và người dùng tự tắt được.</summary>
public sealed class StartupManager
{
    public const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RDM Client";
    private readonly string _keyPath;

    public StartupManager(string keyPath = DefaultKey) => _keyPath = keyPath;

    public bool IsEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(_keyPath); return key?.GetValue(ValueName) != null; }
    }

    public void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        if (enabled) key.SetValue(ValueName, "\"" + exePath + "\"");
        else key.DeleteValue(ValueName, false);
    }
}

/// <summary>Nhật ký hoạt động trên máy được điều khiển để chủ máy xem được. Chỉ ghi sự kiện, luôn lọc secret.</summary>
public sealed class ActivityLog
{
    private const int MaxLines = 500;
    private readonly string _path;
    private readonly object _gate = new object();

    public ActivityLog(string path) => _path = path;

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.AppendAllText(_path, DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + "  " + LogRedactor.Redact(message) + Environment.NewLine);
                var lines = File.ReadAllLines(_path);
                if (lines.Length > MaxLines * 2) File.WriteAllLines(_path, lines.Skip(lines.Length - MaxLines));
            }
            catch (IOException ex) { LogManager.GetCurrentClassLogger().Error(ex, "Could not write the activity log"); }
        }
    }

    public string[] ReadRecent(int count)
    {
        lock (_gate)
        {
            try { return File.Exists(_path) ? File.ReadAllLines(_path).Reverse().Take(count).ToArray() : new string[0]; }
            catch (IOException) { return new string[0]; }
        }
    }
}
