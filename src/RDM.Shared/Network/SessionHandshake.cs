using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Shared.Network;

public sealed class ControllerPeer
{
    public byte[] PublicKey { get; set; }
    public string Username { get; set; }
    public string Fingerprint => DeviceIdentity.Fingerprint(PublicKey);
}

public sealed class HostHandshakeOptions
{
    public DeviceIdentity Identity { get; set; }
    public string RemoteId { get; set; }
    /// <summary>Salt và số vòng PBKDF2 của mật khẩu truy cập; null nếu chưa đặt mật khẩu.</summary>
    public Func<(byte[] Salt, int Iterations)?> ProofParameters { get; set; }
    /// <summary>Quyết định cho phép, có thể chờ chủ máy bấm chấp nhận. Trả về None là được phép.</summary>
    public Func<byte[], byte[], ControllerPeer, CancellationToken, Task<SessionDecline>> Authorize { get; set; }
    public long RekeyBytes { get; set; } = SecureChannel.DefaultRekeyBytes;
}

public sealed class ControllerHandshakeOptions
{
    public DeviceIdentity Identity { get; set; }
    public string ExpectedRemoteId { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    /// <summary>Kiểm tra khóa định danh của máy đích với khóa đã ghim. Trả về false là ngắt ngay, trước khi gửi bất kỳ bằng chứng nào.</summary>
    public Func<byte[], Task<bool>> VerifyHostKey { get; set; }
    public long RekeyBytes { get; set; } = SecureChannel.DefaultRekeyBytes;
}

public sealed class HandshakeResult
{
    public SecureChannel Channel { get; set; }
    public byte[] SessionId { get; set; }
    public byte[] PeerKey { get; set; }
    public ControllerPeer Controller { get; set; }
}

/// <summary>
/// Bắt tay E2EE (docs/04-Security.md): X25519 ephemeral cho forward secrecy, chữ ký Ed25519 trên transcript để chống MITM.
/// Controller chỉ gửi bằng chứng mật khẩu sau khi đã xác minh khóa máy đích, và gửi bên trong kênh mã hóa.
/// Không phụ thuộc đường truyền: dùng được cho P2P và cho Relay (Giai đoạn 5), vì bên giữa chỉ thấy khóa công khai và bản mã.
/// </summary>
public static class SessionHandshake
{
    private const int NonceSize = 32;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Đủ cho chủ máy đọc hộp thoại và PBKDF2 trên máy chậm.</summary>
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(60);

