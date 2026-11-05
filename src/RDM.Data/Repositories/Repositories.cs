using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using RDM.Shared.Models;
using RDM.Shared.Security;

namespace RDM.Data.Repositories;

public sealed class ClientRepository
{
    private readonly SQLiteContext _ctx;
    public ClientRepository(SQLiteContext ctx) => _ctx = ctx;

    private const string Columns = @"c.ID, c.DeviceID, c.RemoteID, c.DeviceName, c.Description, c.GroupID, g.Name AS GroupName,
        c.IPAddress, c.MacAddress, c.OperatingSystem, c.Architecture, c.LastOnline, c.CreatedDate, c.IsEnabled,
        (c.EncryptedCredential IS NOT NULL) AS HasCredential,
        (SELECT GROUP_CONCAT(t.Name, ', ') FROM ClientTags ct JOIN Tags t ON t.ID = ct.TagID WHERE ct.ClientID = c.ID) AS Tags";

    public List<ClientDevice> Query(ClientQuery q)
    {
        var sql = new StringBuilder($"SELECT {Columns}, ");
        var args = new List<(string, object)>();
        if (q.UserId.HasValue)
        {
            sql.Append("EXISTS(SELECT 1 FROM Favorites f WHERE f.ClientID = c.ID AND f.UserID = @uid) AS IsFav");
            args.Add(("@uid", q.UserId.Value));
        }
        else sql.Append("c.IsFavorite AS IsFav");
        sql.Append(" FROM Clients c LEFT JOIN Groups g ON g.ID = c.GroupID WHERE 1=1");

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            sql.Append(@" AND (c.DeviceName LIKE @s ESCAPE '\' OR c.RemoteID LIKE @s ESCAPE '\' OR c.IPAddress LIKE @s ESCAPE '\'
                OR c.OperatingSystem LIKE @s ESCAPE '\' OR g.Name LIKE @s ESCAPE '\'
                OR EXISTS(SELECT 1 FROM ClientTags ct JOIN Tags t ON t.ID = ct.TagID WHERE ct.ClientID = c.ID AND t.Name LIKE @s ESCAPE '\'))");
            args.Add(("@s", "%" + Db.EscapeLike(q.Search.Trim()) + "%"));
        }
        if (q.GroupId.HasValue) { sql.Append(" AND c.GroupID = @g"); args.Add(("@g", q.GroupId.Value)); }
        if (q.FavoritesOnly && q.UserId.HasValue) sql.Append(" AND IsFav = 1");
        sql.Append(" ORDER BY c.DeviceName COLLATE NOCASE LIMIT @limit");
        args.Add(("@limit", q.Limit));

        var result = new List<ClientDevice>();
        using var c = _ctx.Open();
        using var cmd = c.Cmd(sql.ToString(), null, args.ToArray());
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(Map(r));
        return result;
    }

    public ClientDevice GetById(long id)
    {
        using var c = _ctx.Open();
        using var cmd = c.Cmd($"SELECT {Columns}, c.IsFavorite AS IsFav FROM Clients c LEFT JOIN Groups g ON g.ID = c.GroupID WHERE c.ID = @id", null, ("@id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public bool RemoteIdExists(string remoteId, long? exceptId = null)
    {
        using var c = _ctx.Open();
        return c.Scalar("SELECT COUNT(*) FROM Clients WHERE RemoteID = @r AND (@x IS NULL OR ID <> @x)", null, ("@r", remoteId), ("@x", exceptId)) > 0;
    }

    public long Insert(ClientDevice d)
    {
        using var c = _ctx.Open();
        return Insert(c, null, d);
    }

    public long Insert(SQLiteConnection c, SQLiteTransaction tx, ClientDevice d) => c.InsertReturningId(@"
        INSERT INTO Clients(DeviceID, RemoteID, DeviceName, Description, GroupID, IPAddress, MacAddress, OperatingSystem, Architecture, CreatedDate, IsEnabled)
        VALUES(@dev, @rid, @name, @desc, @g, @ip, @mac, @os, @arch, @cd, @en)", tx,
        ("@dev", d.DeviceId), ("@rid", d.RemoteId), ("@name", d.DeviceName), ("@desc", d.Description), ("@g", d.GroupId),
        ("@ip", d.IpAddress), ("@mac", d.MacAddress), ("@os", d.OperatingSystem), ("@arch", d.Architecture), ("@cd", d.CreatedDate), ("@en", d.IsEnabled));

    public void Update(ClientDevice d)
    {
        using var c = _ctx.Open();
        c.Exec(@"UPDATE Clients SET RemoteID=@rid, DeviceName=@name, Description=@desc, GroupID=@g, IPAddress=@ip, MacAddress=@mac,
                 OperatingSystem=@os, Architecture=@arch, IsEnabled=@en WHERE ID=@id", null,
            ("@rid", d.RemoteId), ("@name", d.DeviceName), ("@desc", d.Description), ("@g", d.GroupId), ("@ip", d.IpAddress), ("@mac", d.MacAddress),
            ("@os", d.OperatingSystem), ("@arch", d.Architecture), ("@en", d.IsEnabled), ("@id", d.Id));
    }

    public void Delete(long id)
    {
        using var c = _ctx.Open();
        c.Exec("DELETE FROM Clients WHERE ID=@id", null, ("@id", id));
    }

    public string GetEncryptedCredential(long id, out string deviceId)
    {
        using var c = _ctx.Open();
        using var cmd = c.Cmd("SELECT DeviceID, EncryptedCredential FROM Clients WHERE ID=@id", null, ("@id", id));
        using var r = cmd.ExecuteReader();
        deviceId = null;
        if (!r.Read()) return null;
        deviceId = r.Str("DeviceID");
        return r.Str("EncryptedCredential");
    }

    public void SetEncryptedCredential(long id, string encrypted)
    {
        using var c = _ctx.Open();
        c.Exec("UPDATE Clients SET EncryptedCredential=@e WHERE ID=@id", null, ("@e", encrypted), ("@id", id));
    }

    /// <summary>Favorites là nguồn chính; Clients.IsFavorite cập nhật cùng transaction.</summary>
    public void SetFavorite(long clientId, long userId, bool favorite)
    {
        _ctx.InTransaction((c, tx) =>
        {
            if (favorite)
                c.Exec("INSERT OR IGNORE INTO Favorites(UserID, ClientID, CreatedDate) VALUES(@u, @c, @d)", tx, ("@u", userId), ("@c", clientId), ("@d", DateTime.UtcNow));
            else
                c.Exec("DELETE FROM Favorites WHERE UserID=@u AND ClientID=@c", tx, ("@u", userId), ("@c", clientId));
            c.Exec("UPDATE Clients SET IsFavorite = EXISTS(SELECT 1 FROM Favorites WHERE ClientID=@c) WHERE ID=@c", tx, ("@c", clientId));
        });
    }

    public void SetTags(long clientId, IEnumerable<string> tags)
    {
        _ctx.InTransaction((c, tx) =>
        {
            c.Exec("DELETE FROM ClientTags WHERE ClientID=@c", tx, ("@c", clientId));
            foreach (var tag in tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                c.Exec("INSERT OR IGNORE INTO Tags(Name) VALUES(@n)", tx, ("@n", tag));
                c.Exec("INSERT OR IGNORE INTO ClientTags(ClientID, TagID) SELECT @c, ID FROM Tags WHERE Name=@n", tx, ("@c", clientId), ("@n", tag));
            }
            c.Exec("DELETE FROM Tags WHERE ID NOT IN (SELECT TagID FROM ClientTags)", tx);
        });
    }

    public int Count()
    {
        using var c = _ctx.Open();
        return (int)c.Scalar("SELECT COUNT(*) FROM Clients");
    }

    /// <summary>Cập nhật thông tin từ Relay trong một transaction. Trả về số thiết bị được tự động thêm.</summary>
    public int ApplyPresence(IEnumerable<RDM.Shared.Protocol.PresenceEntry> entries, bool autoRegister)
    {
        return _ctx.InTransaction((c, tx) =>
        {
            var added = 0;
            foreach (var e in entries)
            {
                var info = e.Info ?? new RDM.Shared.Protocol.DeviceInfo();
                var changed = c.Exec(@"UPDATE Clients SET LastOnline=@l, IPAddress=COALESCE(NULLIF(@ip,''),IPAddress), MacAddress=COALESCE(NULLIF(@mac,''),MacAddress),
                        OperatingSystem=COALESCE(NULLIF(@os,''),OperatingSystem), Architecture=COALESCE(NULLIF(@arch,''),Architecture) WHERE RemoteID=@r", tx,
                    ("@l", e.LastSeenUtc), ("@ip", info.LocalIp), ("@mac", info.MacAddress), ("@os", info.OperatingSystem), ("@arch", info.Architecture), ("@r", e.RemoteId));
                if (changed == 0 && autoRegister && e.IsOnline)
                {
                    Insert(c, tx, new ClientDevice
                    {
                        DeviceId = Guid.NewGuid().ToString("N"), RemoteId = e.RemoteId, DeviceName = string.IsNullOrWhiteSpace(info.Name) ? e.RemoteId : info.Name,
                        IpAddress = info.LocalIp, MacAddress = info.MacAddress, OperatingSystem = info.OperatingSystem, Architecture = info.Architecture,
                        CreatedDate = DateTime.UtcNow, IsEnabled = true
                    });
                    c.Exec("UPDATE Clients SET LastOnline=@l WHERE RemoteID=@r", tx, ("@l", e.LastSeenUtc), ("@r", e.RemoteId));
                    added++;
                }
            }
            return added;
        });
    }

    private static ClientDevice Map(SQLiteDataReader r) => new ClientDevice
    {
        Id = r.Long("ID"), DeviceId = r.Str("DeviceID"), RemoteId = r.Str("RemoteID"), DeviceName = r.Str("DeviceName"),
        Description = r.Str("Description"), GroupId = r.LongN("GroupID"), GroupName = r.Str("GroupName"), IpAddress = r.Str("IPAddress"),
        MacAddress = r.Str("MacAddress"), OperatingSystem = r.Str("OperatingSystem"), Architecture = r.Str("Architecture"),
        LastOnline = r.DateN("LastOnline"), CreatedDate = r.Date("CreatedDate"), IsEnabled = r.Bool("IsEnabled"),
        HasCredential = r.Bool("HasCredential"), IsFavorite = r.Bool("IsFav"), Tags = r.Str("Tags")
    };
}

public sealed class GroupRepository
{
    private readonly SQLiteContext _ctx;
    public GroupRepository(SQLiteContext ctx) => _ctx = ctx;

    public List<DeviceGroup> GetAll()
    {
        var list = new List<DeviceGroup>();
        using var c = _ctx.Open();
        using var cmd = c.Cmd(@"SELECT g.ID, g.Name, g.Description, g.CreatedDate, (SELECT COUNT(*) FROM Clients x WHERE x.GroupID = g.ID) AS Cnt
                                FROM Groups g ORDER BY g.Name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DeviceGroup { Id = r.Long("ID"), Name = r.Str("Name"), Description = r.Str("Description"), CreatedDate = r.Date("CreatedDate"), DeviceCount = (int)r.Long("Cnt") });
        return list;
    }

    public long Insert(string name, string description)
    {
        using var c = _ctx.Open();
        return c.InsertReturningId("INSERT INTO Groups(Name, Description, CreatedDate) VALUES(@n, @d, @c)", null, ("@n", name), ("@d", description), ("@c", DateTime.UtcNow));
    }

    /// <summary>Tìm nhóm theo tên hoặc tạo mới trong transaction có sẵn (dùng khi Import).</summary>
    public long GetOrCreate(SQLiteConnection c, SQLiteTransaction tx, string name)
    {
        var id = c.Scalar("SELECT ID FROM Groups WHERE Name=@n", tx, ("@n", name));
        return id != 0 ? id : c.InsertReturningId("INSERT INTO Groups(Name, CreatedDate) VALUES(@n, @c)", tx, ("@n", name), ("@c", DateTime.UtcNow));
    }

    public bool NameExists(string name, long? exceptId = null)
    {
        using var c = _ctx.Open();
        return c.Scalar("SELECT COUNT(*) FROM Groups WHERE Name=@n AND (@x IS NULL OR ID<>@x)", null, ("@n", name), ("@x", exceptId)) > 0;
    }

    public void Update(long id, string name, string description)
    {
        using var c = _ctx.Open();
        c.Exec("UPDATE Groups SET Name=@n, Description=@d WHERE ID=@id", null, ("@n", name), ("@d", description), ("@id", id));
    }

    public void Delete(long id)
    {
        using var c = _ctx.Open();
        c.Exec("DELETE FROM Groups WHERE ID=@id", null, ("@id", id));
    }
}

public sealed class ConnectionHistoryRepository
{
    private readonly SQLiteContext _ctx;
    public ConnectionHistoryRepository(SQLiteContext ctx) => _ctx = ctx;

    public long Start(string remoteId, string deviceName, string user, string type, string status)
    {
        using var c = _ctx.Open();
        return c.InsertReturningId(@"INSERT INTO ConnectionHistory(RemoteID, DeviceName, ControllerUser, StartTime, ConnectionType, ConnectionStatus)
                                     VALUES(@r, @d, @u, @s, @t, @st)", null,
            ("@r", remoteId), ("@d", deviceName), ("@u", user), ("@s", DateTime.UtcNow), ("@t", type), ("@st", status));
    }

    public void Finish(long id, string status)
    {
        using var c = _ctx.Open();
        var now = DateTime.UtcNow;
        c.Exec(@"UPDATE ConnectionHistory SET EndTime=@e, ConnectionStatus=@st,
                 Duration=CAST(ROUND((julianday(@e) - julianday(StartTime)) * 86400) AS INTEGER) WHERE ID=@id", null, ("@e", now), ("@st", status), ("@id", id));
    }

    public List<ConnectionRecord> Query(string search, int limit)
    {
        var list = new List<ConnectionRecord>();
        using var c = _ctx.Open();
        using var cmd = c.Cmd(@"SELECT * FROM ConnectionHistory
              WHERE @s IS NULL OR RemoteID LIKE @s ESCAPE '\' OR DeviceName LIKE @s ESCAPE '\' OR ControllerUser LIKE @s ESCAPE '\'
              ORDER BY StartTime DESC LIMIT @l", null,
            ("@s", string.IsNullOrWhiteSpace(search) ? null : "%" + Db.EscapeLike(search.Trim()) + "%"), ("@l", limit));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new ConnectionRecord
        {
            Id = r.Long("ID"), RemoteId = r.Str("RemoteID"), DeviceName = r.Str("DeviceName"), ControllerUser = r.Str("ControllerUser"),
            StartTime = r.Date("StartTime"), EndTime = r.DateN("EndTime"), DurationSeconds = r.LongN("Duration"),
            ConnectionType = r.Str("ConnectionType"), ConnectionStatus = r.Str("ConnectionStatus")
        });
        return list;
    }

    /// <summary>Mỗi Remote ID một dòng, phiên gần nhất trước.</summary>
    public List<ConnectionRecord> Recent(int limit)
    {
        var seen = new HashSet<string>();
        return Query(null, Math.Max(limit * 20, 500)).Where(h => seen.Add(h.RemoteId)).Take(limit).ToList();
    }

    public int Count()
    {
        using var c = _ctx.Open();
        return (int)c.Scalar("SELECT COUNT(*) FROM ConnectionHistory");
    }

    /// <summary>Thống kê theo ngày (giờ UTC), đủ cả ngày không có dữ liệu.</summary>
    public List<DailyStat> Daily(int days)
    {
        var today = DateTime.UtcNow.Date;
        var from = today.AddDays(-(days - 1));
        var map = new Dictionary<DateTime, DailyStat>();
        for (var i = 0; i < days; i++) { var d = from.AddDays(i); map[d] = new DailyStat { Day = d }; }
        using var c = _ctx.Open();
        using var cmd = c.Cmd(@"SELECT substr(StartTime,1,10) AS Day, COUNT(*) AS Cnt, COALESCE(SUM(Duration),0) AS Dur
                                FROM ConnectionHistory WHERE StartTime >= @f GROUP BY Day", null, ("@f", from));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.Str("Day"), out var day) && map.TryGetValue(day.Date, out var s))
            { s.Connections = (int)r.Long("Cnt"); s.DurationSeconds = r.Long("Dur"); }
        return map.Values.OrderBy(x => x.Day).ToList();
    }
}

/// <summary>Hàng thô của bảng Users: các trường nhạy cảm vẫn là bản mã, UserService mới giải mã.</summary>
public sealed class UserRow
{
    public long Id; public string UserUid, UsernameHash, EncryptedUsername, PasswordHash, EncryptedFullName, EncryptedEmail;
    public UserRole Role; public bool IsEnabled, MustChangePassword; public int FailedAttempts;
    public DateTime? LockedUntil, LastLogin; public DateTime CreatedDate;
}

public sealed class UserRepository
{
    private readonly SQLiteContext _ctx;
    public UserRepository(SQLiteContext ctx) => _ctx = ctx;

    public int Count()
    {
        using var c = _ctx.Open();
        return (int)c.Scalar("SELECT COUNT(*) FROM Users");
    }

    public int CountEnabledAdmins()
    {
        using var c = _ctx.Open();
        return (int)c.Scalar("SELECT COUNT(*) FROM Users WHERE Role=@r AND IsEnabled=1", null, ("@r", UserRole.Administrator));
    }

    public UserRow GetByHash(string usernameHash) => One("UsernameHash=@p", usernameHash);
    public UserRow GetById(long id) => One("ID=@p", id);

    public List<UserRow> GetAll()
    {
        var list = new List<UserRow>();
        using var c = _ctx.Open();
        using var cmd = c.Cmd("SELECT * FROM Users ORDER BY ID");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public long Insert(UserRow u)
    {
        using var c = _ctx.Open();
        return c.InsertReturningId(@"INSERT INTO Users(UserUid, UsernameHash, EncryptedUsername, PasswordHash, EncryptedFullName, EncryptedEmail, Role, IsEnabled, MustChangePassword, CreatedDate)
              VALUES(@uid, @h, @n, @p, @f, @e, @r, @en, @m, @c)", null,
            ("@uid", u.UserUid), ("@h", u.UsernameHash), ("@n", u.EncryptedUsername), ("@p", u.PasswordHash), ("@f", u.EncryptedFullName), ("@e", u.EncryptedEmail),
            ("@r", u.Role), ("@en", u.IsEnabled), ("@m", u.MustChangePassword), ("@c", u.CreatedDate));
    }

    public void UpdateProfile(long id, string encFullName, string encEmail, UserRole role, bool enabled)
    {
        using var c = _ctx.Open();
        c.Exec("UPDATE Users SET EncryptedFullName=@f, EncryptedEmail=@e, Role=@r, IsEnabled=@en WHERE ID=@id", null,
            ("@f", encFullName), ("@e", encEmail), ("@r", role), ("@en", enabled), ("@id", id));
    }

    public void SetPassword(long id, string encPasswordHash, bool mustChange)
    {
        using var c = _ctx.Open();
        c.Exec("UPDATE Users SET PasswordHash=@p, MustChangePassword=@m, FailedAttempts=0, LockedUntil=NULL WHERE ID=@id", null, ("@p", encPasswordHash), ("@m", mustChange), ("@id", id));
    }

    public void RecordFailure(long id, int attempts, DateTime? lockedUntil)
    {
        using var c = _ctx.Open();
        c.Exec("UPDATE Users SET FailedAttempts=@a, LockedUntil=@l WHERE ID=@id", null, ("@a", attempts), ("@l", lockedUntil), ("@id", id));
    }

    public void RecordSuccess(long id)
    {
        using var c = _ctx.Open();
        c.Exec("UPDATE Users SET FailedAttempts=0, LockedUntil=NULL, LastLogin=@d WHERE ID=@id", null, ("@d", DateTime.UtcNow), ("@id", id));
    }

    public void Delete(long id)
    {
        using var c = _ctx.Open();
        c.Exec("DELETE FROM Users WHERE ID=@id", null, ("@id", id));
    }

    private UserRow One(string where, object p)
    {
        using var c = _ctx.Open();
        using var cmd = c.Cmd($"SELECT * FROM Users WHERE {where}", null, ("@p", p));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    private static UserRow Map(SQLiteDataReader r) => new UserRow
    {
        Id = r.Long("ID"), UserUid = r.Str("UserUid"), UsernameHash = r.Str("UsernameHash"), EncryptedUsername = r.Str("EncryptedUsername"),
        PasswordHash = r.Str("PasswordHash"), EncryptedFullName = r.Str("EncryptedFullName"), EncryptedEmail = r.Str("EncryptedEmail"),
        Role = (UserRole)r.Long("Role"), IsEnabled = r.Bool("IsEnabled"), MustChangePassword = r.Bool("MustChangePassword"),
        FailedAttempts = (int)r.Long("FailedAttempts"), LockedUntil = r.DateN("LockedUntil"), LastLogin = r.DateN("LastLogin"), CreatedDate = r.Date("CreatedDate")
    };
}

public sealed class SettingsRepository
{
    private readonly SQLiteContext _ctx;
    private readonly FieldProtector _protector;

    public SettingsRepository(SQLiteContext ctx, FieldProtector protector) { _ctx = ctx; _protector = protector; }

    public string Get(string key, string fallback = null)
    {
        using var c = _ctx.Open();
        using var cmd = c.Cmd("SELECT Value, IsEncrypted FROM Settings WHERE Key=@k", null, ("@k", key));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return fallback;
        var value = r.Str("Value");
        return r.Bool("IsEncrypted") ? _protector.Decrypt(value, Context(key)) : value;
    }

    public void Set(string key, string value, bool encrypted = false)
    {
        var stored = encrypted ? _protector.Encrypt(value, Context(key)) : value;
        using var c = _ctx.Open();
        c.Exec(@"INSERT INTO Settings(Key, Value, IsEncrypted, UpdatedDate) VALUES(@k, @v, @e, @d)
                 ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value, IsEncrypted=excluded.IsEncrypted, UpdatedDate=excluded.UpdatedDate", null,
            ("@k", key), ("@v", stored), ("@e", encrypted), ("@d", DateTime.UtcNow));
    }

    public static string Context(string key) => FieldProtector.Context("Settings", "Value", key);
}

public sealed class AuditLogRepository
{
    private readonly SQLiteContext _ctx;
    public AuditLogRepository(SQLiteContext ctx) => _ctx = ctx;

    public void Insert(AuditEntry e)
    {
        using var c = _ctx.Open();
        c.Exec(@"INSERT INTO AuditLogs(Timestamp, EventType, Severity, Username, RemoteID, DeviceName, Details, Success)
                 VALUES(@t, @ev, @sv, @u, @r, @d, @de, @su)", null,
            ("@t", e.Timestamp), ("@ev", e.EventType), ("@sv", e.Severity), ("@u", e.Username), ("@r", e.RemoteId), ("@d", e.DeviceName), ("@de", e.Details), ("@su", e.Success));
    }

    public List<AuditEntry> Query(string search, int limit)
    {
        var list = new List<AuditEntry>();
        using var c = _ctx.Open();
        using var cmd = c.Cmd(@"SELECT * FROM AuditLogs
            WHERE @s IS NULL OR EventType LIKE @s ESCAPE '\' OR Username LIKE @s ESCAPE '\' OR RemoteID LIKE @s ESCAPE '\' OR Details LIKE @s ESCAPE '\'
            ORDER BY ID DESC LIMIT @l", null,
            ("@s", string.IsNullOrWhiteSpace(search) ? null : "%" + Db.EscapeLike(search.Trim()) + "%"), ("@l", limit));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new AuditEntry
        {
            Id = r.Long("ID"), Timestamp = r.Date("Timestamp"), EventType = r.Str("EventType"), Severity = (AuditSeverity)r.Long("Severity"),
            Username = r.Str("Username"), RemoteId = r.Str("RemoteID"), DeviceName = r.Str("DeviceName"), Details = r.Str("Details"), Success = r.Bool("Success")
        });
        return list;
    }

    public int CountSince(string eventType, DateTime since)
    {
        using var c = _ctx.Open();
        return (int)c.Scalar("SELECT COUNT(*) FROM AuditLogs WHERE EventType=@e AND Timestamp >= @s", null, ("@e", eventType), ("@s", since));
    }

    public int PurgeOlderThan(DateTime cutoff)
    {
        using var c = _ctx.Open();
        return c.Exec("DELETE FROM AuditLogs WHERE Timestamp < @c", null, ("@c", cutoff));
    }
}

/// <summary>Khóa công khai đã ghim của thiết bị. Không phải bí mật, nhưng sửa được nó là MITM được, nên chỉ đổi qua Service có quyền và Audit.</summary>
public sealed class PinnedHostKeyRepository
{
    private readonly SQLiteContext _ctx;
    public PinnedHostKeyRepository(SQLiteContext ctx) => _ctx = ctx;

    public byte[] Get(string remoteId)
    {
        using var c = _ctx.Open();
        using var cmd = c.Cmd("SELECT PublicKey FROM PinnedHostKeys WHERE RemoteID=@r", null, ("@r", remoteId));
        return cmd.ExecuteScalar() as byte[];
    }

    public void Pin(string remoteId, byte[] publicKey, string fingerprint, string user)
    {
        using var c = _ctx.Open();
        c.Exec("INSERT INTO PinnedHostKeys(RemoteID, PublicKey, Fingerprint, PinnedBy, PinnedDate) VALUES(@r, @k, @f, @u, @d)", null,
            ("@r", remoteId), ("@k", publicKey), ("@f", fingerprint), ("@u", user), ("@d", DateTime.UtcNow));
    }

    public void Remove(string remoteId)
    {
        using var c = _ctx.Open();
        c.Exec("DELETE FROM PinnedHostKeys WHERE RemoteID=@r", null, ("@r", remoteId));
    }
}

/// <summary>Lưu metadata khóa. WrappedKey đã được bọc bằng DPAPI trước khi vào đây.</summary>
public sealed class SecurityKeyRepository : IKeyStore
{
    private readonly SQLiteContext _ctx;
    public SecurityKeyRepository(SQLiteContext ctx) => _ctx = ctx;

    public IReadOnlyList<KeyRecord> LoadAll()
    {
        var list = new List<KeyRecord>();
        using var c = _ctx.Open();
        using var cmd = c.Cmd("SELECT * FROM SecurityKeys ORDER BY Purpose, Version");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new KeyRecord
        {
            Purpose = (KeyPurpose)r.Long("Purpose"), Version = (int)r.Long("Version"), Status = (KeyStatus)r.Long("Status"),
            WrappedKey = (byte[])r["WrappedKey"], ProtectionScheme = r.Str("ProtectionScheme"), Fingerprint = r.Str("Fingerprint"),
            CreatedDate = r.Date("CreatedDate"), RotatedDate = r.DateN("RotatedDate"), RevokedDate = r.DateN("RevokedDate")
        });
        return list;
    }

    public void Insert(KeyRecord k)
    {
        using var c = _ctx.Open();
        Insert(c, null, k);
    }

    public static void Insert(SQLiteConnection c, SQLiteTransaction tx, KeyRecord k) => c.Exec(@"
        INSERT INTO SecurityKeys(Purpose, Version, Status, WrappedKey, ProtectionScheme, Fingerprint, CreatedDate)
        VALUES(@p, @v, @s, @w, @sc, @f, @c)", tx,
        ("@p", k.Purpose), ("@v", k.Version), ("@s", k.Status), ("@w", k.WrappedKey), ("@sc", k.ProtectionScheme), ("@f", k.Fingerprint), ("@c", k.CreatedDate));

    public void UpdateStatus(KeyPurpose purpose, int version, KeyStatus status, DateTime when)
    {
        using var c = _ctx.Open();
        var column = status == KeyStatus.Revoked ? "RevokedDate" : "RotatedDate";
        c.Exec($"UPDATE SecurityKeys SET Status=@s, {column}=@w WHERE Purpose=@p AND Version=@v", null, ("@s", status), ("@w", when), ("@p", purpose), ("@v", version));
    }
}
