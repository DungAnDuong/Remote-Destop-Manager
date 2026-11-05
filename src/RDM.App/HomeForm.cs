using System;
using System.Drawing;
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
    private readonly ModernInput _address = new ModernInput("Only needed on a different subnet");
    private readonly ModernInput _password = new ModernInput("Leave blank if the remote computer has no password", true);
    private readonly ModernButton _connect = new ModernButton("CONNECT", ButtonKind.Primary);
    private readonly ModernButton _manage = new ModernButton("Device Management (sign-in required)");
    private readonly GridView<ConnectionRecord> _recent = new GridView<ConnectionRecord>();
    private MainForm _management;
    private readonly NotifyIcon _tray = new NotifyIcon { Visible = true };
    private readonly ContextMenuStrip _trayMenu = new ContextMenuStrip();
    private ToolStripMenuItem _trayStatus, _trayDisconnect;
    private bool _exiting;

    public HomeForm(RdmRuntime rt, AllowControlPanel allow)
    {
        _rt = rt; _allow = allow;
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

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(Dpi.S(6)) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.Controls.Add(_allow, 0, 0);
        columns.Controls.Add(BuildControlSide(), 1, 0);

        Controls.Add(columns);
        Controls.Add(header);

        _connect.Click += async (s, e) => await ConnectAsync();
        _manage.Click += async (s, e) => await OpenManagementAsync();
        _recent.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _recent.SelectedItem != null) { _remoteId.Text = RemoteIdFormat.Display(_recent.SelectedItem.RemoteId); _password.Box.Focus(); } };
        Shown += async (s, e) =>
        {
            Ui.Try(this, () => _rt.Presence.Start());
            AcceptButton = _connect;
            await RefreshRecentAsync();
        };
        SessionRegistry.Changed += OnSessionsChanged;
        BuildTray();
        _allow.ControllerChanged += OnControllerChanged;
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
        FormClosed += (s, e) => SessionRegistry.Changed -= OnSessionsChanged;
    }

    private void BuildTray()
    {
        _trayStatus = new ToolStripMenuItem("RDM is running") { Enabled = false };
        _trayMenu.Items.Add(_trayStatus);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Open RDM", null, (s, e) => RestoreFromTray());
        _trayDisconnect = new ToolStripMenuItem("Disconnect current session", null, (s, e) => _allow.DisconnectAll()) { Visible = false };
        _trayMenu.Items.Add(_trayDisconnect);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (s, e) => { _exiting = true; Close(); });

        _tray.Icon = Theme.AppIcon;
        _tray.Text = "RDM - Remote Desktop Manager";
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (s, e) => RestoreFromTray();
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

    /// <summary>Biểu tượng khay phản ánh phiên đang điều khiển máy này, kèm lệnh ngắt. Chủ máy luôn thấy, không phải cảnh báo bật lên.</summary>
    private void OnControllerChanged(string user)
    {
        if (IsDisposed) return;
        void Apply()
        {
            var active = !string.IsNullOrEmpty(user);
            _trayDisconnect.Visible = active;
            _trayStatus.Text = active ? "Being controlled by: " + user : "RDM is running";
            var tip = active ? "RDM - being controlled by " + user : "RDM - Remote Desktop Manager";
            _tray.Text = tip.Length <= 63 ? tip : tip.Substring(0, 60) + "...";
        }
        if (IsHandleCreated && InvokeRequired) BeginInvoke(new Action(Apply)); else Apply();
    }

    private Control BuildControlSide()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(376)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var card = new Card("Control Another Computer");
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        void Field(string label, Control c)
        {
            flow.Controls.Add(Ui.Text(label, 9f, false, Theme.TextSecondary));
            flow.Controls.Add(c);
        }
        Field("Remote ID", _remoteId);
        Field("Remote computer's access password", _password);
        Field("IP address (optional)", _address);
        _connect.Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(8));
        flow.Controls.Add(_connect);
        var note = Ui.Text(RDM.Shared.Common.ProductMode.Managed ? "Connects over your local network or the Internet." : "Connects directly when possible, otherwise through the Relay Server (Settings).", 8.5f, false, Theme.TextMuted);
        flow.Controls.Add(note);
        _manage.Margin = new Padding(0, Dpi.S(4), 0, 0);
        flow.Controls.Add(_manage);
        flow.Resize += (s, e) =>
        {
            var w = Math.Max(Dpi.S(160), flow.ClientSize.Width - Dpi.S(4));
            foreach (var c in new Control[] { _remoteId, _password, _address, _connect, _manage }) c.Width = w;
        };
        card.Controls.Add(flow);
        layout.Controls.Add(card, 0, 0);

        _recent.AddColumn("Recent computer", r => r.DeviceName, 100).AddColumn("Remote ID", r => RemoteIdFormat.Display(r.RemoteId), 70).AddColumn("Time", r => Ui.When(r.StartTime), 70);
        var recentCard = new Card("Recent Connections (double-click to fill in)");
        recentCard.Controls.Add(_recent);
        layout.Controls.Add(recentCard, 0, 1);
        return layout;
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
            await ConnectFlow.ConnectAsync(_rt, this, _remoteId.Text, _password.Text, _address.Text);
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
        };
        _management.Show();
        await Task.CompletedTask;
    }

    private void EndManagement()
    {
        _rt.Users.Logout();
        _rt.Session.BeginQuickSession(Environment.UserName);
    }
}
