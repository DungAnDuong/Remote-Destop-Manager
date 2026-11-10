using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Client;
using RDM.Controller.Controls;
using RDM.Controller.Forms;
using RDM.Controller.Services;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Shared.Common;
using RDM.Shared.Models;

namespace RDM.App;

/// <summary>
/// Cửa sổ chính kiểu TeamViewer: bên trái cho phép máy này được điều khiển, bên phải điều khiển máy khác.
/// Kết nối nhanh không cần đăng nhập RDM. Quản lý thiết bị đã lưu, người dùng, bảo mật vẫn phải đăng nhập.
/// </summary>
public sealed class HomeForm : Form
{
    private readonly RdmRuntime _rt;
    private readonly AllowControlPanel _allow;
    private readonly ModernInput _remoteId = new ModernInput("123 456 789");
    private readonly ModernInput _password = new ModernInput("Leave blank if the remote computer has no password", true);
    private readonly ModernButton _connect = new ModernButton("CONNECT", ButtonKind.Primary);
    private readonly ModernButton _manage = new ModernButton("Device Management (sign-in required)");
    private readonly GridView<ConnectionRecord> _recent = new GridView<ConnectionRecord>();
    private MainForm _management;
    private SplitContainer _split;
    private bool _splitReady;
    private readonly NotifyIcon _tray = new NotifyIcon { Visible = true };
    private readonly ContextMenuStrip _trayMenu = new ContextMenuStrip();
    private bool _exiting;
    private readonly bool _startHidden;
    private bool _firstShowHandled;

    public HomeForm(RdmRuntime rt, AllowControlPanel allow, bool startHidden = false)
    {
        _rt = rt; _allow = allow; _startHidden = startHidden;
        Text = "REMOTE DESKTOP MANAGER";
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        BackColor = Theme.Page;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(Dpi.S(960), Dpi.S(700));
        Size = new Size(Dpi.S(1040), Dpi.S(800));

        var header = new Panel { Dock = DockStyle.Top, Height = Dpi.S(52), BackColor = Theme.Sidebar };
        header.Controls.Add(new Label
        {
            Text = "RDM  |  REMOTE DESKTOP MANAGER", Dock = DockStyle.Fill, ForeColor = Color.White, Font = Theme.Font(12f, true),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dpi.S(16), 0, 0, 0), UseMnemonic = false
        });

        var menu = BuildMenu();

        // Trên: hai thẻ hành động (Remote ID bên trái, điều khiển máy khác bên phải).
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        top.Controls.Add(_allow, 0, 0);
        top.Controls.Add(BuildControlSide(), 1, 0);

        // Dưới: tab control gồm Device / Connection History / Recent Connections.
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(TabPage("Device", _allow.CreateDeviceView()));
        tabs.TabPages.Add(TabPage("Connection History", _allow.CreateHistoryView()));
        tabs.TabPages.Add(TabPage("Recent Connections", CreateRecentView()));

