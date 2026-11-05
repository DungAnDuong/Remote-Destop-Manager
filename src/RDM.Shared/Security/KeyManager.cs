using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RDM.Shared.Security;

/// <summary>Mỗi mục đích một khóa riêng; không dùng lại khóa cho mục đích khác (Phần 8.4).</summary>
public enum KeyPurpose { Database = 1, Authentication = 2, Session = 3, FileTransfer = 4 }

public enum KeyStatus { Active = 0, Retired = 1, Revoked = 2 }

/// <summary>Bản ghi khóa lưu trữ. WrappedKey đã được bọc, không bao giờ là khóa rõ.</summary>
public sealed class KeyRecord
{
    public KeyPurpose Purpose { get; set; }
    public int Version { get; set; }
    public KeyStatus Status { get; set; }
    public byte[] WrappedKey { get; set; }
    public string ProtectionScheme { get; set; }
    public string Fingerprint { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? RotatedDate { get; set; }
    public DateTime? RevokedDate { get; set; }
}

public interface IKeyStore
{
    IReadOnlyList<KeyRecord> LoadAll();
    void Insert(KeyRecord record);
    void UpdateStatus(KeyPurpose purpose, int version, KeyStatus status, DateTime when);
}

public interface IKeyProtector
{
    string Scheme { get; }
    byte[] Protect(byte[] key, KeyPurpose purpose, int version);
    byte[] Unprotect(byte[] wrapped, KeyPurpose purpose, int version);
}

/// <summary>
/// Bọc khóa bằng Windows DPAPI (phạm vi CurrentUser). Entropy gắn với mục đích và phiên bản,
/// nên không thể tráo blob khóa giữa các bản ghi.
/// </summary>
public sealed class DpapiKeyProtector : IKeyProtector
{
    public string Scheme => "DPAPI-CurrentUser-v1";

    public byte[] Protect(byte[] key, KeyPurpose purpose, int version) =>
        ProtectedData.Protect(key, Entropy(purpose, version), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] wrapped, KeyPurpose purpose, int version)
    {
        try { return ProtectedData.Unprotect(wrapped, Entropy(purpose, version), DataProtectionScope.CurrentUser); }
        catch (CryptographicException ex)
        {
            throw new CryptoFailureException("Could not open the key: a different Windows account or computer, or the key was modified.", ex);
        }
    }

    private static byte[] Entropy(KeyPurpose purpose, int version) => Encoding.UTF8.GetBytes($"RDM|key|{purpose}|{version}");
}

/// <summary>
/// Cửa duy nhất để tiếp cận khóa (RDM.Shared.Security.KeyManager, Phần 8.4).
/// Khóa Session và File chỉ tồn tại trong RAM. Khóa Database và Authentication được bọc rồi mới lưu.
/// Không có hàm nào ở lớp này ghi khóa ra Log.
/// </summary>
public sealed class KeyManager : IDisposable
{
    private readonly IKeyStore _store;
    private readonly IKeyProtector _protector;
    private readonly object _gate = new object();
    private readonly Dictionary<(KeyPurpose, int), byte[]> _cache = new Dictionary<(KeyPurpose, int), byte[]>();

    public KeyManager(IKeyStore store, IKeyProtector protector)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public IKeyProtector Protector => _protector;

    /// <summary>Lấy khóa đang hoạt động, tạo mới nếu chưa có. Trả về bản sao; bên gọi có thể tự xóa.</summary>
    public byte[] GetCurrentKey(KeyPurpose purpose, out int version)
    {
        EnsurePersistent(purpose);
        lock (_gate)
        {
            var active = _store.LoadAll().FirstOrDefault(k => k.Purpose == purpose && k.Status == KeyStatus.Active);
            if (active == null)
            {
                var raw = NewKey();
                var record = BuildRecord(purpose, NextVersion(purpose), raw);
                _store.Insert(record);
                _cache[(purpose, record.Version)] = raw;
                version = record.Version;
                return (byte[])raw.Clone();
            }
            version = active.Version;
            return (byte[])Unwrap(active).Clone();
        }
    }

    /// <summary>Lấy khóa theo phiên bản để giải mã dữ liệu cũ. Khóa đã thu hồi luôn bị từ chối.</summary>
    public byte[] GetKey(KeyPurpose purpose, int version)
    {
        EnsurePersistent(purpose);
        lock (_gate)
        {
            var record = _store.LoadAll().FirstOrDefault(k => k.Purpose == purpose && k.Version == version)
                ?? throw new CryptoFailureException($"{purpose} key version {version} was not found.");
            if (record.Status == KeyStatus.Revoked)
                throw new CryptoFailureException($"{purpose} key version {version} has been revoked.");
            return (byte[])Unwrap(record).Clone();
        }
    }

