using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Controller.Services;

/// <summary>
/// Phía Controller của một phiên. Mạng và giải nén chạy ở luồng nền, UI chỉ đọc ảnh mới nhất nên không bao giờ bị treo vì mạng chậm.
/// Lệnh gửi đi đi qua hàng đợi: lệnh di chuột liền nhau được gộp để không tích tụ độ trễ.
/// </summary>
public sealed class RemoteSession : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int MaxQueuedInput = 5000;

    private readonly SecureChannel _channel;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly object _queueGate = new object();
    private readonly List<(SessionMessage type, byte[] payload, bool isMove)> _queue = new List<(SessionMessage, byte[], bool)>();
    private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Bitmap _surface;
    private int _closed;
    private long _frames, _bytes, _lastFrames, _lastBytes;
    private long _lastStatsMs;

    public object SurfaceLock { get; } = new object();
    public Bitmap Surface => _surface;
    public byte[] HostKey { get; }
    /// <summary>Direct hoặc Relay. Cả hai đều là E2EE.</summary>
    public string ConnectionType { get; set; } = "Direct";
    public string HostFingerprint => DeviceIdentity.Fingerprint(HostKey);
    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = new MonitorInfo[0];
    public int CurrentMonitor { get; private set; }
    public double Fps { get; private set; }
    public double KilobytesPerSecond { get; private set; }
    public int LatencyMs { get; private set; } = -1;
    public int Quality { get; private set; }
    public long TotalFrames => Interlocked.Read(ref _frames);
    public bool IsClosed => _closed != 0;

    private readonly SessionTransfers _transfers;

    public event Action MonitorsChanged;
    public event Action FrameReady;
    public event Action<string> Notice;
    /// <summary>Báo đóng phiên. Đối số true là có chủ ý (người dùng hoặc chủ máy ngắt): không được tự kết nối lại. False là mất kết nối bất thường.</summary>
    public event Action<string, bool> Closed;
    public event Action<ChatMessage> ChatReceived;
    public event Action<FileTransferProgress> TransferChanged;

    public Task SendChatAsync(string text) => _transfers.SendChatAsync(text, _cts.Token);
    public Task SendFileAsync(string path) => _transfers.SendFileAsync(path, _cts.Token);
    public Task UploadAsync(string localPath, string remoteDir, bool overwrite = false) => _transfers.SendFileAsync(localPath, remoteDir, overwrite, _cts.Token);
    public Task<FsListing> ListRemoteAsync(string path) => _transfers.ListRemoteAsync(path, _cts.Token);
    public Task RequestDownloadAsync(string remotePath, string localDir) => _transfers.RequestDownloadAsync(remotePath, localDir, _cts.Token);
    public Task<(bool Ok, string Error)> MutateRemoteAsync(FsOp op, string path, string arg2) => _transfers.MutateRemoteAsync(op, path, arg2, _cts.Token);
    public void PauseTransfer(int id) => _transfers.PauseOutgoing(id);
    public void ResumeTransfer(int id) => _transfers.ResumeOutgoing(id);
    public void CancelTransfer(int id) => _transfers.CancelTransfer(id);

    public RemoteSession(HandshakeResult handshake)
    {
        _channel = handshake.Channel;
        HostKey = handshake.PeerKey;
        _transfers = new SessionTransfers((t, p, c) => _channel.SendAsync(t, p, c), SessionTransfers.DefaultReceiveDir(), _cts.Token);
        _transfers.ChatReceived += m => ChatReceived?.Invoke(m);
        _transfers.TransferChanged += p => TransferChanged?.Invoke(p);
    }

    public void Start()
    {
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(ct));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var reason = "The session has ended.";
        var byUser = false;
        var sender = SendLoopAsync(ct);
        var pinger = PingLoopAsync(ct);
        try
        {
            while (true)
            {
                var (type, payload) = await _channel.ReceiveAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                switch (type)
                {
                    case SessionMessage.Frame: await OnFrameAsync(payload, ct).ConfigureAwait(false); break;
                    case SessionMessage.ScreenInfo:
                        Monitors = SessionWire.ReadMonitors(payload, out var current);
                        CurrentMonitor = current;
                        MonitorsChanged?.Invoke();
                        break;
                    case SessionMessage.Notice: Notice?.Invoke(Wire.Parse(payload, r => r.Str())); break;
                    case SessionMessage.Pong:
                        LatencyMs = (int)Math.Max(0, _clock.ElapsedMilliseconds - Wire.Parse(payload, r => r.ReadInt64()));
                        break;
                    case SessionMessage.Disconnect:
                        reason = Wire.Parse(payload, r => r.Str());
                        byUser = true;
                        return;
                    default:
                        if (!await _transfers.HandleAsync(type, payload).ConfigureAwait(false)) throw new ProtocolException("Invalid message from the remote computer.");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { byUser = true; reason = "Disconnected."; }
        catch (CryptoFailureException) { reason = "Session data was modified or replayed. The session was closed."; Log.Warn("Session closed: data authentication failed"); }
        catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is TimeoutException)
        {
            reason = "Lost connection to the remote computer.";
            Log.Info("Session connection lost: {0}", ex.GetType().Name);
        }
        catch (Exception ex) when (ex is ProtocolException || ex is ArgumentException)
        {
            reason = "The remote computer sent invalid data.";
            Log.Warn(ex, "Invalid session data");
        }
        finally
        {
            Shutdown();
            await Quiet(sender).ConfigureAwait(false);
            await Quiet(pinger).ConfigureAwait(false);
            Closed?.Invoke(reason, byUser);
        }
    }

    private static async Task Quiet(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task OnFrameAsync(byte[] payload, CancellationToken ct)
    {
        var frame = SessionWire.ReadFrame(payload);
        lock (SurfaceLock)
        {
            if (_surface == null || _surface.Width != frame.Width || _surface.Height != frame.Height)
            {
                _surface?.Dispose();
                _surface = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppRgb);
            }
            using var g = Graphics.FromImage(_surface);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var t in frame.Tiles)
            {
                using var ms = new MemoryStream(t.Data);
                using var image = Image.FromStream(ms, false, true);
                g.DrawImage(image, new Rectangle(t.X, t.Y, t.Width, t.Height));
            }
        }
        Quality = frame.Quality;
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, payload.Length);
        UpdateStats();
        // Xác nhận sau khi đã giải nén xong: máy đích dùng đó để biết nên gửi nhanh hay chậm.
        await _channel.SendAsync(SessionMessage.FrameAck, Wire.Build(w => w.Write(frame.Sequence)), ct).ConfigureAwait(false);
        FrameReady?.Invoke();
    }

    private void UpdateStats()
    {
        var now = _clock.ElapsedMilliseconds;
        var elapsed = now - _lastStatsMs;
        if (elapsed < 1000) return;
        Fps = (TotalFrames - _lastFrames) * 1000.0 / elapsed;
        var bytes = Interlocked.Read(ref _bytes);
        KilobytesPerSecond = (bytes - _lastBytes) / 1.024 / elapsed;
        _lastFrames = TotalFrames; _lastBytes = bytes; _lastStatsMs = now;
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _channel.SendAsync(SessionMessage.Ping, Wire.Build(w => w.Write(_clock.ElapsedMilliseconds)), ct).ConfigureAwait(false);
            // Màn hình đứng yên thì không có khung mới: vẫn phải giảm số FPS hiển thị về đúng thực tế.
            if (_clock.ElapsedMilliseconds - _lastStatsMs >= 2000) UpdateStats();
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await _queueSignal.WaitAsync(ct).ConfigureAwait(false);
            List<(SessionMessage type, byte[] payload, bool isMove)> batch;
            lock (_queueGate)
            {
                batch = _queue.ToList();
                _queue.Clear();
            }
            foreach (var item in batch)
                await _channel.SendAsync(item.type, item.payload, ct).ConfigureAwait(false);
        }
    }

    private void Enqueue(SessionMessage type, byte[] payload, bool isMove = false)
    {
        if (IsClosed) return;
        lock (_queueGate)
        {
            if (isMove && _queue.Count > 0 && _queue[_queue.Count - 1].isMove)
            {
                _queue[_queue.Count - 1] = (type, payload, true);
                return;
            }
            if (_queue.Count >= MaxQueuedInput) return;
            _queue.Add((type, payload, isMove));
        }
        try { _queueSignal.Release(); }
        catch (ObjectDisposedException) { }
    }

    public void SendMouse(MouseInputData m) => Enqueue(SessionMessage.Mouse, SessionWire.Mouse(m), m.Action == MouseAction.Move);
    public void SendKey(KeyInputData k) => Enqueue(SessionMessage.Key, SessionWire.Key(k));
    public void SendText(string text) => Enqueue(SessionMessage.Text, Wire.Build(w => w.Str(text)));
    public void SendSpecial(SpecialKey key) => Enqueue(SessionMessage.SpecialKey, Wire.Build(w => w.Write((byte)key)));
    public void SelectMonitor(int index) => Enqueue(SessionMessage.SetMonitor, Wire.Build(w => w.Write(index)));
    /// <param name="quality">0 là tự động, 100 là không mất dữ liệu.</param>
    public void SetQuality(int quality, int maxFps, int scalePercent) => Enqueue(SessionMessage.SetQuality, SessionWire.Quality(quality, maxFps, scalePercent));

    public void Disconnect() => Shutdown();

    private void Shutdown()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
        _transfers.Dispose();
        _channel.Dispose();
    }

    public void Dispose()
    {
        Shutdown();
        lock (SurfaceLock)
        {
            _surface?.Dispose();
            _surface = null;
        }
    }
}

/// <summary>Các phiên đang mở trên Controller (Active Sessions).</summary>
public sealed class ActiveSessionInfo
{
    public string RemoteId { get; set; }
    public string DeviceName { get; set; }
    public DateTime StartedUtc { get; set; }
    public string Fingerprint { get; set; }
    public Action Close { get; set; }
    public Func<string> Stats { get; set; }
}

public static class SessionRegistry
{
    private static readonly object Gate = new object();
    private static readonly List<ActiveSessionInfo> Items = new List<ActiveSessionInfo>();
    public static event Action Changed;

    public static IReadOnlyList<ActiveSessionInfo> All { get { lock (Gate) return Items.ToList(); } }
    public static void Add(ActiveSessionInfo item) { lock (Gate) Items.Add(item); Changed?.Invoke(); }
    public static void Remove(ActiveSessionInfo item) { lock (Gate) Items.Remove(item); Changed?.Invoke(); }
    public static ActiveSessionInfo Find(string remoteId) { lock (Gate) return Items.FirstOrDefault(i => i.RemoteId == remoteId); }
    public static void CloseAll() { foreach (var item in All) item.Close(); }
}
