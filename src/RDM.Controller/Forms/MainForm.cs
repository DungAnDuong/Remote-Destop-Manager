using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Theming;
using RDM.Controller.Views;
using RDM.Data;
using RDM.Shared.Models;

namespace RDM.Controller.Forms;

/// <summary>Cửa sổ chính: Sidebar 12 mục (Phần 4.1), thanh tiêu đề và vùng nội dung.</summary>
public sealed class MainForm : Form
{
    private sealed class NavItem
    {
        public string Key, Glyph; public Func<ViewBase> Create; public Permission? Requires; public SidebarButton Button;
    }

    private readonly RdmRuntime _rt;
    private readonly List<NavItem> _items;
    private readonly Panel _content = new Panel { Dock = DockStyle.Fill };
    private readonly Panel _sidebar = new Panel { Dock = DockStyle.Left };
    private readonly Panel _header = new Panel { Dock = DockStyle.Top };
    private readonly Label _title = new Label { AutoSize = true, UseMnemonic = false };
    private readonly Label _user = new Label { AutoSize = true, UseMnemonic = false };
    private readonly FlowLayoutPanel _headerRight = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
    private NavItem _current;
    private ViewBase _view;
    private readonly Timer _presenceDebounce = new Timer { Interval = 1000 };

    /// <summary>true nếu đóng cửa sổ để đăng nhập lại, không phải thoát ứng dụng.</summary>
    public bool LogoutRequested { get; private set; }

