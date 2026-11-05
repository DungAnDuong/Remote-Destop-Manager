using System;
using System.Collections.Generic;
using System.Data.SQLite;
using RDM.Data.Repositories;
using RDM.Shared.Models;
using RDM.Shared.Security;

namespace RDM.Data.Services;

/// <summary>
/// Xoay khóa: mã hóa lại toàn bộ dữ liệu bằng khóa mới trong MỘT transaction.
/// Có lỗi giữa chừng thì rollback, dữ liệu và khóa cũ vẫn nguyên vẹn.
/// </summary>
public sealed class KeyRotationService
{
    private readonly SQLiteContext _ctx;
    private readonly KeyManager _keys;
    private readonly FieldProtector _protector;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;

    public KeyRotationService(SQLiteContext ctx, KeyManager keys, FieldProtector protector, AuditLogger audit, UserSession session)
    {
        _ctx = ctx; _keys = keys; _protector = protector; _audit = audit; _session = session;
    }

    public int Rotate(KeyPurpose purpose)
    {
        _session.Demand(Permission.ManageSecurity);
        // Bảo đảm đã có khóa hiện tại trước khi xoay.
        KeyManager.Destroy(_keys.GetCurrentKey(purpose, out var oldVersion));
        var record = _keys.PrepareRotation(purpose, out var newKey);
        try
        {
            _ctx.InTransaction((c, tx) =>
            {
                if (purpose == KeyPurpose.Database) ReEncryptAll(c, tx, newKey, record.Version);
                else if (purpose == KeyPurpose.Authentication) RecomputeLookups(c, tx, newKey);
                else throw new ArgumentException("Only Database and Authentication keys can be rotated.", nameof(purpose));

                c.Exec("UPDATE SecurityKeys SET Status=@s, RotatedDate=@d WHERE Purpose=@p AND Version=@v", tx,
                    ("@s", KeyStatus.Retired), ("@d", DateTime.UtcNow), ("@p", purpose), ("@v", oldVersion));
                SecurityKeyRepository.Insert(c, tx, record);
            });
        }
        finally { KeyManager.Destroy(newKey); }
        _audit.Write(AuditEvents.KeyRotated, _session.Username, details: $"{purpose}: v{oldVersion} -> v{record.Version}", severity: AuditSeverity.Warning);
        return record.Version;
    }

    public void Revoke(KeyPurpose purpose, int version)
    {
        _session.Demand(Permission.ManageSecurity);
        _keys.Revoke(purpose, version);
        _audit.Write(AuditEvents.KeyRevoked, _session.Username, details: $"{purpose}: v{version}", severity: AuditSeverity.Critical);
    }

    private void ReEncryptAll(SQLiteConnection c, SQLiteTransaction tx, byte[] key, int version)
    {
        var updates = new List<Action>();

        using (var cmd = c.Cmd("SELECT ID, DeviceID, EncryptedCredential FROM Clients WHERE EncryptedCredential IS NOT NULL", tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.Long("ID");
                var ctx = ClientService.Ctx(r.Str("DeviceID"));
                var value = Swap(r.Str("EncryptedCredential"), ctx, key, version);
                updates.Add(() => c.Exec("UPDATE Clients SET EncryptedCredential=@v WHERE ID=@id", tx, ("@v", value), ("@id", id)));
            }

        using (var cmd = c.Cmd("SELECT ID, UserUid, EncryptedUsername, PasswordHash, EncryptedFullName, EncryptedEmail FROM Users", tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.Long("ID");
                var uid = r.Str("UserUid");
                var name = Swap(r.Str("EncryptedUsername"), UserService.Ctx(uid, "Username"), key, version);
                var pass = Swap(r.Str("PasswordHash"), UserService.Ctx(uid, "PasswordHash"), key, version);
                var full = Swap(r.Str("EncryptedFullName"), UserService.Ctx(uid, "FullName"), key, version);
                var mail = Swap(r.Str("EncryptedEmail"), UserService.Ctx(uid, "Email"), key, version);
                updates.Add(() => c.Exec("UPDATE Users SET EncryptedUsername=@n, PasswordHash=@p, EncryptedFullName=@f, EncryptedEmail=@e WHERE ID=@id", tx,
                    ("@n", name), ("@p", pass), ("@f", full), ("@e", mail), ("@id", id)));
            }

        using (var cmd = c.Cmd("SELECT Key, Value FROM Settings WHERE IsEncrypted=1", tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var k = r.Str("Key");
                var value = Swap(r.Str("Value"), SettingsRepository.Context(k), key, version);
                updates.Add(() => c.Exec("UPDATE Settings SET Value=@v WHERE Key=@k", tx, ("@v", value), ("@k", k)));
            }

        foreach (var update in updates) update();
    }

    private string Swap(string stored, string context, byte[] key, int version) =>
        stored == null ? null : FieldProtector.EncryptWith(key, version, _protector.Decrypt(stored, context), context);

    private void RecomputeLookups(SQLiteConnection c, SQLiteTransaction tx, byte[] key)
    {
        var updates = new List<(long id, string hash)>();
        using (var cmd = c.Cmd("SELECT ID, UserUid, EncryptedUsername FROM Users", tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var name = _protector.Decrypt(r.Str("EncryptedUsername"), UserService.Ctx(r.Str("UserUid"), "Username"));
                updates.Add((r.Long("ID"), FieldProtector.LookupHashWith(key, name)));
            }
        foreach (var (id, hash) in updates)
            c.Exec("UPDATE Users SET UsernameHash=@h WHERE ID=@id", tx, ("@h", hash), ("@id", id));
    }
}
