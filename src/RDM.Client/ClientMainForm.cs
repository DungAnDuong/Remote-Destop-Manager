using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Theming;
using RDM.Shared.Common;
using System.Collections.Generic;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Client;

/// <summary>Phần "Cho phép điều khiển" của RDM (Phần 12). Tiến trình hiện rõ trên màn hình; không có chế độ chạy ẩn.</summary>
public sealed class AllowControlPanel : UserControl
{
    public const string AppTitle = "RDM";
    private readonly ClientStateStore _store;
    private readonly ClientState _state;
    private readonly ClientAuthentication _auth;
    private readonly ConnectionManager _connection;
    private readonly ActivityLog _log;
    private readonly StartupManager _startup = new StartupManager();
    private readonly DeviceInfo _info;
    private readonly SessionManager _sessions;
    private string _controllingUser;
    private HostSession _hostSession;
    private ChatWindow _chatWindow;

    /// <summary>Phát khi phiên điều khiển bắt đầu (tên người điều khiển) hoặc kết thúc (null), để biểu tượng khay cập nhật.</summary>
    public event Action<string> ControllerChanged;

    /// <summary>Ngắt mọi phiên đang điều khiển máy này; dùng cho menu của biểu tượng khay.</summary>
    public void DisconnectAll() => _sessions.DisconnectAll();

