using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RDM.Shared.Network;
using RDM.Shared.Protocol;

namespace RDM.Server;

public enum RegisterOutcome { Registered, Known, Conflict, Revoked, EnrollmentRequired }

/// <summary>
/// Sổ đăng ký thiết bị: Remote ID gắn với khóa công khai ngay lần đăng ký đầu.
/// Database riêng của Relay, chỉ chứa khóa công khai và thông tin hiển thị, không có bí mật nào.
/// </summary>
public sealed class ClientRegistry
{
    private readonly string _connectionString;
    private readonly object _gate = new object();

    public ClientRegistry(IOptions<RelayOptions> options, ILogger<ClientRegistry> log)
    {
        var path = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        lock (_gate)
        {
            using var c = Open();
            Exec(c, @"PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS Devices(
                  RemoteId TEXT PRIMARY KEY, PublicKey BLOB NOT NULL, Name TEXT, OperatingSystem TEXT, Architecture TEXT, LocalIp TEXT, MacAddress TEXT,
                  FirstSeen TEXT NOT NULL, LastSeen TEXT NOT NULL, Revoked INTEGER NOT NULL DEFAULT 0);");
            // Ghi phiên bản SQLite thật đang chạy: thư viện nhúng SQLite nên máy quét NuGet không phải lúc nào cũng thấy lỗ hổng của nó.
            using var version = c.CreateCommand();
            version.CommandText = "SELECT sqlite_version()";
            log.LogInformation("SQLite {Version}", version.ExecuteScalar());
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Đăng ký hoặc nhận lại thiết bị. Remote ID đã gắn khóa khác thì từ chối, chống chiếm Remote ID.</summary>
    public RegisterOutcome Register(string remoteId, byte[] publicKey, DeviceInfo info, bool enrollmentAllowed)
    {
        lock (_gate)
        {
            using var c = Open();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT PublicKey, Revoked FROM Devices WHERE RemoteId=@r";
                q.Parameters.AddWithValue("@r", remoteId);
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    if (r.GetInt64(1) != 0) return RegisterOutcome.Revoked;
                    return ((byte[])r.GetValue(0)).SequenceEqual(publicKey) ? RegisterOutcome.Known : RegisterOutcome.Conflict;
                }
            }
            if (!enrollmentAllowed) return RegisterOutcome.EnrollmentRequired;
            var now = DateTime.UtcNow.ToString("o");
            Exec(c, "INSERT INTO Devices(RemoteId, PublicKey, FirstSeen, LastSeen) VALUES(@r, @k, @n, @n)", ("@r", remoteId), ("@k", publicKey), ("@n", now));
            return RegisterOutcome.Registered;
        }
    }

    public bool Exists(string remoteId)
    {
        lock (_gate)
        {
            using var c = Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM Devices WHERE RemoteId=@r AND Revoked=0";
            q.Parameters.AddWithValue("@r", remoteId);
            return Convert.ToInt64(q.ExecuteScalar()) > 0;
        }
    }

    public void UpdateSeen(string remoteId, DeviceInfo info)
    {
        lock (_gate)
        {
            using var c = Open();
            Exec(c, "UPDATE Devices SET Name=@n, OperatingSystem=@o, Architecture=@a, LocalIp=@i, MacAddress=@m, LastSeen=@l WHERE RemoteId=@r",
                ("@n", info.Name), ("@o", info.OperatingSystem), ("@a", info.Architecture), ("@i", info.LocalIp), ("@m", info.MacAddress), ("@l", DateTime.UtcNow.ToString("o")), ("@r", remoteId));
        }
    }

    public List<PresenceEntry> GetAll()
    {
        lock (_gate)
        {
            var list = new List<PresenceEntry>();
            using var c = Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT RemoteId, Name, OperatingSystem, Architecture, LocalIp, MacAddress, LastSeen FROM Devices WHERE Revoked=0";
            using var r = q.ExecuteReader();
            while (r.Read())
                list.Add(new PresenceEntry
                {
                    RemoteId = r.GetString(0),
                    Info = new DeviceInfo { Name = Text(r, 1), OperatingSystem = Text(r, 2), Architecture = Text(r, 3), LocalIp = Text(r, 4), MacAddress = Text(r, 5) },
                    LastSeenUtc = DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)
                });
            return list;
        }
    }

    private static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

