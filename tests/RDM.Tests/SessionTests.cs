extern alias rdmclient;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RDM.Controller.Services;
using RDM.Data.Services;
using RDM.Shared.Models;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;
using Xunit;
using Xunit.Abstractions;
using ClientAuthentication = rdmclient::RDM.Client.ClientAuthentication;
using ClientState = rdmclient::RDM.Client.ClientState;
using FrameEncoder = rdmclient::RDM.Client.FrameEncoder;
using GdiScreenCapture = rdmclient::RDM.Client.GdiScreenCapture;
using IInputSink = rdmclient::RDM.Client.IInputSink;
using IScreenSource = rdmclient::RDM.Client.IScreenSource;
using RawFrame = rdmclient::RDM.Client.RawFrame;
using SessionManager = rdmclient::RDM.Client.SessionManager;

namespace RDM.Tests;

/// <summary>Màn hình giả: test không đụng tới màn hình và chuột thật của người chạy test.</summary>
public sealed class FakeScreen : IScreenSource
{
    public const int W = 320, H = 192;
    private readonly byte[] _pixels = new byte[W * H * 4];
    public volatile bool Disposed;
    public volatile bool Blocked;

    public FakeScreen() => Fill(new Rectangle(0, 0, W, H), Color.FromArgb(10, 20, 200));

    public void Fill(Rectangle r, Color c)
    {
        lock (_pixels)
            for (var y = r.Top; y < r.Bottom; y++)
                for (var x = r.Left; x < r.Right; x++)
                {
                    var i = (y * W + x) * 4;
                    _pixels[i] = c.B; _pixels[i + 1] = c.G; _pixels[i + 2] = c.R; _pixels[i + 3] = 255;
                }
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => new[]
    {
        new MonitorInfo { Index = 0, X = 0, Y = 0, Width = W, Height = H, IsPrimary = true },
        new MonitorInfo { Index = 1, X = W, Y = 0, Width = W, Height = H }
    };

    public RawFrame Capture(int monitor)
    {
        if (Blocked) return null;
        lock (_pixels) return new RawFrame { Width = W, Height = H, Stride = W * 4, Pixels = (byte[])_pixels.Clone() };
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Input giả: chỉ ghi lại, không gửi vào hệ điều hành.</summary>
public sealed class RecordingInput : IInputSink
{
    public readonly ConcurrentQueue<MouseInputData> Mice = new ConcurrentQueue<MouseInputData>();
    public readonly ConcurrentQueue<KeyInputData> Keys = new ConcurrentQueue<KeyInputData>();
    public readonly ConcurrentQueue<string> Texts = new ConcurrentQueue<string>();
    public readonly ConcurrentQueue<SpecialKey> Specials = new ConcurrentQueue<SpecialKey>();
    public int Released;
    public void Mouse(MonitorInfo monitor, MouseInputData m) => Mice.Enqueue(m);
    public void Key(KeyInputData k) => Keys.Enqueue(k);
    public void Text(string text) => Texts.Enqueue(text);
    public string Special(SpecialKey key) { Specials.Enqueue(key); return key == SpecialKey.CtrlAltDel ? "Cần Service" : null; }
    public void ReleaseAll() => Interlocked.Increment(ref Released);
}

/// <summary>Bên đứng giữa, ví dụ một Relay: nhìn thấy, sửa, phát lại, xóa được khung tin giữa hai đầu.</summary>
public sealed class FrameProxy : IDisposable
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly int _target;
    private readonly List<IDisposable> _open = new List<IDisposable>();
    public readonly ConcurrentQueue<byte[]> Seen = new ConcurrentQueue<byte[]>();
    /// <summary>Biến đổi khung đi về phía máy đích: trả về các khung sẽ chuyển tiếp.</summary>
    public Func<Frame, int, IEnumerable<Frame>> ToHost = (f, i) => new[] { f };
    public Func<Frame, int, IEnumerable<Frame>> ToController = (f, i) => new[] { f };
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FrameProxy(int targetPort)
    {
        _target = targetPort;
        _listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    var server = new TcpClient();
                    await server.ConnectAsync(IPAddress.Loopback, _target);
                    var a = new FramedStream(client.GetStream(), client);
                    var b = new FramedStream(server.GetStream(), server);
                    lock (_open) { _open.Add(a); _open.Add(b); }
                    _ = Pump(a, b, () => ToHost);
                    _ = Pump(b, a, () => ToController);
                }
            }
            catch (Exception) { }
        });
    }

    private async Task Pump(FramedStream from, FramedStream to, Func<Func<Frame, int, IEnumerable<Frame>>> transform)
    {
        var index = 0;
        try
        {
            while (true)
            {
                var f = await from.ReceiveAsync(SessionInfo.MaxFrame, TimeSpan.FromSeconds(30), CancellationToken.None);
                Seen.Enqueue(f.Payload);
                foreach (var o in transform()(f, index++)) await to.SendAsync(o.Type, o.Payload, CancellationToken.None);
            }
        }
        catch (Exception) { from.Dispose(); to.Dispose(); }
    }

    public void Dispose()
    {
        _listener.Stop();
        lock (_open) foreach (var o in _open) o.Dispose();
    }
}

/// <summary>Một máy đích chạy SessionManager thật trên loopback, với màn hình và input giả.</summary>
public sealed class HostFixture : IDisposable
{
    public readonly TempStore Temp = new TempStore();
    public readonly ClientState State;
    public readonly ClientAuthentication Auth;
    public readonly SessionManager Manager;
    public readonly FakeScreen Screen = new FakeScreen();
    public readonly RecordingInput Input = new RecordingInput();
    public readonly DeviceIdentity Identity;
    public int Asked;
    public bool OwnerAnswer = true;

