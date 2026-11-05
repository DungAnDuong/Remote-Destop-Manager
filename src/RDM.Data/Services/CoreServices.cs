using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using RDM.Data.Repositories;
using RDM.Shared.Common;
using RDM.Shared.Models;
using RDM.Shared.Security;

namespace RDM.Data.Services;

/// <summary>Lỗi nghiệp vụ, thông báo an toàn để hiển thị cho người dùng.</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}

/// <summary>Ghi Audit. Không bao giờ làm hỏng thao tác chính; nội dung Details luôn được lọc secret.</summary>
public sealed class AuditLogger
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly AuditLogRepository _repo;
    public AuditLogger(AuditLogRepository repo) => _repo = repo;

    public void Write(string eventType, string username, bool success = true, string details = null, string remoteId = null,
        string deviceName = null, AuditSeverity severity = AuditSeverity.Info)
    {
        try
        {
            _repo.Insert(new AuditEntry
            {
                Timestamp = DateTime.UtcNow, EventType = eventType, Username = username, Success = success, Severity = severity,
                Details = LogRedactor.Redact(details), RemoteId = remoteId, DeviceName = deviceName
            });
        }
        catch (Exception ex) { Log.Error(ex, "Could not write the Audit Log for event {0}", eventType); }
    }
}

/// <summary>Phiên đăng nhập hiện tại và kiểm tra quyền ở tầng Service.</summary>
public sealed class UserSession
{
    private readonly AuditLogger _audit;
    public UserSession(AuditLogger audit) => _audit = audit;

    public UserAccount Current { get; private set; }
    public string Username => Current?.Username;
    /// <summary>
    /// Kết nối nhanh kiểu TeamViewer: người đang dùng Windows, chưa đăng nhập RDM.
    /// Chỉ được kết nối bằng mật khẩu tự nhập; không đọc được mật khẩu đã lưu, không quản lý được gì.
    /// </summary>
    public bool IsQuickSession { get; private set; }
    internal void Set(UserAccount user) { Current = user; IsQuickSession = false; }

    public void BeginQuickSession(string windowsUser)
    {
        Current = new UserAccount { Username = "windows:" + windowsUser, Role = UserRole.Viewer, IsEnabled = true };
        IsQuickSession = true;
    }

    public static bool RoleAllows(UserRole role, Permission permission) => permission switch
    {
        Permission.ViewData => true,
        Permission.QuickConnect or Permission.ManageClients or Permission.UseCredentials => role >= UserRole.Operator,
        _ => role == UserRole.Administrator
    };

    public bool Can(Permission permission) =>
        Current != null && (RoleAllows(Current.Role, permission) || (IsQuickSession && permission == Permission.QuickConnect));

    public void Demand(Permission permission)
    {
        if (Can(permission)) return;
        _audit.Write(AuditEvents.AccessDenied, Username, false, $"Permission required: {permission}", severity: AuditSeverity.Warning);
        throw new UnauthorizedAccessException("Your account does not have permission to do this.");
    }
}

public enum LoginOutcome { Success, Failed }