    /// <summary>Tạo sẵn khóa mới cho xoay khóa, chưa lưu. Bên gọi lưu record trong cùng transaction với dữ liệu mã hóa lại.</summary>
    public KeyRecord PrepareRotation(KeyPurpose purpose, out byte[] rawKey)
    {
        EnsurePersistent(purpose);
        lock (_gate)
        {
            rawKey = NewKey();
            return BuildRecord(purpose, NextVersion(purpose), rawKey);
        }
    }

    /// <summary>Thu hồi một phiên bản khóa. Không cho thu hồi khóa đang hoạt động: phải xoay khóa trước.</summary>
    public void Revoke(KeyPurpose purpose, int version)
    {
        lock (_gate)
        {
            var record = _store.LoadAll().FirstOrDefault(k => k.Purpose == purpose && k.Version == version)
                ?? throw new CryptoFailureException("The key was not found.");
            if (record.Status == KeyStatus.Active)
                throw new InvalidOperationException("An active key cannot be revoked. Rotate the key first.");
            _store.UpdateStatus(purpose, version, KeyStatus.Revoked, DateTime.UtcNow);
            if (_cache.TryGetValue((purpose, version), out var cached)) { Destroy(cached); _cache.Remove((purpose, version)); }
        }
    }

    /// <summary>Danh sách metadata khóa, không bao gồm blob khóa, để hiển thị.</summary>
    public IReadOnlyList<KeyRecord> ListKeys() => _store.LoadAll().Select(k => new KeyRecord
    {
        Purpose = k.Purpose, Version = k.Version, Status = k.Status, Fingerprint = k.Fingerprint,
        ProtectionScheme = k.ProtectionScheme, CreatedDate = k.CreatedDate, RotatedDate = k.RotatedDate, RevokedDate = k.RevokedDate
    }).ToList();

    /// <summary>Khóa dùng một lần cho Session hoặc File: chỉ trong RAM, bên gọi hủy bằng Destroy.</summary>
    public static byte[] CreateEphemeralKey() => NewKey();

    public static void Destroy(byte[] key) { if (key != null) Array.Clear(key, 0, key.Length); }

    public static string ComputeFingerprint(byte[] key)
    {
        using var sha = SHA256.Create();
        var prefix = Encoding.UTF8.GetBytes("RDM|fingerprint|");
        var data = new byte[prefix.Length + key.Length];
        Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
        Buffer.BlockCopy(key, 0, data, prefix.Length, key.Length);
        var hash = sha.ComputeHash(data);
        Array.Clear(data, 0, data.Length);
        return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>Xóa khóa khỏi cache, ví dụ sau Restore hoặc khi đăng xuất.</summary>
    public void ClearCache()
    {
        lock (_gate)
        {
            foreach (var k in _cache.Values) Destroy(k);
            _cache.Clear();
        }
    }

    public void Dispose() => ClearCache();

    private KeyRecord BuildRecord(KeyPurpose purpose, int version, byte[] raw) => new KeyRecord
    {
        Purpose = purpose, Version = version, Status = KeyStatus.Active,
        WrappedKey = _protector.Protect(raw, purpose, version),
        ProtectionScheme = _protector.Scheme,
        Fingerprint = ComputeFingerprint(raw),
        CreatedDate = DateTime.UtcNow
    };

    private byte[] Unwrap(KeyRecord record)
    {
        if (_cache.TryGetValue((record.Purpose, record.Version), out var cached)) return cached;
        var raw = _protector.Unprotect(record.WrappedKey, record.Purpose, record.Version);
        _cache[(record.Purpose, record.Version)] = raw;
        return raw;
    }

    private int NextVersion(KeyPurpose purpose)
    {
        var versions = _store.LoadAll().Where(k => k.Purpose == purpose).Select(k => k.Version).ToList();
        return versions.Count == 0 ? 1 : versions.Max() + 1;
    }

    private static byte[] NewKey() => CryptoRandom.GetBytes(AesGcmCipher.KeySize);

    private static void EnsurePersistent(KeyPurpose purpose)
    {
        if (purpose != KeyPurpose.Database && purpose != KeyPurpose.Authentication)
            throw new ArgumentException("Session and File keys exist only in memory and must not be stored.", nameof(purpose));
    }
}
