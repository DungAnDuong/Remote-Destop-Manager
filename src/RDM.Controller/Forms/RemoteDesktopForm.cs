using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Services;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Shared.Models;
using RDM.Shared.Protocol;

namespace RDM.Controller.Forms;

/// <summary>Vùng hiển thị màn hình máy đích. Đổi tọa độ chuột về dạng chuẩn hóa theo màn hình đang xem.</summary>
public sealed class RemoteView : Control
{
    public RemoteSession Session { get; set; }
    public bool FitToWindow { get; set; } = true;
    public string Overlay { get; set; }

    public RemoteView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        TabStop = true;
    }

    /// <summary>Hình chữ nhật hiển thị, giữ đúng tỷ lệ khi Fit to Window.</summary>
    public Rectangle DisplayArea(Size remote)
    {
        if (remote.Width <= 0 || remote.Height <= 0) return ClientRectangle;
        if (!FitToWindow) return ClientRectangle;
        var scale = Math.Min((double)ClientSize.Width / remote.Width, (double)ClientSize.Height / remote.Height);
        var w = (int)(remote.Width * scale); var h = (int)(remote.Height * scale);
        return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var session = Session;
        if (session != null)
            lock (session.SurfaceLock)
            {
                var surface = session.Surface;
                if (surface != null)
                {
                    g.InterpolationMode = FitToWindow ? InterpolationMode.Bilinear : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(surface, DisplayArea(surface.Size));
                }
            }
        if (!string.IsNullOrEmpty(Overlay))
        {
            var text = TextRenderer.MeasureText(Overlay, Theme.Font(11f, true), new Size(Math.Max(100, Width - 80), 0), TextFormatFlags.WordBreak);
            var box = new Rectangle((Width - text.Width) / 2 - 16, 20, text.Width + 32, text.Height + 20);
            using (var b = new SolidBrush(Color.FromArgb(220, 20, 30, 45))) g.FillRectangle(b, box);
            TextRenderer.DrawText(g, Overlay, Theme.Font(11f, true), box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    public bool TryNormalize(Point p, out int x, out int y)
    {
        x = y = 0;
        var session = Session;
        if (session?.Surface == null) return false;
        Size size;
        lock (session.SurfaceLock)
        {
            if (session.Surface == null) return false;
            size = session.Surface.Size;
        }
        var area = DisplayArea(size);
        if (area.Width <= 1 || area.Height <= 1 || !area.Contains(p)) return false;
        x = (int)((long)(p.X - area.X) * SessionInfo.NormalizedMax / (area.Width - 1));
        y = (int)((long)(p.Y - area.Y) * SessionInfo.NormalizedMax / (area.Height - 1));
        return true;
    }

    private void SendMouse(Point p, MouseAction action, int wheel = 0)
    {
        if (TryNormalize(p, out var x, out var y)) Session?.SendMouse(new MouseInputData { Action = action, X = x, Y = y, WheelDelta = wheel });
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SendMouse(e.Location, MouseAction.Move); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Capture = true;
        var a = e.Button == MouseButtons.Left ? MouseAction.LeftDown : e.Button == MouseButtons.Right ? MouseAction.RightDown : e.Button == MouseButtons.Middle ? MouseAction.MiddleDown : (MouseAction?)null;
        if (a.HasValue) SendMouse(e.Location, a.Value);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        var a = e.Button == MouseButtons.Left ? MouseAction.LeftUp : e.Button == MouseButtons.Right ? MouseAction.RightUp : e.Button == MouseButtons.Middle ? MouseAction.MiddleUp : (MouseAction?)null;
        // Thả chuột ngoài vùng ảnh vẫn phải gửi đi, nếu không nút sẽ kẹt trên máy đích.
        if (a.HasValue)
        {
            var area = Session?.Surface != null ? DisplayArea(Session.Surface.Size) : ClientRectangle;
            SendMouse(new Point(Math.Max(area.Left, Math.Min(area.Right - 1, e.X)), Math.Max(area.Top, Math.Min(area.Bottom - 1, e.Y))), a.Value);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); SendMouse(e.Location, MouseAction.Wheel, e.Delta); }

    /// <summary>
    /// Ký tự đến qua WM_CHAR chính là ký tự do Unikey/IME tạo ra (VK_PACKET, xem KeyboardHook): gửi nguyên chữ,
    /// nhờ đó gõ tiếng Việt ở máy Controller ra đúng chữ trên máy đích.
    /// </summary>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (!char.IsControl(e.KeyChar)) Session?.SendText(e.KeyChar.ToString());
        e.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData) => true;
}

/// <summary>
/// Bắt phím bằng low-level hook khi cửa sổ Remote đang được dùng, nhờ đó Alt+Tab và phím Windows tới được máy đích.
/// Phím VK_PACKET do Unikey/IME sinh ra được cho đi tiếp để thành WM_CHAR. Ctrl+Alt+Del không bắt được: dùng nút Special Keys.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, VK_PACKET = 0xE7, LLKHF_EXTENDED = 1;
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public int Vk, Scan, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

    private readonly HookProc _proc;
    private readonly Func<bool> _active;
    private readonly Action<KeyInputData> _onKey;
    private readonly HashSet<(int vk, int scan, bool ext)> _down = new HashSet<(int, int, bool)>();
    private IntPtr _hook;

    public KeyboardHook(Func<bool> active, Action<KeyInputData> onKey)
    {
        _active = active; _onKey = onKey;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public bool Installed => _hook != IntPtr.Zero;

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _active())
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.Vk != VK_PACKET)
            {
                var down = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                var extended = (k.Flags & LLKHF_EXTENDED) != 0;
                var id = (k.Vk, k.Scan, extended);
                if (down) _down.Add(id); else _down.Remove(id);
                _onKey(new KeyInputData { VirtualKey = k.Vk & 0xFF, ScanCode = k.Scan & 0xFFFF, Extended = extended, Down = down });
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Rời cửa sổ khi còn giữ phím: phải thả trên máy đích, nếu không phím sẽ kẹt.</summary>
    public void ReleaseHeld()
    {
        foreach (var (vk, scan, ext) in _down.ToList()) _onKey(new KeyInputData { VirtualKey = vk & 0xFF, ScanCode = scan & 0xFFFF, Extended = ext, Down = false });
        _down.Clear();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}

/// <summary>Cửa sổ Remote Desktop (Phần 5).</summary>
public sealed class RemoteDesktopForm : Form
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly RdmRuntime _rt;
    private readonly string _remoteId, _name;
    private readonly Func<Task<RemoteSession>> _open;
    private readonly long _historyId;
    private readonly ActiveSessionInfo _info;
    private readonly RemoteView _view = new RemoteView();
    private readonly Panel _scroller = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Label _status = new Label { Dock = DockStyle.Bottom, AutoSize = false, ForeColor = Theme.SidebarText, BackColor = Theme.Sidebar, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
    // Các ComboBox chỉ còn giữ trạng thái lựa chọn; mọi điều khiển nằm trong menu của biểu tượng khay.
    private readonly ComboBox _monitor = Ui.Combo(220), _quality = Ui.Combo(130), _scale = Ui.Combo(150), _viewMode = Ui.Combo(120);
    // Nút menu nổi thay cho biểu tượng khay riêng: toàn ứng dụng chỉ còn một biểu tượng khay (ở cửa sổ chính).
    private readonly Button _menuBtn = new Button { Text = "☰ Menu", FlatStyle = FlatStyle.Flat, AutoSize = false };
    private readonly ContextMenuStrip _menu = new ContextMenuStrip();
    private ToolStripMenuItem _monitorMenu, _fullScreenItem, _reconnect;
    private readonly Timer _statsTimer = new Timer { Interval = 1000 };
    // Nút thoát toàn màn hình, chỉ hiện khi đang full: ở chế độ này không có thanh tiêu đề hay thanh công cụ.
    private readonly Button _exitFull = new Button { Text = "Exit Full Screen  (F11)", Visible = false, FlatStyle = FlatStyle.Flat, AutoSize = false, Size = new Size(Dpi.S(170), Dpi.S(30)) };
    private readonly ChatPanel _chat = new ChatPanel();
    private Form _chatWindow;
    // Thanh nút chọn nhanh màn hình, nổi ở đáy khi máy đích có nhiều màn hình.
    private readonly FlowLayoutPanel _monitorBar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.FromArgb(40, 52, 70), Padding = new Padding(Dpi.S(3)) };
    private bool _ctrlDown;
    private KeyboardHook _hook;
    private RemoteSession _session;
    private bool _invalidatePending, _fullScreen, _closing;
    private FormWindowState _savedState;
    private FormBorderStyle _savedBorder;
    private double _zoom = 1;
    private bool _registered = true;
    // Cửa sổ nhỏ đếm ngược khi mất kết nối ngoài ý muốn; hết giờ thì đóng cửa sổ remote.
    private const int ReconnectWindowSeconds = 60;
    private ConnectionLostForm _lost;

    public RemoteDesktopForm(RdmRuntime rt, string remoteId, string name, RemoteSession session, Func<Task<RemoteSession>> open, long historyId)
    {
        _rt = rt; _remoteId = remoteId; _name = name; _open = open; _historyId = historyId;
        Text = $"{name} – Remote Desktop";
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Size = new Size(Dpi.S(1100), Dpi.S(760));
        MinimumSize = new Size(Dpi.S(700), Dpi.S(420));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = false;

        BuildTray();
        _status.Height = Dpi.S(24);
        _status.Padding = new Padding(Dpi.S(8), 0, 0, 0);
        _view.Dock = DockStyle.Fill;
        _scroller.Controls.Add(_view);
        Controls.Add(_scroller);
        Controls.Add(_status);

        BuildChat();
        _exitFull.BackColor = Theme.Sidebar;
        _exitFull.ForeColor = Color.White;
        _exitFull.FlatAppearance.BorderSize = 0;
        _exitFull.Font = Theme.Font(9f, true);
        _exitFull.Click += (s, e) => ToggleFullScreen();
        Controls.Add(_exitFull);
        Controls.Add(_monitorBar);
        _menuBtn.Location = new Point(Dpi.S(8), Dpi.S(8));
        Controls.Add(_menuBtn);
        Resize += (s, e) => { PositionExitFull(); PositionMonitorBar(); _menuBtn.BringToFront(); };

        _info = new ActiveSessionInfo
        {
            RemoteId = remoteId, DeviceName = name, StartedUtc = DateTime.UtcNow, Fingerprint = session.HostFingerprint,
            Close = () =>
            {
                if (InvokeRequired) BeginInvoke(new Action(Close));
                else Close();
            },
            Stats = () => { var s = _session; return s == null ? "" : $"{s.Fps:0} FPS, {s.LatencyMs} ms, {s.KilobytesPerSecond:0} KB/s"; }
        };
        SessionRegistry.Add(_info);

        _statsTimer.Tick += (s, e) => UpdateStatus();
        Shown += (s, e) =>
        {
            Attach(session);
            _statsTimer.Start();
            _view.Focus();
            _hook = new KeyboardHook(IsCapturingKeys, OnHookKey);
            if (!_hook.Installed) _view.Overlay = "Could not install the keyboard hook: Alt+Tab and the Windows key will not reach the remote computer.";
            _menuBtn.BringToFront();
        };
        Deactivate += (s, e) => _hook?.ReleaseHeld();
        FormClosed += (s, e) => OnClosedCleanup();
    }

    private bool IsCapturingKeys() => _session != null && !_session.IsClosed && GetForegroundWindow() == Handle && _view.Focused;

    private const int VkF11 = 0x7A;

    /// <summary>
    /// Xử lý phím tại máy điều khiển trước khi gửi: F11 bật/tắt toàn màn hình; Ctrl+1..9 chọn màn hình, Ctrl+0 chọn tất cả.
    /// Các phím này không gửi sang máy đích; còn lại gửi bình thường.
    /// </summary>
    private void OnHookKey(KeyInputData k)
    {
        var vk = k.VirtualKey;
        if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) { _ctrlDown = k.Down; _session?.SendKey(k); return; }
        if (vk == VkF11) { if (k.Down) BeginInvoke(new Action(ToggleFullScreen)); return; }
        if (k.Down && _ctrlDown && TryDigit(vk, out var digit))
        {
            BeginInvoke(new Action(() => ApplyMonitor(digit == 0 ? AllMonitorIndex() : digit - 1)));
            return;
        }
        _session?.SendKey(k);
    }

    private static bool TryDigit(int vk, out int digit)
    {
        if (vk >= 0x30 && vk <= 0x39) { digit = vk - 0x30; return true; }
        if (vk >= 0x60 && vk <= 0x69) { digit = vk - 0x60; return true; }
        digit = 0;
        return false;
    }

    private void PositionExitFull()
    {
        if (!_exitFull.Visible) return;
        _exitFull.Location = new Point((ClientSize.Width - _exitFull.Width) / 2, Dpi.S(2));
    }

    /// <summary>Mọi điều khiển phiên nằm trong menu chuột phải của biểu tượng khay, để vùng xem màn hình rộng hết cỡ.</summary>
    private void BuildTray()
    {
        _fullScreenItem = new ToolStripMenuItem("Full Screen", null, (s, e) => ToggleFullScreen()) { CheckOnClick = false };
        _menu.Items.Add(_fullScreenItem);

        _monitorMenu = new ToolStripMenuItem("Monitor") { Enabled = false };
        _menu.Items.Add(_monitorMenu);

        _viewMode.Items.AddRange(new object[] { "Fit to Window", "50%", "75%", "100%", "150%", "200%" });
        _viewMode.SelectedIndex = 0;
        _menu.Items.Add(RadioSubmenu("View", _viewMode, () => ApplyViewMode()));

        _scale.Items.AddRange(new object[] { "Resolution 100%", "75%", "50%" });
        _scale.SelectedIndex = 0;
        _menu.Items.Add(RadioSubmenu("Resolution", _scale, SendQuality));

        _quality.Items.AddRange(new object[] { "Quality: Auto", "Low", "Medium", "High", "Lossless" });
        _quality.SelectedIndex = 0;
        _menu.Items.Add(RadioSubmenu("Quality", _quality, SendQuality));

        _menu.Items.Add(new ToolStripSeparator());

        var special = new ToolStripMenuItem("Special Keys");
        void Special(string text, SpecialKey key) => special.DropDownItems.Add(text, null, (s, e) => { _session?.SendSpecial(key); _view.Focus(); });
        Special("Ctrl+Alt+Del", SpecialKey.CtrlAltDel);
        Special("Windows Key", SpecialKey.WindowsKey);
        Special("Alt+Tab", SpecialKey.AltTab);
        Special("Ctrl+Esc", SpecialKey.CtrlEsc);
        Special("Print Screen", SpecialKey.PrintScreen);
        _menu.Items.Add(special);

        _menu.Items.Add("Keyboard", null, (s, e) =>
        {
            _view.Focus();
            _view.Overlay = _hook?.Installed == true ? "The keyboard is sent to the remote computer while the screen area has focus." : "The keyboard hook is not active.";
            _view.Invalidate();
        });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("File Manager", null, (s, e) => OpenFileManager());
        _menu.Items.Add("Chat", null, (s, e) => OpenChat());

        _menu.Items.Add(new ToolStripSeparator());
        _reconnect = new ToolStripMenuItem("Reconnect", null, async (s, e) => await ReconnectAsync()) { Visible = false };
        _menu.Items.Add(_reconnect);
        _menu.Items.Add("Disconnect", null, (s, e) => Close());

        // Nút menu nổi luôn hiển thị (kể cả khi full), mở đúng menu điều khiển này.
        _menuBtn.Size = new Size(Dpi.S(92), Dpi.S(30));
        _menuBtn.BackColor = Theme.Sidebar;
        _menuBtn.ForeColor = Color.White;
        _menuBtn.FlatAppearance.BorderSize = 0;
        _menuBtn.Font = Theme.Font(9f, true);
        _menuBtn.Click += (s, e) => _menu.Show(_menuBtn, new Point(0, _menuBtn.Height));
    }

    private void BuildChat()
    {
        // Khung chat sống trong cửa sổ riêng (tách rời khỏi màn hình remote), tạo khi mở lần đầu.
        _chat.Dock = DockStyle.Fill;
        _chat.PeerName = _name;
        _chat.Title = "Chat - " + _name;
        _chat.ChatEntered += text =>
        {
            var session = _session;
            if (session == null) return;
            _chat.AddOutgoing(text);
            _ = SendQuiet(session.SendChatAsync(text));
        };
        _chat.SendFileRequested += PickAndSendFile;
        _chat.CloseRequested += () => _chatWindow?.Hide();
        _chat.PauseRequested += id => _session?.PauseTransfer(id);
        _chat.ResumeRequested += id => _session?.ResumeTransfer(id);
        _chat.CancelRequested += id => _session?.CancelTransfer(id);
    }

    // Mở (hoặc đưa lên trước) cửa sổ chat rời. Menu Chat gọi hàm này.
    private void OpenChat()
    {
        if (_chatWindow == null || _chatWindow.IsDisposed)
        {
            _chatWindow = new Form
            {
                Text = "Chat - " + _name,
                Icon = Theme.AppIcon,
                Font = Theme.Font(),
                BackColor = Theme.Surface,
                StartPosition = FormStartPosition.Manual,
                Size = new Size(Dpi.S(360), Dpi.S(540)),
                MinimumSize = new Size(Dpi.S(260), Dpi.S(340)),
            };
            _chatWindow.Location = new Point(Math.Max(0, Bounds.Right - _chatWindow.Width - Dpi.S(40)), Math.Max(0, Bounds.Top + Dpi.S(80)));
            _chatWindow.Controls.Add(_chat);
            // Người dùng tự đóng cửa sổ chat thì chỉ ẩn đi; nhưng khi cửa sổ remote đóng (owner) thì để nó đóng theo.
            _chatWindow.FormClosing += (s, e) => { if (!_closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; _chatWindow.Hide(); _view.Focus(); } };
        }
        if (!_chatWindow.Visible) _chatWindow.Show(this);
        if (_chatWindow.WindowState == FormWindowState.Minimized) _chatWindow.WindowState = FormWindowState.Normal;
        _chatWindow.Activate();
        _chat.FocusInput();
    }

    // Đưa chat lên khi có tin/tiến độ đến, nhưng không cướp tiêu điểm nếu người dùng đang xem màn hình.
    private void EnsureChatVisible()
    {
        if (_chatWindow == null || _chatWindow.IsDisposed || !_chatWindow.Visible) OpenChat();
    }

    private void PickAndSendFile()
    {
        var session = _session;
        if (session == null) return;
        using var dialog = new OpenFileDialog { Title = "Send a file to the remote computer", Multiselect = true };
        if (dialog.ShowDialog(_chatWindow ?? (IWin32Window)this) != DialogResult.OK) return;
        EnsureChatVisible();
        foreach (var path in dialog.FileNames) _ = SendQuiet(session.SendFileAsync(path));
    }

    private FileManagerForm _fileManager;

    private void OpenFileManager()
    {
        var session = _session;
        if (session == null) return;
        if (_fileManager != null && !_fileManager.IsDisposed) { _fileManager.Activate(); return; }
        _fileManager = new FileManagerForm(session, _name);
        _fileManager.FormClosed += (s, e) => _fileManager = null;
        _fileManager.Show(this);
    }

    private static async Task SendQuiet(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>Menu con dạng chọn một, đồng bộ với ComboBox giữ trạng thái để dùng lại logic sẵn có.</summary>
    private static ToolStripMenuItem RadioSubmenu(string title, ComboBox combo, Action onChange)
    {
        var parent = new ToolStripMenuItem(title);
        for (var i = 0; i < combo.Items.Count; i++)
        {
            var item = new ToolStripMenuItem(combo.Items[i].ToString()) { Checked = i == combo.SelectedIndex };
            item.Click += (s, e) =>
            {
                combo.SelectedIndex = parent.DropDownItems.IndexOf(item);
                foreach (ToolStripMenuItem x in parent.DropDownItems) x.Checked = false;
                item.Checked = true;
                onChange();
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private void Attach(RemoteSession session)
    {
        _session = session;
        _view.Session = session;
        _view.Overlay = null;
        session.FrameReady += OnFrameReady;
        session.MonitorsChanged += () => Ui_(RefreshMonitors);
        session.Notice += text => Ui_(() => { _view.Overlay = string.IsNullOrEmpty(text) ? null : text; _view.Invalidate(); });
        session.ChatReceived += m => Ui_(() => { EnsureChatVisible(); _chat.AddIncoming(m.Text); });
        // File qua chat (ToFolder=false) hiện ở cửa sổ chat; file qua trình quản lý file (ToFolder=true) chỉ hiện ở đó.
        session.TransferChanged += p => Ui_(() => { if (!p.ToFolder && _chatWindow != null && !_chatWindow.IsDisposed) _chat.UpdateTransfer(p); });
        session.Closed += (reason, byUser) => Ui_(() => OnSessionClosed(session, reason, byUser));
        session.Start();
        SendQuality();
    }

    private void Ui_(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Gộp yêu cầu vẽ: nếu UI đang bận thì khung sau vẽ đè khung trước, không tích tụ độ trễ.</summary>
    private void OnFrameReady()
    {
        if (_invalidatePending) return;
        _invalidatePending = true;
        Ui_(() =>
        {
            _invalidatePending = false;
            ApplyViewMode(true);
            _view.Invalidate();
        });
    }

    private void RefreshMonitors()
    {
        if (_session == null) return;
        var monitors = _session.Monitors;
        _monitor.Items.Clear();
        foreach (var m in monitors) _monitor.Items.Add(m.Label);
        if (_session.CurrentMonitor < _monitor.Items.Count) _monitor.SelectedIndex = _session.CurrentMonitor;

        _monitorMenu.DropDownItems.Clear();
        _monitorMenu.Enabled = _monitor.Items.Count > 0;
        for (var i = 0; i < _monitor.Items.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(_monitor.Items[i].ToString()) { Checked = i == _monitor.SelectedIndex };
            item.Click += (s, e) => ApplyMonitor(index);
            _monitorMenu.DropDownItems.Add(item);
        }

        // Nút chọn nhanh màn hình: chỉ hiện khi có nhiều hơn một màn hình.
        _monitorBar.Controls.Clear();
        for (var i = 0; i < monitors.Count; i++)
        {
            var index = i;
            var b = new Button { Text = monitors[i].ShortLabel, Width = Dpi.S(monitors[i].IsAll ? 48 : 34), Height = Dpi.S(26), FlatStyle = FlatStyle.Flat, Margin = new Padding(Dpi.S(2)), Font = Theme.Font(9f, true), ForeColor = Color.White };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (s, e) => ApplyMonitor(index);
            _monitorBar.Controls.Add(b);
        }
        _monitorBar.Visible = monitors.Count > 1;
        HighlightMonitorBar(_monitor.SelectedIndex);
        PositionMonitorBar();
        if (_monitorBar.Visible) _monitorBar.BringToFront();
    }

    private void ApplyMonitor(int index)
    {
        if (_monitor.Items.Count == 0) return;
        index = Math.Max(0, Math.Min(index, _monitor.Items.Count - 1));
        _monitor.SelectedIndex = index;
        for (var i = 0; i < _monitorMenu.DropDownItems.Count; i++)
            ((ToolStripMenuItem)_monitorMenu.DropDownItems[i]).Checked = i == index;
        HighlightMonitorBar(index);
        _session?.SelectMonitor(index);
        _view.Focus();
    }

    private void HighlightMonitorBar(int index)
    {
        for (var i = 0; i < _monitorBar.Controls.Count; i++)
            _monitorBar.Controls[i].BackColor = i == index ? Theme.Accent : Theme.Sidebar;
    }

    private void PositionMonitorBar()
    {
        if (!_monitorBar.Visible) return;
        var statusH = _status.Visible ? _status.Height : 0;
        _monitorBar.Location = new Point((ClientSize.Width - _monitorBar.Width) / 2, ClientSize.Height - _monitorBar.Height - statusH - Dpi.S(6));
    }

    /// <summary>Số thứ tự màn hình tổng hợp (All), hoặc màn hình cuối nếu không có mục tổng hợp.</summary>
    private int AllMonitorIndex()
    {
        var monitors = _session?.Monitors;
        if (monitors == null || monitors.Count == 0) return 0;
        for (var i = 0; i < monitors.Count; i++) if (monitors[i].IsAll) return i;
        return monitors.Count - 1;
    }

    private void SendQuality()
    {
        var quality = new[] { 0, 35, 60, 85, 100 }[Math.Max(0, _quality.SelectedIndex)];
        var scale = new[] { 100, 75, 50 }[Math.Max(0, _scale.SelectedIndex)];
        _session?.SetQuality(quality, 30, scale);
    }

    private void ApplyViewMode(bool sizeOnly = false)
    {
        var fit = _viewMode.SelectedIndex == 0;
        if (!sizeOnly) _zoom = new[] { 1, 0.5, 0.75, 1, 1.5, 2 }[_viewMode.SelectedIndex];
        Size? remote = null;
        var session = _session;
        if (session != null) lock (session.SurfaceLock) remote = session.Surface?.Size;
        _view.FitToWindow = fit;
        _scroller.AutoScroll = !fit;
        if (fit)
        {
            if (_view.Dock != DockStyle.Fill) _view.Dock = DockStyle.Fill;
        }
        else if (remote.HasValue)
        {
            var size = new Size((int)(remote.Value.Width * _zoom), (int)(remote.Value.Height * _zoom));
            if (_view.Dock != DockStyle.None) _view.Dock = DockStyle.None;
            if (_view.Size != size) _view.Size = size;
        }
        if (!sizeOnly) _view.Focus();
    }

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _savedState = WindowState; _savedBorder = FormBorderStyle;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _savedBorder;
            WindowState = _savedState;
        }
        // Ẩn thanh trạng thái khi full để màn hình remote chiếm trọn chỗ.
        _status.Visible = !_fullScreen;
        _fullScreenItem.Checked = _fullScreen;
        _exitFull.Visible = _fullScreen;
        if (_fullScreen) { PositionExitFull(); _exitFull.BringToFront(); }
        PositionMonitorBar();
        _view.Focus();
    }

    private void UpdateStatus()
    {
        var s = _session;
        if (s == null || s.IsClosed) return;
        var size = "";
        lock (s.SurfaceLock) if (s.Surface != null) size = $"{s.Surface.Width}x{s.Surface.Height}";
        var keys = _view.Focused ? "keyboard: sending" : "keyboard: click the screen to send";
        _status.Text = $"{s.Fps:0} FPS  |  Latency {s.LatencyMs} ms  |  Quality {s.Quality}  |  {s.KilobytesPerSecond:0} KB/s  |  {size}  |  via {s.ConnectionType}, E2EE AES-256-GCM, device {s.HostFingerprint}  |  {keys}";
    }

    private void OnSessionClosed(RemoteSession session, string reason, bool byUser)
    {
        if (_closing || session != _session) return;
        _hook?.ReleaseHeld();
        _view.Overlay = reason;
        _view.Invalidate();
        _status.Text = reason;
        if (byUser)
        {
            // Kết thúc có chủ ý: không còn là phiên đang hoạt động, dù cửa sổ vẫn mở để người dùng đọc lý do.
            Unregister();
            _reconnect.Visible = true;
            return;
        }
        _reconnect.Visible = true;
        StartReconnectCountdown(session);
    }

    // Mở cửa sổ đếm ngược và liên tục thử kết nối lại trong thời gian đó.
    private void StartReconnectCountdown(RemoteSession session)
    {
        if (_lost != null && !_lost.IsDisposed) return;
        _lost = new ConnectionLostForm(_name, ReconnectWindowSeconds);
        _lost.ReconnectNow += () => _ = ReconnectAsync();
        _lost.DisconnectNow += () => { CloseLost(); Close(); };
        _lost.Expired += () => { CloseLost(); if (!_closing) Close(); };
        _lost.Show(this);
        _ = ReconnectLoop(session);
    }

    private async Task ReconnectLoop(RemoteSession session)
    {
        while (!_closing && _lost != null && !_lost.IsDisposed && _session == session && session.IsClosed)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (_closing || _lost == null || _lost.IsDisposed || _session != session || !session.IsClosed) return;
            if (await ReconnectAsync()) return;
        }
    }

    private void CloseLost()
    {
        var f = _lost;
        _lost = null;
        if (f != null && !f.IsDisposed) { try { f.Close(); } catch (Exception) { } }
    }

    private async Task<bool> ReconnectAsync()
    {
        _reconnect.Enabled = false;
        _view.Overlay = "Reconnecting...";
        _view.Invalidate();
        _lost?.SetStatus("Reconnecting...");
        try
        {
            var fresh = await _open();
            if (_closing) { fresh.Dispose(); return false; }
            var old = _session;
            if (!_registered) { SessionRegistry.Add(_info); _registered = true; }
            Attach(fresh);
            old?.Dispose();
            _reconnect.Visible = false;
            CloseLost();
            _rt.Audit.Write(AuditEvents.SessionStarted, _rt.Session.Username, remoteId: _remoteId, deviceName: _name, details: "Reconnected");
            return true;
        }
        catch (Exception ex) when (ex is SessionRejectedException || ex is RDM.Shared.Network.HostVerificationException || ex is System.Net.Sockets.SocketException || ex is TimeoutException || ex is System.IO.IOException || ex is ProtocolException || ex is ObjectDisposedException)
        {
            _view.Overlay = ex is SessionRejectedException || ex is RDM.Shared.Network.HostVerificationException ? ex.Message : "Could not reconnect yet.";
            _view.Invalidate();
            _reconnect.Visible = true;
            // Bị từ chối (sai mật khẩu, chủ máy từ chối) thì không tự thử lại: dừng đếm ngược và báo lý do.
            if (ex is SessionRejectedException || ex is RDM.Shared.Network.HostVerificationException) CloseLost();
            else _lost?.SetStatus("Could not reconnect yet. Retrying...");
            return false;
        }
        finally { if (!IsDisposed) _reconnect.Enabled = true; }
    }

    private void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        SessionRegistry.Remove(_info);
    }

    private void OnClosedCleanup()
    {
        _closing = true;
        _statsTimer.Dispose();
        _lost?.Close();
        if (_chatWindow != null && !_chatWindow.IsDisposed) { try { _chatWindow.Close(); } catch (Exception) { } }
        _hook?.ReleaseHeld();
        _hook?.Dispose();
        _session?.Dispose();
        Unregister();
        _rt.History.Finish(_historyId, "Disconnected");
        _rt.Audit.Write(AuditEvents.SessionEnded, _rt.Session.Username, remoteId: _remoteId, deviceName: _name);
    }
}

