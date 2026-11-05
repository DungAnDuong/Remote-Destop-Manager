using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Server;

/// <summary>Xử lý một kết nối: TLS → xác thực có thời hạn → vòng lặp theo vai trò. Mọi đường thoát đều giải phóng socket.</summary>
public sealed class ConnectionHandler
{
    private readonly RelayOptions _options;
    private readonly AuthenticationService _auth;
    private readonly PresenceHub _hub;
    private readonly RelayManager _relay;
    private readonly ILogger<ConnectionHandler> _log;

    public ConnectionHandler(IOptions<RelayOptions> options, AuthenticationService auth, PresenceHub hub, RelayManager relay, ILogger<ConnectionHandler> log)
    {
        _options = options.Value; _auth = auth; _hub = hub; _relay = relay; _log = log;
    }

    public async Task HandleAsync(TcpClient tcp, X509Certificate2 certificate, CancellationToken ct)
    {
        var ip = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        using var stream = new SslStream(tcp.GetStream(), false);
        using var channel = new FramedStream(stream, tcp);
        try
        {
            var handshake = TimeSpan.FromSeconds(_options.HandshakeTimeoutSeconds);
            using (var tls = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                tls.CancelAfter(handshake);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, tls.Token);
            }

            var hello = await channel.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, handshake, ct);
            if (hello.Type != MessageType.Hello) { await RejectAsync(channel, AuthCode.ProtocolError, ct); return; }
            var (version, role) = Wire.Parse(hello.Payload, r => (r.ReadByte(), (PeerRole)r.ReadByte()));
            if (version != ProtocolInfo.Version) { await RejectAsync(channel, AuthCode.VersionMismatch, ct); return; }
            if (_auth.IsBlocked(ip)) { await RejectAsync(channel, AuthCode.RateLimited, ct); return; }