        // Thanh chia kéo được giữa vùng hành động (trên) và vùng tab (dưới).
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Page, SplitterWidth = Dpi.S(6), Panel1MinSize = Dpi.S(180), Panel2MinSize = Dpi.S(110) };
        split.Panel1.Padding = new Padding(Dpi.S(6));
        split.Panel2.Padding = new Padding(Dpi.S(6), 0, Dpi.S(6), Dpi.S(6));
        top.Dock = DockStyle.Fill;
        tabs.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(tabs);
        split.SplitterMoved += (s, e) => { if (_splitReady) SaveSplit(split.SplitterDistance); };
        _split = split;

        Controls.Add(split);
        Controls.Add(header);
        Controls.Add(menu);

        _connect.Click += async (s, e) => await ConnectAsync();
        _manage.Click += async (s, e) => await OpenManagementAsync();
        _recent.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _recent.SelectedItem != null) { _remoteId.Text = RemoteIdFormat.Display(_recent.SelectedItem.RemoteId); _password.Box.Focus(); } };
        Shown += async (s, e) =>
        {
            Ui.Try(this, () => _rt.Presence.Start());
            AcceptButton = _connect;
            // Mặc định vùng tab chiếm ~25% chiều cao; nếu lần trước đã kéo thì dùng lại kích thước đã lưu.
            if (_split != null)
            {
                var saved = LoadSplit();
                var def = (int)(_split.Height * 0.72);
                var want = saved > 0 ? saved : def;
                var min = _split.Panel1MinSize;
                var max = Math.Max(min, _split.Height - _split.Panel2MinSize - _split.SplitterWidth);
                try { _split.SplitterDistance = Math.Min(max, Math.Max(min, want)); } catch (InvalidOperationException) { }
                _splitReady = true;
            }
            await RefreshRecentAsync();
        };
        SessionRegistry.Changed += OnSessionsChanged;
        BuildTray();
        _allow.HostSessionsChanged += OnHostSessionsChanged;
        // Đóng cửa sổ chỉ thu nhỏ xuống khay; thoát hẳn qua menu Exit của biểu tượng khay.
        FormClosing += (s, e) =>
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            SessionRegistry.CloseAll();
            _management?.Close();
            _allow.Shutdown();
            _tray.Visible = false;
            _tray.Dispose();
        };
        FormClosed += (s, e) => { SessionRegistry.Changed -= OnSessionsChanged; _allow.HostSessionsChanged -= OnHostSessionsChanged; };

        // Tự khởi động cùng Windows (chạy ẩn): cửa sổ không hiện nên Load của panel không chạy sớm;
        // khởi động nền ngay để máy vẫn nhận được kết nối remote khi đang ẩn dưới khay.
        if (_startHidden) _allow.StartBackground();
    }

    // Tự khởi động cùng Windows: chạy ẩn xuống khay, không hiện cửa sổ chính. Chặn lần hiện đầu tiên,
    // vẫn tạo handle để nền (nhận kết nối, biểu tượng khay) chạy; người dùng mở lại từ khay.
    protected override void SetVisibleCore(bool value)
    {
        if (value && _startHidden && !_firstShowHandled)
        {
            _firstShowHandled = true;
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    private void BuildTray()
    {
        _tray.Icon = Theme.AppIcon;
        _tray.Text = "RDM - Remote Desktop Manager";
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (s, e) => RestoreFromTray();
        RebuildTray();
    }

    /// <summary>Dựng lại menu khay: liệt kê từng máy đang điều khiển máy này, mỗi máy có Chat và Disconnect.</summary>
    private void RebuildTray()
    {
        if (IsDisposed) return;
        var sessions = _allow.ActiveSessions;
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add(new ToolStripMenuItem(
            sessions.Count == 0 ? "RDM is running"
            : sessions.Count == 1 ? "Being controlled by 1 controller"
            : $"Being controlled by {sessions.Count} controllers") { Enabled = false });
        _trayMenu.Items.Add(new ToolStripSeparator());

        foreach (var session in sessions)
        {
            var item = new ToolStripMenuItem(session.ControllerUser ?? "Controller");
            item.DropDownItems.Add("Chat", null, (s, e) => { _allow.ShowChat(session); });
            item.DropDownItems.Add("Disconnect", null, (s, e) => session.Disconnect());
            _trayMenu.Items.Add(item);
        }
        if (sessions.Count > 0) _trayMenu.Items.Add(new ToolStripSeparator());

        _trayMenu.Items.Add("Open RDM", null, (s, e) => RestoreFromTray());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (s, e) => { _exiting = true; Close(); });

        var active = sessions.Count > 0;
        var tip = active ? "RDM - being controlled by " + string.Join(", ", sessions.Select(s => s.ControllerUser)) : "RDM - Remote Desktop Manager";
        _tray.Text = tip.Length <= 63 ? tip : tip.Substring(0, 60) + "...";
    }

    private void OnHostSessionsChanged()
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired) BeginInvoke(new Action(RebuildTray)); else RebuildTray();
    }

    private void HideToTray()
    {
        Hide();
        if (_management == null || _management.IsDisposed)
            _tray.ShowBalloonTip(3000, "RDM", "RDM is still running here. Double-click to open it, or right-click for Exit.", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // Lưu/đọc vị trí thanh chia để lần sau mở lại giữ nguyên kích thước vùng tab.
    private string SplitFile() => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_rt.Context.DatabasePath) ?? ".", "home-layout.txt");

    private void SaveSplit(int distance)
    {
        try { System.IO.File.WriteAllText(SplitFile(), distance.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
    }

    private int LoadSplit()
    {
        try { return int.TryParse(System.IO.File.ReadAllText(SplitFile()).Trim(), out var v) && v > 0 ? v : 0; }
        catch (System.IO.IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; }
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(9.5f), Padding = new Padding(Dpi.S(6), Dpi.S(2), 0, Dpi.S(2)) };
        var settings = new ToolStripMenuItem("Settings", null, (s, e) => _allow.OpenSettings(this));
        var exit = new ToolStripMenuItem("Exit", null, (s, e) => { _exiting = true; Close(); });
        menu.Items.Add(settings);
        menu.Items.Add(exit);
        MainMenuStrip = menu;
        return menu;
    }

    private static TabPage TabPage(string title, Control content)
    {
        var page = new TabPage(title) { BackColor = Theme.Page, Padding = new Padding(Dpi.S(4)) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private Control BuildControlSide()
    {
        var card = new Card("Control Another Computer");
        card.Dock = DockStyle.Fill;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        void Field(string label, Control c)
        {
            flow.Controls.Add(Ui.Text(label, 9f, false, Theme.TextSecondary));
            flow.Controls.Add(c);
        }
        Field("Remote ID", _remoteId);
        Field("Remote computer's access password", _password);
        _connect.Margin = new Padding(0, Dpi.S(8), 0, Dpi.S(8));
        flow.Controls.Add(_connect);
        var note = Ui.Text(RDM.Shared.Common.ProductMode.Managed ? "Connects over your local network or the Internet." : "Connects directly when possible, otherwise through the Relay Server (Settings).", 8.5f, false, Theme.TextMuted);
        flow.Controls.Add(note);
        _manage.Margin = new Padding(0, Dpi.S(4), 0, 0);
        flow.Controls.Add(_manage);
        flow.Resize += (s, e) =>
        {
            var w = Math.Max(Dpi.S(160), flow.ClientSize.Width - Dpi.S(4));
            foreach (var c in new Control[] { _remoteId, _password, _connect, _manage }) c.Width = w;
        };
        card.Controls.Add(flow);
        return card;
    }

    private Control CreateRecentView()
    {
        _recent.AddColumn("Recent computer", r => r.DeviceName, 100).AddColumn("Remote ID", r => RemoteIdFormat.Display(r.RemoteId), 70).AddColumn("Time", r => Ui.When(r.StartTime), 70);
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Dpi.S(4)) };
        panel.Controls.Add(_recent);
        return panel;
    }

    private void OnSessionsChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(async () => { if (!IsDisposed) await RefreshRecentAsync(); }));
    }

    private async Task RefreshRecentAsync()
    {
        try
        {
            var items = await Task.Run(() => _rt.History.Recent(20));
            if (!IsDisposed) _recent.SetItems(items);
        }
        catch (Exception ex) { Ui.Error(this, ex); }
    }

    private async Task ConnectAsync()
    {
        _connect.Enabled = false;
        UseWaitCursor = true;
        try
        {
            // Ô mật khẩu để trống nghĩa là không có mật khẩu, không hỏi thêm: chủ máy sẽ chấp nhận trên màn hình.
            await ConnectFlow.ConnectAsync(_rt, this, _remoteId.Text, _password.Text, string.Empty);
            _password.Text = string.Empty;
            await RefreshRecentAsync();
        }
        catch (Exception ex) { Ui.Error(this, ex); }
        finally
        {
            if (!IsDisposed) { _connect.Enabled = true; UseWaitCursor = false; }
        }
    }

    /// <summary>Mở phần quản lý trong cửa sổ riêng, sau khi đăng nhập. Đóng cửa sổ đó là đăng xuất và quay về kết nối nhanh.</summary>
    private async Task OpenManagementAsync()
    {
        if (_management != null && !_management.IsDisposed)
        {
            _management.Activate();
            return;
        }
        var dataDir = System.IO.Path.GetDirectoryName(_rt.Context.DatabasePath);
        using (var login = new LoginForm(_rt))
        {
            var saved = RememberStore.Load(dataDir);
            if (saved.HasValue) login.Prefill(saved.Value.user, saved.Value.pass);
            if (login.ShowDialog(this) != DialogResult.OK) return;
            if (login.Remember) RememberStore.Save(dataDir, login.EnteredUser, login.EnteredPassword);
            else RememberStore.Clear(dataDir);
        }

        if (_rt.Session.Current?.MustChangePassword == true)
            using (var change = new ChangePasswordForm(_rt, true))
                if (change.ShowDialog(this) != DialogResult.OK) { EndManagement(); return; }

        _management = new MainForm(_rt);
        _management.FormClosed += (s, e) =>
        {
            _management = null;
            EndManagement();
            // Hiện lại cửa sổ chính khi đóng Device Management (trừ khi đang thoát hẳn).
            if (!_exiting && !IsDisposed) { Show(); WindowState = FormWindowState.Normal; Activate(); }
        };
        _management.Show();
        // Ẩn cửa sổ chính để chỉ còn một nút trên thanh taskbar (biểu tượng khay vẫn giữ).
        Hide();
        await Task.CompletedTask;
    }

    private void EndManagement()
    {
        _rt.Users.Logout();
        _rt.Session.BeginQuickSession(Environment.UserName);
    }
}
