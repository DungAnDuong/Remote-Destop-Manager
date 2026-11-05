using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Forms;
using RDM.Controller.Theming;
using RDM.Shared.Common;
using RDM.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace RDM.Tests;

/// <summary>
/// Dựng giao diện thật, không cần người bấm. Đặt RDM_UI_SHOTS=thư mục để lưu ảnh từng trang.
/// Test này xác nhận trang dựng được và không phát sinh lỗi, không thay thế kiểm thử bằng tay.
/// </summary>
public class UiTests
{
    private readonly ITestOutputHelper _out;
    public UiTests(ITestOutputHelper output) => _out = output;

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(90)))
        {
            // Chụp stack của luồng UI để biết chính xác chỗ treo, rồi không để nó ảnh hưởng test khác.
#pragma warning disable CS0618, CS0612
            t.Suspend();
            var stack = new StackTrace(t, false).ToString();
            t.Resume();
#pragma warning restore CS0618, CS0612
            throw new TimeoutException("Giao diện bị treo tại:\n" + stack);
        }
        if (failure != null) throw new Exception("Lỗi giao diện: " + failure, failure);
    }

    private static void Pump(int ms = 500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void Seed(TestDb db)
    {
        var a = db.Rt.Groups.Add("Nhà máy Bình Dương", "Khu A");
        var b = db.Rt.Groups.Add("Văn phòng Hà Nội", null);
        for (var i = 1; i <= 40; i++)
            db.Rt.Clients.Add(new ClientDevice
            {
                DeviceName = $"PC-KT-{i:00}", RemoteId = (123456700 + i).ToString(CultureInfo.InvariantCulture), GroupId = i % 2 == 0 ? a : b,
                IpAddress = $"10.0.{i % 4}.{i}", OperatingSystem = i % 3 == 0 ? "Windows 7 SP1" : "Windows 10", Architecture = "x64", IsEnabled = i != 5
            }, i % 2 == 0 ? "mat-khau" : null, i % 5 == 0 ? new[] { "kho" } : null);
        db.Rt.Clients.SetFavorite(db.Rt.Clients.Query("PC-KT-02", null, false).Single().Id, true);

        using var c = db.Rt.Context.Open();
        using var tx = c.BeginTransaction();
        var rnd = new Random(7);
        for (var d = 0; d < 14; d++)
            for (var n = rnd.Next(0, 9); n > 0; n--)
            {
                var start = DateTime.UtcNow.Date.AddDays(-d).AddHours(rnd.Next(1, 8));
                var seconds = rnd.Next(60, 3000);
                using var cmd = new SQLiteCommand("INSERT INTO ConnectionHistory(RemoteID, DeviceName, ControllerUser, StartTime, EndTime, Duration, ConnectionType, ConnectionStatus) VALUES('123456701','PC-KT-01','admin',@s,@e,@d,'Relay','Disconnected')", c, tx);
                cmd.Parameters.AddWithValue("@s", start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@e", start.AddSeconds(seconds).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@d", seconds);
                cmd.ExecuteNonQuery();
            }
        tx.Commit();
    }

    [Theory]
    [InlineData(ThemeMode.Light, 1240, 800)]
    [InlineData(ThemeMode.Dark, 1240, 800)]
    [InlineData(ThemeMode.Light, 1024, 768)]
    public void EveryPage_RendersWithoutError(ThemeMode mode, int width, int height)
    {
        RunSta(() =>
        {
            var errors = new List<Exception>();
            Ui.ErrorOverride = errors.Add;
            Dpi.Init();
            Theme.Set(mode);
            try
            {
                using var db = new TestDb().LoginAdmin();
                Seed(db);
                var outDir = Environment.GetEnvironmentVariable("RDM_UI_SHOTS");
                using var form = new MainForm(db.Rt) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) };
                form.MinimumSize = new Size(0, 0);
                form.Size = new Size(width, height);

                // Chạy bằng message loop thật như ứng dụng, chuyển trang theo Timer.
                var pages = new Queue<string>(form.PageNames);
                string shown = null;
                using var timer = new System.Windows.Forms.Timer { Interval = 300 };
                timer.Tick += (s, e) =>
                {
                    if (shown != null && !string.IsNullOrEmpty(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                        // DrawToBitmap vẽ cả viền cửa sổ, nên ảnh phải bằng kích thước cả cửa sổ để không mất phần dưới.
                        using var bmp = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                        bmp.Save(Path.Combine(outDir, $"{mode}-{width}-{shown.Replace(' ', '_')}.png"), ImageFormat.Png);
                    }
                    if (pages.Count == 0) { timer.Stop(); form.Close(); return; }
                    shown = pages.Dequeue();
                    form.Navigate(shown);
                };
                timer.Start();
                Application.Run(form);
            }
            finally { Ui.ErrorOverride = null; Theme.Set(ThemeMode.Light); }
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        });
    }

    [Theory] // Cửa sổ Remote Desktop với màn hình giả: chỉ kiểm tra bố cục, không dùng màn hình thật.
    [InlineData(ThemeMode.Light)]
    public void RemoteDesktopForm_RendersFrames_AndNeverShowsUnhandledError(ThemeMode mode)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Dpi.Init();
                Theme.Set(mode);
                using var host = new HostFixture();
                using var db = new TestDb().LoginAdmin();
                var handshake = host.ConnectAsync().GetAwaiter().GetResult();
                var session = new RDM.Controller.Services.RemoteSession(handshake);
                var id = db.Rt.History.Start(host.State.RemoteId, "May Test", "admin", "Direct", "Connected");
                using var form = new RemoteDesktopForm(db.Rt, host.State.RemoteId, "May Test", session, () => throw new InvalidOperationException(), id)
                { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) };
                using var timer = new System.Windows.Forms.Timer { Interval = 2500 };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var outDir = Environment.GetEnvironmentVariable("RDM_UI_SHOTS");
                    if (!string.IsNullOrEmpty(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                        using var bmp = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                        bmp.Save(Path.Combine(outDir, "RemoteDesktop.png"), ImageFormat.Png);
                    }
                    Assert.True(session.TotalFrames >= 1, "Chưa nhận được khung hình nào");
                    Assert.Single(RDM.Controller.Services.SessionRegistry.All);
                    form.Close();
                };
                timer.Start();
                Application.Run(form);
                Assert.Empty(RDM.Controller.Services.SessionRegistry.All);
                Assert.NotNull(db.Rt.History.Query(null, 10).Single(h => h.Id == id).EndTime);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Giao diện Remote bị treo");
        if (failure != null) throw new Exception(failure.ToString(), failure);
    }

    [Fact] // Icon RDM thay cho icon mặc định của .NET, ở cả file exe và từng cửa sổ.
    public void Icon_IsRdm_OnExeAndAllForms()
    {
        RunSta(() =>
        {
            Dpi.Init();
            var appIcon = Theme.AppIcon;
            Assert.NotNull(appIcon);
            using var db = new TestDb();
            using (var login = new LoginForm(db.Rt)) Assert.Same(appIcon, login.Icon);
            db.LoginAdmin();
            using (var main = new MainForm(db.Rt)) Assert.Same(appIcon, main.Icon);
            using (var dialog = new ClientEditForm(db.Rt, null)) Assert.Same(appIcon, dialog.Icon);

            // Icon nhúng trong exe phải giống icon trong file, không phải icon mặc định.
            // Thư mục test: tests\RDM.Tests\bin\<nền tảng>\<cấu hình>.
            var cfg = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
            var exe = Path.Combine(cfg.Parent.Parent.Parent.Parent.Parent.FullName, "src", "RDM.App", "bin", cfg.Parent.Name, cfg.Name, "RDM.exe");
            Assert.True(File.Exists(exe), "Chưa build RDM.exe: " + exe);
            using var fromExe = Icon.ExtractAssociatedIcon(exe);
            using var expected = new Icon(typeof(Theme).Assembly.GetManifestResourceStream("RDM.ico"), fromExe.Size);
            using var a = fromExe.ToBitmap();
            using var b = expected.ToBitmap();
            var same = 0;
            for (var y = 0; y < a.Height; y++)
                for (var x = 0; x < a.Width; x++)
                    if (a.GetPixel(x, y) == b.GetPixel(x, y)) same++;
            Assert.True(same > a.Width * a.Height * 9 / 10, $"Icon trong exe khác icon RDM: {same}/{a.Width * a.Height}");
        });
    }

    [Fact]
    public void Viewer_CannotOpenAdminPages_AndManageButtonsDisabled()
    {
        RunSta(() =>
        {
            Dpi.Init();
            using var db = new TestDb().LoginAdmin();
            db.LoginAs("viewer", "Viewer-Password-1", UserRole.Viewer);
            using var form = new MainForm(db.Rt);
            Assert.False(form.IsPageEnabled("User Management"));
            Assert.False(form.IsPageEnabled("Security Center"));
            Assert.True(form.IsPageEnabled("My Computers"));
        });
    }

    [Fact]
    public void Dialogs_CanBeConstructed_InBothStates()
    {
        RunSta(() =>
        {
            Dpi.Init();
            using var db = new TestDb();
            using (var setup = new LoginForm(db.Rt)) Assert.Equal("Create Account", setup.AcceptButton is ModernButton b ? b.Text : "");
            db.LoginAdmin();
            using (var login = new LoginForm(db.Rt)) Assert.Equal("Sign In", login.AcceptButton is ModernButton b ? b.Text : "");
            using var add = new ClientEditForm(db.Rt, null);
            using var edit = new ClientEditForm(db.Rt, new ClientDevice { DeviceName = "PC", RemoteId = "123456789", HasCredential = true });
            using var group = new GroupEditForm(db.Rt, null);
            using var user = new UserEditForm(db.Rt, null);
            using var password = new ChangePasswordForm(db.Rt, true);
        });
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 5)]
    [InlineData(10, 10)]
    [InlineData(11, 20)]
    [InlineData(173, 200)]
    public void BarChart_NiceMax(double input, double expected) => Assert.Equal(expected, BarChart.NiceMax(input), 6);
}