public sealed class UserService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly UserRepository _repo;
    private readonly FieldProtector _protector;
    private readonly PasswordHasher _hasher;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;
    private readonly Lazy<string> _dummyHash;

    public UserService(UserRepository repo, FieldProtector protector, PasswordHasher hasher, AuditLogger audit, UserSession session)
    {
        _repo = repo; _protector = protector; _hasher = hasher; _audit = audit; _session = session;
        _dummyHash = new Lazy<string>(() => _hasher.Hash(Convert.ToBase64String(CryptoRandom.GetBytes(16))));
    }

    public bool HasAnyUser() => _repo.Count() > 0;

    /// <summary>Tạo Administrator đầu tiên. Chỉ được gọi khi chưa có tài khoản; không có mật khẩu mặc định.</summary>
    public UserAccount CreateFirstAdministrator(string username, string password, string fullName)
    {
        if (HasAnyUser()) throw new ValidationException("An account already exists.");
        var id = InsertUser(username, password, fullName, null, UserRole.Administrator, false);
        _audit.Write(AuditEvents.UserCreated, Normalize(username), details: "Created the first Administrator", severity: AuditSeverity.Warning);
        return ToAccount(_repo.GetById(id));
    }

    /// <summary>
    /// Đăng nhập. Mọi trường hợp thất bại trả cùng một kết quả và đều chạy PBKDF2,
    /// nên không lộ tài khoản có tồn tại hay không qua nội dung hoặc thời gian phản hồi.
    /// </summary>
    public LoginOutcome Login(string username, string password)
    {
        var name = Normalize(username);
        var row = string.IsNullOrEmpty(name) ? null : _repo.GetByHash(_protector.LookupHash(name));
        if (row == null)
        {
            _hasher.Verify(password, _dummyHash.Value, out _);
            // Không ghi chuỗi đã nhập: người dùng có thể đã gõ nhầm mật khẩu vào ô tên đăng nhập.
            _audit.Write(AuditEvents.LoginFailed, null, false, "Account does not exist", severity: AuditSeverity.Warning);
            return LoginOutcome.Failed;
        }

        var storedHash = _protector.Decrypt(row.PasswordHash, Ctx(row.UserUid, "PasswordHash"));
        var ok = _hasher.Verify(password, storedHash, out var needsRehash);
        var locked = row.LockedUntil.HasValue && row.LockedUntil.Value > DateTime.UtcNow;

        if (locked || !row.IsEnabled)
        {
            _audit.Write(AuditEvents.LoginFailed, name, false, locked ? "Account is temporarily locked" : "Account is disabled", severity: AuditSeverity.Warning);
            return LoginOutcome.Failed;
        }
        if (!ok)
        {
            var attempts = row.FailedAttempts + 1;
            var lockNow = attempts >= MaxFailedAttempts;
            _repo.RecordFailure(row.Id, lockNow ? 0 : attempts, lockNow ? DateTime.UtcNow + LockoutDuration : (DateTime?)null);
            _audit.Write(AuditEvents.LoginFailed, name, false, lockNow ? "Wrong password, account temporarily locked" : "Wrong password",
                severity: lockNow ? AuditSeverity.Critical : AuditSeverity.Warning);
            return LoginOutcome.Failed;
        }

        if (needsRehash) _repo.SetPassword(row.Id, EncryptHash(row.UserUid, _hasher.Hash(password)), row.MustChangePassword);
        _repo.RecordSuccess(row.Id);
        _session.Set(ToAccount(_repo.GetById(row.Id)));
        _audit.Write(AuditEvents.LoginSuccess, name);
        return LoginOutcome.Success;
    }

    public void Logout()
    {
        if (_session.Current == null) return;
        if (!_session.IsQuickSession) _audit.Write(AuditEvents.Logout, _session.Username);
        _session.Set(null);
    }

    public List<UserAccount> GetAll()
    {
        _session.Demand(Permission.ManageUsers);
        return _repo.GetAll().Select(ToAccount).ToList();
    }

    public void Create(string username, string password, string fullName, string email, UserRole role)
    {
        _session.Demand(Permission.ManageUsers);
        InsertUser(username, password, fullName, email, role, true);
        _audit.Write(AuditEvents.UserCreated, _session.Username, details: $"Created account {Normalize(username)}, role {role}", severity: AuditSeverity.Warning);
    }

    public void Update(long id, string fullName, string email, UserRole role, bool enabled)
    {
        _session.Demand(Permission.ManageUsers);
        var row = _repo.GetById(id) ?? throw new ValidationException("The account does not exist.");
        var removesAdmin = row.Role == UserRole.Administrator && row.IsEnabled && (role != UserRole.Administrator || !enabled);
        if (removesAdmin && _repo.CountEnabledAdmins() <= 1) throw new ValidationException("At least one active Administrator must remain.");
        _repo.UpdateProfile(id, _protector.Encrypt(fullName, Ctx(row.UserUid, "FullName")), _protector.Encrypt(email, Ctx(row.UserUid, "Email")), role, enabled);
        var username = DecryptUsername(row);
        if (role != row.Role)
            _audit.Write(AuditEvents.RoleChanged, _session.Username, details: $"{username}: {row.Role} -> {role}", severity: AuditSeverity.Warning);
        _audit.Write(AuditEvents.UserUpdated, _session.Username, details: $"Updated account {username}");
        if (_session.Current?.Id == id) _session.Set(ToAccount(_repo.GetById(id)));
    }

    public void Delete(long id)
    {
        _session.Demand(Permission.ManageUsers);
        if (_session.Current?.Id == id) throw new ValidationException("You cannot delete the account you are signed in with.");
        var row = _repo.GetById(id) ?? throw new ValidationException("The account does not exist.");
        if (row.Role == UserRole.Administrator && row.IsEnabled && _repo.CountEnabledAdmins() <= 1)
            throw new ValidationException("At least one active Administrator must remain.");
        var username = DecryptUsername(row);
        _repo.Delete(id);
        _audit.Write(AuditEvents.UserDeleted, _session.Username, details: $"Deleted account {username}", severity: AuditSeverity.Warning);
    }

    public void ChangeOwnPassword(string currentPassword, string newPassword)
    {
        var me = _session.Current ?? throw new UnauthorizedAccessException("Not signed in.");
        var row = _repo.GetById(me.Id);
        if (!_hasher.Verify(currentPassword, _protector.Decrypt(row.PasswordHash, Ctx(row.UserUid, "PasswordHash")), out _))
        {
            _audit.Write(AuditEvents.PasswordChanged, me.Username, false, "Current password is incorrect", severity: AuditSeverity.Warning);
            throw new ValidationException("The current password is incorrect.");
        }
        CheckPolicy(newPassword, me.Username);
        _repo.SetPassword(me.Id, EncryptHash(row.UserUid, _hasher.Hash(newPassword)), false);
        _session.Set(ToAccount(_repo.GetById(me.Id)));
        _audit.Write(AuditEvents.PasswordChanged, me.Username);
    }

    /// <summary>Administrator đặt lại mật khẩu; người dùng phải đổi ở lần đăng nhập sau.</summary>
    public void ResetPassword(long id, string newPassword)
    {
        _session.Demand(Permission.ManageUsers);
        var row = _repo.GetById(id) ?? throw new ValidationException("The account does not exist.");
        var username = DecryptUsername(row);
        CheckPolicy(newPassword, username);
        _repo.SetPassword(id, EncryptHash(row.UserUid, _hasher.Hash(newPassword)), true);
        _audit.Write(AuditEvents.PasswordChanged, _session.Username, details: $"Reset the password of {username}", severity: AuditSeverity.Warning);
    }

    private long InsertUser(string username, string password, string fullName, string email, UserRole role, bool mustChange)
    {
        var name = Normalize(username);
        if (string.IsNullOrEmpty(name) || name.Length < 3 || name.Length > 64) throw new ValidationException("The username must be 3 to 64 characters.");
        CheckPolicy(password, name);
        var hash = _protector.LookupHash(name);
        if (_repo.GetByHash(hash) != null) throw new ValidationException("The username already exists.");
        var uid = Guid.NewGuid().ToString("N");
        return _repo.Insert(new UserRow
        {
            UserUid = uid, UsernameHash = hash,
            EncryptedUsername = _protector.Encrypt(name, Ctx(uid, "Username")),
            PasswordHash = EncryptHash(uid, _hasher.Hash(password)),
            EncryptedFullName = _protector.Encrypt(fullName, Ctx(uid, "FullName")),
            EncryptedEmail = _protector.Encrypt(email, Ctx(uid, "Email")),
            Role = role, IsEnabled = true, MustChangePassword = mustChange, CreatedDate = DateTime.UtcNow
        });
    }

    private static void CheckPolicy(string password, string username)
    {
        var problem = PasswordPolicy.Validate(password, username);
        if (problem != null) throw new ValidationException(problem);
    }

    private string EncryptHash(string uid, string hash) => _protector.Encrypt(hash, Ctx(uid, "PasswordHash"));
    private string DecryptUsername(UserRow row) => _protector.Decrypt(row.EncryptedUsername, Ctx(row.UserUid, "Username"));

    private UserAccount ToAccount(UserRow r) => new UserAccount
    {
        Id = r.Id, UserUid = r.UserUid, Username = DecryptUsername(r),
        FullName = _protector.Decrypt(r.EncryptedFullName, Ctx(r.UserUid, "FullName")),
        Email = _protector.Decrypt(r.EncryptedEmail, Ctx(r.UserUid, "Email")),
        Role = r.Role, IsEnabled = r.IsEnabled, FailedAttempts = r.FailedAttempts, LockedUntil = r.LockedUntil,
        MustChangePassword = r.MustChangePassword, CreatedDate = r.CreatedDate, LastLogin = r.LastLogin
    };

    public static string Normalize(string username) => username?.Trim().ToLowerInvariant();
    public static string Ctx(string uid, string field) => FieldProtector.Context("Users", field, uid);
}

