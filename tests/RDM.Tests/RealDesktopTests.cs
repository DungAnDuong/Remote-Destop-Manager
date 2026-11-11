extern alias rdmclient;

using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Services;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;
using Xunit;
using Xunit.Abstractions;
using ClientAuthentication = rdmclient::RDM.Client.ClientAuthentication;
using GdiScreenCapture = rdmclient::RDM.Client.GdiScreenCapture;
using SendInputController = rdmclient::RDM.Client.SendInputController;
using SessionManager = rdmclient::RDM.Client.SessionManager;
using IScreenSource = rdmclient::RDM.Client.IScreenSource;
using IInputSink = rdmclient::RDM.Client.IInputSink;

namespace RDM.Tests;

/// <summary>
/// Kiểm thử trên màn hình thật. Cần phiên Windows đang có màn hình, và CHUỘT SẼ DI CHUYỂN trong vài giây.
/// Không chạy trong bộ test thường; chạy bằng build\real-desktop-test.ps1 khi đang ngồi ở máy.
/// Input chỉ nhắm vào cửa sổ riêng của test. Không lưu ảnh màn hình ra đĩa.
/// </summary>
[Trait("Category", TestCategories.RealDesktop)]
public class RealDesktopTests
{
    private static readonly Color Marker = Color.FromArgb(255, 0, 200);
    private readonly ITestOutputHelper _out;
    public RealDesktopTests(ITestOutputHelper output) => _out = output;

    private sealed class Target : Form
    {
        public readonly TextBox Box = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14f) };
        public volatile int Clicks;
        public Target()
        {
            Text = "RDM Real Desktop Test: vui long khong dung chuot";
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(500, 320);
            BackColor = Marker;
            Padding = new Padding(0, 100, 0, 0);
            Box.MouseDown += (s, e) => Clicks++;
            Controls.Add(Box);
        }
    }

    private static (int x, int y) Normalize(Point screen, MonitorInfo m) =>
        ((int)((long)(screen.X - m.X) * SessionInfo.NormalizedMax / (m.Width - 1)), (int)((long)(screen.Y - m.Y) * SessionInfo.NormalizedMax / (m.Height - 1)));

    [Fact]
    public void FullPipeline_CaptureAndInput_OnRealDesktop()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "Kiểm thử bị treo");
        if (failure != null) throw new Exception(failure.ToString(), failure);
    }

    private void Run()
    {
        using (var probe = new GdiScreenCapture())
            Assert.True(probe.Capture(0) != null, "Không chụp được màn hình: phiên Windows này không có màn hình. Hãy chạy khi đang ngồi ở máy và đã mở khóa.");

        Environment.SetEnvironmentVariable("RDM_SESSION_BIND", "127.0.0.1");
        var originalCursor = Cursor.Position;
        using var temp = new TempStore();
        var state = temp.Store.LoadOrCreate();
        state.SessionPort = 0;
        var auth = new ClientAuthentication(temp.Store, state, 1000);
        auth.SetPassword(null, "Mat-Khau-Test-Thuc-1");
        auth.SetUnattended(true);
        using var host = new SessionManager(state, auth, () => ((IScreenSource)new GdiScreenCapture(), (IInputSink)new SendInputController()));
        host.StartListening();
        using var hostKey = DeviceIdentity.FromSeed(state.IdentitySeed);
        using var controller = DeviceIdentity.Generate();
        using var form = new Target();

        RemoteSession session = null;
        string error = null;
        var report = "";

        async void Script()
        {
            try
            {
                var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(IPAddress.Loopback, host.ListeningPort);
                var handshake = await SessionHandshake.ConnectAsync(new FramedStream(tcp.GetStream(), tcp), new ControllerHandshakeOptions
                {
                    Identity = controller, ExpectedRemoteId = state.RemoteId, Username = "test", Password = "Mat-Khau-Test-Thuc-1",
                    VerifyHostKey = k => Task.FromResult(true)
                }, CancellationToken.None);
                session = new RemoteSession(handshake);
                session.Start();
                await Wait.Until(() => session.TotalFrames >= 1 && session.Monitors.Count > 0, "khung hình thật đầu tiên", 20);

                // 1. Hình ảnh: thấy đúng màu đánh dấu của cửa sổ test tại đúng vị trí.
                var monitor = session.Monitors[session.CurrentMonitor];
                var markerPoint = form.PointToScreen(new Point(form.ClientSize.Width / 2, 40));
                await Task.Delay(800);
                Color seen;
                lock (session.SurfaceLock) seen = session.Surface.GetPixel(markerPoint.X - monitor.X, markerPoint.Y - monitor.Y);
                Assert.True(Math.Abs(seen.R - Marker.R) < 50 && Math.Abs(seen.G - Marker.G) < 60 && Math.Abs(seen.B - Marker.B) < 60, "Màu tại cửa sổ test sai: " + seen);

                // 2. Chuột: click đúng vào ô chữ của cửa sổ test.
                var center = form.Box.PointToScreen(new Point(form.Box.Width / 2, form.Box.Height / 2));
                var (x, y) = Normalize(center, monitor);
                session.SendMouse(new MouseInputData { Action = MouseAction.Move, X = x, Y = y });
                session.SendMouse(new MouseInputData { Action = MouseAction.LeftDown, X = x, Y = y });
                session.SendMouse(new MouseInputData { Action = MouseAction.LeftUp, X = x, Y = y });
                await Wait.Until(() => form.Clicks >= 1, "click tới đúng ô chữ", 10);

                // 3. Bàn phím: scan code và chữ tiếng Việt.
                session.SendKey(new KeyInputData { VirtualKey = 0x41, ScanCode = 0x1E, Down = true });
                session.SendKey(new KeyInputData { VirtualKey = 0x41, ScanCode = 0x1E, Down = false });
                session.SendText("Việt Nam ơi");
                await Wait.Until(() => form.Box.Text.Contains("Việt Nam ơi"), "chữ tiếng Việt", 10);
                Assert.StartsWith("a", form.Box.Text, StringComparison.OrdinalIgnoreCase);

                // 4. Vùng thay đổi: màn hình nhận được cả nội dung mới vừa gõ.
                var before = session.TotalFrames;
                form.Box.AppendText(" thay doi");
                await Wait.Until(() => session.TotalFrames > before, "khung sau khi đổi", 10);

                // 5. Đo FPS khi màn hình liên tục đổi.
                var sw = Stopwatch.StartNew();
                var startFrames = session.TotalFrames;
                var random = new Random(1);
                while (sw.ElapsedMilliseconds < 4000)
                {
                    form.Box.BackColor = Color.FromArgb(random.Next(256), random.Next(256), random.Next(256));
                    await Task.Delay(15);
                }
                var fps = (session.TotalFrames - startFrames) * 1000.0 / sw.ElapsedMilliseconds;
                report = $"Màn hình {monitor.Width}x{monitor.Height}: {fps:0.0} FPS khi đổi liên tục, độ trễ {session.LatencyMs} ms, chất lượng {session.Quality}, 64-bit={Environment.Is64BitProcess}";

                // 6. Công tắc khẩn cấp với hình ảnh thật.
                host.DisconnectAll();
                await Wait.Until(() => host.Sessions.Count == 0, "chủ máy ngắt", 10);
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally
            {
                session?.Dispose();
                Cursor.Position = originalCursor;
                form.Close();
            }
        }

        form.Shown += (s, e) => { form.Activate(); Script(); };
        Application.Run(form);
        _out.WriteLine(report);
        Assert.True(error == null, error);
    }
}
