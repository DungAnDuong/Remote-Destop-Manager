using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RDM.Shared.Network;
using RDM.Shared.Protocol;

namespace RDM.Server;

/// <summary>
/// Chuyển tiếp phiên giữa hai đầu. Server chỉ chép byte và không có khóa: nội dung là bản mã E2EE của hai đầu.
/// Tuyệt đối không ghi nội dung vào Log. Mỗi phiên có hai vé dùng một lần, mỗi đầu một vé, có hạn ngắn.
/// </summary>
public sealed class RelayManager
{
    private sealed class Peer
    {
        public FramedStream Channel;
        public Stream Stream;
    }

    private sealed class Pair
    {
        public readonly string RequesterId, TargetId;
        public readonly DateTime Expires;
        public Peer Requester, Target;
        public readonly TaskCompletionSource<bool> Both = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> RequesterReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> TargetReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Pair(string requesterId, string targetId, DateTime expires) { RequesterId = requesterId; TargetId = targetId; Expires = expires; }
    }

    private readonly RelayOptions _options;
    private readonly PresenceHub _hub;
    private readonly ClientRegistry _registry;
    private readonly ILogger<RelayManager> _log;
    private readonly ConcurrentDictionary<string, (Pair pair, bool isRequester)> _tickets = new ConcurrentDictionary<string, (Pair, bool)>();
    private int _active;
    private long _bytes;

    public RelayManager(IOptions<RelayOptions> options, PresenceHub hub, ClientRegistry registry, ILogger<RelayManager> log)
    {
        _options = options.Value; _hub = hub; _registry = registry; _log = log;
    }

    public int ActiveSessions => Volatile.Read(ref _active);
    public long RelayedBytes => Interlocked.Read(ref _bytes);

    /// <summary>Trả lời yêu cầu của một thiết bị. Chỉ phát vé khi máy đích Online và cho phép truy cập.</summary>
    public RelayTicket CreateTicket(DeviceSession requester, string targetId, bool wantRelay, out DeviceSession target, out byte[] targetToken)
    {
        target = null; targetToken = null;
        if (!AllowRequest(requester)) return new RelayTicket { Code = TicketCode.RateLimited };
        if (targetId == requester.RemoteId) return new RelayTicket { Code = TicketCode.SameDevice };

        target = _hub.Find(targetId);
        if (target == null) return new RelayTicket { Code = _registry.Exists(targetId) ? TicketCode.Offline : TicketCode.NotFound };
        if (!Volatile.Read(ref target.AccessEnabled)) return new RelayTicket { Code = TicketCode.AccessDisabled };

        var result = new RelayTicket { Code = TicketCode.Ok, LocalIp = target.Info?.LocalIp, SessionPort = target.Info?.SessionPort ?? 0, PublicIp = target.PublicIp };
        if (!wantRelay) return result;

        Purge();
        if (ActiveSessions + _tickets.Count / 2 >= _options.MaxRelaySessions) return new RelayTicket { Code = TicketCode.Busy };
        var pair = new Pair(requester.RemoteId, targetId, DateTime.UtcNow.AddSeconds(_options.TicketSeconds));
        var requesterToken = RandomNumberGenerator.GetBytes(32);
        targetToken = RandomNumberGenerator.GetBytes(32);
        _tickets[Convert.ToHexString(requesterToken)] = (pair, true);
        _tickets[Convert.ToHexString(targetToken)] = (pair, false);
        result.Token = requesterToken;
        _log.LogInformation("Relay ticket issued: {Requester} to {Target}", requester.RemoteId, targetId);
        return result;
    }

    private bool AllowRequest(DeviceSession s)
    {
        lock (s.Requests)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (s.Requests.Count > 0 && s.Requests.Peek() < cutoff) s.Requests.Dequeue();
            if (s.Requests.Count >= _options.SessionRequestsPerMinute) return false;
            s.Requests.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    private void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _tickets.Where(t => t.Value.pair.Expires < now).ToList()) _tickets.TryRemove(item.Key, out _);
    }

    /// <summary>Chạy một đầu của phiên. Trả về false nếu vé không hợp lệ, hết hạn hoặc đã dùng.</summary>
    public async Task<bool> RunPeerAsync(byte[] token, FramedStream channel, Stream stream, CancellationToken ct)
    {
        // TryRemove bảo đảm vé chỉ dùng được một lần, kể cả khi hai kết nối dùng cùng lúc.
        if (!_tickets.TryRemove(Convert.ToHexString(token), out var entry) || entry.pair.Expires < DateTime.UtcNow) return false;
        var pair = entry.pair;
        var me = new Peer { Channel = channel, Stream = stream };
        lock (pair)
        {
            if (entry.isRequester) pair.Requester = me; else pair.Target = me;
            if (pair.Requester != null && pair.Target != null) pair.Both.TrySetResult(true);
        }

        var wait = pair.Expires - DateTime.UtcNow;
        if (await Task.WhenAny(pair.Both.Task, Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, ct)) != pair.Both.Task)
        {
            _log.LogInformation("Relay session expired waiting for the other side: {Requester} to {Target}", pair.RequesterId, pair.TargetId);
            return true;
        }

        var partner = entry.isRequester ? pair.Target : pair.Requester;
        var myReady = entry.isRequester ? pair.RequesterReady : pair.TargetReady;
        try { await channel.SendAsync(MessageType.RelayReady, null, ct); }
        catch
        {
            // Báo cho đầu kia, nếu không nó sẽ chờ mãi và giữ kết nối.
            myReady.TrySetResult(false);
            throw;
        }
        myReady.TrySetResult(true);
        // Chờ cả hai đã nhận RelayReady rồi mới chép, để byte của bên kia không chen vào trước thông điệp này.
        var ready = await Task.WhenAll(pair.RequesterReady.Task, pair.TargetReady.Task);
        if (!ready[0] || !ready[1]) return true;

        if (entry.isRequester)
        {
            Interlocked.Increment(ref _active);
            _log.LogInformation("Relay session started: {Requester} to {Target}", pair.RequesterId, pair.TargetId);
        }
        try { await CopyAsync(stream, partner.Stream, ct); }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is TimeoutException) { }
        finally
        {
            // Một đầu ngắt thì đóng đầu kia ngay, không để phiên treo.
            partner.Channel.Dispose();
            if (entry.isRequester)
            {
                Interlocked.Decrement(ref _active);
                _log.LogInformation("Relay session ended: {Requester} to {Target}", pair.RequesterId, pair.TargetId);
            }
        }
        return true;
    }

    private async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var idle = TimeSpan.FromSeconds(_options.RelayIdleSeconds);
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(idle);
            var n = await from.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
            if (n == 0) return;
            // Cùng thời hạn cho cả ghi: bên nhận cố tình không đọc không được giữ chỗ phiên mãi.
            await to.WriteAsync(buffer, 0, n, timeout.Token);
            await to.FlushAsync(timeout.Token);
            Interlocked.Add(ref _bytes, n);
        }
    }
}