using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.Shared.Network;

/// <summary>
/// Tìm máy trong cùng mạng theo Remote ID, để kết nối nhanh không cần Relay.
/// Chỉ trả lời khi đúng Remote ID nên không dùng để liệt kê thiết bị được.
/// Câu trả lời không được tin: nó chỉ gợi ý địa chỉ, danh tính máy được xác minh bằng khóa trong bắt tay E2EE.
/// </summary>
public static class LanDiscovery
{
    public const int Port = 7445;
    private const string QueryPrefix = "RDM-FIND-1|";
    private const string ReplyPrefix = "RDM-HERE-1|";

    /// <summary>Tìm máy; trả về null nếu không thấy trong thời gian chờ.</summary>
    public static async Task<IPEndPoint> FindAsync(string remoteId, TimeSpan timeout, IEnumerable<IPAddress> targets = null, int port = Port)
    {
        // Bind ngay: không dựa vào lần gửi đầu, vì mọi lần gửi đều có thể lỗi (máy không có mạng) và khi đó không nhận được.
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var query = Encoding.ASCII.GetBytes(QueryPrefix + remoteId);
        foreach (var target in (targets ?? BroadcastAddresses()).ToList())
        {
            try { await udp.SendAsync(query, query.Length, new IPEndPoint(target, port)).ConfigureAwait(false); }
            catch (SocketException) { }
        }
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(left)).ConfigureAwait(false) != receive) return null;
            UdpReceiveResult result;
            try { result = await receive.ConfigureAwait(false); }
            catch (SocketException) { continue; }
            var parts = Encoding.ASCII.GetString(result.Buffer).Split('|');
            if (parts.Length == 3 && parts[0] + "|" == ReplyPrefix && parts[1] == remoteId && int.TryParse(parts[2], out var sessionPort) && sessionPort > 0 && sessionPort <= 65535)
                return new IPEndPoint(result.RemoteEndPoint.Address, sessionPort);
        }
    }

    /// <summary>Địa chỉ broadcast của từng card mạng, vì 255.255.255.255 thường chỉ đi qua một card.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var a in n.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null))
            {
                var ip = a.Address.GetAddressBytes();
                var mask = a.IPv4Mask.GetAddressBytes();
                yield return new IPAddress(new[] { (byte)(ip[0] | ~mask[0]), (byte)(ip[1] | ~mask[1]), (byte)(ip[2] | ~mask[2]), (byte)(ip[3] | ~mask[3]) });
            }
    }

    /// <summary>Trả lời truy vấn trên máy được điều khiển.</summary>
    public sealed class Responder : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly Func<string> _remoteId;
        private readonly Func<int> _sessionPort;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public int BoundPort => ((IPEndPoint)_udp.Client.LocalEndPoint).Port;

        public Responder(IPAddress bind, int port, Func<string> remoteId, Func<int> sessionPort)
        {
            _remoteId = remoteId; _sessionPort = sessionPort;
            _udp = new UdpClient(new IPEndPoint(bind, port));
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var r = await _udp.ReceiveAsync().ConfigureAwait(false);
                    if (r.Buffer.Length > 64) continue;
                    var text = Encoding.ASCII.GetString(r.Buffer);
                    var port = _sessionPort();
                    // Sai Remote ID hoặc đã tắt Remote Access thì im lặng.
                    if (port <= 0 || text != QueryPrefix + _remoteId()) continue;
                    var reply = Encoding.ASCII.GetBytes(ReplyPrefix + _remoteId() + "|" + port);
                    await _udp.SendAsync(reply, reply.Length, r.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
        }
    }
}