    private readonly Label _remoteId = new Label { AutoSize = true, UseMnemonic = false };
    private readonly Label _status = new Label { AutoSize = true, UseMnemonic = false };
    private Label _deviceInfo;
    private readonly ListBox _history = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };

    public AllowControlPanel(ClientStateStore store, ClientState state, ClientAuthentication auth, ConnectionManager connection, ActivityLog log, DeviceInfo info, SessionManager sessions)
    {
        _store = store; _state = state; _auth = auth; _connection = connection; _log = log; _info = info; _sessions = sessions;
        Font = Theme.Font();
        BackColor = Theme.Page;
        Dock = DockStyle.Fill;
        BuildUi();
        _connection.Changed += OnConnectionChanged;
        _sessions.AskOwner = AskOwnerAsync;
        _sessions.Changed += OnConnectionChanged;
        _sessions.Activity += m => { _log.Write(m); OnConnectionChanged(); };
        Load += (s, e) =>
        {
            TryStartListening();
            _connection.Start();
            UpdateView();
        };
    }

    /// <summary>Dừng nhận kết nối và ngắt mọi phiên khi đóng chương trình.</summary>
    public void Shutdown()
    {
        _connection.Changed -= OnConnectionChanged;
        _sessions.Changed -= OnConnectionChanged;
        _sessions.AskOwner = null;
        _sessions.Dispose();
        if (_hostSession != null) { _hostSession.ChatReceived -= OnHostChat; _hostSession.TransferChanged -= OnHostTransfer; _hostSession = null; }
        _chatWindow?.Dispose();
        ControllerChanged?.Invoke(null);
    }

    private void BuildUi()
    {
        // Cửa sổ chính chỉ còn thẻ Remote ID; Thiết bị và Nhật ký được đưa vào tab ở đáy (do HomeForm tạo),
        // còn Quyền truy cập và cấu hình nằm trong form Cài đặt (mở từ menu).
        var idCard = new Card("Allow Remote Control: This Computer's Remote ID");
        idCard.Dock = DockStyle.Fill;
        var idFlow = Flow();
        _remoteId.Font = Theme.Font(26f, true);
        _remoteId.ForeColor = Theme.Text;
        _remoteId.Margin = new Padding(0, 0, Dpi.S(16), 0);
        var copy = new ModernButton("Copy ID");
        copy.Click += (s, e) => { Clipboard.SetText(_state.RemoteId); };
        idFlow.Controls.Add(_remoteId);
        idFlow.Controls.Add(copy);
        _status.Font = Theme.Font(9.5f);
        _status.MaximumSize = new Size(Dpi.S(460), 0);
        _status.ForeColor = Theme.TextSecondary;
        _status.Margin = new Padding(0, Dpi.S(6), 0, 0);
        idFlow.SetFlowBreak(copy, true);
        idFlow.Controls.Add(_status);
        idCard.Controls.Add(idFlow);
        Controls.Add(idCard);

        // Nhãn thiết bị và danh sách nhật ký chuẩn bị sẵn để HomeForm đưa vào các tab.
        var fingerprint = DeviceIdentity.Fingerprint(DeviceIdentity.FromSeed(_state.IdentitySeed).PublicKey);
        _deviceInfo = Text2($"Device Name:  {_info.Name}\nOperating System:  {_info.OperatingSystem} ({_info.Architecture}), {(Environment.Is64BitProcess ? "x64" : "x86")} build\nIP Address:  {_info.LocalIp ?? "–"}\nFingerprint:  {fingerprint}");
        _deviceInfo.Padding = new Padding(Dpi.S(8));
        _history.BackColor = Theme.Surface;
        _history.ForeColor = Theme.TextSecondary;
        _history.Font = Theme.Font(8.5f);
    }

    /// <summary>Nội dung tab "Device" cho cửa sổ chính.</summary>
    public Control CreateDeviceView()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Dpi.S(6)) };
        panel.Controls.Add(_deviceInfo);
        return panel;
    }

    /// <summary>Nội dung tab "Connection History" cho cửa sổ chính.</summary>
    public Control CreateHistoryView()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Dpi.S(4)) };
        panel.Controls.Add(_history);
        return panel;
    }

    private static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, WrapContents = true };

    private static Label Text2(string text) => new Label { Text = text, Dock = DockStyle.Fill, UseMnemonic = false, Font = Theme.Font(9f), ForeColor = Theme.TextSecondary, BackColor = Color.Transparent };

    private void OnConnectionChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(UpdateView));
    }

    private void UpdateView()
    {
        if (IsDisposed) return;
        _remoteId.Text = RemoteIdFormat.Display(_state.RemoteId);
        _status.Text = "Connection Status: " + DescribeConnection();
        _history.Items.Clear();
        _history.Items.AddRange(_log.ReadRecent(100));
        UpdateSessionBar(_sessions.Sessions);
        _refreshSettings?.Invoke();
    }

    // Làm tươi các nhãn trong form Cài đặt nếu đang mở (trạng thái truy cập thay đổi theo phiên).
    private Action _refreshSettings;

    private string DescribeConnection()
    {
        if (RDM.Shared.Common.ProductMode.Managed)
        {
            // Thông báo lỗi gốc có thể chứa địa chỉ máy chủ: bản phát hành chỉ nói điều người dùng làm được.
            switch (_connection.State)
            {
                case RelayState.Online: return "✔ Online";
                case RelayState.Connecting: return "… Connecting";
                case RelayState.Rejected: return "✖ This computer cannot connect. Please contact support.";
                case RelayState.Offline: return "▲ Offline. Check your Internet connection.";
                default: return "– Not connected";
            }
        }
        if (string.IsNullOrWhiteSpace(_state.RelayHost)) return "– Relay Server is not configured. Open Settings to configure it.";
        switch (_connection.State)
        {
            case RelayState.Online: return "✔ Online";
            case RelayState.Connecting: return "… Connecting to Relay";
            case RelayState.Rejected: return "✖ Rejected: " + _connection.LastError;
            case RelayState.Offline: return "▲ Offline: " + _connection.LastError;
            default: return "– Not connected";
        }
    }

    private string DescribeAccess()
    {
        var parts = _state.AccessEnabled ? "✔ Remote Access: on" : "✖ Remote Access: OFF, all connection requests are refused";
        parts += "\nAccess password: " + (_auth.HasPassword ? "set" : "not set");
        parts += "\nUnattended Access: " + (_state.UnattendedEnabled ? "ON, connections do not need your approval" : "off, you must accept each connection");
        parts += _sessions.ListeningPort > 0 ? $"\nAccepting direct connections on port {_sessions.ListeningPort}." : "\nNot accepting direct connections.";
        return parts;
    }

    private void ToggleUnattended(IWin32Window owner)
    {
        try
        {
            if (!_state.UnattendedEnabled)
            {
                var answer = MessageBox.Show(owner, "Enable Unattended Access: anyone with the Remote ID and password can connect WITHOUT you accepting on screen.\n\nOnly enable this on a computer you own or are responsible for. Continue?",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }
            _auth.SetUnattended(!_state.UnattendedEnabled);
            _log.Write(_state.UnattendedEnabled ? "Unattended Access enabled" : "Unattended Access disabled");
        }
        catch (ClientValidationException ex) { ShowError(ex.Message); }
        catch (IOException ex) { ShowError("Could not save the settings: " + ex.Message); }
        UpdateView();
    }

    private void ToggleAccess()
    {
        try
        {
            _auth.SetAccessEnabled(!_state.AccessEnabled);
            _connection.NotifyAccessChanged(_state.AccessEnabled);
            // Tắt Remote Access là công tắc khẩn cấp: ngắt ngay mọi phiên và ngừng nhận kết nối.
            if (_state.AccessEnabled) TryStartListening();
            else { _sessions.StopListening(); _sessions.DisconnectAll(); }
            _log.Write(_state.AccessEnabled ? "Remote Access enabled" : "Remote Access disabled");
        }
        catch (IOException ex) { ShowError("Could not save the settings: " + ex.Message); }
        UpdateView();
    }

    private void ChangePassword(IWin32Window owner)
    {
        using var dialog = new AccessPasswordDialog(_auth);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        _log.Write(dialog.Cleared ? "Access password removed" : "Access password changed");
        UpdateView();
    }

    /// <summary>Mở form Cài đặt (gồm Quyền truy cập, Relay và tùy chọn chung). Gọi từ menu của cửa sổ chính.</summary>
    public void OpenSettings(IWin32Window owner)
    {
        using var dialog = new ClientSettingsDialog(_state, _startup, this);
        _refreshSettings = dialog.RefreshAccess;
        try
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            try
            {
                _store.Save(_state);
                _startup.Set(dialog.StartWithWindows, Application.ExecutablePath);
                _log.Write("Settings changed");
                _ = _connection.RestartAsync();
            }
            catch (IOException ex) { ShowError("Could not save the settings: " + ex.Message); }
            UpdateView();
        }
        finally { _refreshSettings = null; }
    }

    // --- Các thao tác Quyền truy cập, gọi từ form Cài đặt ---
    internal bool AccessEnabled => _state.AccessEnabled;
    internal bool UnattendedEnabled => _state.UnattendedEnabled;
    internal string AccessSummary() => DescribeAccess();
    internal void ToggleAccessFromSettings(IWin32Window owner) => ToggleAccess();
    internal void ToggleUnattendedFromSettings(IWin32Window owner) => ToggleUnattended(owner);
    internal void ChangePasswordFromSettings(IWin32Window owner) => ChangePassword(owner);

    private void TryStartListening()
    {
        try { _sessions.StartListening(); }
        catch (System.Net.Sockets.SocketException) { _log.Write($"Could not open port {_state.SessionPort} for incoming connections"); }
    }

    /// <summary>Báo trạng thái phiên cho biểu tượng khay của cửa sổ chính (chủ máy vẫn luôn thấy và ngắt được qua khay).</summary>
    private void UpdateSessionBar(IReadOnlyList<HostSession> sessions)
    {
        var session = sessions.Count == 0 ? null : sessions[0];
        var user = session?.ControllerUser;
        if (user != _controllingUser) { _controllingUser = user; ControllerChanged?.Invoke(user); }

        if (ReferenceEquals(session, _hostSession)) return;
        if (_hostSession != null) { _hostSession.ChatReceived -= OnHostChat; _hostSession.TransferChanged -= OnHostTransfer; }
        _hostSession = session;
        // Nghe chat và chỉ tiến độ file GỬI ĐI: khi nhận file (kể cả qua trình quản lý file) máy bị điều khiển không hiển thị gì.
        if (session != null) { session.ChatReceived += OnHostChat; session.TransferChanged += OnHostTransfer; }
    }

    private void OnHostChat(ChatMessage m)
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() => ShowChat().AddIncoming(m.Text)));
    }

    // File qua chat (ToFolder=false) hiển thị ở cửa sổ chat cả khi gửi và nhận.
    // File qua trình quản lý file (ToFolder=true) máy bị điều khiển không hiển thị gì.
    private void OnHostTransfer(FileTransferProgress p)
    {
        if (IsDisposed || p.ToFolder) return;
        BeginInvoke(new Action(() => ShowChat().UpdateTransfer(p)));
    }

    /// <summary>Mở (nếu cần) cửa sổ chat của máy được điều khiển và trả về bảng chat để ghi tiếp.</summary>
    private ChatPanel ShowChat()
    {
        if (_chatWindow == null || _chatWindow.IsDisposed)
        {
            _chatWindow = new ChatWindow(_controllingUser ?? "Controller");
            _chatWindow.Panel.ChatEntered += text =>
            {
                var s = _hostSession;
                if (s == null) return;
                _chatWindow.Panel.AddOutgoing(text);
                _ = QuietTask(s.SendChatAsync(text));
            };
            _chatWindow.Panel.SendFileRequested += () =>
            {
                var s = _hostSession;
                if (s == null) return;
                using var dialog = new OpenFileDialog { Title = "Send a file to the controller", Multiselect = true };
                if (dialog.ShowDialog(_chatWindow) != DialogResult.OK) return;
                foreach (var path in dialog.FileNames) _ = QuietTask(s.SendFileAsync(path));
            };
            _chatWindow.Panel.PauseRequested += id => _hostSession?.PauseTransfer(id);
            _chatWindow.Panel.ResumeRequested += id => _hostSession?.ResumeTransfer(id);
            _chatWindow.Panel.CancelRequested += id => _hostSession?.CancelTransfer(id);
        }
        // Chỉ đưa cửa sổ lên trước khi mới hiện lại, tránh cướp tiêu điểm theo từng khúc tiến độ.
        var wasHidden = !_chatWindow.Visible;
        if (!_chatWindow.Visible) _chatWindow.Show();
        if (_chatWindow.WindowState == FormWindowState.Minimized) _chatWindow.WindowState = FormWindowState.Normal;
        if (wasHidden) _chatWindow.Activate();
        return _chatWindow.Panel;
    }

    private static async Task QuietTask(Task t) { try { await t.ConfigureAwait(false); } catch (Exception) { } }

    /// <summary>Hỏi chủ máy; không trả lời trong thời gian quy định thì coi là từ chối.</summary>
    private Task<bool> AskOwnerAsync(ControllerPeer peer, System.Threading.CancellationToken ct)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated) { result.SetResult(false); return result.Task; }
        BeginInvoke(new Action(() =>
        {
            using var dialog = new ApprovalDialog(peer);
            result.TrySetResult(dialog.ShowDialog(this) == DialogResult.Yes);
        }));
        return result.Task;
    }

    private void ShowError(string message) => MessageBox.Show(this, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
}