/// <summary>Định danh của Controller và khóa đã ghim của máy đích (chống MITM cho E2EE).</summary>
public sealed class HostKeyService
{
    private readonly PinnedHostKeyRepository _repo;
    private readonly SettingsRepository _settings;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;
    private readonly object _gate = new object();

    public HostKeyService(PinnedHostKeyRepository repo, SettingsRepository settings, AuditLogger audit, UserSession session)
    {
        _repo = repo; _settings = settings; _audit = audit; _session = session;
    }

    /// <summary>Khóa Ed25519 của Controller, lưu dạng mã hóa. Tạo lần đầu khi cần, kể cả với tài khoản không có quyền đổi cấu hình.</summary>
    public DeviceIdentity GetControllerIdentity()
    {
        lock (_gate)
        {
            var stored = _settings.Get(SettingKeys.ControllerIdentity);
            if (stored != null) return DeviceIdentity.FromSeed(Convert.FromBase64String(stored));
            var identity = DeviceIdentity.Generate();
            _settings.Set(SettingKeys.ControllerIdentity, Convert.ToBase64String(identity.ExportSeed()), true);
            return identity;
        }
    }

    public byte[] GetPinned(string remoteId) => _repo.Get(remoteId);

    public void Pin(string remoteId, byte[] publicKey)
    {
        _session.Demand(Permission.QuickConnect);
        var fingerprint = DeviceIdentity.Fingerprint(publicKey);
        _repo.Pin(remoteId, publicKey, fingerprint, _session.Username);
        _audit.Write(AuditEvents.HostKeyPinned, _session.Username, remoteId: remoteId, details: "Pinned device key " + fingerprint, severity: AuditSeverity.Warning);
    }

