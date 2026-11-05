using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using NLog;

namespace RDM.Data;

/// <summary>Quản lý phiên bản Schema. Mỗi migration chạy trong một transaction.</summary>
public static class DatabaseMigration
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private sealed class Step
    {
        public int Version; public string Description; public string Sql;
    }

    private static readonly Step[] Steps =
    {
        new Step { Version = 1, Description = "Initial schema", Sql = @"
CREATE TABLE Groups(
  ID INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
  Description TEXT,
  ParentID INTEGER REFERENCES Groups(ID) ON DELETE SET NULL,
  CreatedDate TEXT NOT NULL);

CREATE TABLE Clients(
  ID INTEGER PRIMARY KEY AUTOINCREMENT,
  DeviceID TEXT NOT NULL UNIQUE,
  RemoteID TEXT NOT NULL UNIQUE,
  DeviceName TEXT NOT NULL,
  Description TEXT,
  GroupID INTEGER REFERENCES Groups(ID) ON DELETE SET NULL,
  EncryptedCredential TEXT,
  IPAddress TEXT,
  MacAddress TEXT,
  OperatingSystem TEXT,
  Architecture TEXT,
  LastOnline TEXT,
  CreatedDate TEXT NOT NULL,
  IsFavorite INTEGER NOT NULL DEFAULT 0,
  IsEnabled INTEGER NOT NULL DEFAULT 1);
CREATE INDEX IX_Clients_GroupID ON Clients(GroupID);
CREATE INDEX IX_Clients_DeviceName ON Clients(DeviceName);
CREATE INDEX IX_Clients_LastOnline ON Clients(LastOnline);

CREATE TABLE Tags(ID INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL COLLATE NOCASE UNIQUE);
CREATE TABLE ClientTags(
  ClientID INTEGER NOT NULL REFERENCES Clients(ID) ON DELETE CASCADE,
  TagID INTEGER NOT NULL REFERENCES Tags(ID) ON DELETE CASCADE,
  PRIMARY KEY(ClientID, TagID));

CREATE TABLE ConnectionHistory(
  ID INTEGER PRIMARY KEY AUTOINCREMENT,
  RemoteID TEXT NOT NULL,
  DeviceName TEXT,
  ControllerUser TEXT,
  StartTime TEXT NOT NULL,
  EndTime TEXT,
  Duration INTEGER,
  ConnectionType TEXT,
  ConnectionStatus TEXT);
CREATE INDEX IX_History_StartTime ON ConnectionHistory(StartTime);
CREATE INDEX IX_History_RemoteID ON ConnectionHistory(RemoteID);

CREATE TABLE Users(
  ID INTEGER PRIMARY KEY AUTOINCREMENT,
  UserUid TEXT NOT NULL UNIQUE,
  UsernameHash TEXT NOT NULL UNIQUE,
  EncryptedUsername TEXT NOT NULL,
  PasswordHash TEXT NOT NULL,
  EncryptedFullName TEXT,
  EncryptedEmail TEXT,
  Role INTEGER NOT NULL,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  FailedAttempts INTEGER NOT NULL DEFAULT 0,
  LockedUntil TEXT,
  MustChangePassword INTEGER NOT NULL DEFAULT 0,
  CreatedDate TEXT NOT NULL,
  LastLogin TEXT);

CREATE TABLE Settings(
  Key TEXT PRIMARY KEY,
  Value TEXT,
  IsEncrypted INTEGER NOT NULL DEFAULT 0,
  UpdatedDate TEXT NOT NULL);

CREATE TABLE Favorites(
  UserID INTEGER NOT NULL REFERENCES Users(ID) ON DELETE CASCADE,
  ClientID INTEGER NOT NULL REFERENCES Clients(ID) ON DELETE CASCADE,
  CreatedDate TEXT NOT NULL,
  PRIMARY KEY(UserID, ClientID));

CREATE TABLE AuditLogs(
  ID INTEGER PRIMARY KEY AUTOINCREMENT,
  Timestamp TEXT NOT NULL,
  EventType TEXT NOT NULL,
  Severity INTEGER NOT NULL DEFAULT 0,
  Username TEXT,
  RemoteID TEXT,
  DeviceName TEXT,
  Details TEXT,
  Success INTEGER NOT NULL DEFAULT 1);
CREATE INDEX IX_Audit_Timestamp ON AuditLogs(Timestamp);
CREATE INDEX IX_Audit_EventType ON AuditLogs(EventType);

CREATE TABLE SecurityKeys(
  ID INTEGER PRIMARY KEY AUTOINCREMENT,
  Purpose INTEGER NOT NULL,
  Version INTEGER NOT NULL,
  Status INTEGER NOT NULL,
  WrappedKey BLOB NOT NULL,
  ProtectionScheme TEXT NOT NULL,
  Fingerprint TEXT NOT NULL,
  CreatedDate TEXT NOT NULL,
  RotatedDate TEXT,
  RevokedDate TEXT,
  UNIQUE(Purpose, Version));
CREATE INDEX IX_Keys_PurposeStatus ON SecurityKeys(Purpose, Status);
" },
        new Step { Version = 2, Description = "Pinned device identity keys", Sql = @"
CREATE TABLE PinnedHostKeys(
  RemoteID TEXT PRIMARY KEY,
  PublicKey BLOB NOT NULL,
  Fingerprint TEXT NOT NULL,
  PinnedBy TEXT,
  PinnedDate TEXT NOT NULL);
" }
    };

    public static int LatestVersion => Steps.Max(s => s.Version);

    public static int CurrentVersion(SQLiteConnection c) => (int)c.Scalar("PRAGMA user_version");

    public static void Migrate(SQLiteContext context)
    {
        int current;
        using (var c = context.Open()) current = CurrentVersion(c);

        if (current > LatestVersion)
            throw new InvalidOperationException(
                $"The database is version {current}, newer than this application supports ({LatestVersion}). Update RDM. The database was not changed.");
        if (current == LatestVersion) return;

        if (current > 0) BackupBeforeMigration(context, current);

        foreach (var step in Steps.Where(s => s.Version > current).OrderBy(s => s.Version))
        {
            context.InTransaction((c, tx) =>
            {
                c.Exec(step.Sql, tx);
                c.Exec(@"CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER PRIMARY KEY, AppliedDate TEXT NOT NULL, Description TEXT)", tx);
                c.Exec("INSERT INTO SchemaVersion(Version, AppliedDate, Description) VALUES(@v, @d, @t)", tx,
                    ("@v", step.Version), ("@d", DateTime.UtcNow), ("@t", step.Description));
                c.Exec($"PRAGMA user_version = {step.Version}", tx);
            });
            Log.Info("Applied migration {0}: {1}", step.Version, step.Description);
        }
    }

    private static void BackupBeforeMigration(SQLiteContext context, int version)
    {
        var target = Path.ChangeExtension(context.DatabasePath, null) + $".pre-v{version}.bak";
        using var src = context.Open();
        using var dst = new SQLiteConnection($"Data Source={target};Version=3");
        dst.Open();
        src.BackupDatabase(dst, "main", "main", -1, null, 0);
        Log.Info("Backed up the database before upgrading from version {0}.", version);
    }
}

public static class DatabaseInitializer
{
    /// <summary>Tạo thư mục, file Database và Schema nếu chưa có.</summary>
    public static SQLiteContext Initialize(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath)));
        var context = new SQLiteContext(databasePath);
        DatabaseMigration.Migrate(context);
        return context;
    }
}