    public static async Task<HandshakeResult> AcceptAsync(FramedStream stream, HostHandshakeOptions o, CancellationToken ct)
    {
        var hello = await stream.ReceiveAsync(Protocol.ProtocolInfo.MaxHandshakeFrame, StepTimeout, ct).ConfigureAwait(false);
        if (hello.Type != MessageType.SessionHello) throw new ProtocolException("Not a session request.");
        var (peerEphemeral, peerNonce) = Wire.Parse(hello.Payload, r =>
        {
            if (r.ReadByte() != ProtocolInfo.Version) throw new ProtocolException("Incompatible protocol version.");
            return (r.Blob(), r.Blob());
        });
        if (peerEphemeral.Length != X25519PublicKeyParameters.KeySize || peerNonce.Length != NonceSize) throw new ProtocolException("Invalid handshake parameters.");

        var ephemeral = new X25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        var myEphemeral = ephemeral.GeneratePublicKey().GetEncoded();
        var nonce = CryptoRandom.GetBytes(NonceSize);
        var proofParams = o.ProofParameters?.Invoke();
        var salt = proofParams?.Salt ?? new byte[0];
        var iterations = proofParams?.Iterations ?? 0;

        var transcript = Transcript(peerEphemeral, peerNonce, myEphemeral, nonce, o.Identity.PublicKey, o.RemoteId, salt, iterations);
        var signature = o.Identity.Sign(Signed(SessionInfo.HostSignContext, transcript));
        await stream.SendAsync(MessageType.SessionHelloReply, Wire.Build(w =>
        {
            w.Blob(myEphemeral); w.Blob(nonce); w.Blob(o.Identity.PublicKey); w.Str(o.RemoteId); w.Blob(salt); w.Write(iterations); w.Blob(signature);
        }), ct).ConfigureAwait(false);

        var channel = DeriveChannel(stream, ephemeral, peerEphemeral, transcript, true, o.RekeyBytes);
        try
        {
            var (type, payload) = await channel.ReceiveAsync(DecisionTimeout, ct).ConfigureAwait(false);
            if (type != SessionMessage.Auth) throw new ProtocolException("Missing authentication message.");
            ControllerPeer peer = null;
            byte[] peerSignature = null, proof = null;
            Wire.Parse(payload, r =>
            {
                peer = new ControllerPeer { PublicKey = r.Blob(), Username = r.Str() };
                peerSignature = r.Blob();
                proof = r.Blob();
                return 0;
            });

            var decision = DeviceIdentity.Verify(peer.PublicKey, Signed(SessionInfo.ControllerSignContext, transcript), peerSignature)
                ? await o.Authorize(transcript, proof.Length == 0 ? null : proof, peer, ct).ConfigureAwait(false)
                : SessionDecline.BadSignature;

            var sessionId = decision == SessionDecline.None ? CryptoRandom.GetBytes(16) : new byte[0];
            await channel.SendAsync(SessionMessage.AuthResult, Wire.Build(w => { w.Write((byte)decision); w.Blob(sessionId); }), ct).ConfigureAwait(false);
            if (decision != SessionDecline.None) throw new SessionRejectedException(decision);
            return new HandshakeResult { Channel = channel, SessionId = sessionId, PeerKey = peer.PublicKey, Controller = peer };
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    public static async Task<HandshakeResult> ConnectAsync(FramedStream stream, ControllerHandshakeOptions o, CancellationToken ct)
    {
        var ephemeral = new X25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        var myEphemeral = ephemeral.GeneratePublicKey().GetEncoded();
        var nonce = CryptoRandom.GetBytes(NonceSize);
        await stream.SendAsync(MessageType.SessionHello, Wire.Build(w => { w.Write(ProtocolInfo.Version); w.Blob(myEphemeral); w.Blob(nonce); }), ct).ConfigureAwait(false);

        var reply = await stream.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, StepTimeout, ct).ConfigureAwait(false);
        if (reply.Type != MessageType.SessionHelloReply) throw new ProtocolException("The remote computer sent an invalid reply.");
        byte[] peerEphemeral = null, peerNonce = null, hostKey = null, salt = null, signature = null;
        string remoteId = null;
        var iterations = 0;
        Wire.Parse(reply.Payload, r =>
        {
            peerEphemeral = r.Blob(); peerNonce = r.Blob(); hostKey = r.Blob(); remoteId = r.Str(); salt = r.Blob(); iterations = r.ReadInt32(); signature = r.Blob();
            return 0;
        });
        if (peerEphemeral.Length != X25519PublicKeyParameters.KeySize || peerNonce.Length != NonceSize) throw new ProtocolException("Invalid handshake parameters.");
        if (iterations != 0 && (iterations < 1000 || iterations > 50_000_000)) throw new ProtocolException("Invalid password parameters.");

        var transcript = Transcript(myEphemeral, nonce, peerEphemeral, peerNonce, hostKey, remoteId, salt, iterations);
        if (!DeviceIdentity.Verify(hostKey, Signed(SessionInfo.HostSignContext, transcript), signature))
            throw new HostVerificationException("The remote computer's signature is invalid. This may be a man-in-the-middle attack.");
        if (!string.Equals(remoteId, o.ExpectedRemoteId, StringComparison.Ordinal))
            throw new HostVerificationException("The computer that answered has a different Remote ID from the one you requested.", true);
        if (!await o.VerifyHostKey(hostKey).ConfigureAwait(false))
            throw new HostVerificationException("The remote computer's identity key was not accepted.");

        var channel = DeriveChannel(stream, ephemeral, peerEphemeral, transcript, false, o.RekeyBytes);
        try
        {
            var proof = iterations > 0 && !string.IsNullOrEmpty(o.Password)
                ? await Task.Run(() => AccessProof.Compute(o.Password, salt, iterations, transcript), ct).ConfigureAwait(false)
                : new byte[0];
            var mySignature = o.Identity.Sign(Signed(SessionInfo.ControllerSignContext, transcript));
            await channel.SendAsync(SessionMessage.Auth, Wire.Build(w => { w.Blob(o.Identity.PublicKey); w.Str(o.Username); w.Blob(mySignature); w.Blob(proof); }), ct).ConfigureAwait(false);

            var (type, payload) = await channel.ReceiveAsync(DecisionTimeout, ct).ConfigureAwait(false);
            if (type != SessionMessage.AuthResult) throw new ProtocolException("Missing authentication result.");
            var decision = SessionDecline.None;
            var sessionId = Wire.Parse(payload, r => { decision = (SessionDecline)r.ReadByte(); return r.Blob(); });
            if (decision != SessionDecline.None) throw new SessionRejectedException(decision);
            return new HandshakeResult { Channel = channel, SessionId = sessionId, PeerKey = hostKey };
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    private static byte[] Transcript(byte[] ctrlEphemeral, byte[] ctrlNonce, byte[] hostEphemeral, byte[] hostNonce, byte[] hostKey, string remoteId, byte[] salt, int iterations)
    {
        var data = Wire.Build(w =>
        {
            w.Str("RDM-SESSION-TRANSCRIPT-v1");
            w.Blob(ctrlEphemeral); w.Blob(ctrlNonce); w.Blob(hostEphemeral); w.Blob(hostNonce); w.Blob(hostKey); w.Str(remoteId); w.Blob(salt); w.Write(iterations);
        });
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    private static byte[] Signed(string context, byte[] transcript) => Wire.Build(w => { w.Str(context); w.Blob(transcript); });

    private static SecureChannel DeriveChannel(FramedStream stream, X25519PrivateKeyParameters ephemeral, byte[] peerEphemeral, byte[] transcript, bool isHost, long rekeyBytes)
    {
        var agreement = new X25519Agreement();
        agreement.Init(ephemeral);
        var secret = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(new X25519PublicKeyParameters(peerEphemeral, 0), secret, 0);
        // Điểm yếu nhỏ của X25519 cho ra bí mật toàn số 0: từ chối, không dùng làm khóa.
        if (Array.TrueForAll(secret, b => b == 0)) throw new HostVerificationException("The other side's ephemeral key is invalid.");
        var keys = Hkdf.Derive(secret, transcript, "rdm-session-keys", AesGcmCipher.KeySize * 2);
        var toHost = new byte[AesGcmCipher.KeySize];
        var toController = new byte[AesGcmCipher.KeySize];
        Buffer.BlockCopy(keys, 0, toHost, 0, toHost.Length);
        Buffer.BlockCopy(keys, toHost.Length, toController, 0, toController.Length);
        try { return new SecureChannel(stream, isHost ? toController : toHost, isHost ? toHost : toController, isHost, rekeyBytes); }
        finally
        {
            Array.Clear(secret, 0, secret.Length); Array.Clear(keys, 0, keys.Length);
            Array.Clear(toHost, 0, toHost.Length); Array.Clear(toController, 0, toController.Length);
        }
    }
}

/// <summary>Không xác minh được máy đích. Không được tự động thử lại hay bỏ qua.</summary>
public sealed class HostVerificationException : Exception
{
    /// <summary>Máy trả lời là một máy RDM khác, có Remote ID khác: không phải tấn công, chỉ là gõ nhầm cửa, ví dụ trùng IP nội bộ giữa hai mạng.</summary>
    public bool WrongMachine { get; }
    public HostVerificationException(string message, bool wrongMachine = false) : base(message) => WrongMachine = wrongMachine;
}