    /// <summary>Bỏ ghim sau khi thiết bị được cài lại thật. Chỉ quản trị viên, vì đây là bước cho phép chấp nhận khóa mới.</summary>
    public void RemovePin(string remoteId)
    {
        _session.Demand(Permission.ManageSecurity);
        _repo.Remove(remoteId);
        _audit.Write(AuditEvents.HostKeyRemoved, _session.Username, remoteId: remoteId, severity: AuditSeverity.Critical);
    }

    public void ReportMismatch(string remoteId, byte[] presentedKey) =>
        _audit.Write(AuditEvents.HostKeyMismatch, _session.Username, false, "Device key differs from the pinned key: " + DeviceIdentity.Fingerprint(presentedKey), remoteId, severity: AuditSeverity.Critical);
}

public sealed class ClientService
{
    private readonly ClientRepository _repo;
    private readonly FieldProtector _protector;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;

    public ClientService(ClientRepository repo, FieldProtector protector, AuditLogger audit, UserSession session)
    {
        _repo = repo; _protector = protector; _audit = audit; _session = session;
    }

    public List<ClientDevice> Query(string search, long? groupId, bool favoritesOnly) => _repo.Query(new ClientQuery
    {
        Search = search, GroupId = groupId, FavoritesOnly = favoritesOnly, UserId = _session.Current?.Id
    });

