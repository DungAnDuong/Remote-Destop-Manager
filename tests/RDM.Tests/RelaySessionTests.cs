extern alias rdmclient;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RDM.Controller.Services;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;
using Xunit;
using Xunit.Abstractions;

namespace RDM.Tests;

/// <summary>Phiên thật đi qua RDM.Server.exe: máy đích và bên điều khiển chỉ kết nối ra ngoài tới Relay, không nối trực tiếp với nhau.</summary>
public class RelaySessionTests : IClassFixture<RelaySessionTests.Shared>
{
    public sealed class Shared : IDisposable
    {
        public RelayServer Server { get; } = new RelayServer();
        public void Dispose() => Server.Dispose();
    }

    private readonly RelayServer _server;
    private readonly ITestOutputHelper _out;
    public RelaySessionTests(Shared shared, ITestOutputHelper output) { _server = shared.Server; _out = output; }

    /// <summary>Máy đích đăng ký với Relay, nhận yêu cầu qua Relay.</summary>
    private sealed class RelayedHost : IDisposable
    {
        public readonly HostFixture Host = new HostFixture();
        public readonly DeviceRelayClient Device;
        public string RemoteId => Host.State.RemoteId;

        public RelayedHost(RelayServer server, bool accessEnabled = true)
        {
            Device = new DeviceRelayClient(server.Endpoint(), Host.Identity, Host.State.RemoteId, new DeviceInfo { Name = "May dich", LocalIp = "203.0.113.1", SessionPort = 7444 }, accessEnabled);
            Device.IncomingSession += token => Host.Manager.AcceptRelayed(server.Endpoint(), token);
            Device.Start();
        }

        public void Dispose()
        {
            Device.StopAsync().GetAwaiter().GetResult();
            Host.Dispose();
        }
    }

    private sealed class Requester : IDisposable
    {
        public readonly DeviceIdentity Identity = DeviceIdentity.Generate();
        public readonly DeviceRelayClient Device;
        public readonly string RemoteId = RemoteIdGenerator.Generate();

        public Requester(RelayServer server)
        {
            Device = new DeviceRelayClient(server.Endpoint(), Identity, RemoteId, new DeviceInfo { Name = "May dieu khien" }, true);
            Device.Start();
        }

        public void Dispose()
        {
            Device.StopAsync().GetAwaiter().GetResult();
            Identity.Dispose();
        }
    }

    private async Task<RemoteSession> OpenAsync(RelayedHost host, Requester requester, string password = null, Func<byte[], Task<bool>> verify = null)
    {
        await Wait.Until(() => host.Device.IsOnline && requester.Device.IsOnline, "hai máy Online");
        var ticket = await requester.Device.RequestSessionAsync(host.RemoteId, true, CancellationToken.None);
        Assert.Equal(TicketCode.Ok, ticket.Code);
        var stream = await RelayJoin.ConnectAsync(_server.Endpoint(), ticket.Token, CancellationToken.None);
        using var identity = DeviceIdentity.Generate();
        var result = await SessionHandshake.ConnectAsync(stream, new ControllerHandshakeOptions
        {
            Identity = identity, ExpectedRemoteId = host.RemoteId, Username = "nguoi-dung", Password = password,
            VerifyHostKey = verify ?? (k => Task.FromResult(k.SequenceEqual(host.Host.Identity.PublicKey)))
        }, CancellationToken.None);
        return new RemoteSession(result) { ConnectionType = "Relay" };
    }

