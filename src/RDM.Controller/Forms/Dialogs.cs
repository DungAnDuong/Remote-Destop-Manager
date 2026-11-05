using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NLog;
using RDM.Controller.Controls;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Data.Services;
using RDM.Shared.Common;
using RDM.Shared.Models;
using RDM.Shared.Security;

namespace RDM.Controller.Forms;

/// <summary>Tiện ích giao diện dùng chung.</summary>
public static class Ui
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    public const string AppTitle = "REMOTE DESKTOP MANAGER";

    /// <summary>Kiểm thử gán để nhận lỗi thay vì mở MessageBox làm treo tiến trình test.</summary>
    public static Action<Exception> ErrorOverride { get; set; }

    public static Label Text(string text, float size = 9.5f, bool bold = false, Color? color = null) => new Label
    {
        Text = text, AutoSize = true, Font = Theme.Font(size, bold), ForeColor = color ?? Theme.Text, BackColor = Color.Transparent,
        UseMnemonic = false, Margin = new Padding(0, 0, 0, Dpi.S(4))
    };

    public static void Info(IWin32Window owner, string message) => MessageBox.Show(owner, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static bool Confirm(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>Hiển thị lỗi: lỗi nghiệp vụ hiện nguyên văn, lỗi hệ thống chỉ ghi chi tiết vào Log.</summary>
    public static void Error(IWin32Window owner, Exception ex)
    {
        if (ErrorOverride != null) { ErrorOverride(ex); return; }
        string message;
        switch (ex)
        {
            case ValidationException _:
            case UnauthorizedAccessException _:
            case CryptoFailureException _:
            case System.IO.InvalidDataException _:
                message = ex.Message;
                break;
            case SQLiteException _:
                Log.Error(ex, "Database error");
                message = "Could not access the database. Please try again; check the log if this continues.";
                break;
            case System.IO.IOException _:
                Log.Error(ex, "File error");
                message = "Could not read or write the file: " + ex.Message;
                break;
            default:
                Log.Error(ex, "Unexpected error");
                message = "An unexpected error occurred. Details were written to the log.";
                break;
        }
        MessageBox.Show(owner, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static bool Try(IWin32Window owner, Action action)
    {
        try { action(); return true; }
        catch (Exception ex) { Error(owner, ex); return false; }
    }

    public static string When(DateTime? utc) => utc.HasValue && utc.Value != DateTime.MinValue ? DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "–";

    public static string Duration(long? seconds)
    {
        if (!seconds.HasValue) return "–";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s" : $"{t.Seconds} s";
    }

    public static ComboBox Combo(int width = 220)
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Theme.Font(9.5f), Width = Dpi.S(width),
            BackColor = Theme.Surface, ForeColor = Theme.Text, Margin = new Padding(0, Dpi.S(4), Dpi.S(8), Dpi.S(8))
        };
    }

    public static CheckBox Check(string text) => new CheckBox
    {
        Text = text, AutoSize = true, Font = Theme.Font(9.5f), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(8)), UseMnemonic = false
    };
}

/// <summary>Khung hộp thoại: cột trường dữ liệu, nút OK/Hủy. Xử lý OK chạy async để không treo UI (PBKDF2 mất thời gian).</summary>
public abstract class DialogBase : Form
{
    protected TableLayoutPanel Fields { get; }
    protected ModernButton OkButton { get; }
    protected ModernButton CancelBtn { get; }
    private readonly FlowLayoutPanel _buttons;
    private const int FieldWidth = 380;

    protected DialogBase(string title, string okText = "Save")
    {
        Text = title;
        Icon = Theme.AppIcon;
        Font = Theme.Font(9.5f);
        BackColor = Theme.Page;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(12));

        Fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0) };
        Fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(FieldWidth)));

        OkButton = new ModernButton(okText, ButtonKind.Primary) { Margin = new Padding(Dpi.S(8), 0, 0, 0) };
        CancelBtn = new ModernButton("Cancel") { Margin = new Padding(Dpi.S(8), 0, 0, 0), DialogResult = DialogResult.Cancel };
        _buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Width = Dpi.S(FieldWidth), Height = Dpi.S(46), Padding = new Padding(0, Dpi.S(10), 0, 0), Margin = new Padding(0), BackColor = Color.Transparent };
        _buttons.Controls.Add(OkButton);
        _buttons.Controls.Add(CancelBtn);

        var outer = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        outer.Controls.Add(Fields, 0, 0);
        outer.Controls.Add(_buttons, 0, 1);
        Controls.Add(outer);

        AcceptButton = OkButton;
        CancelButton = CancelBtn;
        OkButton.Click += async (s, e) => await OnOkClickedAsync();
    }

    protected T AddField<T>(string label, T control, string hint = null) where T : Control
    {
        Fields.Controls.Add(Ui.Text(label, 9f, false, Theme.TextSecondary));
        control.Width = Dpi.S(FieldWidth);
        control.Margin = new Padding(0, 0, 0, hint == null ? Dpi.S(10) : Dpi.S(2));
        Fields.Controls.Add(control);
        if (hint != null) AddHint(hint);
        return control;
    }

    protected void AddHint(string text)
    {
        var hint = Ui.Text(text, 8.5f, false, Theme.TextMuted);
        hint.MaximumSize = new Size(Dpi.S(FieldWidth), 0);
        hint.Margin = new Padding(0, 0, 0, Dpi.S(10));
        Fields.Controls.Add(hint);
    }

    /// <summary>Trả về true để đóng hộp thoại. Ngoại lệ được hiển thị và hộp thoại giữ nguyên.</summary>
    protected abstract Task<bool> AcceptAsync();

    private async Task OnOkClickedAsync()
    {
        OkButton.Enabled = CancelBtn.Enabled = false;
        UseWaitCursor = true;
        try
        {
            if (await AcceptAsync()) { DialogResult = DialogResult.OK; Close(); }
        }
        catch (Exception ex) { Ui.Error(this, ex); }
        finally
        {
            if (!IsDisposed) { OkButton.Enabled = CancelBtn.Enabled = true; UseWaitCursor = false; }
        }
    }
}

public sealed class LoginForm : DialogBase
{
    private readonly RdmRuntime _rt;
    private readonly ModernInput _user = new ModernInput();
    private readonly ModernInput _pass = new ModernInput(null, true);
    private readonly ModernInput _pass2;
    private readonly ModernInput _fullName;
    private readonly bool _setup;

    public LoginForm(RdmRuntime rt) : base(Ui.AppTitle, rt.Users.HasAnyUser() ? "Sign In" : "Create Account")
    {
        _rt = rt;
        _setup = !rt.Users.HasAnyUser();
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Fields.Controls.Add(Ui.Text("REMOTE DESKTOP MANAGER", 15f, true));
        Fields.Controls.Add(Ui.Text(_setup ? "First run: create the Administrator account." : "Sign in to continue.", 9.5f, false, Theme.TextSecondary));
        AddField("Username", _user);
        if (_setup)
        {
            _fullName = AddField("Full name (optional)", new ModernInput());
            AddField("Password", _pass, $"At least {PasswordPolicy.MinLength} characters. This is an RDM password, not your Windows password.");
            _pass2 = AddField("Confirm password", new ModernInput(null, true));
        }
        else AddField("Password", _pass);
        CancelBtn.Text = "Exit";
    }

    protected override async Task<bool> AcceptAsync()
    {
        string user = _user.Text, pass = _pass.Text;
        if (_setup)
        {
            if (pass != _pass2.Text) throw new ValidationException("The passwords do not match.");
            var fullName = _fullName.Text;
            await Task.Run(() => _rt.Users.CreateFirstAdministrator(user, pass, string.IsNullOrWhiteSpace(fullName) ? null : fullName));
        }
        var outcome = await Task.Run(() => _rt.Users.Login(user, pass));
        _pass.Text = string.Empty;
        if (_pass2 != null) _pass2.Text = string.Empty;
        if (outcome == LoginOutcome.Success) return true;
        // Một thông báo chung, không tiết lộ tài khoản có tồn tại hay đang bị khóa.
        throw new ValidationException($"Incorrect username or password, or the account is temporarily locked ({UserService.LockoutDuration.TotalMinutes:0} minutes after {UserService.MaxFailedAttempts} failed attempts).");
    }
}

public sealed class ChangePasswordForm : DialogBase
{
    private readonly RdmRuntime _rt;
    private readonly ModernInput _current, _new, _new2;

    public ChangePasswordForm(RdmRuntime rt, bool forced = false) : base("Change RDM Password")
    {
        _rt = rt;
        if (forced) AddHint("An administrator reset your password. Change it before continuing.");
        _current = AddField("Current password", new ModernInput(null, true));
        _new = AddField("New password", new ModernInput(null, true), $"At least {PasswordPolicy.MinLength} characters.");
        _new2 = AddField("Confirm new password", new ModernInput(null, true));
    }

