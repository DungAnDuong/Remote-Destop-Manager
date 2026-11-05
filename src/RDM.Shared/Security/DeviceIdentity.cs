using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace RDM.Shared.Security;

/// <summary>
/// Định danh thiết bị bằng khóa Ed25519. Xác thực bằng chữ ký nên không có token nào để bị đánh cắp,
/// và Relay chỉ giữ khóa công khai. Cũng là gốc tin cậy cho xác thực khóa đầu cuối E2EE (Giai đoạn 4).
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    public const int SeedSize = 32;
    public const int SignatureSize = 64;
    private readonly byte[] _seed;

    public byte[] PublicKey { get; }

    private DeviceIdentity(byte[] seed)
    {
        if (seed == null || seed.Length != SeedSize) throw new ArgumentException("An Ed25519 key must be 32 bytes.", nameof(seed));
        _seed = (byte[])seed.Clone();
        PublicKey = new Ed25519PrivateKeyParameters(_seed, 0).GeneratePublicKey().GetEncoded();
    }

    public static DeviceIdentity Generate() => new DeviceIdentity(CryptoRandom.GetBytes(SeedSize));
    public static DeviceIdentity FromSeed(byte[] seed) => new DeviceIdentity(seed);

    public byte[] ExportSeed() => (byte[])_seed.Clone();

    public byte[] Sign(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(_seed, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey == null || publicKey.Length != Ed25519PublicKeyParameters.KeySize || signature == null || signature.Length != SignatureSize) return false;
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    /// <summary>Fingerprint để người dùng đối chiếu bằng mắt: 16 ký tự hex của SHA-256.</summary>
    public static string Fingerprint(byte[] publicKey)
    {
        using var sha = SHA256.Create();
        var hex = BitConverter.ToString(sha.ComputeHash(publicKey), 0, 8).Replace("-", "");
        var sb = new StringBuilder();
        for (var i = 0; i < hex.Length; i += 4) sb.Append(i > 0 ? "-" : "").Append(hex, i, 4);
        return sb.ToString();
    }

    public void Dispose() => Array.Clear(_seed, 0, _seed.Length);
}

/// <summary>Sinh Remote ID 9 chữ số bằng CSPRNG, không có độ lệch. Remote ID không phải bí mật.</summary>
public static class RemoteIdGenerator
{
    private const uint Min = 100_000_000, Range = 900_000_000;

    public static string Generate()
    {
        // Loại bỏ giá trị vượt bội số lớn nhất của Range để không lệch phân phối.
        var limit = uint.MaxValue - uint.MaxValue % Range;
        while (true)
        {
            var value = BitConverter.ToUInt32(CryptoRandom.GetBytes(4), 0);
            if (value < limit) return (Min + value % Range).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Bằng chứng mật khẩu truy cập: HMAC(PBKDF2(mật khẩu, salt), transcript).
/// Gắn với transcript của phiên nên không phát lại được và mật khẩu không bao giờ đi qua mạng.
/// </summary>
public static class AccessProof
{
    public const int KeySize = 32;

    public static byte[] Compute(string password, byte[] salt, int iterations, byte[] transcript)
    {
        var key = PasswordHasher.Derive(password, salt, iterations, KeySize);
        try { return ComputeWithKey(key, transcript); }
        finally { Array.Clear(key, 0, key.Length); }
    }

    public static byte[] ComputeWithKey(byte[] derivedKey, byte[] transcript)
    {
        using var hmac = new HMACSHA256(derivedKey);
        return hmac.ComputeHash(transcript);
    }
}
