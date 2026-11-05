using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Shared.Network;

public enum RelayState { Stopped, Connecting, Online, Offline, Rejected }

/// <summary>Relay từ chối vì lý do không tự khắc phục bằng cách thử lại (sai token, Remote ID bị trùng, ...).</summary>
public sealed class RelayRejectedException : Exception
{
    public AuthCode Code { get; }
    public RelayRejectedException(AuthCode code) : base(Describe(code)) => Code = code;

    public static string Describe(AuthCode code) => code switch
    {
        AuthCode.BadSignature => "The Relay rejected the device signature.",
        AuthCode.RemoteIdConflict => "This Remote ID is already registered by another device.",
        AuthCode.EnrollmentRequired => "The Relay requires a valid device enrollment code.",
        AuthCode.BadToken => "The Relay rejected the Controller token.",
        AuthCode.Revoked => "This device was revoked on the Relay.",
        AuthCode.VersionMismatch => "The protocol version is incompatible with the Relay.",
        AuthCode.RateLimited => "Too many failed authentications; the Relay has temporarily blocked this address.",
        AuthCode.ServerBusy => "The Relay is overloaded.",
        _ => "The Relay refused the connection."
    };

    /// <summary>Quá tải hoặc tạm chặn sẽ tự hết, nên vẫn thử lại; còn lại cần người sửa cấu hình.</summary>
    public bool Retryable => Code == AuthCode.RateLimited || Code == AuthCode.ServerBusy;
}