    protected override async Task<bool> AcceptAsync()
    {
        if (_new.Text != _new2.Text) throw new ValidationException("The passwords do not match.");
        string current = _current.Text, next = _new.Text;
        await Task.Run(() => _rt.Users.ChangeOwnPassword(current, next));
        return true;
    }
}

public sealed class ClientEditForm : DialogBase
{
    private readonly RdmRuntime _rt;
    private readonly ClientDevice _device;
    private readonly ModernInput _name, _remoteId, _ip, _mac, _os, _desc, _tags, _credential;
    private readonly ComboBox _group, _arch;
    private readonly CheckBox _enabled;
    private readonly List<DeviceGroup> _groups;

    public ClientEditForm(RdmRuntime rt, ClientDevice device) : base(device == null ? "Add Client" : "Edit Client")
    {
        _rt = rt;
        _device = device;
        _groups = rt.Groups.GetAll();
        _name = AddField("Device Name", new ModernInput());
        _remoteId = AddField("Remote ID", new ModernInput("123 456 789"));
        _group = AddField("Group", Ui.Combo());
        _group.Items.Add("(No group)");
        foreach (var g in _groups) _group.Items.Add(g.Name);
        _group.SelectedIndex = 0;
        _ip = AddField("IP Address", new ModernInput());
        _mac = AddField("MAC Address", new ModernInput());
        _os = AddField("Operating System", new ModernInput());
        _arch = AddField("Architecture", Ui.Combo());
        _arch.Items.AddRange(new object[] { "", "x64", "x86" });
        _arch.SelectedIndex = 0;
        _desc = AddField("Description", new ModernInput());
        _tags = AddField("Tags (comma-separated)", new ModernInput());
        _credential = AddField("Device's RDM access password", new ModernInput(null, true),
            device?.HasCredential == true ? "Saved (encrypted). Leave blank to keep it. Not a Windows password." : "Optional. Encrypted with AES-256-GCM. Not a Windows password.");
        _enabled = Ui.Check("Enabled");
        _enabled.Checked = device?.IsEnabled ?? true;
        Fields.Controls.Add(_enabled);

        if (device != null)
        {
            _name.Text = device.DeviceName; _remoteId.Text = RemoteIdFormat.Display(device.RemoteId); _ip.Text = device.IpAddress; _mac.Text = device.MacAddress;
            _os.Text = device.OperatingSystem; _desc.Text = device.Description; _tags.Text = device.Tags;
            _arch.SelectedItem = device.Architecture ?? "";
            var index = _groups.FindIndex(g => g.Id == device.GroupId);
            _group.SelectedIndex = index + 1;
        }
    }

    protected override Task<bool> AcceptAsync()
    {
        var d = _device ?? new ClientDevice();
        d.DeviceName = _name.Text; d.RemoteId = _remoteId.Text; d.IpAddress = Blank(_ip.Text); d.MacAddress = Blank(_mac.Text); d.OperatingSystem = Blank(_os.Text);
        d.Architecture = Blank(_arch.SelectedItem?.ToString()); d.Description = Blank(_desc.Text); d.IsEnabled = _enabled.Checked;
        d.GroupId = _group.SelectedIndex > 0 ? _groups[_group.SelectedIndex - 1].Id : (long?)null;
        var tags = _tags.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var credential = _credential.Text;
        if (_device == null) _rt.Clients.Add(d, credential, tags);
        else
        {
            _rt.Clients.Update(d, tags);
            if (!string.IsNullOrEmpty(credential)) _rt.Clients.SetCredential(d.Id, credential);
        }
        return Task.FromResult(true);
    }

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class GroupEditForm : DialogBase
{
    private readonly RdmRuntime _rt;
    private readonly DeviceGroup _group;
    private readonly ModernInput _name, _desc;

    public GroupEditForm(RdmRuntime rt, DeviceGroup group) : base(group == null ? "Add Group" : "Edit Group")
    {
        _rt = rt; _group = group;
        _name = AddField("Group name", new ModernInput());
        _desc = AddField("Description", new ModernInput());
        if (group != null) { _name.Text = group.Name; _desc.Text = group.Description; }
    }

    protected override Task<bool> AcceptAsync()
    {
        if (_group == null) _rt.Groups.Add(_name.Text, _desc.Text);
        else _rt.Groups.Update(_group.Id, _name.Text, _desc.Text);
        return Task.FromResult(true);
    }
}

public sealed class UserEditForm : DialogBase
{
    private readonly RdmRuntime _rt;
    private readonly UserAccount _user;
    private readonly ModernInput _name, _full, _email, _pass;
    private readonly ComboBox _role;
    private readonly CheckBox _enabled;

