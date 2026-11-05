using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using RDM.Data.Services;
using RDM.Shared.Common;
using RDM.Shared.Models;
using RDM.Shared.Security;

namespace RDM.Data;

/// <summary>
/// Sao lưu và phục hồi có mật khẩu. Khóa bọc DPAPI không dùng được trên máy khác, nên khóa dữ liệu
/// được mang theo trong phần đã mã hóa bằng khóa sinh từ mật khẩu backup, rồi bọc lại DPAPI khi Restore.
/// Định dạng: "RDMBAK1\0" | số vòng (int32) | salt (16) | AES-256-GCM(payload), header là AAD.
/// </summary>
public sealed class BackupManager
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RDMBAK1\0");
    private const int SaltSize = 16;
    private readonly SQLiteContext _ctx;
    private readonly KeyManager _keys;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;
    private readonly int _iterations;

    public BackupManager(SQLiteContext ctx, KeyManager keys, AuditLogger audit, UserSession session, int iterations = PasswordHasher.DefaultIterations)
    {
        _ctx = ctx; _keys = keys; _audit = audit; _session = session; _iterations = iterations;
    }

    public void Backup(string targetPath, string password)
    {
        _session.Demand(Permission.ManageSecurity);
        var problem = PasswordPolicy.Validate(password);
        if (problem != null) throw new ValidationException(problem);

        var tempDb = TempPath();
        byte[] payload = null, derived = null;
        try
        {
            using (var src = _ctx.Open())
            using (var dst = new SQLiteConnection(ConnectionTo(tempDb)))
            {
                dst.Open();
                src.BackupDatabase(dst, "main", "main", -1, null, 0);
            }
            SQLiteConnection.ClearAllPools();

            payload = BuildPayload(File.ReadAllBytes(tempDb));
            var salt = CryptoRandom.GetBytes(SaltSize);
            var header = BuildHeader(_iterations, salt);
            derived = PasswordHasher.Derive(password, salt, _iterations, AesGcmCipher.KeySize);
            var sealedData = AesGcmCipher.Seal(derived, payload, header);

            // Ghi file tạm rồi đổi tên, tránh để lại file backup hỏng khi đang ghi.
            var part = targetPath + ".part";
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(header, 0, header.Length);
                fs.Write(sealedData, 0, sealedData.Length);
            }
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(part, targetPath);
            _audit.Write(AuditEvents.BackupCreated, _session.Username, details: $"Backup to {Path.GetFileName(targetPath)}", severity: AuditSeverity.Warning);
        }
        catch (Exception ex) when (!(ex is ValidationException))
        {
            _audit.Write(AuditEvents.BackupFailed, _session.Username, false, ex.GetType().Name, severity: AuditSeverity.Warning);
            throw;
        }
        finally
        {
            Wipe(payload); Wipe(derived);
            DeleteQuietly(tempDb);
        }
    }

    /// <summary>
    /// Phục hồi. Trình tự: xác thực GCM → kiểm tra tính toàn vẹn SQLite → bọc lại khóa → sao lưu DB hiện tại → thay thế.
    /// Sai mật khẩu hoặc file bị sửa thì DB hiện tại không bị đụng tới. Sau khi xong phải khởi động lại ứng dụng.
    /// </summary>
    public void Restore(string sourcePath, string password)
    {
        _session.Demand(Permission.ManageSecurity);
        var username = _session.Username;
        var tempDb = TempPath();
        byte[] payload = null;
        Dictionary<(KeyPurpose, int), byte[]> keys = null;
        try
        {
            payload = OpenFile(sourcePath, password);
            keys = ParsePayload(payload, out var dbBytes);
            File.WriteAllBytes(tempDb, dbBytes);

            using (var c = new SQLiteConnection(ConnectionTo(tempDb)))
            {
                c.Open();
                using (var cmd = new SQLiteCommand("PRAGMA integrity_check", c))
                    if (!string.Equals(cmd.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The backup file contains a corrupted database.");
                if (DatabaseMigration.CurrentVersion(c) > DatabaseMigration.LatestVersion)
                    throw new InvalidDataException("The backup file was created by a newer version of RDM.");

                foreach (var pair in keys)
                    c.Exec("UPDATE SecurityKeys SET WrappedKey=@w, ProtectionScheme=@s WHERE Purpose=@p AND Version=@v", null,
                        ("@w", _keys.Protector.Protect(pair.Value, pair.Key.Item1, pair.Key.Item2)), ("@s", _keys.Protector.Scheme),
                        ("@p", pair.Key.Item1), ("@v", pair.Key.Item2));
            }
            SQLiteConnection.ClearAllPools();

            var safety = _ctx.DatabasePath + ".before-restore.bak";
            using (var src = _ctx.Open())
            using (var dst = new SQLiteConnection(ConnectionTo(safety)))
            {
                dst.Open();
                src.BackupDatabase(dst, "main", "main", -1, null, 0);
            }
            SQLiteConnection.ClearAllPools();
            _keys.ClearCache();

            DeleteQuietly(_ctx.DatabasePath + "-wal");
            DeleteQuietly(_ctx.DatabasePath + "-shm");
            File.Copy(tempDb, _ctx.DatabasePath, true);
            DatabaseMigration.Migrate(_ctx);
            _audit.Write(AuditEvents.BackupRestored, username, details: $"Restored from {Path.GetFileName(sourcePath)}", severity: AuditSeverity.Critical);
        }
        catch (Exception ex)
        {
            _audit.Write(AuditEvents.BackupFailed, username, false, "Restore failed: " + ex.GetType().Name, severity: AuditSeverity.Critical);
            throw;
        }
        finally
        {
            if (keys != null) foreach (var k in keys.Values) Wipe(k);
            Wipe(payload);
            DeleteQuietly(tempDb);
        }
    }

    private static byte[] OpenFile(string path, string password)
    {
        var file = File.ReadAllBytes(path);
        var headerSize = Magic.Length + sizeof(int) + SaltSize;
        if (file.Length <= headerSize || !Magic.SequenceEqual(file.Take(Magic.Length)))
            throw new InvalidDataException("This is not a valid RDM backup file.");
        var iterations = BitConverter.ToInt32(file, Magic.Length);
        // Giới hạn trên chặn file giả mạo khiến PBKDF2 chạy gần như vô tận.
        if (iterations < 100_000 || iterations > 50_000_000)
            throw new InvalidDataException("Invalid backup file parameters.");
        var salt = new byte[SaltSize];
        Buffer.BlockCopy(file, Magic.Length + sizeof(int), salt, 0, SaltSize);
        var header = new byte[headerSize];
        Buffer.BlockCopy(file, 0, header, 0, headerSize);
        var body = new byte[file.Length - headerSize];
        Buffer.BlockCopy(file, headerSize, body, 0, body.Length);
        var derived = PasswordHasher.Derive(password, salt, iterations, AesGcmCipher.KeySize);
        try { return AesGcmCipher.Open(derived, body, header); }
        finally { Wipe(derived); }
    }

    private static byte[] BuildHeader(int iterations, byte[] salt)
    {
        var header = new byte[Magic.Length + sizeof(int) + salt.Length];
        Buffer.BlockCopy(Magic, 0, header, 0, Magic.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(iterations), 0, header, Magic.Length, sizeof(int));
        Buffer.BlockCopy(salt, 0, header, Magic.Length + sizeof(int), salt.Length);
        return header;
    }

    private byte[] BuildPayload(byte[] db)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var records = _keys.ListKeys().Where(k => k.Status != KeyStatus.Revoked).ToList();
        w.Write(records.Count);
        foreach (var k in records)
        {
            var raw = _keys.GetKey(k.Purpose, k.Version);
            w.Write((int)k.Purpose); w.Write(k.Version); w.Write(raw.Length); w.Write(raw);
            KeyManager.Destroy(raw);
        }
        w.Write(db.Length);
        w.Write(db);
        w.Flush();
        return ms.ToArray();
    }

    private static Dictionary<(KeyPurpose, int), byte[]> ParsePayload(byte[] payload, out byte[] db)
    {
        using var r = new BinaryReader(new MemoryStream(payload));
        var keys = new Dictionary<(KeyPurpose, int), byte[]>();
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var purpose = (KeyPurpose)r.ReadInt32();
            var version = r.ReadInt32();
            keys[(purpose, version)] = r.ReadBytes(r.ReadInt32());
        }
        db = r.ReadBytes(r.ReadInt32());
        return keys;
    }

    private static string ConnectionTo(string path) => new SQLiteConnectionStringBuilder { DataSource = path, Version = 3, Pooling = false }.ToString();
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "rdm-" + Guid.NewGuid().ToString("N") + ".tmp");
    private static void Wipe(byte[] b) { if (b != null) Array.Clear(b, 0, b.Length); }
    private static void DeleteQuietly(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