    public HostFixture()
    {
        Environment.SetEnvironmentVariable("RDM_SESSION_BIND", "127.0.0.1");
        State = Temp.Store.LoadOrCreate();
        State.SessionPort = 0;
        Auth = new ClientAuthentication(Temp.Store, State, 1000);
        Identity = DeviceIdentity.FromSeed(State.IdentitySeed);
        Manager = new SessionManager(State, Auth, () => ((IScreenSource)Screen, (IInputSink)Input))
        {
            DiscoveryPort = 0,
            AskOwner = (peer, ct) => { Interlocked.Increment(ref Asked); return Task.FromResult(OwnerAnswer); }
        };
        Manager.StartListening();
    }

    public void Dispose()
    {
        Manager.Dispose();
        Identity.Dispose();
        Temp.Dispose();
    }

    public async Task<HandshakeResult> ConnectAsync(string password = null, int? port = null, Func<byte[], Task<bool>> verify = null, string username = "nguoi-dung", string expectedId = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port ?? Manager.ListeningPort);
        var stream = new FramedStream(tcp.GetStream(), tcp);
        using var controller = DeviceIdentity.Generate();
        try
        {
            return await SessionHandshake.ConnectAsync(stream, new ControllerHandshakeOptions
            {
                Identity = controller, ExpectedRemoteId = expectedId ?? State.RemoteId, Username = username, Password = password,
                VerifyHostKey = verify ?? (k => Task.FromResult(k.SequenceEqual(Identity.PublicKey)))
            }, CancellationToken.None);
        }
        catch { stream.Dispose(); throw; }
    }
}

public class SessionEndToEndTests
{
    private const string Pw = "Mat-Khau-Truy-Cap-1";

    [Fact] // Hình ảnh: khung đầu tiên toàn màn hình, tiếp theo chỉ vùng thay đổi, đứng yên thì không gửi.
    public async Task Screen_FirstFrameFull_ThenOnlyChanges_ThenNothingWhenStatic()
    {
        using var host = new HostFixture();
        using var session = new RemoteSession(await host.ConnectAsync());
        session.Start();
        await Wait.Until(() => session.TotalFrames >= 1, "khung đầu tiên");
        Assert.Equal(2, session.Monitors.Count);
        lock (session.SurfaceLock)
        {
            Assert.Equal(FakeScreen.W, session.Surface.Width);
            var p = session.Surface.GetPixel(100, 100);
            Assert.InRange(p.B, 180, 220); Assert.InRange(p.R, 0, 30);
        }

        await Task.Delay(800);
        var idle = session.TotalFrames;
        await Task.Delay(800);
        Assert.Equal(idle, session.TotalFrames);

        host.Screen.Fill(new Rectangle(10, 10, 20, 20), Color.FromArgb(240, 20, 20));
        await Wait.Until(() => session.TotalFrames > idle, "khung có thay đổi");
        // JPEG có lấy mẫu màu thấp nên không kỳ vọng đúng tuyệt đối, nhưng phải rõ ràng là đỏ chứ không còn xanh.
        await Wait.Until(() => { lock (session.SurfaceLock) { var p = session.Surface.GetPixel(20, 20); return p.R > 150 && p.B < 120; } }, "vùng đổi đã vẽ");
        lock (session.SurfaceLock) Assert.InRange(session.Surface.GetPixel(200, 150).B, 180, 220);
    }

    [Fact] // Chuột, bàn phím, tiếng Việt và phím đặc biệt tới đúng máy đích, đúng thứ tự.
    public async Task Input_ArrivesInOrder_IncludingVietnamese()
    {
        using var host = new HostFixture();
        using var session = new RemoteSession(await host.ConnectAsync());
        session.Start();
        await Wait.Until(() => session.TotalFrames >= 1, "khung đầu tiên");
        session.SendMouse(new MouseInputData { Action = MouseAction.LeftDown, X = 100, Y = 200 });
        session.SendMouse(new MouseInputData { Action = MouseAction.LeftUp, X = 100, Y = 200 });
        session.SendMouse(new MouseInputData { Action = MouseAction.Wheel, X = 1, Y = 2, WheelDelta = -120 });
        session.SendKey(new KeyInputData { VirtualKey = 0x41, ScanCode = 30, Down = true });
        session.SendKey(new KeyInputData { VirtualKey = 0x41, ScanCode = 30, Down = false });
        session.SendText("Việt Nam ơi");
        session.SendSpecial(SpecialKey.WindowsKey);
        await Wait.Until(() => !host.Input.Specials.IsEmpty, "đủ lệnh");
        Assert.Equal(new[] { MouseAction.LeftDown, MouseAction.LeftUp, MouseAction.Wheel }, host.Input.Mice.Select(m => m.Action));
        Assert.Equal(-120, host.Input.Mice.Last().WheelDelta);
        Assert.Equal(new[] { true, false }, host.Input.Keys.Select(k => k.Down));
        Assert.Equal("Việt Nam ơi", host.Input.Texts.Single());
    }

    [Fact] // Di chuột nhanh không tích tụ, và không làm mất lệnh click.
    public async Task MouseMoves_AreCoalesced_ClicksPreserved()
    {
        using var host = new HostFixture();
        using var session = new RemoteSession(await host.ConnectAsync());
        // Xếp hàng trước khi vòng gửi chạy, để chắc chắn các lệnh nằm cạnh nhau trong hàng đợi.
        for (var i = 0; i < 1000; i++) session.SendMouse(new MouseInputData { Action = MouseAction.Move, X = i, Y = i });
        session.SendMouse(new MouseInputData { Action = MouseAction.LeftDown, X = 7, Y = 7 });
        session.Start();
        await Wait.Until(() => host.Input.Mice.Any(m => m.Action == MouseAction.LeftDown), "click");
        var moves = host.Input.Mice.Where(m => m.Action == MouseAction.Move).ToList();
        Assert.Single(moves);
        Assert.Equal(999, moves[0].X);
    }