    public int Count() => _repo.Count();

    public long Add(ClientDevice d, string credential, IEnumerable<string> tags)
    {
        _session.Demand(Permission.ManageClients);
        Normalize(d);
        if (_repo.RemoteIdExists(d.RemoteId)) throw new ValidationException($"Remote ID {RemoteIdFormat.Display(d.RemoteId)} already exists.");
        d.DeviceId = Guid.NewGuid().ToString("N");
        d.CreatedDate = DateTime.UtcNow;
        d.Id = _repo.Insert(d);
        if (!string.IsNullOrEmpty(credential)) _repo.SetEncryptedCredential(d.Id, _protector.Encrypt(credential, Ctx(d.DeviceId)));
        if (tags != null) _repo.SetTags(d.Id, tags);
        _audit.Write(AuditEvents.ClientCreated, _session.Username, remoteId: d.RemoteId, deviceName: d.DeviceName);
        return d.Id;
    }

    public void Update(ClientDevice d, IEnumerable<string> tags)
    {
        _session.Demand(Permission.ManageClients);
        Normalize(d);
        if (_repo.RemoteIdExists(d.RemoteId, d.Id)) throw new ValidationException($"Remote ID {RemoteIdFormat.Display(d.RemoteId)} already exists.");
        _repo.Update(d);
        if (tags != null) _repo.SetTags(d.Id, tags);
        _audit.Write(AuditEvents.ClientUpdated, _session.Username, remoteId: d.RemoteId, deviceName: d.DeviceName);
    }

    public void Delete(long id)
    {
        _session.Demand(Permission.ManageClients);
        var d = _repo.GetById(id);
        if (d == null) return;
        _repo.Delete(id);
        _audit.Write(AuditEvents.ClientDeleted, _session.Username, remoteId: d.RemoteId, deviceName: d.DeviceName, severity: AuditSeverity.Warning);
    }

    public void SetCredential(long id, string credential)
    {
        _session.Demand(Permission.ManageClients);
        var d = _repo.GetById(id) ?? throw new ValidationException("The device does not exist.");
        if (string.IsNullOrEmpty(credential)) throw new ValidationException("The password cannot be empty.");
        _repo.SetEncryptedCredential(id, _protector.Encrypt(credential, Ctx(d.DeviceId)));
        _audit.Write(AuditEvents.CredentialChanged, _session.Username, remoteId: d.RemoteId, deviceName: d.DeviceName, severity: AuditSeverity.Warning);
    }

    public void RevokeCredential(long id)
    {
        _session.Demand(Permission.ManageClients);
        var d = _repo.GetById(id) ?? throw new ValidationException("The device does not exist.");
        _repo.SetEncryptedCredential(id, null);
        _audit.Write(AuditEvents.CredentialRevoked, _session.Username, remoteId: d.RemoteId, deviceName: d.DeviceName, severity: AuditSeverity.Warning);
    }

    /// <summary>Giải mã credential khi thực sự cần kết nối. Có kiểm tra quyền và ghi Audit (không ghi giá trị).</summary>
    public string UseCredential(long id)
    {
        _session.Demand(Permission.UseCredentials);
        var enc = _repo.GetEncryptedCredential(id, out var deviceId);
        if (enc == null) return null;
        var plain = _protector.Decrypt(enc, Ctx(deviceId));
        var d = _repo.GetById(id);
        _audit.Write(AuditEvents.CredentialUsed, _session.Username, remoteId: d?.RemoteId, deviceName: d?.DeviceName);
        return plain;
    }