/// <summary>Vòng kết nối tới Relay: tự nối lại với backoff và jitter, giải phóng socket mỗi lần, dừng được bằng hủy.</summary>
public abstract class RelayClientBase : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private readonly RelayEndpoint _endpoint;
    private CancellationTokenSource _cts;
    private Task _loop;
    private RelayState _state = RelayState.Stopped;

    protected RelayClientBase(RelayEndpoint endpoint) => _endpoint = endpoint;

    public RelayState State => _state;
    public string LastError { get; private set; }
    public AuthCode? LastRejection { get; private set; }
    public event Action<RelayState> StateChanged;

    /// <summary>Thời gian chờ trước lần thử đầu sau khi mất kết nối; kiểm thử có thể giảm xuống.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunLoopAsync(ct));
    }

    public async Task StopAsync()
    {
        var cts = _cts; var loop = _loop;
        _cts = null; _loop = null;
        if (cts == null) return;
        cts.Cancel();
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        cts.Dispose();
        SetState(RelayState.Stopped);
    }

    /// <summary>Hủy vòng kết nối, không chờ. Muốn chờ giải phóng hết thì dùng StopAsync.</summary>
    public void Dispose() => _cts?.Cancel();

    protected abstract Task RunSessionAsync(FramedStream channel, CancellationToken ct);

    protected void MarkOnline() { LastError = null; LastRejection = null; SetState(RelayState.Online); }

    private void SetState(RelayState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { Log.Error(ex, "Error handling a Relay state event"); }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;
        var random = new Random();
        while (!ct.IsCancellationRequested)
        {
            SetState(RelayState.Connecting);
            var wasOnline = false;
            try
            {
                using var channel = await TlsConnector.ConnectAsync(_endpoint, ct).ConfigureAwait(false);
                await RunSessionAsync(channel, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (RelayRejectedException ex)
            {
                LastError = ex.Message;
                LastRejection = ex.Code;
                Log.Warn("Relay rejected: {0}", ex.Code);
                if (!ex.Retryable) { SetState(RelayState.Rejected); return; }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                wasOnline = _state == RelayState.Online;
                LastError = Describe(ex);
                Log.Info("Lost Relay connection: {0}", ex.GetType().Name);
            }
            SetState(RelayState.Offline);
            if (wasOnline) backoff = InitialBackoff;
            var jitter = TimeSpan.FromMilliseconds(random.Next(0, 500));
            try { await Task.Delay(backoff + jitter, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException || ex is SocketException || ex is AuthenticationException || ex is TimeoutException ||
        ex is ProtocolException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is InvalidOperationException;

    private static string Describe(Exception ex) => ex switch
    {
        AuthenticationException _ => "The Relay certificate is invalid, or TLS 1.2+ could not be established.",
        SocketException _ => "Could not connect to the Relay.",
        TimeoutException _ => "The Relay is not responding.",
        ProtocolException _ => "The Relay returned invalid data.",
        _ => "Lost connection to the Relay."
    };

    /// <summary>Gửi Hello, nhận Challenge; trả về nonce của Server.</summary>
    protected static async Task<byte[]> HelloAsync(FramedStream channel, PeerRole role, CancellationToken ct)
    {
        await channel.SendAsync(MessageType.Hello, Wire.Build(w => { w.Write(ProtocolInfo.Version); w.Write((byte)role); }), ct).ConfigureAwait(false);
        var frame = await channel.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (frame.Type == MessageType.AuthResult) throw new RelayRejectedException(ReadAuth(frame, out _));
        if (frame.Type != MessageType.Challenge) throw new ProtocolException("The Relay did not send a challenge.");
        return Wire.Parse(frame.Payload, r => r.Blob());
    }

    /// <summary>Đọc kết quả xác thực; trả về chu kỳ Heartbeat do Server quy định.</summary>
    protected static async Task<TimeSpan> ReadAuthResultAsync(FramedStream channel, CancellationToken ct)
    {
        var frame = await channel.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (frame.Type != MessageType.AuthResult) throw new ProtocolException("The Relay did not send an authentication result.");
        var code = ReadAuth(frame, out var seconds);
        if (code != AuthCode.Ok) throw new RelayRejectedException(code);
        if (seconds < 1 || seconds > 300) throw new ProtocolException("Invalid Heartbeat interval.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static AuthCode ReadAuth(Frame frame, out int heartbeatSeconds)
    {
        var seconds = 0;
        var code = Wire.Parse(frame.Payload, r => { var c = (AuthCode)r.ReadByte(); seconds = r.ReadInt32(); return c; });
        heartbeatSeconds = seconds;
        return code;
    }
}

/// <summary>Xin phiên qua Relay. Controller không phụ thuộc phần thiết bị, chỉ cần giao diện này.</summary>
public interface IRelayBroker
{
    bool IsOnline { get; }
    RelayEndpoint Endpoint { get; }
    /// <param name="wantRelay">false: chỉ hỏi trạng thái và địa chỉ. true: xin vé chuyển tiếp, máy đích sẽ kết nối tới Relay ngay.</param>
    Task<RelayTicket> RequestSessionAsync(string remoteId, bool wantRelay, CancellationToken ct);
}

/// <summary>Đi vào đường chuyển tiếp. Sau RelayReady, luồng này nối thẳng với bên kia: phiên E2EE chạy trên nó như một kết nối trực tiếp.</summary>
public static class RelayJoin
{
    public static readonly TimeSpan PartnerTimeout = TimeSpan.FromSeconds(35);

    public static async Task<FramedStream> ConnectAsync(RelayEndpoint endpoint, byte[] token, CancellationToken ct)
    {
        var channel = await TlsConnector.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        try
        {
            await channel.SendAsync(MessageType.Hello, Wire.Build(w => { w.Write(ProtocolInfo.Version); w.Write((byte)PeerRole.RelayPeer); }), ct).ConfigureAwait(false);
            await channel.SendAsync(MessageType.RelayJoin, Wire.Build(w => w.Blob(token)), ct).ConfigureAwait(false);
            var frame = await channel.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, PartnerTimeout, ct).ConfigureAwait(false);
            if (frame.Type != MessageType.RelayReady) throw new ProtocolException("The Relay did not open the session.");
            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }
}

/// <summary>Đăng ký thiết bị và gửi Heartbeat.</summary>
public sealed class DeviceRelayClient : RelayClientBase, IRelayBroker
{
    private readonly DeviceIdentity _identity;
    private readonly string _remoteId;
    private readonly DeviceInfo _info;
    private readonly string _enrollmentToken;
    private volatile bool _accessEnabled;
    private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
    private readonly RelayEndpoint _relay;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<RelayTicket>> _pending = new ConcurrentDictionary<int, TaskCompletionSource<RelayTicket>>();
    private FramedStream _channel;
    private int _nextRequest;

    public string RemoteId => _remoteId;
    public bool IsOnline => State == RelayState.Online && _channel != null;
    public RelayEndpoint Endpoint => _relay;

    /// <summary>Có Controller muốn kết nối qua Relay: máy này phải dùng vé để vào đường chuyển tiếp.</summary>
    public event Action<byte[]> IncomingSession;

    public async Task<RelayTicket> RequestSessionAsync(string remoteId, bool wantRelay, CancellationToken ct)
    {
        var channel = _channel ?? throw new InvalidOperationException("Not connected to the Relay.");
        var id = Interlocked.Increment(ref _nextRequest);
        // Bên gọi không được chạy trên luồng nhận dữ liệu: nếu nó chặn chờ StopAsync thì hai bên chờ nhau mãi.
        var result = new TaskCompletionSource<RelayTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = result;
        try
        {
            await channel.SendAsync(MessageType.SessionRequest, Wire.Build(w => { w.Write(id); w.Str(remoteId); w.Write(wantRelay); }), ct).ConfigureAwait(false);
            var done = await Task.WhenAny(result.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            if (done != result.Task) throw new TimeoutException("The Relay did not answer the session request.");
            return await result.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public DeviceRelayClient(RelayEndpoint endpoint, DeviceIdentity identity, string remoteId, DeviceInfo info, bool accessEnabled, string enrollmentToken = null) : base(endpoint)
    {
        _relay = endpoint;
        _identity = identity; _remoteId = remoteId; _info = info; _accessEnabled = accessEnabled; _enrollmentToken = enrollmentToken;
    }

    /// <summary>Báo ngay cho Relay khi chủ máy bật hoặc tắt Remote Access.</summary>
    public void SetAccessEnabled(bool enabled)
    {
        _accessEnabled = enabled;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task RunSessionAsync(FramedStream channel, CancellationToken ct)
    {
        var nonce = await HelloAsync(channel, PeerRole.Device, ct).ConfigureAwait(false);
        var signature = _identity.Sign(Wire.DeviceAuthMessage(nonce, _remoteId, _identity.PublicKey));
        await channel.SendAsync(MessageType.DeviceAuth, Wire.Build(w =>
        {
            w.Str(_remoteId); w.Blob(_identity.PublicKey); w.Blob(signature); w.Str(_enrollmentToken); w.Info(_info); w.Write(_accessEnabled);
        }), ct).ConfigureAwait(false);
        var interval = await ReadAuthResultAsync(channel, ct).ConfigureAwait(false);
        _channel = channel;
        MarkOnline();

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = SendHeartbeatsAsync(channel, interval, session.Token);
        try
        {
            while (true)
            {
                var frame = await channel.ReceiveAsync(ProtocolInfo.MaxFrame, TimeSpan.FromTicks(interval.Ticks * 3), ct).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case MessageType.HeartbeatAck: break;
                    case MessageType.SessionTicket:
                        var ticket = Wire.ReadTicket(frame.Payload, out var requestId);
                        if (_pending.TryGetValue(requestId, out var waiting)) waiting.TrySetResult(ticket);
                        break;
                    case MessageType.IncomingSession:
                        var token = Wire.Parse(frame.Payload, r => r.Blob());
                        IncomingSession?.Invoke(token);
                        break;
                    default: throw new ProtocolException("Unexpected message from the Relay.");
                }
            }
        }
        finally
        {
            _channel = null;
            foreach (var p in _pending.Values) p.TrySetException(new IOException("Lost connection to the Relay."));
            session.Cancel();
            try { await sender.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException) { }
        }
    }

    private async Task SendHeartbeatsAsync(FramedStream channel, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await channel.SendAsync(MessageType.Heartbeat, Wire.Build(w => w.Write(_accessEnabled)), ct).ConfigureAwait(false);
            await _wake.WaitAsync(interval, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Controller theo dõi Online/Offline của các thiết bị.</summary>
public sealed class PresenceRelayClient : RelayClientBase
{
    private readonly string _token;

    /// <summary>Ảnh chụp đầy đủ (true) hoặc thay đổi lẻ (false). Gọi trên luồng nền.</summary>
    public event Action<IReadOnlyList<PresenceEntry>, bool> EntriesReceived;

    public PresenceRelayClient(RelayEndpoint endpoint, string controllerToken) : base(endpoint) => _token = controllerToken;

    protected override async Task RunSessionAsync(FramedStream channel, CancellationToken ct)
    {
        await HelloAsync(channel, PeerRole.Controller, ct).ConfigureAwait(false);
        await channel.SendAsync(MessageType.ControllerAuth, Wire.Build(w => w.Str(_token)), ct).ConfigureAwait(false);
        var interval = await ReadAuthResultAsync(channel, ct).ConfigureAwait(false);
        await channel.SendAsync(MessageType.SubscribeAll, null, ct).ConfigureAwait(false);
        MarkOnline();

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = PingAsync(channel, interval, session.Token);
        var snapshot = new List<PresenceEntry>();
        try
        {
            while (true)
            {
                var frame = await channel.ReceiveAsync(ProtocolInfo.MaxFrame, TimeSpan.FromTicks(interval.Ticks * 3), ct).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case MessageType.HeartbeatAck: break;
                    case MessageType.PresenceSnapshot:
                        snapshot.AddRange(Wire.ReadEntries(frame.Payload, out var last));
                        if (last)
                        {
                            EntriesReceived?.Invoke(snapshot, true);
                            snapshot = new List<PresenceEntry>();
                        }
                        break;
                    case MessageType.PresenceChanged:
                        EntriesReceived?.Invoke(Wire.ReadEntries(frame.Payload, out _), false);
                        break;
                    default: throw new ProtocolException("Unexpected message from the Relay.");
                }
            }
        }
        finally
        {
            session.Cancel();
            try { await pinger.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException) { }
        }
    }

    private static async Task PingAsync(FramedStream channel, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            await channel.SendAsync(MessageType.Heartbeat, Wire.Build(w => w.Write(true)), ct).ConfigureAwait(false);
        }
    }
}