    [Fact] // Kill switch: chủ máy ngắt thì Controller biết ngay và input dừng; phím đang giữ được thả.
    public async Task OwnerDisconnectAll_EndsSession_AndReleasesInput()
    {
        using var host = new HostFixture();
        using var session = new RemoteSession(await host.ConnectAsync());
        var closed = new TaskCompletionSource<(string reason, bool deliberate)>();
        session.Closed += (r, u) => closed.TrySetResult((r, u));
        session.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên đã mở");
        host.Manager.DisconnectAll();
        await Wait.Until(() => host.Manager.Sessions.Count == 0, "phiên đã đóng");
        Assert.True(await Task.WhenAny(closed.Task, Task.Delay(15000)) == closed.Task, "Controller phải nhận được tín hiệu đóng phiên");
        // Có chủ ý: Controller không được tự kết nối lại, nếu không công tắc khẩn cấp sẽ vô tác dụng.
        Assert.True(closed.Task.Result.deliberate, "Chủ máy ngắt phải được báo là có chủ ý");
        Assert.Contains("owner", closed.Task.Result.reason);
        Assert.True(host.Input.Released >= 1);
        Assert.True(host.Screen.Disposed);
    }

    [Fact] // Sau khi chủ máy ngắt, không ai vào lại được ngay, kể cả có mật khẩu đúng và Unattended.
    public async Task AfterOwnerDisconnect_UnattendedCannotReconnectImmediately()
    {
        using var host = new HostFixture();
        host.Auth.SetPassword(null, Pw);
        host.Auth.SetUnattended(true);
        using var session = new RemoteSession(await host.ConnectAsync(Pw));
        session.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên đã mở");
        host.Manager.DisconnectAll();
        await Wait.Until(() => host.Manager.Sessions.Count == 0, "phiên đã đóng");
        var ex = await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync(Pw));
        Assert.Equal(SessionDecline.OwnerRejected, ex.Reason);
    }

    [Fact] // Tắt Remote Access phải chặn được kết nối mới.
    public async Task StopListening_RefusesNewConnections()
    {
        using var host = new HostFixture();
        var port = host.Manager.ListeningPort;
        host.Manager.StopListening();
        await Assert.ThrowsAnyAsync<SocketException>(() => host.ConnectAsync(port: port));
    }

    [Fact] // Quyết định truy cập đi qua mạng thật.
    public async Task Access_DisabledFlag_IsRejected()
    {
        using var host = new HostFixture();
        host.Auth.SetAccessEnabled(false);
        var ex = await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync());
        Assert.Equal(SessionDecline.AccessDisabled, ex.Reason);
        Assert.Equal(0, host.Asked);
    }

    [Fact] // Mặc định an toàn: không có mật khẩu thì phải hỏi chủ máy.
    public async Task NoPassword_AsksOwner_RejectionRespected_ThenCooldownBlocksHarassment()
    {
        using var host = new HostFixture();
        host.OwnerAnswer = false;
        var ex = await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync());
        Assert.Equal(SessionDecline.OwnerRejected, ex.Reason);
        Assert.Equal(1, host.Asked);
        await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync());
        Assert.Equal(1, host.Asked); // Lần thứ hai trong thời gian chờ không được hiện hộp thoại lên màn hình chủ máy.
    }

    [Fact]
    public async Task Password_Right_Unattended_DoesNotAskOwner()
    {
        using var host = new HostFixture();
        host.Auth.SetPassword(null, Pw);
        host.Auth.SetUnattended(true);
        using var result = (await host.ConnectAsync(Pw)).Channel;
        Assert.Equal(0, host.Asked);
    }

    [Fact]
    public async Task Password_Right_WithoutUnattended_StillAsksOwner()
    {
        using var host = new HostFixture();
        host.Auth.SetPassword(null, Pw);
        using var result = (await host.ConnectAsync(Pw)).Channel;
        Assert.Equal(1, host.Asked);
    }

    [Fact] // Brute Force qua mạng: khóa sau 5 lần, kể cả mật khẩu đúng.
    public async Task BruteForce_OverNetwork_Locks()
    {
        using var host = new HostFixture();
        host.Auth.SetPassword(null, Pw);
        host.Auth.SetUnattended(true);
        for (var i = 0; i < ClientAuthentication.MaxFailedAttempts - 1; i++)
            Assert.Equal(SessionDecline.BadPassword, (await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync("sai-mat-khau-" + i))).Reason);
        Assert.Equal(SessionDecline.Locked, (await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync("sai-nua"))).Reason);
        Assert.Equal(SessionDecline.Locked, (await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync(Pw))).Reason);
    }

    [Fact] // Nhiều người điều khiển cùng vào một máy: mặc định cho phép nhiều phiên.
    public async Task MultipleControllers_AreAllowed()
    {
        using var host = new HostFixture();
        using var first = new RemoteSession(await host.ConnectAsync());
        first.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên 1");
        using var second = new RemoteSession(await host.ConnectAsync());
        second.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 2, "phiên 2");
    }

    [Fact] // Vẫn có thể đặt trần số phiên; vượt trần thì phiên mới bị Busy.
    public async Task SessionsBeyondMax_AreBusy()
    {
        using var host = new HostFixture();
        host.Manager.MaxSessions = 1;
        using var first = new RemoteSession(await host.ConnectAsync());
        first.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên đầu");
        var ex = await Assert.ThrowsAsync<SessionRejectedException>(() => host.ConnectAsync());
        Assert.Equal(SessionDecline.Busy, ex.Reason);
    }

    [Fact]
    public async Task ScreenBlocked_NoticeThenRecovers()
    {
        using var host = new HostFixture();
        using var session = new RemoteSession(await host.ConnectAsync());
        var notices = new ConcurrentQueue<string>();
        session.Notice += n => notices.Enqueue(n);
        session.Start();
        await Wait.Until(() => session.TotalFrames >= 1, "khung đầu");
        host.Screen.Blocked = true;
        await Wait.Until(() => notices.Any(n => n.Contains("UAC")), "thông báo UAC");
        var before = session.TotalFrames;
        host.Screen.Blocked = false;
        await Wait.Until(() => notices.Any(string.IsNullOrEmpty) && session.TotalFrames > before, "hồi phục");
    }

    [Fact] // Monitor Selection.
    public async Task SelectMonitor_IsApplied()
    {
        using var host = new HostFixture();
        using var session = new RemoteSession(await host.ConnectAsync());
        session.Start();
        await Wait.Until(() => session.TotalFrames >= 1, "khung đầu");
        session.SelectMonitor(1);
        await Wait.Until(() => session.CurrentMonitor == 1, "đã đổi monitor");
    }
}