    [Fact] // Hình ảnh và lệnh điều khiển đi hai chiều qua Relay, không cần mở cổng nào trên máy đích.
    public async Task FullSession_ThroughRelay_ImageAndInput()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        host.Host.Manager.StopListening(); // Máy đích không nhận kết nối trực tiếp: chỉ còn đường Relay.
        using var session = await OpenAsync(host, requester);
        session.Start();
        await Wait.Until(() => session.TotalFrames >= 1, "khung hình qua Relay");
        Assert.Equal(1, host.Host.Asked);
        session.SendText("Qua Relay tiếng Việt");
        await Wait.Until(() => host.Host.Input.Texts.Contains("Qua Relay tiếng Việt"), "chữ tới máy đích");
        host.Host.Screen.Fill(new Rectangle(0, 0, 64, 64), Color.Red);
        var before = session.TotalFrames;
        await Wait.Until(() => session.TotalFrames > before, "khung thay đổi");
    }

    [Fact] // Xác thực chạy nguyên vẹn qua Relay: sai mật khẩu vẫn bị từ chối.
    public async Task Password_IsEnforcedThroughRelay()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        host.Host.Auth.SetPassword(null, "Mat-Khau-Truy-Cap-1");
        host.Host.Auth.SetUnattended(true);
        var ex = await Assert.ThrowsAsync<SessionRejectedException>(() => OpenAsync(host, requester, "sai-mat-khau-123"));
        Assert.Equal(SessionDecline.BadPassword, ex.Reason);
        using var session = await OpenAsync(host, requester, "Mat-Khau-Truy-Cap-1");
        Assert.Equal(0, host.Host.Asked);
    }

    [Fact] // Relay giả mạo khóa máy đích cũng bị chặn: khóa ghim vẫn có hiệu lực khi đi qua Relay.
    public async Task PinnedKey_IsEnforcedThroughRelay()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        await Assert.ThrowsAsync<HostVerificationException>(() => OpenAsync(host, requester, null, k => Task.FromResult(false)));
        Assert.Equal(0, host.Host.Asked);
    }

    [Fact] // Một đầu ngắt thì đầu kia biết ngay.
    public async Task Disconnect_PropagatesBothWays()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        var session = await OpenAsync(host, requester);
        session.Start();
        await Wait.Until(() => host.Host.Manager.Sessions.Count == 1, "phiên mở");
        session.Dispose();
        await Wait.Until(() => host.Host.Manager.Sessions.Count == 0, "máy đích thấy đã ngắt");
    }

    [Fact] // Công tắc khẩn cấp qua Relay: có chủ ý, không tự kết nối lại.
    public async Task OwnerStop_ThroughRelay_IsDeliberate()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        using var session = await OpenAsync(host, requester);
        var closed = new TaskCompletionSource<bool>();
        session.Closed += (r, deliberate) => closed.TrySetResult(deliberate);
        session.Start();
        await Wait.Until(() => host.Host.Manager.Sessions.Count == 1, "phiên mở");
        host.Host.Manager.DisconnectAll();
        Assert.True(await Task.WhenAny(closed.Task, Task.Delay(15000)) == closed.Task, "Phải nhận được tín hiệu đóng");
        Assert.True(closed.Task.Result);
    }

    [Fact] // Nhiều phiên đồng thời không lẫn dữ liệu.
    public async Task ConcurrentSessions_DoNotMixData()
    {
        var pairs = Enumerable.Range(0, 5).Select(_ => (host: new RelayedHost(_server), requester: new Requester(_server))).ToList();
        var sessions = new List<RemoteSession>();
        try
        {
            foreach (var p in pairs) sessions.Add(await OpenAsync(p.host, p.requester));
            foreach (var s in sessions) s.Start();
            await Wait.Until(() => sessions.All(s => s.TotalFrames >= 1), "mọi phiên có hình", 30);
            for (var i = 0; i < pairs.Count; i++) sessions[i].SendText("phien-" + i);
            for (var i = 0; i < pairs.Count; i++)
            {
                var index = i;
                await Wait.Until(() => pairs[index].host.Host.Input.Texts.Count == 1, "chữ tới đúng máy " + index);
                Assert.Equal("phien-" + i, pairs[i].host.Host.Input.Texts.Single());
            }
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
            foreach (var p in pairs) { p.requester.Dispose(); p.host.Dispose(); }
        }
    }

    [Fact]
    public async Task Ticket_ReportsTargetState()
    {
        using var requester = new Requester(_server);
        await Wait.Until(() => requester.Device.IsOnline, "Online");
        Assert.Equal(TicketCode.NotFound, (await requester.Device.RequestSessionAsync("999999999", true, CancellationToken.None)).Code);
        Assert.Equal(TicketCode.SameDevice, (await requester.Device.RequestSessionAsync(requester.RemoteId, true, CancellationToken.None)).Code);

        string offlineId;
        using (var gone = new RelayedHost(_server))
        {
            offlineId = gone.RemoteId;
            await Wait.Until(() => gone.Device.IsOnline, "Online");
        }
        await Wait.Until(async () => (await requester.Device.RequestSessionAsync(offlineId, false, CancellationToken.None)).Code == TicketCode.Offline, "Offline");

        using var off = new RelayedHost(_server, accessEnabled: false);
        await Wait.Until(() => off.Device.IsOnline, "Online");
        await Task.Delay(200);
        var denied = await requester.Device.RequestSessionAsync(off.RemoteId, true, CancellationToken.None);
        Assert.Equal(TicketCode.AccessDisabled, denied.Code);
        Assert.Empty(denied.Token);
    }

    [Fact] // Hỏi trạng thái không làm máy đích kết nối, và trả về địa chỉ để thử trực tiếp trước.
    public async Task QueryWithoutRelay_GivesAddress_AndNoTicket()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        await Wait.Until(() => host.Device.IsOnline && requester.Device.IsOnline, "hai máy Online");
        var incoming = 0;
        host.Device.IncomingSession += _ => Interlocked.Increment(ref incoming);
        var info = await requester.Device.RequestSessionAsync(host.RemoteId, false, CancellationToken.None);
        Assert.Equal(TicketCode.Ok, info.Code);
        Assert.Equal("203.0.113.1", info.LocalIp);
        Assert.Equal(7444, info.SessionPort);
        Assert.Empty(info.Token);
        await Task.Delay(300);
        Assert.Equal(0, incoming);
    }

    [Fact] // Vé dùng một lần: phát lại vé đã dùng bị từ chối.
    public async Task Ticket_IsSingleUse()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        await Wait.Until(() => host.Device.IsOnline && requester.Device.IsOnline, "hai máy Online");
        var ticket = await requester.Device.RequestSessionAsync(host.RemoteId, true, CancellationToken.None);
        using var first = await RelayJoin.ConnectAsync(_server.Endpoint(), ticket.Token, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => RelayJoin.ConnectAsync(_server.Endpoint(), ticket.Token, CancellationToken.None));
    }

    [Fact] // Vé bịa ra không vào được.
    public async Task ForgedTicket_IsRejected()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => RelayJoin.ConnectAsync(_server.Endpoint(), CryptoRandom.GetBytes(32), CancellationToken.None));
    }

    [Fact] // Đường Relay không kết nối khi sai khóa ghim của Relay.
    public async Task Join_RequiresValidRelayCertificate()
    {
        var wrong = new RelayEndpoint { Host = "localhost", Port = _server.Port, PinnedKeySha256 = new string('a', 64) };
        await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(() => RelayJoin.ConnectAsync(wrong, CryptoRandom.GetBytes(32), CancellationToken.None));
    }

    [Fact] // Đo thật: Relay có đủ nhanh để truyền màn hình không. Chỉ trên loopback.
    public async Task Throughput_ThroughRelay_IsMeasured()
    {
        using var a = DeviceIdentity.Generate();
        using var b = DeviceIdentity.Generate();
        var targetId = RemoteIdGenerator.Generate();
        var tokenSource = new TaskCompletionSource<byte[]>();
        var target = new DeviceRelayClient(_server.Endpoint(), a, targetId, new DeviceInfo(), true);
        var req = new DeviceRelayClient(_server.Endpoint(), b, RemoteIdGenerator.Generate(), new DeviceInfo(), true);
        target.IncomingSession += t => tokenSource.TrySetResult(t);
        target.Start(); req.Start();
        try
        {
            await Wait.Until(() => target.IsOnline && req.IsOnline, "hai máy Online");
            var ticket = await req.RequestSessionAsync(targetId, true, CancellationToken.None);
            var targetToken = await tokenSource.Task;
            var joinA = RelayJoin.ConnectAsync(_server.Endpoint(), ticket.Token, CancellationToken.None);
            var joinB = RelayJoin.ConnectAsync(_server.Endpoint(), targetToken, CancellationToken.None);
            using var left = await joinA;
            using var right = await joinB;
            var block = new byte[256 * 1024];
            const int count = 200;
            var sw = Stopwatch.StartNew();
            var reader = Task.Run(async () => { for (var i = 0; i < count; i++) await right.ReceiveAsync(SessionInfo.MaxFrame, TimeSpan.FromSeconds(30), CancellationToken.None); });
            for (var i = 0; i < count; i++) await left.SendAsync(MessageType.Sealed, block, CancellationToken.None);
            await reader;
            var mbps = block.Length * count / 1048576.0 / sw.Elapsed.TotalSeconds;
            _out.WriteLine($"Thông lượng Relay (chưa tính E2EE): {mbps:0} MB/s, 64-bit={Environment.Is64BitProcess}");
            Assert.True(mbps > 10, $"Quá chậm: {mbps:0} MB/s");
        }
        finally
        {
            await target.StopAsync();
            await req.StopAsync();
        }
    }

    [Fact] // Log của Relay không chứa nội dung phiên.
    public async Task RelayLog_HasNoSessionContent()
    {
        using var host = new RelayedHost(_server);
        using var requester = new Requester(_server);
        using var session = await OpenAsync(host, requester);
        session.Start();
        await Wait.Until(() => session.TotalFrames >= 1, "khung hình");
        session.SendText("NOI-DUNG-BI-MAT-QUA-RELAY");
        await Wait.Until(() => host.Host.Input.Texts.Contains("NOI-DUNG-BI-MAT-QUA-RELAY"), "chữ tới nơi");
        Assert.DoesNotContain("NOI-DUNG-BI-MAT-QUA-RELAY", _server.Output);
        Assert.DoesNotContain("nguoi-dung", _server.Output);
        Assert.Contains("Relay session started", _server.Output);
    }
}