/// <summary>Cửa sổ nhỏ báo mất kết nối và đếm ngược thời gian còn lại để kết nối lại tự động.</summary>
public sealed class ConnectionLostForm : Form
{
    private readonly Timer _timer = new Timer { Interval = 1000 };
    private readonly Label _countdown = new Label { AutoSize = false, Dock = DockStyle.Top, Height = Dpi.S(28), UseMnemonic = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(10f, true), ForeColor = Theme.Text };
    private readonly Label _state = new Label { AutoSize = false, Dock = DockStyle.Top, Height = Dpi.S(22), UseMnemonic = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(9f), ForeColor = Theme.TextSecondary };
    private int _left;

    /// <summary>Người dùng bấm thử lại ngay.</summary>
    public event Action ReconnectNow;
    /// <summary>Người dùng chọn ngắt và đóng cửa sổ remote.</summary>
    public event Action DisconnectNow;
    /// <summary>Hết thời gian đếm ngược mà chưa kết nối lại được.</summary>
    public event Action Expired;

    public ConnectionLostForm(string name, int seconds)
    {
        _left = seconds;
        Text = "Connection lost";
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        BackColor = Theme.Page;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Dpi.S(380), Dpi.S(176));

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(Dpi.S(18)), BackColor = Color.Transparent };
        var title = new Label { Text = "Lost connection to " + name, AutoSize = false, Width = Dpi.S(344), Height = Dpi.S(24), UseMnemonic = false, Font = Theme.Font(11f, true), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleCenter };
        _countdown.Width = _state.Width = Dpi.S(344);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent, Margin = new Padding(0, Dpi.S(12), 0, 0), Anchor = AnchorStyles.None };
        var retry = new ModernButton("Reconnect now", ButtonKind.Primary);
        retry.Click += (s, e) => ReconnectNow?.Invoke();
        var stop = new ModernButton("Disconnect", ButtonKind.Danger) { Margin = new Padding(Dpi.S(8), 0, 0, 0) };
        stop.Click += (s, e) => DisconnectNow?.Invoke();
        buttons.Controls.Add(retry);
        buttons.Controls.Add(stop);
        flow.Controls.Add(title);
        flow.Controls.Add(_countdown);
        flow.Controls.Add(_state);
        flow.Controls.Add(buttons);
        Controls.Add(flow);

        UpdateCountdown();
        _state.Text = "Reconnecting...";
        _timer.Tick += (s, e) =>
        {
            if (--_left <= 0) { _timer.Stop(); Expired?.Invoke(); }
            else UpdateCountdown();
        };
        Shown += (s, e) => { _timer.Start(); Activate(); };
        FormClosed += (s, e) => _timer.Dispose();
    }

    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => _state.Text = text)); } catch (Exception) { } }
        else _state.Text = text;
    }

    private void UpdateCountdown() => _countdown.Text = $"Reconnecting automatically — {_left}s left";
}