/// <summary>Kiểm thử bảo mật E2EE với kẻ tấn công đứng giữa, đúng mô hình "Relay không đáng tin".</summary>
public class E2eeAttackTests
{
    private static async Task<HandshakeResult> Through(HostFixture host, FrameProxy proxy, string password = null, string username = "BI-MAT-USER", Func<byte[], Task<bool>> verify = null) =>
        await host.ConnectAsync(password, proxy.Port, verify, username);

    [Fact] // Relay chỉ thấy bản mã: tên người dùng, mật khẩu, chữ gõ, hình ảnh đều không đọc được.
    public async Task Eavesdropper_SeesNoPlaintext()
    {
        using var host = new HostFixture();
        const string pw = "Mat-Khau-BI-MAT-Truy-Cap";
        host.Auth.SetPassword(null, pw);
        host.Auth.SetUnattended(true);
        using var proxy = new FrameProxy(host.Manager.ListeningPort);
        using var session = new RemoteSession(await Through(host, proxy, pw));
        session.Start();
        await Wait.Until(() => session.TotalFrames >= 1, "khung đầu");
        session.SendText("TIN-NHAN-BI-MAT");
        await Wait.Until(() => !host.Input.Texts.IsEmpty, "chữ tới nơi");

        var all = proxy.Seen.SelectMany(b => b).ToArray();
        var utf8 = Encoding.GetEncoding(28591).GetString(all);
        foreach (var secret in new[] { "BI-MAT-USER", "TIN-NHAN-BI-MAT", pw }) Assert.DoesNotContain(secret, utf8);
        // Dấu hiệu đầu file JPEG: hình màn hình không được xuất hiện dạng rõ.
        Assert.DoesNotContain("ÿØÿà", utf8);
        // Khóa công khai của máy đích thì được phép thấy: nó không phải bí mật.
        Assert.True(all.Length > 1000);
    }

    [Fact] // MITM: kẻ giữa thay khóa tạm của mình vào thì chữ ký không còn khớp.
    public async Task Mitm_SwappingEphemeralKey_IsDetected()
    {
        using var host = new HostFixture();
        using var proxy = new FrameProxy(host.Manager.ListeningPort);
        proxy.ToController = (f, i) =>
        {
            if (f.Type != MessageType.SessionHelloReply) return new[] { f };
            var bad = (byte[])f.Payload.Clone();
            bad[5] ^= 0xFF; // Nằm trong khóa tạm của máy đích.
            return new[] { new Frame(f.Type, bad) };
        };
        await Assert.ThrowsAsync<HostVerificationException>(() => Through(host, proxy));
    }

    [Fact] // MITM: kẻ giả mạo dùng khóa thật của nó (chữ ký hợp lệ) thì khóa ghim chặn lại, và KHÔNG nhận được bằng chứng mật khẩu.
    public async Task Impostor_WithOwnValidKey_IsBlockedByPin_BeforeAnyProofIsSent()
    {
        using var real = new HostFixture();
        using var impostor = new HostFixture();
        impostor.Auth.SetPassword(null, "Mat-Khau-Cua-Ke-Gia-Mao");
        impostor.State.RemoteId = real.State.RemoteId; // Giả mạo đúng Remote ID.
        using var proxy = new FrameProxy(impostor.Manager.ListeningPort);
        byte[] presented = null;
        await Assert.ThrowsAsync<HostVerificationException>(() => real.ConnectAsync("mat-khau-that", proxy.Port, k =>
        {
            presented = k;
            return Task.FromResult(k.SequenceEqual(real.Identity.PublicKey));
        }));
        Assert.Equal(impostor.Identity.PublicKey, presented);
        Assert.Equal(0, impostor.Asked);
        // Chỉ có Hello và HelloReply đi qua: bước Auth chứa bằng chứng mật khẩu chưa hề được gửi.
        Assert.Equal(2, proxy.Seen.Count);
    }

    [Fact] // Máy trả lời khác Remote ID đang tìm thì phải từ chối.
    public async Task WrongRemoteId_IsRejected()
    {
        using var host = new HostFixture();
        await Assert.ThrowsAsync<HostVerificationException>(() => host.ConnectAsync(expectedId: "999999999"));
    }

    private static async Task<RemoteSession> OpenWith(HostFixture host, FrameProxy proxy)
    {
        var session = new RemoteSession(await Through(host, proxy));
        return session;
    }

