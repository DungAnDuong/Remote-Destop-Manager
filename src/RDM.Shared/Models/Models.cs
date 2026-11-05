using System;

namespace RDM.Shared.Models;

public enum UserRole { Viewer = 0, Operator = 1, Administrator = 2 }

public enum Permission { ViewData, QuickConnect, ManageClients, UseCredentials, ManageUsers, ManageSecurity, ManageSettings, ViewAudit }

public enum AuditSeverity { Info = 0, Warning = 1, Critical = 2 }

/// <summary>Thông tin một thiết bị. Không chứa credential: credential chỉ đọc qua ClientService khi cần.</summary>
public sealed class ClientDevice
{
    public long Id { get; set; }
    public string DeviceId { get; set; }
    public string RemoteId { get; set; }
    public string DeviceName { get; set; }
    public string Description { get; set; }
    public long? GroupId { get; set; }
    public string GroupName { get; set; }
    public string IpAddress { get; set; }
    public string MacAddress { get; set; }
    public string OperatingSystem { get; set; }
    public string Architecture { get; set; }
    public DateTime? LastOnline { get; set; }
    public DateTime CreatedDate { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool HasCredential { get; set; }
    public string Tags { get; set; }
}

public sealed class ClientQuery
{
    public string Search { get; set; }
    public long? GroupId { get; set; }
    public bool FavoritesOnly { get; set; }
    public long? UserId { get; set; }
    public int Limit { get; set; } = 100000;
}

public sealed class DeviceGroup
{
    public long Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public DateTime CreatedDate { get; set; }
    public int DeviceCount { get; set; }
}

public sealed class ConnectionRecord
{
    public long Id { get; set; }
    public string RemoteId { get; set; }
    public string DeviceName { get; set; }
    public string ControllerUser { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public long? DurationSeconds { get; set; }
    public string ConnectionType { get; set; }
    public string ConnectionStatus { get; set; }
}

/// <summary>Số liệu của một ngày cho biểu đồ Dashboard.</summary>
public sealed class DailyStat
{
    public DateTime Day { get; set; }
    public int Connections { get; set; }
    public long DurationSeconds { get; set; }
}

public sealed class UserAccount
{
    public long Id { get; set; }
    public string UserUid { get; set; }
    public string Username { get; set; }
    public string FullName { get; set; }
    public string Email { get; set; }
    public UserRole Role { get; set; }
    public bool IsEnabled { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? LastLogin { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; }
    public AuditSeverity Severity { get; set; }
    public string Username { get; set; }
    public string RemoteId { get; set; }
    public string DeviceName { get; set; }
    public string Details { get; set; }
    public bool Success { get; set; }
}

/// <summary>Tên sự kiện Audit (Phần 8.8).</summary>
public static class AuditEvents
{
    public const string LoginSuccess = "Login.Success";
    public const string LoginFailed = "Login.Failed";
    public const string Logout = "Logout";
    public const string UserCreated = "User.Created";
    public const string UserUpdated = "User.Updated";
    public const string UserDeleted = "User.Deleted";
    public const string PasswordChanged = "User.PasswordChanged";
    public const string RoleChanged = "User.RoleChanged";
    public const string AccessDenied = "Access.Denied";
    public const string ClientCreated = "Client.Created";
    public const string ClientUpdated = "Client.Updated";
    public const string ClientDeleted = "Client.Deleted";
    public const string CredentialChanged = "Client.CredentialChanged";
    public const string CredentialRevoked = "Client.CredentialRevoked";
    public const string CredentialUsed = "Client.CredentialUsed";
    public const string ClientsImported = "Client.Imported";
    public const string ClientsExported = "Client.Exported";
    public const string GroupChanged = "Group.Changed";
    public const string SettingChanged = "Setting.Changed";
    public const string KeyRotated = "Key.Rotated";
    public const string KeyRevoked = "Key.Revoked";
    public const string BackupCreated = "Backup.Created";
    public const string BackupRestored = "Backup.Restored";
    public const string BackupFailed = "Backup.Failed";
    public const string SessionStarted = "Session.Started";
    public const string SessionEnded = "Session.Ended";
    public const string SessionFailed = "Session.Failed";
    public const string HostKeyPinned = "HostKey.Pinned";
    public const string HostKeyMismatch = "HostKey.Mismatch";
    public const string HostKeyRemoved = "HostKey.Removed";
}

public sealed class DashboardStats
{
    public int TotalDevices { get; set; }
    public int TotalConnections { get; set; }
    public int FailedLogins24h { get; set; }
    public int ActiveKeys { get; set; }
    public int SchemaVersion { get; set; }
}