    public MainForm(RdmRuntime rt)
    {
        _rt = rt;
        Text = Ui.AppTitle + " – Device Management";
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(Dpi.S(1000), Dpi.S(700));
        Size = new Size(Dpi.S(1240), Dpi.S(800));
        DoubleBuffered = true;

        _items = new List<NavItem>
        {
            Nav("Dashboard", "▦", () => new DashboardView(_rt)),
            Nav("My Computers", "▣", () => new ClientManagerView(_rt, false)),
            Nav("Groups", "☷", () => new GroupManagerView(_rt)),
            Nav("Recent Connections", "⟲", () => new ConnectionHistoryView(_rt, true)),
            Nav("Favorites", "★", () => new ClientManagerView(_rt, true)),
            Nav("Active Sessions", "▶", () => new ActiveSessionsView(_rt)),
            Nav("File Transfer", "⇄", () => new PendingFeatureView(_rt, "File Transfer", "Phase 6", "Two-pane LOCAL COMPUTER | REMOTE COMPUTER file transfer, encrypted and verified with SHA-256.")),
            Nav("Connection History", "☰", () => new ConnectionHistoryView(_rt, false)),
            Nav("User Management", "☺", () => new UserManagementView(_rt), Permission.ManageUsers),
            Nav("Security Center", "⛨", CreateSecurityCenter, Permission.ManageSecurity),
            Nav("Settings", "⚙", () => new SettingsView(_rt)),
            Nav("About", "ℹ", () => new AboutView(_rt))
        };

        BuildChrome();
        Theme.Changed += OnThemeChanged;
        // Gộp nhiều thay đổi liên tiếp thành một lần làm mới, tránh làm nặng UI khi có nhiều thiết bị.
        _presenceDebounce.Tick += async (s, e) =>
        {
            _presenceDebounce.Stop();
            if (_view != null && _view.ShowsPresence) await _view.RefreshAsync();
        };
        _rt.Presence.Changed += OnPresenceChanged;
        // Đăng xuất hoặc thoát phải đóng mọi phiên trước, khi vòng message vẫn còn chạy.
        FormClosing += (s, e) => RDM.Controller.Services.SessionRegistry.CloseAll();
        Show(_items[0]);
        Shown += (s, e) => Ui.Try(this, () => _rt.Presence.Start());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _rt.Presence.Changed -= OnPresenceChanged;
            _presenceDebounce.Dispose();
        }
        base.Dispose(disposing);
    }

    private static NavItem Nav(string key, string glyph, Func<ViewBase> create, Permission? requires = null) =>
        new NavItem { Key = key, Glyph = glyph, Create = create, Requires = requires };

    private ViewBase CreateSecurityCenter()
    {
        var view = new SecurityCenterView(_rt);
        view.RestoreCompleted += Logout;
        return view;
    }

    private void BuildChrome()
    {
        _sidebar.Width = Dpi.S(216);
        _sidebar.BackColor = Theme.Sidebar;
        var brand = new Label
        {
            Text = "REMOTE DESKTOP\nMANAGER", Dock = DockStyle.Top, Height = Dpi.S(70), ForeColor = Color.White, BackColor = Theme.Sidebar,
            Font = Theme.Font(11.5f, true), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dpi.S(14), 0, 0, 0), UseMnemonic = false
        };
        var nav = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, AutoScroll = true };
        // Dock.Top xếp ngược thứ tự thêm vào, nên thêm từ mục cuối lên.
        foreach (var item in Enumerable.Reverse(_items))
        {
            item.Button = new SidebarButton(item.Glyph, item.Key);
            if (item.Requires.HasValue && !_rt.Session.Can(item.Requires.Value)) item.Button.Enabled = false;
            var captured = item;
            item.Button.Click += (s, e) => Show(captured);
            nav.Controls.Add(item.Button);
        }
        _sidebar.Controls.Add(nav);
        _sidebar.Controls.Add(brand);

        _header.Height = Dpi.S(56);
        _title.Font = Theme.Font(14f, true);
        _title.Location = new Point(Dpi.S(20), Dpi.S(14));
        _user.Font = Theme.Font(9f);
        _user.Margin = new Padding(0, Dpi.S(9), Dpi.S(10), 0);
        _user.Text = $"{_rt.Session.Username} ({_rt.Session.Current?.Role})";
        _headerRight.Padding = new Padding(0, Dpi.S(11), Dpi.S(12), 0);
        _headerRight.BackColor = Color.Transparent;
        _headerRight.Controls.Add(_user);
        _headerRight.Controls.Add(Header("Light/Dark", ToggleTheme));
        _headerRight.Controls.Add(Header("Change Password", ChangePassword));
        _headerRight.Controls.Add(Header("Sign Out", Logout));
        _header.Controls.Add(_title);
        _header.Controls.Add(_headerRight);

        Controls.Add(_content);
        Controls.Add(_header);
        Controls.Add(_sidebar);
        ApplyTheme();
    }

    private ModernButton Header(string text, Action action)
    {
        var b = new ModernButton(text, ButtonKind.Ghost) { Margin = new Padding(0, 0, Dpi.S(4), 0) };
        b.Click += (s, e) => action();
        return b;
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Page;
        _content.BackColor = Theme.Page;
        _header.BackColor = Theme.Surface;
        _title.ForeColor = Theme.Text;
        _user.ForeColor = Theme.TextSecondary;
        foreach (Control c in _headerRight.Controls) c.Invalidate();
    }

    private void OnThemeChanged()
    {
        ApplyTheme();
        Invalidate(true);
        // Dựng lại trang hiện tại để toàn bộ control lấy màu mới.
        if (_current != null) Show(_current);
    }

    /// <summary>Sự kiện đến từ luồng nền nên phải chuyển về luồng UI.</summary>
    private void OnPresenceChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() => { if (!IsDisposed) _presenceDebounce.Start(); }));
    }

    private void ToggleTheme()
    {
        var next = Theme.Mode == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
        if (Ui.Try(this, () => _rt.Settings.SetTheme(next.ToString()))) Theme.Set(next);
    }

    private void ChangePassword()
    {
        using var dlg = new ChangePasswordForm(_rt);
        if (dlg.ShowDialog(this) == DialogResult.OK) Ui.Info(this, "Password changed.");
    }

    private void Logout()
    {
        LogoutRequested = true;
        Close();
    }

    public IReadOnlyList<string> PageNames => _items.Select(i => i.Key).ToList();
    public bool IsPageEnabled(string key) => _items.First(i => i.Key == key).Button.Enabled;
    public void Navigate(string key) => Show(_items.First(i => i.Key == key));

    private async void Show(NavItem item)
    {
        _current = item;
        foreach (var i in _items) i.Button.Select(i == item);
        _title.Text = item.Key;
        _content.SuspendLayout();
        var old = _view;
        _view = item.Create();
        _content.Controls.Add(_view);
        if (old != null) { _content.Controls.Remove(old); old.Dispose(); }
        _content.ResumeLayout();
        try { await _view.RefreshAsync(); }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
}