    [Fact] // Replay: phát lại đúng gói lệnh đã ghi được.
    public async Task Replay_OfInputCommand_ClosesSession_WithoutExecutingTwice()
    {
        using var host = new HostFixture();
        using var proxy = new FrameProxy(host.Manager.ListeningPort);
        Frame captured = null;
        var armed = false;
        proxy.ToHost = (f, i) =>
        {
            if (!armed || f.Type != MessageType.Sealed || captured != null) return new[] { f };
            captured = f;
            return new[] { f, f }; // Gửi hai lần.
        };
        using var session = await OpenWith(host, proxy);
        session.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên đã mở");
        await Task.Delay(300);
        armed = true;
        session.SendText("CHI-MOT-LAN");
        await Wait.Until(() => host.Manager.Sessions.Count == 0, "phiên bị đóng do Replay");
        Assert.True(host.Input.Texts.Count(t => t == "CHI-MOT-LAN") <= 1);
    }

    [Fact] // Sửa một bit của lệnh: không được thực thi.
    public async Task Tampering_FlipBit_ClosesSession_AndCommandIsNotExecuted()
    {
        using var host = new HostFixture();
        using var proxy = new FrameProxy(host.Manager.ListeningPort);
        var armed = false;
        proxy.ToHost = (f, i) =>
        {
            if (!armed || f.Type != MessageType.Sealed) return new[] { f };
            var bad = (byte[])f.Payload.Clone();
            bad[0] ^= 1;
            return new[] { new Frame(f.Type, bad) };
        };
        using var session = await OpenWith(host, proxy);
        session.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên đã mở");
        await Task.Delay(300);
        armed = true;
        session.SendText("LENH-BI-SUA");
        await Wait.Until(() => host.Manager.Sessions.Count == 0, "phiên bị đóng");
        Assert.Empty(host.Input.Texts);
    }

    [Fact] // Xóa một gói ở giữa: gói sau không còn khớp bộ đếm nên bị phát hiện.
    public async Task DroppedFrame_IsDetected()
    {
        using var host = new HostFixture();
        using var proxy = new FrameProxy(host.Manager.ListeningPort);
        var armed = false; var dropped = false;
        proxy.ToHost = (f, i) =>
        {
            if (armed && !dropped && f.Type == MessageType.Sealed) { dropped = true; return new Frame[0]; }
            return new[] { f };
        };
        using var session = await OpenWith(host, proxy);
        session.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên đã mở");
        await Task.Delay(300);
        armed = true;
        session.SendText("BI-XOA");
        await Task.Delay(100);
        session.SendText("GOI-SAU");
        await Wait.Until(() => host.Manager.Sessions.Count == 0, "phiên bị đóng");
        Assert.DoesNotContain("GOI-SAU", host.Input.Texts);
    }

    [Fact] // Khung không mã hóa chèn vào giữa phiên bị từ chối.
    public async Task InjectedPlainFrame_ClosesSession()
    {
        using var host = new HostFixture();
        using var proxy = new FrameProxy(host.Manager.ListeningPort);
        var armed = false;
        proxy.ToHost = (f, i) => armed ? new[] { new Frame(MessageType.Heartbeat, new byte[1]) } : new[] { f };
        using var session = await OpenWith(host, proxy);
        session.Start();
        await Wait.Until(() => host.Manager.Sessions.Count == 1, "phiên đã mở");
        await Task.Delay(300);
        armed = true;
        session.SendText("x");
        await Wait.Until(() => host.Manager.Sessions.Count == 0, "phiên bị đóng");
    }
}

public class SecureChannelTests
{
    private readonly ITestOutputHelper _out;
    public SecureChannelTests(ITestOutputHelper output) => _out = output;

    private static async Task<(SecureChannel a, SecureChannel b, Action dispose)> PairAsync(long rekeyBytes = SecureChannel.DefaultRekeyBytes)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        listener.Stop();
        byte[] k1 = CryptoRandom.GetBytes(32), k2 = CryptoRandom.GetBytes(32);
        var a = new SecureChannel(new FramedStream(client.GetStream(), client), k1, k2, false, rekeyBytes);
        var b = new SecureChannel(new FramedStream(server.GetStream(), server), k2, k1, true, rekeyBytes);
        return (a, b, () => { a.Dispose(); b.Dispose(); });
    }

    [Fact] // Đổi khóa phiên: dữ liệu vẫn đúng trong suốt quá trình.
    public async Task Rekey_HappensTransparently()
    {
        var (a, b, dispose) = await PairAsync(rekeyBytes: 4096);
        try
        {
            var block = CryptoRandom.GetBytes(1024);
            var receiver = Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    var (type, payload) = await b.ReceiveAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                    Assert.Equal(SessionMessage.Frame, type);
                    Assert.Equal(block, payload);
                }
            });
            for (var i = 0; i < 100; i++) await a.SendAsync(SessionMessage.Frame, block, CancellationToken.None);
            await receiver;
            Assert.True(a.SendRekeys >= 20, "Số lần đổi khóa: " + a.SendRekeys);
            Assert.Equal(a.SendRekeys, b.ReceiveRekeys);
        }
        finally { dispose(); }
    }

    [Fact] // Đo thật: mã hóa có đủ nhanh cho truyền màn hình không.
    public async Task Throughput_IsMeasured()
    {
        var (a, b, dispose) = await PairAsync();
        try
        {
            var block = new byte[256 * 1024];
            const int count = 200;
            var sw = Stopwatch.StartNew();
            var receiver = Task.Run(async () => { for (var i = 0; i < count; i++) await b.ReceiveAsync(TimeSpan.FromSeconds(30), CancellationToken.None); });
            for (var i = 0; i < count; i++) await a.SendAsync(SessionMessage.Frame, block, CancellationToken.None);
            await receiver;
            sw.Stop();
            var mbps = block.Length * count / 1048576.0 / sw.Elapsed.TotalSeconds;
            _out.WriteLine($"Thông lượng kênh E2EE: {mbps:0} MB/s, 64-bit={Environment.Is64BitProcess}");
            Assert.True(mbps > 10, $"Quá chậm: {mbps:0} MB/s");
        }
        finally { dispose(); }
    }

    [Fact] // Khóa khác nhau cho mỗi phiên.
    public async Task TwoHandshakes_ProduceIndependentSessions()
    {
        using var host = new HostFixture();
        var first = await host.ConnectAsync();
        var firstId = (byte[])first.SessionId.Clone();
        first.Channel.Dispose();
        await Wait.Until(() => host.Manager.Sessions.Count == 0, "phiên đầu đóng", 30);
        var second = await host.ConnectAsync();
        Assert.NotEqual(firstId, second.SessionId);
        second.Channel.Dispose();
    }
}

