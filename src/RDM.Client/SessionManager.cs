using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Client;

/// <summary>Một phiên đang chạy trên máy được điều khiển.</summary>
public sealed class HostSession
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    public const int MaxInFlightFrames = 2;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);
    private const int MinQuality = 30, MaxAutoQuality = 75;

    private readonly SecureChannel _channel;
    private readonly IScreenSource _screen;
    private readonly IInputSink _input;
    private readonly CancellationTokenSource _cts;
    private readonly FrameEncoder _encoder = new FrameEncoder();
    private readonly SemaphoreSlim _ackSignal = new SemaphoreSlim(0);
    private readonly object _settingsGate = new object();
    private IReadOnlyList<MonitorInfo> _monitors;
    private int _monitor, _quality = 60, _maxFps = 20, _scale = 100;
    private bool _autoQuality = true;
    private long _sequence;
    private long _acked;
    private readonly Dictionary<long, long> _sentAt = new Dictionary<long, long>();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _closed;

    public string ControllerUser { get; }
    public string ControllerFingerprint { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public long FramesSent { get; private set; }
    public long BytesSent { get; private set; }
    public event Action<HostSession, string> Ended;

    private readonly SessionTransfers _transfers;
    public event Action<ChatMessage> ChatReceived;
    public event Action<FileTransferProgress> TransferChanged;

    public Task SendChatAsync(string text) => _transfers.SendChatAsync(text, _cts.Token);
    public Task SendFileAsync(string path) => _transfers.SendFileAsync(path, _cts.Token);
    public void PauseTransfer(int id) => _transfers.PauseOutgoing(id);
    public void ResumeTransfer(int id) => _transfers.ResumeOutgoing(id);
    public void CancelTransfer(int id) => _transfers.CancelTransfer(id);

    public HostSession(SecureChannel channel, ControllerPeer peer, IScreenSource screen, IInputSink input, CancellationToken shutdown)
    {
        _channel = channel; _screen = screen; _input = input;
        ControllerUser = peer.Username; ControllerFingerprint = peer.Fingerprint;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _cts.CancelAfter(MaxDuration);
        _transfers = new SessionTransfers((t, p, c) => _channel.SendAsync(t, p, c), SessionTransfers.DefaultReceiveDir(), _cts.Token);
        _transfers.ChatReceived += m => ChatReceived?.Invoke(m);
        _transfers.TransferChanged += p => TransferChanged?.Invoke(p);
    }

    public void Disconnect() => Cancel();

    private void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Chủ máy ngắt: báo cho Controller biết đây là chủ ý để nó KHÔNG tự kết nối lại.
    /// Gửi khung xong thì chờ ngắn để Controller đọc được và tự đóng trước (đóng nhẹ nhàng, tránh RST làm mất khung);
    /// nếu nó không đóng kịp thì tự đóng sau tối đa chừng ấy thời gian. Việc chặn kết nối mới đã có hiệu lực ngay ở DisconnectAll.
    /// </summary>
    public void DisconnectByOwner()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _channel.SendAsync(SessionMessage.Disconnect, Wire.Build(w => w.Str("The computer's owner ended the session.")), timeout.Token).ConfigureAwait(false);
                // Nếu bên kia đóng trước (đã nhận khung) thì vòng nhận gặp EOF và phiên kết thúc sớm hơn mốc này.
                await Task.Delay(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is SocketException) { }
            finally { Cancel(); }
        });
    }

    public async Task RunAsync()
    {
        var reason = "Ended";
        try
        {
            _monitors = _screen.GetMonitors();
            await _channel.SendAsync(SessionMessage.ScreenInfo, SessionWire.Monitors(_monitors, _monitor), _cts.Token).ConfigureAwait(false);
            var capture = Task.Run(() => CaptureLoopAsync(_cts.Token));
            var receive = ReceiveLoopAsync(_cts.Token);
            var first = await Task.WhenAny(capture, receive).ConfigureAwait(false);
            _cts.Cancel();
            await Task.WhenAll(Quiet(capture), Quiet(receive)).ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { reason = _cts.IsCancellationRequested ? "Disconnected" : reason; }
        catch (CryptoFailureException) { reason = "Session data was modified or replayed"; Log.Warn("Session closed: data authentication failed"); }
        catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is TimeoutException || ex is ProtocolException)
        {
            reason = "Connection lost";
            Log.Info("Session ended: {0}", ex.GetType().Name);
        }
        finally { Close(reason); }
    }

    private static async Task Quiet(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        _transfers.Dispose();
        _input.ReleaseAll();
        _screen.Dispose();
        _channel.Dispose();
        _cts.Dispose();
        Ended?.Invoke(this, reason);
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var blocked = false;
        var lastCapture = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            int monitor, quality, fps, scale;
            lock (_settingsGate) { monitor = _monitor; quality = _quality; fps = _maxFps; scale = _scale; }

            // Kiểm soát luồng: Controller chưa xác nhận kịp thì chờ. Mạng chậm sẽ tự giảm FPS, không tích tụ độ trễ.
            while (Interlocked.Read(ref _sequence) - Interlocked.Read(ref _acked) >= MaxInFlightFrames)
                await _ackSignal.WaitAsync(250, ct).ConfigureAwait(false);

            var wait = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, fps)) - (_clock.Elapsed - lastCapture);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            lastCapture = _clock.Elapsed;

            RawFrame frame;
            try { frame = _screen.Capture(monitor); }
            catch (Exception ex) when (IsTransientCapture(ex))
            {
                // Lỗi GDI/GDI+ thoáng qua (ví dụ màn hình đổi mạnh khi mở hộp thoại): bỏ khung này, không ngắt phiên.
                Log.Debug("Transient capture error: {0}", ex.GetType().Name);
                _encoder.Reset();
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }
            if (frame == null)
            {
                if (!blocked)
                {
                    blocked = true;
                    await _channel.SendAsync(SessionMessage.Notice, Wire.Build(w => w.Str("Cannot capture the screen: the remote computer may be showing UAC or the lock screen, or its Windows session is disconnected from the display.")), ct).ConfigureAwait(false);
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            if (blocked)
            {
                blocked = false;
                _encoder.Reset();
                await _channel.SendAsync(SessionMessage.Notice, Wire.Build(w => w.Str(string.Empty)), ct).ConfigureAwait(false);
            }

            byte[] payload;
            long seq;
            try
            {
                var changes = _encoder.FindChanges(frame);
                if (changes.Count == 0) continue;
                var tiles = new List<FrameTile>();
                foreach (var r in changes.Take(SessionInfo.MaxTiles))
                    tiles.Add(new FrameTile { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Data = FrameEncoder.Encode(frame, r, quality, scale) });
                seq = Interlocked.Increment(ref _sequence);
                var message = new FrameMessage { Sequence = seq, Monitor = monitor, Width = frame.Width, Height = frame.Height, Quality = quality };
                message.Tiles.AddRange(tiles);
                payload = SessionWire.Frame(message);
            }
            catch (Exception ex) when (IsTransientCapture(ex))
            {
                // Lỗi nén GDI+ thoáng qua: bỏ khung, dựng lại bộ so khung, không ngắt phiên.
                Log.Debug("Transient encode error: {0}", ex.GetType().Name);
                _encoder.Reset();
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }
            lock (_sentAt) _sentAt[seq] = _clock.ElapsedMilliseconds;
            await _channel.SendAsync(SessionMessage.Frame, payload, ct).ConfigureAwait(false);
            FramesSent++;
            BytesSent += payload.Length;
        }
        ct.ThrowIfCancellationRequested();
    }

    // Lỗi vẽ/chụp tạm thời không nên ngắt phiên; lỗi kênh/IO thì vẫn để ngắt như cũ.
    private static bool IsTransientCapture(Exception ex) =>
        ex is System.Runtime.InteropServices.ExternalException
        || ex is InvalidOperationException
        || ex is OutOfMemoryException
        || ex is ArgumentException;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // Ping của Controller 2 giây một lần: im lặng lâu hơn nhiều thì coi là mất kết nối.
        var idle = TimeSpan.FromSeconds(20);
        while (true)
        {
            var (type, payload) = await _channel.ReceiveAsync(idle, ct).ConfigureAwait(false);
            switch (type)
            {
                case SessionMessage.FrameAck: OnAck(Wire.Parse(payload, r => r.ReadInt64())); break;
                case SessionMessage.Ping: await _channel.SendAsync(SessionMessage.Pong, payload, ct).ConfigureAwait(false); break;
                case SessionMessage.Mouse: _input.Mouse(CurrentMonitor(), SessionWire.ReadMouse(payload)); break;
                case SessionMessage.Key: _input.Key(SessionWire.ReadKey(payload)); break;
                case SessionMessage.Text:
                    _input.Text(Wire.Parse(payload, r => r.Str()));
                    break;
                case SessionMessage.SpecialKey:
                    var notice = _input.Special((SpecialKey)Wire.Parse(payload, r => r.ReadByte()));
                    if (notice != null) await _channel.SendAsync(SessionMessage.Notice, Wire.Build(w => w.Str(notice)), ct).ConfigureAwait(false);
                    break;
                case SessionMessage.SetMonitor: await OnSetMonitorAsync(Wire.Parse(payload, r => r.ReadInt32()), ct).ConfigureAwait(false); break;
                case SessionMessage.SetQuality: OnSetQuality(payload); break;
                case SessionMessage.Disconnect: return;
                default:
                    if (!await _transfers.HandleAsync(type, payload).ConfigureAwait(false)) throw new ProtocolException("Invalid message from the controlling computer.");
                    break;
            }
        }
    }

    private MonitorInfo CurrentMonitor()
    {
        lock (_settingsGate) return _monitors[Math.Min(_monitor, _monitors.Count - 1)];
    }

    /// <summary>Tự điều chỉnh chất lượng theo thời gian xác nhận khung hình (Phần 5.1).</summary>
    private void OnAck(long sequence)
    {
        long sent;
        lock (_sentAt)
        {
            if (!_sentAt.TryGetValue(sequence, out sent)) return;
            _sentAt.Remove(sequence);
        }
        Interlocked.Exchange(ref _acked, Math.Max(Interlocked.Read(ref _acked), sequence));
        try { _ackSignal.Release(); } catch (ObjectDisposedException) { }
        var rtt = _clock.ElapsedMilliseconds - sent;
        lock (_settingsGate)
        {
            if (!_autoQuality) return;
            if (rtt > 400) _quality = Math.Max(MinQuality, _quality - 5);
            else if (rtt < 120) _quality = Math.Min(MaxAutoQuality, _quality + 1);
        }
    }

    private async Task OnSetMonitorAsync(int index, CancellationToken ct)
    {
        var monitors = _screen.GetMonitors();
        lock (_settingsGate)
        {
            _monitors = monitors;
            _monitor = Math.Max(0, Math.Min(index, monitors.Count - 1));
        }
        _encoder.Reset();
        await _channel.SendAsync(SessionMessage.ScreenInfo, SessionWire.Monitors(monitors, _monitor), ct).ConfigureAwait(false);
    }

    private void OnSetQuality(byte[] payload)
    {
        var (quality, fps, scale) = Wire.Parse(payload, r => (r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        lock (_settingsGate)
        {
            _autoQuality = quality <= 0;
            if (quality > 0) _quality = Math.Min(100, quality);
            _maxFps = Math.Max(1, Math.Min(60, fps));
            var newScale = Math.Max(25, Math.Min(100, scale));
            if (newScale != _scale) { _scale = newScale; _encoder.Reset(); }
        }
    }
}

/// <summary>Nhận kết nối, xác thực, giữ danh sách phiên. Nút tắt khẩn cấp của chủ máy đi qua lớp này.</summary>
public sealed class SessionManager : IDisposable
{
    /// <summary>Số phiên tối đa cùng lúc vào máy này. Cho phép nhiều người điều khiển một máy (như TeamViewer);
    /// vẫn có trần để một cơn kết nối dồn dập không làm quá tải CPU/băng thông của máy bị điều khiển.</summary>
    public const int DefaultMaxSessions = 10;
    public int MaxSessions { get; set; } = DefaultMaxSessions;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly ClientState _state;
    private readonly ClientAuthentication _auth;
    private readonly Func<(IScreenSource screen, IInputSink input)> _ioFactory;
    private readonly List<HostSession> _sessions = new List<HostSession>();
    private readonly object _gate = new object();
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private TcpListener _listener;
    private LanDiscovery.Responder _discovery;
    /// <summary>Cổng UDP tìm máy; kiểm thử dùng cổng riêng.</summary>
    public int DiscoveryPort { get; set; } = LanDiscovery.Port;
    private int _pendingHandshakes;
    private int _asking;
    private DateTime _nextAskUtc = DateTime.MinValue;
    /// <summary>Sau mỗi lần bị từ chối phải chờ: kẻ không có mật khẩu không thể liên tục hiện hộp thoại quấy rối chủ máy.</summary>
    public static readonly TimeSpan AskCooldown = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OwnerBlock = TimeSpan.FromSeconds(60);
    private DateTime _ownerBlockUntilUtc = DateTime.MinValue;

    /// <summary>Hỏi chủ máy trên màn hình. Trả về true nếu chấp nhận.</summary>
    public Func<ControllerPeer, CancellationToken, Task<bool>> AskOwner { get; set; }
    public event Action Changed;
    public event Action<string> Activity;

    public int ListeningPort { get; private set; }

    public SessionManager(ClientState state, ClientAuthentication auth, Func<(IScreenSource screen, IInputSink input)> ioFactory)
    {
        _state = state; _auth = auth; _ioFactory = ioFactory;
    }

    public IReadOnlyList<HostSession> Sessions { get { lock (_gate) return _sessions.ToList(); } }

    /// <summary>Địa chỉ lắng nghe: mặc định mọi card mạng; RDM_SESSION_BIND giới hạn lại khi cần, ví dụ 127.0.0.1 để thử nghiệm.</summary>
    public static IPAddress BindAddress()
    {
        var value = Environment.GetEnvironmentVariable("RDM_SESSION_BIND");
        return IPAddress.TryParse(value, out var ip) ? ip : IPAddress.Any;
    }

    public void StartListening()
    {
        lock (_gate)
        {
            if (_listener != null || !_state.AccessEnabled) return;
            var listener = new TcpListener(BindAddress(), _state.SessionPort);
            listener.Start(8);
            _listener = listener;
            ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(listener));
            // Không mở được cổng tìm máy thì vẫn kết nối được bằng IP: không coi là lỗi.
            try { _discovery = new LanDiscovery.Responder(BindAddress(), DiscoveryPort, () => _state.RemoteId, () => ListeningPort); }
            catch (SocketException ex) { Log.Info("Could not open the discovery port: {0}", ex.SocketErrorCode); }
        }
        Log.Info("Listening for sessions on port {0}", ListeningPort);
        Changed?.Invoke();
    }

    public void StopListening()
    {
        lock (_gate)
        {
            _listener?.Stop();
            _listener = null;
            _discovery?.Dispose();
            _discovery = null;
            ListeningPort = 0;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Công tắc khẩn cấp của chủ máy. Ngắt ngay mọi phiên, và chặn kết nối mới một lúc,
    /// để bên kia không thể lập tức vào lại, kể cả khi đang bật Unattended.
    /// </summary>
    public void DisconnectAll()
    {
        _ownerBlockUntilUtc = DateTime.UtcNow + OwnerBlock;
        foreach (var s in Sessions) s.DisconnectByOwner();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; }

            // Giới hạn số bắt tay đồng thời để không bị làm ngập.
            if (Interlocked.Increment(ref _pendingHandshakes) > 4)
            {
                Interlocked.Decrement(ref _pendingHandshakes);
                tcp.Close();
                continue;
            }
            tcp.NoDelay = true;
            var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?";
            _ = Task.Run(() => HandleConnectionAsync(new FramedStream(tcp.GetStream(), tcp), remote));
        }
    }

    /// <summary>
    /// Relay báo có người muốn kết nối: vào đường chuyển tiếp rồi xử lý y như kết nối trực tiếp, gồm mật khẩu và chủ máy chấp nhận.
    /// Chỉ cần kết nối ra ngoài nên đi được qua NAT và tường lửa của máy này.
    /// </summary>
    public void AcceptRelayed(RelayEndpoint relay, byte[] token)
    {
        if (!_state.AccessEnabled || _shutdown.IsCancellationRequested) return;
        if (Interlocked.Increment(ref _pendingHandshakes) > 4)
        {
            Interlocked.Decrement(ref _pendingHandshakes);
            return;
        }
        _ = Task.Run(async () =>
        {
            FramedStream stream;
            try { stream = await RelayJoin.ConnectAsync(relay, token, _shutdown.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ProtocolException || ex is TimeoutException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is AuthenticationException)
            {
                Interlocked.Decrement(ref _pendingHandshakes);
                Log.Info("Could not join the relayed session: {0}", ex.GetType().Name);
                return;
            }
            await HandleConnectionAsync(stream, "Relay").ConfigureAwait(false);
        });
    }

    private async Task HandleConnectionAsync(FramedStream stream, string remote)
    {
        HandshakeResult result = null;
        try
        {
            using var identity = DeviceIdentity.FromSeed(_state.IdentitySeed);
            result = await SessionHandshake.AcceptAsync(stream, new HostHandshakeOptions
            {
                Identity = identity,
                RemoteId = _state.RemoteId,
                ProofParameters = () => _auth.TryGetProofParameters(out var salt, out var iterations) ? (salt, iterations) : ((byte[], int)?)null,
                Authorize = AuthorizeAsync
            }, _shutdown.Token).ConfigureAwait(false);
        }
        catch (SessionRejectedException ex)
        {
            Activity?.Invoke($"Refused connection from {remote}: {ex.Message}");
            return;
        }
        catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ProtocolException || ex is TimeoutException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is CryptoFailureException || ex is HostVerificationException)
        {
            Log.Info("Handshake failed from {0}: {1}", remote, ex.GetType().Name);
            stream.Dispose();
            return;
        }
        finally { Interlocked.Decrement(ref _pendingHandshakes); }

        var (screen, input) = _ioFactory();
        var session = new HostSession(result.Channel, result.Controller, screen, input, _shutdown.Token);
        session.Ended += OnSessionEnded;
        lock (_gate) _sessions.Add(session);
        Activity?.Invoke($"Session started by {result.Controller.Username} via {remote}");
        Changed?.Invoke();
        await session.RunAsync().ConfigureAwait(false);
    }

    private async Task<SessionDecline> AuthorizeAsync(byte[] transcript, byte[] proof, ControllerPeer peer, CancellationToken ct)
    {
        if (DateTime.UtcNow < _ownerBlockUntilUtc) return SessionDecline.OwnerRejected;
        if (Sessions.Count >= MaxSessions) return SessionDecline.Busy;
        switch (_auth.Evaluate(transcript, proof))
        {
            case AccessDecision.DeniedDisabled: return SessionDecline.AccessDisabled;
            case AccessDecision.DeniedLocked: return SessionDecline.Locked;
            case AccessDecision.DeniedBadPassword: return SessionDecline.BadPassword;
            case AccessDecision.Allowed: return SessionDecline.None;
        }
        var ask = AskOwner;
        if (ask == null || DateTime.UtcNow < _nextAskUtc) return SessionDecline.OwnerRejected;
        if (Interlocked.Exchange(ref _asking, 1) != 0) return SessionDecline.Busy;
        try
        {
            var accepted = await ask(peer, ct).ConfigureAwait(false);
            if (!accepted) _nextAskUtc = DateTime.UtcNow + AskCooldown;
            return accepted ? SessionDecline.None : SessionDecline.OwnerRejected;
        }
        finally { Interlocked.Exchange(ref _asking, 0); }
    }

    private void OnSessionEnded(HostSession s, string reason)
    {
        lock (_gate) _sessions.Remove(s);
        Activity?.Invoke($"Session of {s.ControllerUser} ended: {reason}");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        StopListening();
        DisconnectAll();
    }
}
