using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace RDM.Shared.Security;

/// <summary>Lỗi xác thực hoặc giải mã. Không kèm dữ liệu nhạy cảm trong thông báo.</summary>
public sealed class CryptoFailureException : Exception
{
    public CryptoFailureException(string message, Exception inner = null) : base(message, inner) { }
}

public static class CryptoRandom
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public static byte[] GetBytes(int count)
    {
        var b = new byte[count];
        lock (Rng) Rng.GetBytes(b);
        return b;
    }
}

/// <summary>
/// AES-256-GCM. Dùng BouncyCastle vì .NET Framework 4.8 không có lớp AesGcm.
/// Định dạng đầu ra: nonce (12) | ciphertext | tag (16).
/// Nonce ngẫu nhiên 96 bit: phù hợp dữ liệu lưu trữ. Phiên mạng dùng nonce theo bộ đếm (xem docs/04-Security.md).
/// </summary>
public static class AesGcmCipher
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    private const int TagBits = 128;
    public const int Overhead = NonceSize + TagBits / 8;

    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad = null)
    {
        CheckKey(key);
        if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
        var nonce = CryptoRandom.GetBytes(NonceSize);
        var gcm = Create(true, key, nonce, aad);
        var output = new byte[NonceSize + gcm.GetOutputSize(plaintext.Length)];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        var n = gcm.ProcessBytes(plaintext, 0, plaintext.Length, output, NonceSize);
        gcm.DoFinal(output, NonceSize + n);
        return output;
    }

    public static byte[] Open(byte[] key, byte[] sealedData, byte[] aad = null)
    {
        CheckKey(key);
        if (sealedData == null || sealedData.Length < Overhead)
            throw new CryptoFailureException("Invalid encrypted data.");
        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(sealedData, 0, nonce, 0, NonceSize);
        var gcm = Create(false, key, nonce, aad);
        var inputLength = sealedData.Length - NonceSize;
        var output = new byte[gcm.GetOutputSize(inputLength)];
        try
        {
            var n = gcm.ProcessBytes(sealedData, NonceSize, inputLength, output, 0);
            gcm.DoFinal(output, n);
            return output;
        }
        catch (InvalidCipherTextException ex)
        {
            Array.Clear(output, 0, output.Length);
            throw new CryptoFailureException("Data authentication failed: wrong key, or the data was modified.", ex);
        }
    }

    private static GcmBlockCipher Create(bool encrypt, byte[] key, byte[] nonce, byte[] aad)
    {
        var gcm = new GcmBlockCipher(AesUtilities.CreateEngine());
        gcm.Init(encrypt, new AeadParameters(new KeyParameter(key), TagBits, nonce, aad));
        return gcm;
    }

    private static void CheckKey(byte[] key)
    {
        if (key == null || key.Length != KeySize) throw new ArgumentException("An AES-256 key must be 32 bytes.", nameof(key));
    }
}

/// <summary>PBKDF2-HMAC-SHA256 với salt riêng. Định dạng: pbkdf2-sha256$vòng$salt$hash.</summary>
public sealed class PasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Prefix = "pbkdf2-sha256";
    private readonly int _iterations;

    public PasswordHasher(int iterations = DefaultIterations)
    {
        if (iterations < 1000) throw new ArgumentOutOfRangeException(nameof(iterations));
        _iterations = iterations;
    }

    public string Hash(string password)
    {
        var salt = CryptoRandom.GetBytes(SaltSize);
        var hash = Derive(password, salt, _iterations, HashSize);
        var result = $"{Prefix}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        Array.Clear(hash, 0, hash.Length);
        return result;
    }

    /// <param name="needsRehash">true nếu băm bằng số vòng cũ thấp hơn và nên băm lại.</param>
    public bool Verify(string password, string stored, out bool needsRehash)
    {
        needsRehash = false;
        var parts = (stored ?? string.Empty).Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations < 1000) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        var actual = Derive(password, salt, iterations, expected.Length);
        var ok = FixedTimeEquals(actual, expected);
        Array.Clear(actual, 0, actual.Length);
        needsRehash = ok && iterations < _iterations;
        return ok;
    }

    public static byte[] Derive(string password, byte[] salt, int iterations, int length)
    {
        var bytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        try
        {
            // netstandard2.0 không có Rfc2898DeriveBytes với SHA-256, nên dùng BouncyCastle.
            var generator = new Pkcs5S2ParametersGenerator(new Sha256Digest());
            generator.Init(bytes, salt, iterations);
            return ((KeyParameter)generator.GenerateDerivedMacParameters(length * 8)).GetKey();
        }
        finally { Array.Clear(bytes, 0, bytes.Length); }
    }

    /// <summary>So sánh thời gian không đổi, chống timing attack.</summary>
    public static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