    public void SetFavorite(long id, bool favorite)
    {
        var me = _session.Current ?? throw new UnauthorizedAccessException("Not signed in.");
        _repo.SetFavorite(id, me.Id, favorite);
    }

    public static string Ctx(string deviceId) => FieldProtector.Context("Clients", "EncryptedCredential", deviceId);

    internal static void Normalize(ClientDevice d)
    {
        d.RemoteId = RemoteIdFormat.Normalize(d.RemoteId) ?? throw new ValidationException("The Remote ID must be 9 to 12 digits.");
        d.DeviceName = d.DeviceName?.Trim();
        if (string.IsNullOrEmpty(d.DeviceName)) throw new ValidationException("The device name cannot be empty.");
        if (d.DeviceName.Length > 128) throw new ValidationException("The device name can be at most 128 characters.");
    }
}

public sealed class GroupService
{
    private readonly GroupRepository _repo;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;

    public GroupService(GroupRepository repo, AuditLogger audit, UserSession session) { _repo = repo; _audit = audit; _session = session; }

    public List<DeviceGroup> GetAll() => _repo.GetAll();

    public long Add(string name, string description)
    {
        _session.Demand(Permission.ManageClients);
        name = Check(name, null);
        var id = _repo.Insert(name, description);
        _audit.Write(AuditEvents.GroupChanged, _session.Username, details: $"Created group {name}");
        return id;
    }

    public void Update(long id, string name, string description)
    {
        _session.Demand(Permission.ManageClients);
        name = Check(name, id);
        _repo.Update(id, name, description);
        _audit.Write(AuditEvents.GroupChanged, _session.Username, details: $"Edited group {name}");
    }

    public void Delete(long id, string nameForAudit)
    {
        _session.Demand(Permission.ManageClients);
        _repo.Delete(id);
        _audit.Write(AuditEvents.GroupChanged, _session.Username, details: $"Deleted group {nameForAudit}", severity: AuditSeverity.Warning);
    }

    private string Check(string name, long? exceptId)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new ValidationException("The group name cannot be empty.");
        if (_repo.NameExists(name, exceptId)) throw new ValidationException("The group name already exists.");
        return name;
    }
}

public static class SettingKeys
{
    public const string Theme = "ui.theme";
    public const string RelayHost = "relay.host";
    public const string RelayPort = "relay.port";
    public const string RelayToken = "relay.token";
    public const string RelayPin = "relay.pin";
    public const string AutoRegister = "relay.autoRegister";
    public const string ControllerIdentity = "session.identitySeed";
    public const string StrictPinning = "security.strictPinning";
    public const string AuditRetentionDays = "audit.retentionDays";
}

public sealed class SettingsService
{
    private readonly SettingsRepository _repo;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;

    public SettingsService(SettingsRepository repo, AuditLogger audit, UserSession session) { _repo = repo; _audit = audit; _session = session; }

    public string Get(string key, string fallback = null) => _repo.Get(key, fallback);

    /// <summary>Tùy chọn cá nhân, không đòi quyền quản trị.</summary>
    public void SetTheme(string theme) => _repo.Set(SettingKeys.Theme, theme);

    public void Set(string key, string value, bool encrypted = false)
    {
        _session.Demand(Permission.ManageSettings);
        var old = encrypted ? null : _repo.Get(key);
        if (!encrypted && old == value) return;
        _repo.Set(key, value, encrypted);
        // Giá trị mã hóa chỉ ghi tên khóa, không ghi nội dung.
        _audit.Write(AuditEvents.SettingChanged, _session.Username, details: encrypted ? $"{key} changed" : $"{key}: {old} -> {value}");
    }
}