    public UserEditForm(RdmRuntime rt, UserAccount user) : base(user == null ? "Add User" : "Edit User")
    {
        _rt = rt; _user = user;
        _name = AddField("Username", new ModernInput());
        _full = AddField("Full name", new ModernInput());
        _email = AddField("Email", new ModernInput());
        _role = AddField("Role", Ui.Combo());
        _role.Items.AddRange(new object[] { UserRole.Viewer, UserRole.Operator, UserRole.Administrator });
        _role.SelectedItem = user?.Role ?? UserRole.Operator;
        if (user == null) _pass = AddField("Initial password", new ModernInput(null, true), $"At least {PasswordPolicy.MinLength} characters. The user must change it at first sign-in.");
        _enabled = Ui.Check("Active");
        _enabled.Checked = user?.IsEnabled ?? true;
        if (user != null) Fields.Controls.Add(_enabled);
        if (user != null) { _name.Text = user.Username; _name.Enabled = false; _full.Text = user.FullName; _email.Text = user.Email; }
    }

    protected override async Task<bool> AcceptAsync()
    {
        string name = _name.Text, full = Blank(_full.Text), email = Blank(_email.Text);
        var role = (UserRole)_role.SelectedItem;
        var enabled = _enabled.Checked;
        if (_user == null)
        {
            var pass = _pass.Text;
            await Task.Run(() => _rt.Users.Create(name, pass, full, email, role));
        }
        else _rt.Users.Update(_user.Id, full, email, role, enabled);
        return true;
    }

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Xác nhận khóa của thiết bị ở lần kết nối đầu. Đây là lúc duy nhất Relay độc hại có thể chen vào nếu người dùng bấm bừa,
/// nên chế độ StrictPinning buộc phải nhập lại fingerprint đọc từ màn hình máy đích.
/// </summary>
public sealed class FingerprintForm : DialogBase
{
    private readonly string _fingerprint;
    private readonly ModernInput _typed;

    public FingerprintForm(string remoteId, string fingerprint, bool strict) : base("Verify Device (First Connection)", "Trust This Device")
    {
        _fingerprint = fingerprint;
        Fields.Controls.Add(Ui.Text($"First connection to {RemoteIdFormat.Display(remoteId)}.", 10.5f, true));
        AddHint("Compare this with the Fingerprint line shown in RDM on the remote computer, by phone or in person. A different fingerprint may mean a man-in-the-middle attack: click Cancel.");
        Fields.Controls.Add(Ui.Text(fingerprint, 14f, true));
        if (strict) _typed = AddField("Enter the fingerprint read from the remote computer", new ModernInput());
    }

    protected override Task<bool> AcceptAsync()
    {
        if (_typed != null && !string.Equals(_typed.Text.Replace(" ", "").Replace("-", ""), _fingerprint.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("The fingerprint does not match. Do not continue.");
        return Task.FromResult(true);
    }
}

/// <summary>Hỏi một mật khẩu: đặt lại mật khẩu, mật khẩu backup, mật khẩu thiết bị. Bên gọi đọc Password rồi tự xử lý.</summary>
public sealed class PasswordPromptForm : DialogBase
{
    private readonly ModernInput _pass;
    private readonly ModernInput _pass2;
    private readonly bool _requirePolicy;
    private readonly bool _allowEmpty;

    public string Password { get; private set; }

    public PasswordPromptForm(string title, string label, string hint, bool confirm, bool requirePolicy, bool allowEmpty = false) : base(title, "Continue")
    {
        _requirePolicy = requirePolicy;
        _allowEmpty = allowEmpty;
        _pass = AddField(label, new ModernInput(null, true), hint);
        if (confirm) _pass2 = AddField("Confirm", new ModernInput(null, true));
    }

    protected override Task<bool> AcceptAsync()
    {
        if (_pass2 != null && _pass.Text != _pass2.Text) throw new ValidationException("The entries do not match.");
        if (_pass.Text.Length == 0 && !_allowEmpty) throw new ValidationException("Please enter a password.");
        if (_requirePolicy)
        {
            var problem = PasswordPolicy.Validate(_pass.Text);
            if (problem != null) throw new ValidationException(problem);
        }
        Password = _pass.Text;
        return Task.FromResult(true);
    }
}