public class FrameEncoderTests
{
    private readonly ITestOutputHelper _out;
    public FrameEncoderTests(ITestOutputHelper output) => _out = output;

    private static RawFrame Frame(int w, int h, byte value = 50)
    {
        var f = new RawFrame { Width = w, Height = h, Stride = w * 4, Pixels = new byte[w * h * 4] };
        for (var i = 0; i < f.Pixels.Length; i++) f.Pixels[i] = value;
        return f;
    }

    [Fact]
    public void FirstFrame_IsFull_UnchangedIsEmpty()
    {
        var e = new FrameEncoder();
        Assert.Equal(new Rectangle(0, 0, 640, 360), e.FindChanges(Frame(640, 360)).Single());
        Assert.Empty(e.FindChanges(Frame(640, 360)));
    }

    [Fact] // Đổi một điểm ảnh chỉ gửi đúng một khối.
    public void OnePixelChange_ReturnsOneTile()
    {
        var e = new FrameEncoder();
        e.FindChanges(Frame(640, 360));
        var f = Frame(640, 360);
        f.Pixels[(100 * 640 + 200) * 4] = 99;
        Assert.Equal(new Rectangle(192, 64, 64, 64), e.FindChanges(f).Single());
        Assert.Empty(e.FindChanges(f));
    }

    [Fact] // Khối ở mép phải và mép dưới không vượt khỏi màn hình.
    public void EdgeTile_IsClippedToScreen()
    {
        var e = new FrameEncoder();
        e.FindChanges(Frame(650, 370));
        var f = Frame(650, 370);
        f.Pixels[(369 * 650 + 649) * 4] = 99;
        Assert.Equal(new Rectangle(640, 320, 10, 50), e.FindChanges(f).Single());
    }

    [Fact]
    public void AdjacentTiles_AreMerged_MostlyChanged_BecomesFullFrame()
    {
        var e = new FrameEncoder();
        e.FindChanges(Frame(640, 640));
        var f = Frame(640, 640);
        f.Pixels[0] = 99; f.Pixels[64 * 4] = 99;
        Assert.Equal(new Rectangle(0, 0, 128, 64), e.FindChanges(f).Single());
        Assert.Equal(new Rectangle(0, 0, 640, 640), e.FindChanges(Frame(640, 640, 7)).Single());
    }

    [Fact]
    public void ResolutionChange_SendsFull()
    {
        var e = new FrameEncoder();
        e.FindChanges(Frame(640, 360));
        Assert.Equal(new Rectangle(0, 0, 800, 600), e.FindChanges(Frame(800, 600)).Single());
    }

    [Theory]
    [InlineData(60, 100)]
    [InlineData(100, 100)]
    [InlineData(60, 50)]
    public void Encode_ProducesDecodableImage(int quality, int scale)
    {
        var f = Frame(200, 100);
        var bytes = FrameEncoder.Encode(f, new Rectangle(64, 0, 64, 64), quality, scale);
        using var image = Image.FromStream(new MemoryStream(bytes));
        Assert.Equal(64 * scale / 100, image.Width);
        Assert.Equal(quality >= 100 ? System.Drawing.Imaging.ImageFormat.Png.Guid : System.Drawing.Imaging.ImageFormat.Jpeg.Guid, image.RawFormat.Guid);
    }

    [Fact] // Đúng kích thước nếu có màn hình; không có màn hình (phiên bị ngắt) thì trả về null, tuyệt đối không ném lỗi.
    public void GdiCapture_NeverThrows_EvenWithoutADisplay()
    {
        using var capture = new GdiScreenCapture();
        var monitors = capture.GetMonitors();
        for (var i = 0; i < 5; i++)
        {
            var frame = capture.Capture(0);
            if (frame == null) continue;
            Assert.Equal(monitors[0].Width, frame.Width);
            Assert.Equal(monitors[0].Height, frame.Height);
        }
    }
}

/// <summary>Kết nối nhanh kiểu TeamViewer: không đăng nhập nhưng không được vượt quyền.</summary>
public class QuickSessionTests
{
    [Fact]
    public void QuickSession_CanOnlyQuickConnect_NotReadSecretsOrManage()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.AddClient("PC", "123456789", "mat-khau-da-luu");
        db.Rt.Users.Logout();
        db.Rt.Session.BeginQuickSession("nguoi-dung-windows");
        var rt = db.Rt;

