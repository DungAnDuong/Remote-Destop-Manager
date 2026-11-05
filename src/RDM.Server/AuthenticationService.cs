using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RDM.Shared.Common;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Server;

/// <summary>Xác thực thiết bị và Controller, chống Brute Force theo IP. Không ghi token hay khóa vào Log.</summary>
public sealed class AuthenticationService
{
    private sealed class Attempts { public int Count; public DateTime BlockedUntil; public DateTime LastFailure; }

    private readonly RelayOptions _options;
    private readonly ClientRegistry _registry;
    private readonly ILogger<AuthenticationService> _log;
    private readonly ConcurrentDictionary<IPAddress, Attempts> _attempts = new ConcurrentDictionary<IPAddress, Attempts>();

    public AuthenticationService(IOptions<RelayOptions> options, ClientRegistry registry, ILogger<AuthenticationService> log)
    {
        _options = options.Value; _registry = registry; _log = log;
    }

    public bool IsBlocked(IPAddress ip) => _attempts.TryGetValue(ip, out var a) && a.BlockedUntil > DateTime.UtcNow;

    public AuthCode AuthenticateDevice(IPAddress ip, byte[] nonce, string remoteId, byte[] publicKey, byte[] signature, string enrollmentToken, DeviceInfo info)
    {
        var normalized = RemoteIdFormat.Normalize(remoteId);
        if (normalized == null || normalized != remoteId) return Fail(ip, AuthCode.ProtocolError, "Invalid Remote ID format");
        // Kiểm tra chữ ký trước: Remote ID không phải bí mật, chỉ chủ khóa mới được đi tiếp.
        if (!DeviceIdentity.Verify(publicKey, Wire.DeviceAuthMessage(nonce, remoteId, publicKey), signature))
            return Fail(ip, AuthCode.BadSignature, "Invalid device signature");

        var enrollmentOk = string.IsNullOrEmpty(_options.EnrollmentTokenSha256) || TokenMatches(enrollmentToken, _options.EnrollmentTokenSha256);
        switch (_registry.Register(remoteId, publicKey, info, enrollmentOk))
        {
            case RegisterOutcome.Registered:
                _log.LogInformation("Registered new device {RemoteId} from {Ip}", remoteId, ip);
                return Success(ip);
            case RegisterOutcome.Known: return Success(ip);
            case RegisterOutcome.Conflict:
                // Dễ gặp khi hai máy trùng ID ngẫu nhiên, nên Client được đổi ID mới; đồng thời có thể là hành vi chiếm ID, nên vẫn ghi cảnh báo và tính lỗi.
                return Fail(ip, AuthCode.RemoteIdConflict, $"Remote ID {remoteId} is registered with a different key");
            case RegisterOutcome.Revoked: return Fail(ip, AuthCode.Revoked, $"Device revoked {remoteId}");
            default: return Fail(ip, AuthCode.EnrollmentRequired, "Missing or invalid enrollment code");
        }
    }

    public AuthCode AuthenticateController(IPAddress ip, string token)
    {
        if (string.IsNullOrEmpty(_options.ControllerTokenSha256) || !TokenMatches(token, _options.ControllerTokenSha256))
            return Fail(ip, AuthCode.BadToken, "Token Controller sai");
        return Success(ip);
    }

    /// <summary>Vé chuyển tiếp sai cũng tính vào giới hạn: chặn dò vé.</summary>
    public void RecordBadTicket(IPAddress ip) => Fail(ip, AuthCode.BadToken, "Invalid relay ticket");

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty))).ToLowerInvariant();

    private static bool TokenMatches(string token, string expectedHash)
    {
        var a = Encoding.ASCII.GetBytes(HashToken(token));
        var b = Encoding.ASCII.GetBytes(expectedHash.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private AuthCode Success(IPAddress ip)
    {
        _attempts.TryRemove(ip, out _);
        return AuthCode.Ok;
    }

    private AuthCode Fail(IPAddress ip, AuthCode code, string reason)
    {
        var a = _attempts.GetOrAdd(ip, _ => new Attempts());
        lock (a)
        {
            // Hết thời gian chặn thì đếm lại từ đầu.
            if (DateTime.UtcNow - a.LastFailure > TimeSpan.FromMinutes(_options.AuthBlockMinutes)) a.Count = 0;
            a.LastFailure = DateTime.UtcNow;
            if (++a.Count >= _options.MaxAuthFailures)
            {
                a.BlockedUntil = DateTime.UtcNow.AddMinutes(_options.AuthBlockMinutes);
                _log.LogWarning("Temporarily blocked {Ip} after {Count} failed authentications", ip, a.Count);
            }
        }
        _log.LogWarning("Authentication failed from {Ip}: {Reason}", ip, reason);
        return code;
    }
}