            // Vào đường chuyển tiếp: vé là bằng chứng, không cần bắt tay xác thực thêm. Sau bước này Server chỉ chép byte.
            if (role == PeerRole.RelayPeer)
            {
                var join = await channel.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, handshake, ct);
                if (join.Type != MessageType.RelayJoin) { await RejectAsync(channel, AuthCode.ProtocolError, ct); return; }
                var token = Wire.Parse(join.Payload, r => r.Blob());
                if (!await _relay.RunPeerAsync(token, channel, stream, ct)) _auth.RecordBadTicket(ip);
                return;
            }

            var nonce = RandomNumberGenerator.GetBytes(32);
            await channel.SendAsync(MessageType.Challenge, Wire.Build(w => w.Blob(nonce)), ct);
            var auth = await channel.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, handshake, ct);

            switch (role)
            {
                case PeerRole.Device when auth.Type == MessageType.DeviceAuth:
                    await RunDeviceAsync(channel, ip, nonce, auth, ct);
                    break;
                case PeerRole.Controller when auth.Type == MessageType.ControllerAuth:
                    await RunControllerAsync(channel, ip, auth, ct);
                    break;
                default:
                    await RejectAsync(channel, AuthCode.ProtocolError, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is AuthenticationException || ex is ProtocolException || ex is TimeoutException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is SocketException)
        {
            _log.LogDebug("Closing connection from {Ip}: {Type}", ip, ex.GetType().Name);
        }
    }

    private Task RejectAsync(FramedStream channel, AuthCode code, CancellationToken ct) => SendAuthAsync(channel, code, ct);

    private Task SendAuthAsync(FramedStream channel, AuthCode code, CancellationToken ct) =>
        channel.SendAsync(MessageType.AuthResult, Wire.Build(w => { w.Write((byte)code); w.Write(_options.HeartbeatIntervalSeconds); }), ct);

    private async Task RunDeviceAsync(FramedStream channel, IPAddress ip, byte[] nonce, Frame auth, CancellationToken ct)
    {
        string remoteId = null, token = null;
        byte[] publicKey = null, signature = null;
        DeviceInfo info = null;
        var access = false;
        Wire.Parse(auth.Payload, r =>
        {
            remoteId = r.Str(); publicKey = r.Blob(); signature = r.Blob(); token = r.Str(); info = r.Info(); access = r.ReadBoolean();
            return 0;
        });

        var code = _auth.AuthenticateDevice(ip, nonce, remoteId, publicKey, signature, token, info);
        await SendAuthAsync(channel, code, ct);
        if (code != AuthCode.Ok) return;

        var session = new DeviceSession { RemoteId = remoteId, Info = info, Channel = channel, AccessEnabled = access, LastHeartbeatTicks = DateTime.UtcNow.Ticks, PublicIp = ip.ToString() };
        _hub.DeviceOnline(session);
        try
        {
            var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
            while (true)
            {
                var frame = await channel.ReceiveAsync(256, timeout, ct);
                Volatile.Write(ref session.LastHeartbeatTicks, DateTime.UtcNow.Ticks);
                if (frame.Type == MessageType.SessionRequest)
                {
                    await HandleSessionRequestAsync(session, frame, ct);
                    continue;
                }
                if (frame.Type != MessageType.Heartbeat) throw new ProtocolException("The device sent an unexpected message.");
                var enabled = Wire.Parse(frame.Payload, r => r.ReadBoolean());
                if (enabled != Volatile.Read(ref session.AccessEnabled))
                {
                    Volatile.Write(ref session.AccessEnabled, enabled);
                    _hub.AccessChanged(session);
                }
                await channel.SendAsync(MessageType.HeartbeatAck, null, ct);
            }
        }
        finally { _hub.DeviceOffline(session); }
    }

    /// <summary>Thiết bị hỏi về một Remote ID, hoặc xin vé chuyển tiếp tới nó.</summary>
    private async Task HandleSessionRequestAsync(DeviceSession requester, Frame frame, CancellationToken ct)
    {
        var (requestId, target, wantRelay) = Wire.Parse(frame.Payload, r => (r.ReadInt32(), r.Str(), r.ReadBoolean()));
        var ticket = _relay.CreateTicket(requester, target, wantRelay, out var targetSession, out var targetToken);
        // Báo máy đích trước: nó sẽ tự kết nối tới Relay. Lỗi gửi cho máy đích không được làm rớt kết nối của bên xin.
        if (targetToken != null)
        {
            try { await targetSession.Channel.SendAsync(MessageType.IncomingSession, Wire.Build(w => w.Blob(targetToken)), ct); }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                ticket = new RelayTicket { Code = TicketCode.Offline };
            }
        }
        await requester.Channel.SendAsync(MessageType.SessionTicket, Wire.Ticket(requestId, ticket), ct);
    }

    private async Task RunControllerAsync(FramedStream channel, IPAddress ip, Frame auth, CancellationToken ct)
    {
        var token = Wire.Parse(auth.Payload, r => r.Str());
        var code = _auth.AuthenticateController(ip, token);
        await SendAuthAsync(channel, code, ct);
        if (code != AuthCode.Ok) return;

        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        var first = await channel.ReceiveAsync(256, timeout, ct);
        if (first.Type != MessageType.SubscribeAll) throw new ProtocolException("The controller sent an unexpected message.");

        var subscriber = new PresenceSubscriber { Stream = channel };
        var snapshot = _hub.Subscribe(subscriber);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = Task.CompletedTask;
        try
        {
            for (var i = 0; i == 0 || i < snapshot.Count; i += ProtocolInfo.SnapshotChunk)
            {
                var chunk = snapshot.Skip(i).Take(ProtocolInfo.SnapshotChunk).ToList();
                var isLast = i + ProtocolInfo.SnapshotChunk >= snapshot.Count;
                await channel.SendAsync(MessageType.PresenceSnapshot, Wire.Entries(chunk, isLast), ct);
            }
            // Chỉ gửi thay đổi sau ảnh chụp: gửi xen kẽ thì ảnh chụp cũ có thể đè lên một thay đổi mới hơn.
            writer = WriteQueueAsync(channel, subscriber, session.Token);
            while (true)
            {
                var frame = await channel.ReceiveAsync(256, timeout, ct);
                if (frame.Type != MessageType.Heartbeat) throw new ProtocolException("The controller sent an unexpected message.");
                await channel.SendAsync(MessageType.HeartbeatAck, null, ct);
            }
        }
        finally
        {
            _hub.Unsubscribe(subscriber);
            session.Cancel();
            try { await writer; }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException) { }
        }
    }

    private static async Task WriteQueueAsync(FramedStream channel, PresenceSubscriber subscriber, CancellationToken ct)
    {
        await foreach (var (type, payload) in subscriber.Queue.Reader.ReadAllAsync(ct))
            await channel.SendAsync(type, payload, ct);
    }
}