/// <summary>Khung hộp thoại đơn giản cho Client.</summary>
public abstract class ClientDialog : Form
{
    protected readonly TableLayoutPanel Fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, BackColor = Color.Transparent };
    protected readonly ModernButton Ok;
    private const int FieldWidth = 380;

    protected ClientDialog(string title, string okText)
    {
        Text = title;
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        BackColor = Theme.Page;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(12));
        Fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(FieldWidth)));
        Ok = new ModernButton(okText, ButtonKind.Primary) { Margin = new Padding(Dpi.S(8), 0, 0, 0) };
        var cancel = new ModernButton("Cancel") { Margin = new Padding(Dpi.S(8), 0, 0, 0), DialogResult = DialogResult.Cancel };
        Buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Width = Dpi.S(FieldWidth), Height = Dpi.S(46), Padding = new Padding(0, Dpi.S(10), 0, 0), BackColor = Color.Transparent };
        Buttons.Controls.Add(Ok);
        Buttons.Controls.Add(cancel);
        var outer = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        outer.Controls.Add(Fields, 0, 0);
        outer.Controls.Add(Buttons, 0, 1);
        Controls.Add(outer);
        AcceptButton = Ok;
        CancelButton = cancel;
        Ok.Click += async (s, e) =>
        {
            Ok.Enabled = false;
            UseWaitCursor = true;
            try
            {
                if (await AcceptAsync()) { DialogResult = DialogResult.OK; Close(); }
            }
            catch (ClientValidationException ex) { MessageBox.Show(this, ex.Message, AllowControlPanel.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { if (!IsDisposed) { Ok.Enabled = true; UseWaitCursor = false; } }
        };
    }

    protected FlowLayoutPanel Buttons { get; }
    protected abstract Task<bool> AcceptAsync();

    protected T Field<T>(string label, T control, string hint = null) where T : Control
    {
        Fields.Controls.Add(new Label { Text = label, AutoSize = true, UseMnemonic = false, ForeColor = Theme.TextSecondary, Font = Theme.Font(9f), BackColor = Color.Transparent });
        control.Width = Dpi.S(FieldWidth);
        control.Margin = new Padding(0, 0, 0, hint == null ? Dpi.S(10) : Dpi.S(2));
        Fields.Controls.Add(control);
        if (hint != null)
            Fields.Controls.Add(new Label { Text = hint, AutoSize = true, MaximumSize = new Size(Dpi.S(FieldWidth), 0), UseMnemonic = false, ForeColor = Theme.TextMuted, Font = Theme.Font(8.5f), BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, Dpi.S(10)) });
        return control;
    }
}