public class ManualAddressTests
{
    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", 7444)]
    [InlineData(" 192.168.1.5:8000 ", "192.168.1.5", 8000)]
    [InlineData("may-a", "may-a", 7444)]
    [InlineData("may-a:8000", "may-a", 8000)]
    [InlineData("fe80::1", "fe80::1", 7444)]
    public void Parses(string text, string host, int port)
    {
        Assert.Equal((host, port), ConnectFlow.ParseManualAddress(text));
    }

    [Theory]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("khong hop le")]
    public void RejectsInvalid(string text)
    {
        Assert.Throws<RDM.Data.Services.ValidationException>(() => ConnectFlow.ParseManualAddress(text));
    }
}

public class RelayLimitTests
{
    [Fact] // Chống Flood: giới hạn số yêu cầu mỗi phút cho mỗi thiết bị.
    public async Task SessionRequests_AreRateLimited()
    {
        using var server = new RelayServer(new Dictionary<string, string> { ["Relay__SessionRequestsPerMinute"] = "3" });
        using var id = DeviceIdentity.Generate();
        var device = new DeviceRelayClient(server.Endpoint(), id, RemoteIdGenerator.Generate(), new DeviceInfo(), true);
        device.Start();
        await Wait.Until(() => device.IsOnline, "Online");
        for (var i = 0; i < 3; i++) Assert.Equal(TicketCode.NotFound, (await device.RequestSessionAsync("999999999", false, CancellationToken.None)).Code);
        Assert.Equal(TicketCode.RateLimited, (await device.RequestSessionAsync("999999999", false, CancellationToken.None)).Code);
        await device.StopAsync();
    }