/// <summary>Giao diện và thông báo phải hoàn toàn bằng tiếng Anh. Chú thích trong code thì được giữ tiếng Việt.</summary>
public class UiLanguageTests
{
    [Fact]
    public void SourceCode_HasNoVietnameseOutsideComments()
    {
        var cfg = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        var src = Path.Combine(cfg.Parent.Parent.Parent.Parent.Parent.FullName, "src");
        var vietnamese = new System.Text.RegularExpressions.Regex("[\u00C0-\u00C3\u00C8-\u00CA\u00CC\u00CD\u00D2-\u00D5\u00D9\u00DA\u00DD\u00E0-\u00E3\u00E8-\u00EA\u00EC\u00ED\u00F2-\u00F5\u00F9\u00FA\u00FD\u0102\u0103\u0110\u0111\u0128\u0129\u0168\u0169\u01A0\u01A1\u01AF\u01B0\u1EA0-\u1EF9]");
        // Tiếng Việt không dấu không có ký tự đặc biệt để nhận ra: chặn các từ đặc trưng trong chuỗi, từng lọt vào giao diện.
        var unaccented = new System.Text.RegularExpressions.Regex("\"[^\"]*\\b(Xoay|Khong|Thoat|Nguoi dung|Mat khau|Ket noi|Dang nhap|Thiet bi|Vui long|Hoac|Luu|Chua)\\b[^\"]*\"");
        var hits = new List<string>();
        foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\bin\") || file.Contains(@"\obj\")) continue;
            var n = 0;
            foreach (var line in File.ReadAllLines(file))
            {
                n++;
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                var code = comment >= 0 ? line.Substring(0, comment) : line;
                // Bộ lọc secret trong log cố ý nhận cả từ tiếng Việt người dùng có thể nhập; không hiển thị lên giao diện.
                if (Path.GetFileName(file) == "Validators.cs" && code.Contains("private[_ ]")) continue;
                if (vietnamese.IsMatch(code) || unaccented.IsMatch(code)) hits.Add($"{Path.GetFileName(file)}:{n}");
            }
        }
        Assert.True(hits.Count == 0, "Còn tiếng Việt ngoài chú thích: " + string.Join(", ", hits));
    }
}