/// <summary>Hỏi chủ máy có chấp nhận kết nối. Mặc định là từ chối khi hết giờ.</summary>
public sealed class ApprovalDialog : Form
{
    public const int TimeoutSeconds = 30;
    private readonly Timer _timer = new Timer { Interval = 1000 };
    private readonly Label _countdown = new Label { AutoSize = true, UseMnemonic = false };
    private int _left = TimeoutSeconds;

    public ApprovalDialog(ControllerPeer peer)
    {
        Text = "Remote Control Request";
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        BackColor = Theme.Page;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Dpi.S(460), Dpi.S(210));
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(Dpi.S(20)), BackColor = Color.Transparent };
        flow.Controls.Add(new Label { Text = peer.Username + " wants to view and control this computer.", AutoSize = true, MaximumSize = new Size(Dpi.S(420), 0), UseMnemonic = false, Font = Theme.Font(11f, true), ForeColor = Theme.Text });
        flow.Controls.Add(new Label { Text = "Controller fingerprint: " + peer.Fingerprint, AutoSize = true, UseMnemonic = false, Font = Theme.Font(9f), ForeColor = Theme.TextSecondary, Margin = new Padding(0, Dpi.S(8), 0, Dpi.S(4)) });
        _countdown.Font = Theme.Font(9f);
        _countdown.ForeColor = Theme.TextSecondary;
        flow.Controls.Add(_countdown);
        var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, Dpi.S(14), 0, 0) };
        var accept = new ModernButton("Accept", ButtonKind.Primary) { DialogResult = DialogResult.Yes };
        var reject = new ModernButton("Reject", ButtonKind.Danger) { DialogResult = DialogResult.No };
        buttons.Controls.Add(reject);
        buttons.Controls.Add(accept);
        flow.Controls.Add(buttons);
        Controls.Add(flow);
        CancelButton = reject;
        UpdateCountdown();
        _timer.Tick += (s, e) =>
        {
            if (--_left <= 0) { DialogResult = DialogResult.No; Close(); }
            else UpdateCountdown();
        };
        Shown += (s, e) => { _timer.Start(); Activate(); };
        FormClosed += (s, e) => _timer.Dispose();
    }

    private void UpdateCountdown() => _countdown.Text = $"Automatically rejected in {_left} seconds. Only accept if you know this person.";
}


