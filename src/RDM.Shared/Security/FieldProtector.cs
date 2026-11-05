using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RDM.Shared.Security;

/// <summary>
/// Mã hóa từng trường trong Database bằng AES-256-GCM.
/// Định dạng: v1.{phiên bản khóa}.{base64}. AAD là ngữ cảnh "bảng|cột|khóa dòng",
/// nên chép bản mã từ dòng này sang dòng khác sẽ bị phát hiện khi giải mã.
/// </summary>
public sealed class FieldProtector
{
    private const string Format = "v1";
    private readonly KeyManager _keys;

    public FieldProtector(KeyManager keys) => _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    public static string Context(string table, string column, string rowKey) => $"{table}|{column}|{rowKey}";

    public string Encrypt(string plaintext, string context)
    {
        if (plaintext == null) return null;
        var key = _keys.GetCurrentKey(KeyPurpose.Database, out var version);
        try { return EncryptWith(key, version, plaintext, context); }
        finally { KeyManager.Destroy(key); }
    }

    public static string EncryptWith(byte[] key, int version, string plaintext, string context)
    {
        if (plaintext == null) return null;
        var data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var sealedData = AesGcmCipher.Seal(key, data, Aad(version, context));
            return $"{Format}.{version.ToString(CultureInfo.InvariantCulture)}.{Convert.ToBase64String(sealedData)}";
        }
        finally { Array.Clear(data, 0, data.Length); }
    }

    public string Decrypt(string stored, string context)
    {
        if (stored == null) return null;
        var parts = stored.Split('.');
        if (parts.Length != 3 || parts[0] != Format || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new CryptoFailureException("Invalid encrypted field format.");
        byte[] sealedData;
        try { sealedData = Convert.FromBase64String(parts[2]); }
        catch (FormatException ex) { throw new CryptoFailureException("Invalid encrypted field format.", ex); }
        var key = _keys.GetKey(KeyPurpose.Database, version);
        try
        {
            var data = AesGcmCipher.Open(key, sealedData, Aad(version, context));
            try { return Encoding.UTF8.GetString(data); }
            finally { Array.Clear(data, 0, data.Length); }
        }
        finally { KeyManager.Destroy(key); }
    }

    /// <summary>HMAC-SHA256 để tra cứu giá trị (ví dụ tên đăng nhập) mà không lưu dạng rõ.</summary>
    public string LookupHash(string value)
    {
        var key = _keys.GetCurrentKey(KeyPurpose.Authentication, out _);
        try { return LookupHashWith(key, value); }
        finally { KeyManager.Destroy(key); }
    }

    public static string LookupHashWith(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static byte[] Aad(int version, string context) => Encoding.UTF8.GetBytes($"{Format}|{version}|{context}");
}