/// <summary>Một thiết bị đang kết nối.</summary>
public sealed class DeviceSession
{
    public string RemoteId { get; init; }
    public DeviceInfo Info { get; init; }
    public FramedStream Channel { get; init; }
    public string PublicIp { get; init; }
    /// <summary>Giới hạn số lần xin phiên: thời điểm các lần gần nhất.</summary>
    public readonly System.Collections.Generic.Queue<DateTime> Requests = new System.Collections.Generic.Queue<DateTime>();
    public bool AccessEnabled;
    public long LastHeartbeatTicks;
}

/// <summary>Một Controller đang theo dõi. Hàng đợi có giới hạn: Controller chậm bị ngắt để tự nhận lại ảnh chụp, không làm nghẽn Server.</summary>
public sealed class PresenceSubscriber
{
    public Channel<(MessageType Type, byte[] Payload)> Queue { get; } = Channel.CreateBounded<(MessageType, byte[])>(new BoundedChannelOptions(4096) { SingleReader = true });
    public FramedStream Stream { get; init; }
}

/// <summary>Trạng thái Online trong RAM và phát thay đổi tới các Controller.</summary>
public sealed class PresenceHub
{
    private readonly ClientRegistry _registry;
    private readonly ILogger<PresenceHub> _log;
    private readonly ConcurrentDictionary<string, DeviceSession> _online = new ConcurrentDictionary<string, DeviceSession>();
    private readonly ConcurrentDictionary<PresenceSubscriber, byte> _subscribers = new ConcurrentDictionary<PresenceSubscriber, byte>();

    public PresenceHub(ClientRegistry registry, ILogger<PresenceHub> log) { _registry = registry; _log = log; }

    public int OnlineCount => _online.Count;
    public int ControllerCount => _subscribers.Count;
    public IEnumerable<DeviceSession> Sessions => _online.Values;
    public DeviceSession Find(string remoteId) => _online.TryGetValue(remoteId, out var s) ? s : null;

    public void DeviceOnline(DeviceSession session)
    {
        DeviceSession previous = null;
        _online.AddOrUpdate(session.RemoteId, session, (_, old) => { previous = old; return session; });
        previous?.Channel.Dispose();
        _registry.UpdateSeen(session.RemoteId, session.Info);
        Broadcast(ToEntry(session, true));
    }

    /// <summary>Chỉ báo Offline nếu đúng phiên này: phiên mới cùng Remote ID có thể đã thay thế phiên cũ.</summary>
    public void DeviceOffline(DeviceSession session)
    {
        if (!((ICollection<KeyValuePair<string, DeviceSession>>)_online).Remove(new KeyValuePair<string, DeviceSession>(session.RemoteId, session))) return;
        _registry.UpdateSeen(session.RemoteId, session.Info);
        Broadcast(ToEntry(session, false));
    }

    public void AccessChanged(DeviceSession session) => Broadcast(ToEntry(session, true));

    /// <summary>Đăng ký nhận thay đổi TRƯỚC khi chụp ảnh, để không bỏ sót thay đổi xảy ra giữa chừng.</summary>
    public List<PresenceEntry> Subscribe(PresenceSubscriber subscriber)
    {
        _subscribers[subscriber] = 0;
        var all = _registry.GetAll();
        foreach (var e in all)
            if (_online.TryGetValue(e.RemoteId, out var s))
            {
                e.IsOnline = true;
                e.AccessEnabled = Volatile.Read(ref s.AccessEnabled);
                e.LastSeenUtc = DateTime.UtcNow;
            }
        return all;
    }

    public void Unsubscribe(PresenceSubscriber subscriber) => _subscribers.TryRemove(subscriber, out _);

    private void Broadcast(PresenceEntry entry)
    {
        var payload = Wire.Entries(new[] { entry }, true);
        foreach (var s in _subscribers.Keys)
            if (!s.Queue.Writer.TryWrite((MessageType.PresenceChanged, payload)))
            {
                _log.LogWarning("Controller is receiving too slowly; disconnecting to resynchronize.");
                s.Stream.Dispose();
            }
    }

    private static PresenceEntry ToEntry(DeviceSession s, bool online) => new PresenceEntry
    {
        RemoteId = s.RemoteId, IsOnline = online, AccessEnabled = Volatile.Read(ref s.AccessEnabled), LastSeenUtc = DateTime.UtcNow, Info = s.Info
    };
}