public sealed class AccessPasswordDialog : ClientDialog
{
    private readonly ClientAuthentication _auth;
    private readonly ModernInput _current, _new, _again;
    private readonly CheckBox _clear;

    public bool Cleared { get; private set; }

    public AccessPasswordDialog(ClientAuthentication auth) : base("Access Password", "Save")
    {
        _auth = auth;
        if (auth.HasPassword) _current = Field("Current password", new ModernInput(null, true));
        _new = Field("New password", new ModernInput(null, true), $"At least {PasswordPolicy.MinLength} characters. This is an RDM password, not your Windows password.");
        _again = Field("Confirm new password", new ModernInput(null, true));
        if (auth.HasPassword)
        {
            _clear = new CheckBox { Text = "Remove password (also disables Unattended Access)", AutoSize = true, Font = Theme.Font(9.5f), ForeColor = Theme.Text, BackColor = Color.Transparent };
            Fields.Controls.Add(_clear);
        }
    }

    protected override async Task<bool> AcceptAsync()
    {
        var current = _current?.Text;
        if (_clear?.Checked == true)
        {
            await Task.Run(() => _auth.ClearPassword(current));
            Cleared = true;
            return true;
        }
        if (_new.Text != _again.Text) throw new ClientValidationException("The passwords do not match.");
        var next = _new.Text;
        await Task.Run(() => _auth.SetPassword(current, next));
        return true;
    }
}