        Assert.True(rt.Session.Can(Permission.QuickConnect));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Clients.UseCredential(id));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Clients.Delete(id));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Users.GetAll());
        Assert.Throws<UnauthorizedAccessException>(() => rt.KeyRotation.Rotate(KeyPurpose.Database));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Settings.Set(SettingKeys.RelayHost, "evil"));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Backup.Backup(Path.Combine(db.Dir, "x.rdmbak"), "Backup-Password-1"));
        Assert.Throws<UnauthorizedAccessException>(() => rt.HostKeys.RemovePin("123456789"));

        // Ghim khóa thiết bị mới thì được: cần cho lần kết nối đầu.
        using var key = DeviceIdentity.Generate();
        rt.HostKeys.Pin("987654321", key.PublicKey);
        Assert.Contains(rt.AuditLogs.Query(null, 5), a => a.EventType == AuditEvents.HostKeyPinned && a.Username == "windows:nguoi-dung-windows");
    }

    [Fact] // Bản Managed đồng bộ cấu hình Relay đóng gói lúc khởi động, khi mới chỉ có quick session quyền Viewer:
           // không được ném "no permission" (nếu không sẽ hiện hộp thoại lỗi và bỏ dở việc ghi -> trạng thái Unknown).
    public void ApplyManagedRelay_WorksInQuickSession_AndPersists()
    {
        using var db = new TestDb();
        db.Rt.Session.BeginQuickSession("u");
        var rt = db.Rt;
        Assert.False(rt.Session.Can(Permission.ManageSettings));

        rt.Settings.ApplyManagedRelay("relay.example.net", 31408, "pin-value", "enroll-token");

        Assert.Equal("relay.example.net", rt.Settings.Get(SettingKeys.RelayHost));
        Assert.Equal("31408", rt.Settings.Get(SettingKeys.RelayPort));
        Assert.Equal("pin-value", rt.Settings.Get(SettingKeys.RelayPin));
        Assert.Equal("enroll-token", rt.Settings.Get(SettingKeys.RelayToken)); // Get tự giải mã token.
    }

    [Fact] // Đăng nhập RDM với vai trò Viewer không được tự có thêm quyền kết nối.
    public void ViewerLogin_CannotQuickConnect_ButLogoutReturnsToQuickSession()
    {
        using var db = new TestDb().LoginAdmin();
        db.LoginAs("viewer", "Viewer-Password-1", UserRole.Viewer);
        Assert.False(db.Rt.Session.IsQuickSession);
        Assert.False(db.Rt.Session.Can(Permission.QuickConnect));
        db.Rt.Users.Logout();
        db.Rt.Session.BeginQuickSession("u");
        Assert.True(db.Rt.Session.Can(Permission.QuickConnect));
        Assert.Equal(LoginOutcome.Success, db.Rt.Users.Login("admin", TestDb.AdminPassword));
        Assert.False(db.Rt.Session.IsQuickSession);
        Assert.True(db.Rt.Session.Can(Permission.ManageSecurity));
    }
}

public class LanDiscoveryTests
{
    private static LanDiscovery.Responder Start(string remoteId, Func<int> port) => new LanDiscovery.Responder(IPAddress.Loopback, 0, () => remoteId, port);

    [Fact]
    public async Task FindsRightId_ReturnsSessionPort()
    {
        using var r = Start("123456789", () => 7444);
        var found = await LanDiscovery.FindAsync("123456789", TimeSpan.FromSeconds(3), new[] { IPAddress.Loopback }, r.BoundPort);
        Assert.Equal(7444, found.Port);
        Assert.Equal(IPAddress.Loopback, found.Address);
    }

    [Fact] // Mọi lần gửi đều lỗi (máy không có mạng) thì trả về không tìm thấy, không được ném lỗi socket chưa bind.
    public async Task AllSendsFail_ReturnsNull()
    {
        Assert.Null(await LanDiscovery.FindAsync("123456789", TimeSpan.FromMilliseconds(300), new IPAddress[0], 1));
    }

    [Fact] // Không dùng để dò thiết bị được: sai ID thì im lặng.
    public async Task WrongId_GetsNoAnswer()
    {
        using var r = Start("123456789", () => 7444);
        Assert.Null(await LanDiscovery.FindAsync("111111111", TimeSpan.FromMilliseconds(500), new[] { IPAddress.Loopback }, r.BoundPort));
    }

    [Fact] // Đã tắt Remote Access thì máy không xuất hiện.
    public async Task NotListening_GetsNoAnswer()
    {
        using var r = Start("123456789", () => 0);
        Assert.Null(await LanDiscovery.FindAsync("123456789", TimeSpan.FromMilliseconds(500), new[] { IPAddress.Loopback }, r.BoundPort));
    }

    [Fact] // Dữ liệu rác không làm dừng bộ trả lời.
    public async Task Garbage_DoesNotStopResponder()
    {
        using var r = Start("123456789", () => 7444);
        using (var udp = new UdpClient())
        {
            var random = new Random(9);
            for (var i = 0; i < 200; i++)
            {
                var b = new byte[random.Next(0, 400)];
                random.NextBytes(b);
                await udp.SendAsync(b, b.Length, new IPEndPoint(IPAddress.Loopback, r.BoundPort));
            }
        }
        Assert.NotNull(await LanDiscovery.FindAsync("123456789", TimeSpan.FromSeconds(3), new[] { IPAddress.Loopback }, r.BoundPort));
    }

    [Fact] // Kẻ giả mạo trả lời UDP cũng chỉ dẫn được tới bắt tay E2EE, nơi khóa ghim chặn lại.
    public async Task SpoofedAnswer_IsStoppedByPinnedKey()
    {
        using var real = new HostFixture();
        using var impostor = new HostFixture();
        impostor.State.RemoteId = real.State.RemoteId;
        using var fake = Start(real.State.RemoteId, () => impostor.Manager.ListeningPort);
        var found = await LanDiscovery.FindAsync(real.State.RemoteId, TimeSpan.FromSeconds(3), new[] { IPAddress.Loopback }, fake.BoundPort);
        Assert.Equal(impostor.Manager.ListeningPort, found.Port);
        await Assert.ThrowsAsync<HostVerificationException>(() => real.ConnectAsync("mat-khau-that", found.Port, k => Task.FromResult(k.SequenceEqual(real.Identity.PublicKey))));
        Assert.Equal(0, impostor.Asked);
    }
}