    [Fact] // Giới hạn phiên chuyển tiếp đồng thời trên Server.
    public async Task MaxRelaySessions_IsEnforced()
    {
        using var server = new RelayServer(new Dictionary<string, string> { ["Relay__MaxRelaySessions"] = "1" });
        using var a = DeviceIdentity.Generate();
        using var b = DeviceIdentity.Generate();
        var targetId = RemoteIdGenerator.Generate();
        var target = new DeviceRelayClient(server.Endpoint(), a, targetId, new DeviceInfo(), true);
        var req = new DeviceRelayClient(server.Endpoint(), b, RemoteIdGenerator.Generate(), new DeviceInfo(), true);
        target.Start(); req.Start();
        await Wait.Until(() => target.IsOnline && req.IsOnline, "hai máy Online");
        Assert.Equal(TicketCode.Ok, (await req.RequestSessionAsync(targetId, true, CancellationToken.None)).Code);
        Assert.Equal(TicketCode.Busy, (await req.RequestSessionAsync(targetId, true, CancellationToken.None)).Code);
        await target.StopAsync();
        await req.StopAsync();
    }

    /// <summary>Hai đầu của một đường chuyển tiếp, chưa qua bắt tay E2EE.</summary>
    private static async Task<(FramedStream left, FramedStream right, DeviceRelayClient a, DeviceRelayClient b)> OpenPipeAsync(RelayServer server, DeviceIdentity ida, DeviceIdentity idb)
    {
        var targetId = RemoteIdGenerator.Generate();
        var targetToken = new TaskCompletionSource<byte[]>();
        var target = new DeviceRelayClient(server.Endpoint(), ida, targetId, new DeviceInfo(), true);
        var req = new DeviceRelayClient(server.Endpoint(), idb, RemoteIdGenerator.Generate(), new DeviceInfo(), true);
        target.IncomingSession += t => targetToken.TrySetResult(t);
        target.Start(); req.Start();
        await Wait.Until(() => target.IsOnline && req.IsOnline, "hai máy Online");
        var ticket = await req.RequestSessionAsync(targetId, true, CancellationToken.None);
        var token = await targetToken.Task;
        var joinLeft = RelayJoin.ConnectAsync(server.Endpoint(), ticket.Token, CancellationToken.None);
        var joinRight = RelayJoin.ConnectAsync(server.Endpoint(), token, CancellationToken.None);
        return (await joinLeft, await joinRight, target, req);
    }