/// <summary>
/// Form Cài đặt dạng tab: Quyền truy cập (bật/tắt điều khiển, mật khẩu, điều khiển ẩn),
/// Relay (bản không đóng gói) và tùy chọn chung. Quyền truy cập tác động ngay; Relay/khởi động lưu khi bấm Save.
/// </summary>
public sealed class ClientSettingsDialog : Form
{
    private readonly ClientState _state;
    private readonly AllowControlPanel _owner;
    private readonly ModernInput _host = new ModernInput("relay.example.com"), _port = new ModernInput(), _pin = new ModernInput(), _token = new ModernInput(null, true), _sessionPort = new ModernInput();
    private readonly Label _accessStatus = new Label { AutoSize = false, Dock = DockStyle.Top, UseMnemonic = false, Font = Theme.Font(9f), ForeColor = Theme.TextSecondary, Height = Dpi.S(96) };
    private readonly ModernButton _toggleAccess = new ModernButton("Disable Remote Access");
    private readonly ModernButton _unattended = new ModernButton("Enable Unattended Access");

    public bool StartWithWindows => _startupField.Checked;

    public ClientSettingsDialog(ClientState state, StartupManager startup, AllowControlPanel owner)
    {
        _state = state;
        _owner = owner;
        Text = "Settings";
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        BackColor = Theme.Page;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Dpi.S(470), Dpi.S(430));

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Dpi.S(12), Dpi.S(6)) };
        tabs.TabPages.Add(BuildAccessTab());
        if (!RDM.Shared.Common.ProductMode.Managed) tabs.TabPages.Add(BuildRelayTab());
        tabs.TabPages.Add(BuildGeneralTab(startup));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = Dpi.S(52), Padding = new Padding(Dpi.S(12)), BackColor = Theme.Page };
        var save = new ModernButton("Save", ButtonKind.Primary);
        save.Click += (s, e) => Save();
        var close = new ModernButton("Close") { Margin = new Padding(Dpi.S(8), 0, 0, 0), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(close);
        CancelButton = close;

        Controls.Add(tabs);
        Controls.Add(buttons);
        RefreshAccess();
    }

    private TabPage BuildAccessTab()
    {
        var page = new TabPage("Access Permission") { BackColor = Theme.Page, Padding = new Padding(Dpi.S(14)) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        _accessStatus.Width = Dpi.S(420);
        flow.Controls.Add(_accessStatus);
        var changePassword = new ModernButton("Change Password") { Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(6)) };
        changePassword.Click += (s, e) => { _owner.ChangePasswordFromSettings(this); RefreshAccess(); };
        _unattended.Margin = new Padding(0, 0, 0, Dpi.S(6));
        _unattended.Click += (s, e) => { _owner.ToggleUnattendedFromSettings(this); RefreshAccess(); };
        _toggleAccess.Click += (s, e) => { _owner.ToggleAccessFromSettings(this); RefreshAccess(); };
        flow.Controls.Add(changePassword);
        flow.Controls.Add(_unattended);
        flow.Controls.Add(_toggleAccess);
        foreach (var c in new Control[] { changePassword, _unattended, _toggleAccess }) c.Width = Dpi.S(240);
        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildRelayTab()
    {
        var page = new TabPage("Relay") { BackColor = Theme.Page, Padding = new Padding(Dpi.S(14)), AutoScroll = true };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        void Field(string label, Control c, string hint = null)
        {
            flow.Controls.Add(new Label { Text = label, AutoSize = true, UseMnemonic = false, ForeColor = Theme.TextSecondary, Font = Theme.Font(9f), Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(2)) });
            c.Width = Dpi.S(420);
            flow.Controls.Add(c);
            if (hint != null) flow.Controls.Add(new Label { Text = hint, AutoSize = false, Width = Dpi.S(420), Height = Dpi.S(30), UseMnemonic = false, ForeColor = Theme.TextMuted, Font = Theme.Font(8.5f) });
        }
        Field("Relay Server", _host);
        Field("Port", _port);
        Field("Relay pinned key (SHA-256, optional)", _pin, "Only for a Relay with an internal certificate. Get this value from the Relay administrator.");
        Field("Device enrollment code (if the Relay requires one)", _token, _state.EnrollmentToken != null ? "Saved. Leave blank to keep it." : null);
        Field("Port for direct connections", _sessionPort, "The firewall must allow this port. Changing it requires restarting RDM.");
        _host.Text = _state.RelayHost;
        _port.Text = _state.RelayPort.ToString();
        _pin.Text = _state.RelayPin;
        _sessionPort.Text = _state.SessionPort.ToString();
        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildGeneralTab(StartupManager startup)
    {
        var page = new TabPage("General") { BackColor = Theme.Page, Padding = new Padding(Dpi.S(16), Dpi.S(20), Dpi.S(16), Dpi.S(16)) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        _startupField = new CheckBox { Text = "Start with Windows", AutoSize = true, Font = Theme.Font(9.5f), ForeColor = Theme.Text, BackColor = Color.Transparent, Checked = startup.IsEnabled, Margin = new Padding(0, Dpi.S(6), 0, 0) };
        flow.Controls.Add(_startupField);
        page.Controls.Add(flow);
        return page;
    }

    private CheckBox _startupField;

    /// <summary>Làm tươi nhãn và nút ở tab Quyền truy cập theo trạng thái hiện tại.</summary>
    public void RefreshAccess()
    {
        if (IsDisposed) return;
        _accessStatus.Text = _owner.AccessSummary();
        _unattended.Text = _owner.UnattendedEnabled ? "Disable Unattended Access" : "Enable Unattended Access";
        _toggleAccess.Text = _owner.AccessEnabled ? "Disable Remote Access" : "Enable Remote Access";
        _toggleAccess.Kind = _owner.AccessEnabled ? ButtonKind.Danger : ButtonKind.Primary;
        _toggleAccess.Invalidate();
        _unattended.Invalidate();
    }

    private void Save()
    {
        if (!RDM.Shared.Common.ProductMode.Managed)
        {
            try
            {
                var host = _host.Text.Trim();
                if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ClientValidationException("Invalid Relay address.");
                if (!int.TryParse(_port.Text, out var port) || port < 1 || port > 65535) throw new ClientValidationException("Port must be between 1 and 65535.");
                var pin = _pin.Text.Trim();
                if (pin.Length != 0 && pin.Length != 64) throw new ClientValidationException("The pinned key must be 64 hex characters.");
                if (!int.TryParse(_sessionPort.Text, out var sessionPort) || sessionPort < 1 || sessionPort > 65535) throw new ClientValidationException("The connection port must be between 1 and 65535.");
                _state.RelayHost = host.Length == 0 ? null : host;
                _state.SessionPort = sessionPort;
                _state.RelayPort = port;
                _state.RelayPin = pin.Length == 0 ? null : pin;
                if (_token.Text.Length > 0) _state.EnrollmentToken = _token.Text;
            }
            catch (ClientValidationException ex)
            {
                MessageBox.Show(this, ex.Message, AllowControlPanel.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
