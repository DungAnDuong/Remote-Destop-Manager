using System;
using System.IO;
using RDM.Data.Repositories;
using RDM.Data.Services;
using RDM.Shared.Models;
using RDM.Shared.Security;

namespace RDM.Data;

/// <summary>Nơi duy nhất lắp ráp các dịch vụ (composition root) cho Controller và kiểm thử.</summary>
public sealed class RdmRuntime : IDisposable
{
    public SQLiteContext Context { get; }
    public KeyManager Keys { get; }
    public FieldProtector Protector { get; }
    public AuditLogger Audit { get; }
    public UserSession Session { get; }
    public UserService Users { get; }
    public ClientService Clients { get; }
    public GroupService Groups { get; }
    public SettingsService Settings { get; }
    public ClientTransferService Transfer { get; }
    public KeyRotationService KeyRotation { get; }
    public BackupManager Backup { get; }
    public ConnectionHistoryRepository History { get; }
    public AuditLogRepository AuditLogs { get; }
    public PresenceService Presence { get; }
    public HostKeyService HostKeys { get; }

    public RdmRuntime(string databasePath, int passwordIterations = PasswordHasher.DefaultIterations, IKeyProtector protector = null)
    {
        Context = DatabaseInitializer.Initialize(databasePath);
        Keys = new KeyManager(new SecurityKeyRepository(Context), protector ?? new DpapiKeyProtector());
        Protector = new FieldProtector(Keys);
        AuditLogs = new AuditLogRepository(Context);
        Audit = new AuditLogger(AuditLogs);
        Session = new UserSession(Audit);
        History = new ConnectionHistoryRepository(Context);
        var clients = new ClientRepository(Context);
        var groups = new GroupRepository(Context);
        Users = new UserService(new UserRepository(Context), Protector, new PasswordHasher(passwordIterations), Audit, Session);
        Clients = new ClientService(clients, Protector, Audit, Session);
        Groups = new GroupService(groups, Audit, Session);
        var settings = new SettingsRepository(Context, Protector);
        Settings = new SettingsService(settings, Audit, Session);
        HostKeys = new HostKeyService(new PinnedHostKeyRepository(Context), settings, Audit, Session);
        Presence = new PresenceService(Settings, clients, Audit);
        Transfer = new ClientTransferService(Context, clients, groups, Audit, Session);
        KeyRotation = new KeyRotationService(Context, Keys, Protector, Audit, Session);
        Backup = new BackupManager(Context, Keys, Audit, Session, Math.Max(passwordIterations, 100_000));
    }

    public static string DefaultDatabasePath() => Path.Combine(SQLiteContext.DefaultDataDirectory(), SQLiteContext.DatabaseFileName);

    /// <summary>Xóa Audit Log quá hạn theo cấu hình; 0 hoặc giá trị không hợp lệ nghĩa là giữ vô thời hạn.</summary>
    public int PurgeAudit()
    {
        return int.TryParse(Settings.Get(SettingKeys.AuditRetentionDays, "365"), out var days) && days > 0
            ? AuditLogs.PurgeOlderThan(DateTime.UtcNow.AddDays(-days))
            : 0;
    }

    public DashboardStats GetDashboardStats()
    {
        var stats = new DashboardStats
        {
            TotalDevices = Clients.Count(),
            TotalConnections = History.Count(),
            FailedLogins24h = AuditLogs.CountSince(AuditEvents.LoginFailed, DateTime.UtcNow.AddDays(-1))
        };
        foreach (var k in Keys.ListKeys()) if (k.Status == KeyStatus.Active) stats.ActiveKeys++;
        using var c = Context.Open();
        stats.SchemaVersion = DatabaseMigration.CurrentVersion(c);
        return stats;
    }

    public void Dispose()
    {
        Presence.Dispose();
        Keys.Dispose();
    }
}