public class PlatformInfoTests
{
    [Theory] // Phiên bản, Server?, .NET 4.8 được hỗ trợ?, hết hỗ trợ?, TLS 1.3?
    [InlineData("6.1.7600", false, false, true, false)] // Windows 7 chưa SP1
    [InlineData("6.1.7601", false, true, true, false)]  // Windows 7 SP1
    [InlineData("6.2.9200", false, false, true, false)] // Windows 8 RTM
    [InlineData("6.2.9200", true, true, true, false)]   // Server 2012
    [InlineData("6.3.9600", false, true, true, false)]  // Windows 8.1 / Server 2012 R2
    [InlineData("10.0.19045", false, true, false, false)] // Windows 10
    [InlineData("10.0.14393", true, true, false, false)]  // Server 2016
    [InlineData("10.0.17763", true, true, false, false)]  // Server 2019
    [InlineData("10.0.20348", true, true, false, true)]   // Server 2022
    [InlineData("10.0.22631", false, true, false, true)]  // Windows 11
    [InlineData("10.0.26100", true, true, false, true)]   // Server 2025
    public void Matrix(string version, bool server, bool net48, bool eol, bool tls13)
    {
        var p = PlatformInfo.Evaluate(new Version(version), server, true, true);
        Assert.Equal(net48, p.MeetsNet48Minimum);
        Assert.Equal(eol, p.IsEndOfLifeOs);
        Assert.Equal(tls13, p.SupportsTls13);
    }

    [Fact]
    public void Detect_RunsOnThisMachine()
    {
        var p = PlatformInfo.Detect();
        Assert.True(p.MeetsNet48Minimum);
        Assert.False(string.IsNullOrEmpty(p.Note));
    }
}