public class SessionWireTests
{
    [Theory] // Tọa độ ảnh nằm ngoài màn hình không được chấp nhận.
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(95, 0, 10, 10)]
    [InlineData(0, 95, 10, 10)]
    [InlineData(int.MaxValue, 0, 10, 10)]
    public void Frame_TileOutsideScreen_IsRejected(int x, int y, int w, int h)
    {
        var f = new FrameMessage { Sequence = 1, Width = 100, Height = 100, Tiles = { new FrameTile { X = x, Y = y, Width = w, Height = h, Data = new byte[3] } } };
        Assert.Throws<ProtocolException>(() => SessionWire.ReadFrame(SessionWire.Frame(f)));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 70000)]
    public void Mouse_OutOfRange_IsRejected(int x, int y) =>
        Assert.Throws<ProtocolException>(() => SessionWire.ReadMouse(SessionWire.Mouse(new MouseInputData { X = x, Y = y })));

    [Fact]
    public void Key_InvalidVirtualKey_IsRejected() =>
        Assert.Throws<ProtocolException>(() => SessionWire.ReadKey(SessionWire.Key(new KeyInputData { VirtualKey = 999 })));

    [Fact] // Dữ liệu rác chỉ gây ProtocolException.
    public void Parsers_RandomGarbage_OnlyProtocolException()
    {
        var random = new Random(4242);
        for (var i = 0; i < 3000; i++)
        {
            var b = new byte[random.Next(0, 200)];
            random.NextBytes(b);
            try { SessionWire.ReadFrame(b); } catch (ProtocolException) { }
            try { SessionWire.ReadMonitors(b, out _); } catch (ProtocolException) { }
            try { SessionWire.ReadMouse(b); } catch (ProtocolException) { }
            try { SessionWire.ReadKey(b); } catch (ProtocolException) { }
        }
    }
}

public class HostKeyServiceTests
{
    [Fact]
    public void ControllerIdentity_IsStable_AndStoredEncrypted()
    {
        using var db = new TestDb().LoginAdmin();
        using var a = db.Rt.HostKeys.GetControllerIdentity();
        using var b = db.Rt.HostKeys.GetControllerIdentity();
        Assert.Equal(a.PublicKey, b.PublicKey);
        Assert.DoesNotContain(Convert.ToBase64String(a.ExportSeed()), db.RawDatabaseAsLatin1());
    }

    [Fact] // Xoay khóa Database vẫn đọc được định danh Controller.
    public void ControllerIdentity_SurvivesKeyRotation()
    {
        using var db = new TestDb().LoginAdmin();
        using var a = db.Rt.HostKeys.GetControllerIdentity();
        db.Rt.KeyRotation.Rotate(KeyPurpose.Database);
        using var b = db.Rt.HostKeys.GetControllerIdentity();
        Assert.Equal(a.PublicKey, b.PublicKey);
    }

    [Fact]
    public void Pin_And_Remove_RequirePermissions_AndAreAudited()
    {
        using var db = new TestDb().LoginAdmin();
        using var id = DeviceIdentity.Generate();
        db.Rt.HostKeys.Pin("123456789", id.PublicKey);
        Assert.Equal(id.PublicKey, db.Rt.HostKeys.GetPinned("123456789"));
        db.LoginAs("viewer", "Viewer-Password-1", UserRole.Viewer);
        Assert.Throws<UnauthorizedAccessException>(() => db.Rt.HostKeys.Pin("987654321", id.PublicKey));
        Assert.Throws<UnauthorizedAccessException>(() => db.Rt.HostKeys.RemovePin("123456789"));
        Assert.Equal(id.PublicKey, db.Rt.HostKeys.GetPinned("123456789"));
        Assert.Contains(db.Rt.AuditLogs.Query(null, 50), a => a.EventType == AuditEvents.HostKeyPinned);
    }

    [Fact] // Operator không tự bỏ ghim được: đó là thao tác cho phép chấp nhận khóa lạ.
    public void Operator_CanPin_ButCannotRemovePin()
    {
        using var db = new TestDb().LoginAdmin();
        using var id = DeviceIdentity.Generate();
        db.LoginAs("oper", "Operator-Password-1", UserRole.Operator);
        db.Rt.HostKeys.Pin("123456789", id.PublicKey);
        Assert.Throws<UnauthorizedAccessException>(() => db.Rt.HostKeys.RemovePin("123456789"));
    }

    [Fact]
    public void Mismatch_IsAuditedAsCritical()
    {
        using var db = new TestDb().LoginAdmin();
        using var id = DeviceIdentity.Generate();
        db.Rt.HostKeys.ReportMismatch("123456789", id.PublicKey);
        Assert.Contains(db.Rt.AuditLogs.Query(null, 10), a => a.EventType == AuditEvents.HostKeyMismatch && a.Severity == AuditSeverity.Critical);
    }

    [Fact] // Migration từ v1 lên v2 giữ nguyên dữ liệu và có bản sao lưu trước khi nâng cấp.
    public void Migration_FromV1_KeepsData_AndBacksUpFirst()
    {
        using var db = new TestDb().LoginAdmin();
        db.AddClient("PC", "123456789");
        using (var c = db.Rt.Context.Open())
        {
            using (var cmd = new System.Data.SQLite.SQLiteCommand("DROP TABLE PinnedHostKeys; DELETE FROM SchemaVersion WHERE Version=2; PRAGMA user_version=1;", c)) cmd.ExecuteNonQuery();
        }
        RDM.Data.DatabaseMigration.Migrate(db.Rt.Context);
        Assert.Equal(1, db.Rt.Clients.Count());
        Assert.Null(db.Rt.HostKeys.GetPinned("123456789"));
        Assert.Contains(Directory.GetFiles(db.Dir), f => f.EndsWith(".pre-v1.bak"));
    }
}