    [Fact] // Bên nhận vẫn kết nối nhưng cố tình không đọc: Relay phải đóng phiên, không giữ chỗ mãi.
    public async Task StalledReceiver_IsDisconnected()
    {
        using var server = new RelayServer(new Dictionary<string, string> { ["Relay__RelayIdleSeconds"] = "2" });
        using var ida = DeviceIdentity.Generate();
        using var idb = DeviceIdentity.Generate();
        var (left, right, a, b) = await OpenPipeAsync(server, ida, idb);
        using var stop = new CancellationTokenSource();
        // Chiều ngược lại vẫn có dữ liệu, nên phiên không bị đóng vì rỗi: chỉ còn chiều ghi bị kẹt.
        var keepAlive = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { await right.SendAsync(MessageType.Sealed, new byte[8], CancellationToken.None); await Task.Delay(300); } }
            catch (Exception) { }
        });
        var block = new byte[256 * 1024];
        var sender = Task.Run(async () =>
        {
            while (true) await left.SendAsync(MessageType.Sealed, block, CancellationToken.None);
        });
        try
        {
            Assert.True(await Task.WhenAny(sender, Task.Delay(30000)) == sender, "Relay vẫn giữ phiên có bên nhận không đọc");
            await Assert.ThrowsAnyAsync<Exception>(() => sender);
        }
        finally
        {
            stop.Cancel();
            left.Dispose(); right.Dispose();
            await a.StopAsync(); await b.StopAsync();
        }
    }

    [Fact] // Một đầu bỏ đi khi đã vào đường chuyển tiếp: đầu kia phải biết, không chờ mãi.
    public async Task PartnerLeaves_OtherSideIsClosed()
    {
        using var server = new RelayServer();
        using var ida = DeviceIdentity.Generate();
        using var idb = DeviceIdentity.Generate();
        var (left, right, a, b) = await OpenPipeAsync(server, ida, idb);
        left.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => right.ReceiveAsync(SessionInfo.MaxFrame, TimeSpan.FromSeconds(15), CancellationToken.None));
        right.Dispose();
        await a.StopAsync(); await b.StopAsync();
    }

    [Fact] // Vé hết hạn không dùng được, kể cả khi đang chờ đầu kia.
    public async Task ExpiredTicket_IsRejected()
    {
        using var server = new RelayServer(new Dictionary<string, string> { ["Relay__TicketSeconds"] = "1" });
        using var a = DeviceIdentity.Generate();
        using var b = DeviceIdentity.Generate();
        var targetId = RemoteIdGenerator.Generate();
        var target = new DeviceRelayClient(server.Endpoint(), a, targetId, new DeviceInfo(), true);
        var req = new DeviceRelayClient(server.Endpoint(), b, RemoteIdGenerator.Generate(), new DeviceInfo(), true);
        target.Start(); req.Start();
        await Wait.Until(() => target.IsOnline && req.IsOnline, "hai máy Online");
        var ticket = await req.RequestSessionAsync(targetId, true, CancellationToken.None);
        await Task.Delay(1500);
        await Assert.ThrowsAnyAsync<Exception>(() => RelayJoin.ConnectAsync(server.Endpoint(), ticket.Token, CancellationToken.None));
        await target.StopAsync();
        await req.StopAsync();
    }
}
