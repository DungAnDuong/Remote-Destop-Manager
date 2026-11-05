using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Forms;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Data.Services;
using RDM.Shared.Common;
using RDM.Shared.Models;
using RDM.Shared.Security;

namespace RDM.Controller.Views;

internal static class ViewHelpers
{
    /// <summary>Trang chuẩn: thanh công cụ ở trên, bảng trong thẻ ở dưới.</summary>
    public static TableLayoutPanel Page(Control top, Control content)
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(top, 0, 0);
        page.Controls.Add(content, 0, 1);
        return page;
    }

    public static Card InCard(Control content, string title = null)
    {
        var card = new Card(title);
        if (title == null) card.Padding = new Padding(Dpi.S(4));
        card.Controls.Add(content);
        return card;
    }

    public static ModernButton Button(string text, bool enabled, Func<Task> action, Control owner, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new ModernButton(text, kind) { Enabled = enabled };
        b.Click += async (s, e) =>
        {
            try { await action(); }
            catch (Exception ex) { Ui.Error(owner.FindForm(), ex); }
        };
        return b;
    }

    public static bool ShowDialog(Form dialog, Control owner)
    {
        using (dialog) return dialog.ShowDialog(owner.FindForm()) == DialogResult.OK;
    }
}

/// <summary>Các phiên đang mở trên Controller này.</summary>
public sealed class ActiveSessionsView : ViewBase
{
    private readonly GridView<RDM.Controller.Services.ActiveSessionInfo> _grid = new GridView<RDM.Controller.Services.ActiveSessionInfo>();
    private readonly Timer _timer = new Timer { Interval = 2000 };

    public ActiveSessionsView(RdmRuntime rt) : base(rt)
    {
        _grid.AddColumn("Device Name", s => s.DeviceName, 110).AddColumn("Remote ID", s => RemoteIdFormat.Display(s.RemoteId), 70).AddColumn("Started", s => Ui.When(s.StartedUtc), 75)
             .AddColumn("Performance", s => s.Stats?.Invoke(), 90).AddColumn("Device fingerprint", s => s.Fingerprint, 100);
        var bar = Toolbar();
        bar.Controls.Add(ViewHelpers.Button("Disconnect", true, () =>
        {
            var s = _grid.SelectedItem;
            if (s == null) Ui.Info(FindForm(), "Select a session."); else s.Close();
            return Task.CompletedTask;
        }, this, ButtonKind.Danger));
        bar.Controls.Add(ViewHelpers.Button("Disconnect All", true, () =>
        {
            RDM.Controller.Services.SessionRegistry.CloseAll();
            return Task.CompletedTask;
        }, this));
        Controls.Add(ViewHelpers.Page(bar, ViewHelpers.InCard(_grid)));
        _timer.Tick += async (s, e) => await RefreshAsync();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    public override Task RefreshAsync()
    {
        _grid.SetItems(RDM.Controller.Services.SessionRegistry.All, (a, b) => a.RemoteId == b.RemoteId);
        return Task.CompletedTask;
    }
}

public sealed class GroupManagerView : ViewBase
{
    private readonly GridView<DeviceGroup> _grid = new GridView<DeviceGroup>();

    public GroupManagerView(RdmRuntime rt) : base(rt)
    {
        var can = rt.Session.Can(Permission.ManageClients);
        _grid.AddColumn("Group name", g => g.Name, 120).AddColumn("Description", g => g.Description, 180).AddColumn("Devices", g => g.DeviceCount, 50).AddColumn("Created", g => Ui.When(g.CreatedDate), 70);
        var bar = Toolbar();
        bar.Controls.Add(ViewHelpers.Button("Add Group", can, async () => { if (ViewHelpers.ShowDialog(new GroupEditForm(Rt, null), this)) await RefreshAsync(); }, this, ButtonKind.Primary));
        bar.Controls.Add(ViewHelpers.Button("Edit", can, async () =>
        {
            var g = _grid.SelectedItem; if (g == null) { Ui.Info(FindForm(), "Select a group."); return; }
            if (ViewHelpers.ShowDialog(new GroupEditForm(Rt, g), this)) await RefreshAsync();
        }, this));
        bar.Controls.Add(ViewHelpers.Button("Delete", can, async () =>
        {
            var g = _grid.SelectedItem; if (g == null) { Ui.Info(FindForm(), "Select a group."); return; }
            if (Ui.Confirm(FindForm(), $"Delete group \"{g.Name}\"?\n{g.DeviceCount} devices in the group become ungrouped; they are not deleted.")
                && Ui.Try(FindForm(), () => Rt.Groups.Delete(g.Id, g.Name))) await RefreshAsync();
        }, this));
        Controls.Add(ViewHelpers.Page(bar, ViewHelpers.InCard(_grid)));
    }

    public override Task RefreshAsync() => LoadAsync(() => Rt.Groups.GetAll(), items => _grid.SetItems(items, (a, b) => a.Id == b.Id));
}

/// <summary>Connection History và Recent Connections dùng chung một bảng. Dữ liệu có từ Giai đoạn 4.</summary>
public sealed class ConnectionHistoryView : ViewBase
{
    private readonly GridView<ConnectionRecord> _grid = new GridView<ConnectionRecord>();
    private readonly ModernInput _search = new ModernInput("Search by Remote ID, device, user");
    private readonly bool _recentOnly;

    public ConnectionHistoryView(RdmRuntime rt, bool recentOnly) : base(rt)
    {
        _recentOnly = recentOnly;
        _grid.AddColumn("Remote ID", h => RemoteIdFormat.Display(h.RemoteId), 75).AddColumn("Device Name", h => h.DeviceName, 110).AddColumn("Controller User", h => h.ControllerUser, 80)
             .AddColumn("Start Time", h => Ui.When(h.StartTime), 80).AddColumn("End Time", h => Ui.When(h.EndTime), 80).AddColumn("Duration", h => Ui.Duration(h.DurationSeconds), 70)
             .AddColumn("Type", h => h.ConnectionType, 50).AddColumn("Status", h => h.ConnectionStatus, 65);
        var bar = Toolbar();
        if (!recentOnly)
        {
            _search.Width = Dpi.S(300);
            bar.Controls.Add(_search);
            bar.Controls.Add(ViewHelpers.Button("Search", true, RefreshAsync, this, ButtonKind.Primary));
            _search.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RefreshAsync(); } };
        }
        bar.Controls.Add(ViewHelpers.Button("Reload", true, RefreshAsync, this));
                Controls.Add(ViewHelpers.Page(bar, ViewHelpers.InCard(_grid)));
    }

    public override Task RefreshAsync()
    {
        var search = _search.Text;
        return LoadAsync(() => _recentOnly ? Rt.History.Recent(50) : Rt.History.Query(search, 5000), items => _grid.SetItems(items));
    }
}

public sealed class UserManagementView : ViewBase
{
    private readonly GridView<UserAccount> _grid = new GridView<UserAccount>();

    public UserManagementView(RdmRuntime rt) : base(rt)
    {
        _grid.AddColumn("Username", u => u.Username, 90).AddColumn("Full name", u => u.FullName, 110).AddColumn("Email", u => u.Email, 120).AddColumn("Role", u => u.Role.ToString(), 70)
             .AddColumn("Status", u => !u.IsEnabled ? "Disabled" : u.LockedUntil > DateTime.UtcNow ? "Locked" : "Active", 65)
             .AddColumn("Last sign-in", u => Ui.When(u.LastLogin), 85);
        var bar = Toolbar();
        bar.Controls.Add(ViewHelpers.Button("Add User", true, async () => { if (ViewHelpers.ShowDialog(new UserEditForm(Rt, null), this)) await RefreshAsync(); }, this, ButtonKind.Primary));
        bar.Controls.Add(ViewHelpers.Button("Edit", true, async () =>
        {
            var u = Selected(); if (u == null) return;
            if (ViewHelpers.ShowDialog(new UserEditForm(Rt, u), this)) await RefreshAsync();
        }, this));
        bar.Controls.Add(ViewHelpers.Button("Reset Password", true, async () =>
        {
            var u = Selected(); if (u == null) return;
            using var dlg = new PasswordPromptForm("Reset Password", $"New password for {u.Username}", "The user must change the password at next sign-in.", true, true);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            var pw = dlg.Password;
            await BusyAsync(null, async () =>
            {
                await Task.Run(() => Rt.Users.ResetPassword(u.Id, pw));
                Ui.Info(FindForm(), "Password reset.");
                await RefreshAsync();
            });
        }, this));
        bar.Controls.Add(ViewHelpers.Button("Delete", true, async () =>
        {
            var u = Selected(); if (u == null) return;
            if (Ui.Confirm(FindForm(), $"Delete user \"{u.Username}\"?") && Ui.Try(FindForm(), () => Rt.Users.Delete(u.Id))) await RefreshAsync();
        }, this));
        Controls.Add(ViewHelpers.Page(bar, ViewHelpers.InCard(_grid)));
    }

    private UserAccount Selected()
    {
        var u = _grid.SelectedItem;
        if (u == null) Ui.Info(FindForm(), "Select a user.");
        return u;
    }

    public override Task RefreshAsync() => LoadAsync(() => Rt.Users.GetAll(), items => _grid.SetItems(items, (a, b) => a.Id == b.Id));
}

public sealed class SecurityCenterView : ViewBase
{
    private readonly GridView<KeyRecord> _keys = new GridView<KeyRecord>();
    private readonly GridView<AuditEntry> _audit = new GridView<AuditEntry>();
    private readonly ModernInput _search = new ModernInput("Search the Audit Log");

    /// <summary>Phát sinh sau khi Restore thành công: phải đăng nhập lại vì toàn bộ dữ liệu đã thay đổi.</summary>
    public event Action RestoreCompleted;

    public SecurityCenterView(RdmRuntime rt) : base(rt)
    {
        _keys.AddColumn("Purpose", k => k.Purpose.ToString(), 70).AddColumn("Version", k => k.Version, 45).AddColumn("Status", k => k.Status.ToString(), 50)
             .AddColumn("Fingerprint", k => k.Fingerprint, 90).AddColumn("Protected by", k => k.ProtectionScheme, 90).AddColumn("Created", k => Ui.When(k.CreatedDate), 70).AddColumn("Rotated", k => Ui.When(k.RotatedDate), 70);
        _audit.AddColumn("Time", a => Ui.When(a.Timestamp), 100).AddColumn("Event", a => a.EventType, 90).AddColumn("Severity", a => a.Severity.ToString(), 45).AddColumn("Result", a => a.Success ? "Success" : "Failed", 50)
              .AddColumn("User", a => a.Username, 60).AddColumn("Remote ID", a => RemoteIdFormat.Display(a.RemoteId), 60).AddColumn("Details", a => a.Details, 180);

        var p = PlatformInfo.Detect();
        var os = Ui.Text($"{(p.IsEndOfLifeOs ? "▲" : "✔")} Windows {p.OsVersion} ({(p.Is64BitOs ? "x64" : "x86")}), {(p.Is64BitProcess ? "x64" : "x86")} process. {p.Note}", 9f, false, Theme.TextSecondary);
        os.Margin = new Padding(Dpi.S(2), 0, 0, Dpi.S(8));

        var keyBar = Toolbar();
        keyBar.Controls.Add(ViewHelpers.Button("Rotate Database Key", true, () => RotateAsync(KeyPurpose.Database), this, ButtonKind.Primary));
        keyBar.Controls.Add(ViewHelpers.Button("Rotate Authentication Key", true, () => RotateAsync(KeyPurpose.Authentication), this));
        keyBar.Controls.Add(ViewHelpers.Button("Revoke Selected Key", true, RevokeAsync, this, ButtonKind.Danger));
        keyBar.Controls.Add(ViewHelpers.Button("Backup", true, BackupAsync, this));
        keyBar.Controls.Add(ViewHelpers.Button("Restore", true, RestoreAsync, this));

        var auditBar = Toolbar();
        _search.Width = Dpi.S(280);
        auditBar.Controls.Add(_search);
        auditBar.Controls.Add(ViewHelpers.Button("Search", true, RefreshAsync, this));
        _search.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RefreshAsync(); } };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.Controls.Add(os, 0, 0);
        layout.Controls.Add(keyBar, 0, 1);
        layout.Controls.Add(ViewHelpers.InCard(_keys, "Encryption Keys"), 0, 2);
        layout.Controls.Add(auditBar, 0, 3);
        layout.Controls.Add(ViewHelpers.InCard(_audit, "Audit Log"), 0, 4);
        Controls.Add(layout);
    }

    public override Task RefreshAsync()
    {
        var search = _search.Text;
        return LoadAsync(() => new { Keys = Rt.Keys.ListKeys(), Audit = Rt.AuditLogs.Query(search, 2000) }, d =>
        {
            _keys.SetItems(d.Keys);
            _audit.SetItems(d.Audit);
        });
    }

    private async Task RotateAsync(KeyPurpose purpose)
    {
        if (!Ui.Confirm(FindForm(), $"Rotate the {purpose} key? Related data will be reprocessed with the new key in one transaction.")) return;
        await BusyAsync(null, async () =>
        {
            var version = await Task.Run(() => Rt.KeyRotation.Rotate(purpose));
            Ui.Info(FindForm(), $"Rotated the {purpose} key to version {version}.");
            await RefreshAsync();
        });
    }

    private async Task RevokeAsync()
    {
        var k = _keys.SelectedItem;
        if (k == null) { Ui.Info(FindForm(), "Select a key."); return; }
        if (!Ui.Confirm(FindForm(), $"Revoke {k.Purpose} key version {k.Version}?\nCurrent data was re-encrypted during rotation and is not affected. This cannot be undone.\nNote: old backup files carry their own copy of the key, so revoking does not invalidate them. Delete any backups you no longer trust.")) return;
        Ui.Try(FindForm(), () => Rt.KeyRotation.Revoke(k.Purpose, k.Version));
        await RefreshAsync();
    }

    private async Task BackupAsync()
    {
        using var save = new SaveFileDialog { Filter = "RDM Backup (*.rdmbak)|*.rdmbak", FileName = $"RDM_{DateTime.Now:yyyyMMdd_HHmm}.rdmbak" };
        if (save.ShowDialog(FindForm()) != DialogResult.OK) return;
        var path = save.FileName;
        using var prompt = new PasswordPromptForm("Backup Password", "Password protecting the backup file", "This password cannot be recovered if forgotten. Keep it safe.", true, true);
        if (prompt.ShowDialog(FindForm()) != DialogResult.OK) return;
        var pw = prompt.Password;
        await BusyAsync(null, async () =>
        {
            await Task.Run(() => Rt.Backup.Backup(path, pw));
            Ui.Info(FindForm(), "Backup file created.");
            await RefreshAsync();
        });
    }

    private async Task RestoreAsync()
    {
        using var open = new OpenFileDialog { Filter = "RDM Backup (*.rdmbak)|*.rdmbak" };
        if (open.ShowDialog(FindForm()) != DialogResult.OK) return;
        var path = open.FileName;
        if (!Ui.Confirm(FindForm(), "Restoring replaces all current data.\nThe current copy is saved as RDM_Database.db.before-restore.bak. Continue?")) return;
        using var prompt = new PasswordPromptForm("Backup Password", "Password of the backup file", null, false, false);
        if (prompt.ShowDialog(FindForm()) != DialogResult.OK) return;
        var pw = prompt.Password;
        await BusyAsync(null, async () =>
        {
            await Task.Run(() => Rt.Backup.Restore(path, pw));
            Ui.Info(FindForm(), "Restore succeeded. Please sign in again.");
            RestoreCompleted?.Invoke();
        });
    }
}

public sealed class SettingsView : ViewBase
{
    private readonly ComboBox _theme = Ui.Combo(240);
    private readonly ModernInput _host = new ModernInput("relay.example.com");
    private readonly ModernInput _port = new ModernInput("7443");
    private readonly ModernInput _token = new ModernInput("Leave blank to keep it", true);
    private readonly CheckBox _pinning = Ui.Check("StrictPinning: require re-entering a new device's fingerprint at first connection");
    private readonly ModernInput _retention = new ModernInput("365");
    private readonly ModernInput _pin = new ModernInput("Only for a Relay with an internal certificate");
    private readonly CheckBox _autoRegister = Ui.Check("Automatically add new devices when they register with the Relay");
    private readonly bool _canManage;

    public SettingsView(RdmRuntime rt) : base(rt)
    {
        _canManage = rt.Session.Can(Permission.ManageSettings);
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent };
        _theme.Items.AddRange(new object[] { "Light", "Dark" });
        Field(flow, "Appearance", _theme);
        _theme.SelectedIndexChanged += (s, e) =>
        {
            var mode = _theme.SelectedIndex == 1 ? ThemeMode.Dark : ThemeMode.Light;
            if (mode == Theme.Mode) return;
            if (Ui.Try(FindForm(), () => Rt.Settings.SetTheme(mode.ToString()))) BeginInvoke(new Action(() => Theme.Set(mode)));
        };
        if (!ProductMode.Managed)
        {
            flow.Controls.Add(Ui.Text("Relay Server", 10.5f, true));
            Field(flow, "Host", _host);
            Field(flow, "Port", _port);
            Field(flow, "Controller token (stored encrypted with AES-256-GCM)", _token);
            Field(flow, "Relay pinned key, SHA-256 (optional)", _pin);
            flow.Controls.Add(_autoRegister);
        }
        flow.Controls.Add(Ui.Text("Security", 10.5f, true));
        flow.Controls.Add(_pinning);
        Field(flow, "Audit Log retention in days (0 = keep forever)", _retention);
        var save = ViewHelpers.Button("Save Settings", _canManage, SaveAsync, this, ButtonKind.Primary);
        flow.Controls.Add(save);
        foreach (var c in new Control[] { _host, _port, _token, _pin, _autoRegister, _pinning, _retention }) c.Enabled = _canManage;
        if (!_canManage) flow.Controls.Add(Ui.Text("Only an Administrator can change system settings.", 9f, false, Theme.TextMuted));
        Controls.Add(ViewHelpers.InCard(flow, "Settings"));
    }

    private static void Field(FlowLayoutPanel flow, string label, Control control)
    {
        flow.Controls.Add(Ui.Text(label, 9f, false, Theme.TextSecondary));
        control.Width = Dpi.S(360);
        flow.Controls.Add(control);
    }

    public override Task RefreshAsync() => LoadAsync(() => new
    {
        Host = Rt.Settings.Get(SettingKeys.RelayHost, ""),
        Port = Rt.Settings.Get(SettingKeys.RelayPort, RDM.Shared.Protocol.ProtocolInfo.DefaultRelayPort.ToString()),
        RelayPin = Rt.Settings.Get(SettingKeys.RelayPin, ""),
        Auto = Rt.Settings.Get(SettingKeys.AutoRegister, "false"),
        Pin = Rt.Settings.Get(SettingKeys.StrictPinning, "true"),
        Retention = Rt.Settings.Get(SettingKeys.AuditRetentionDays, "365")
    }, d =>
    {
        _theme.SelectedIndex = Theme.Mode == ThemeMode.Dark ? 1 : 0;
        _host.Text = d.Host; _port.Text = d.Port; _retention.Text = d.Retention;
        _pinning.Checked = d.Pin == "true";
        _pin.Text = d.RelayPin;
        _autoRegister.Checked = d.Auto == "true";
    });

    private async Task SaveAsync()
    {
        if (!int.TryParse(_retention.Text, out var days) || days < 0) throw new ValidationException("Invalid Audit Log retention.");
        if (ProductMode.Managed)
        {
            Rt.Settings.Set(SettingKeys.StrictPinning, _pinning.Checked ? "true" : "false");
            Rt.Settings.Set(SettingKeys.AuditRetentionDays, days.ToString());
            Ui.Info(FindForm(), "Settings saved.");
            return;
        }
        if (!int.TryParse(_port.Text, out var port) || port < 1 || port > 65535) throw new ValidationException("Port must be between 1 and 65535.");
        var host = _host.Text.Trim();
        if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ValidationException("Invalid host.");
        var pin = _pin.Text.Trim();
        if (pin.Length != 0 && pin.Length != 64) throw new ValidationException("The pinned key must be 64 hex characters.");
        Rt.Settings.Set(SettingKeys.RelayPin, pin);
        Rt.Settings.Set(SettingKeys.AutoRegister, _autoRegister.Checked ? "true" : "false");
        Rt.Settings.Set(SettingKeys.RelayHost, host);
        Rt.Settings.Set(SettingKeys.RelayPort, port.ToString());
        Rt.Settings.Set(SettingKeys.StrictPinning, _pinning.Checked ? "true" : "false");
        Rt.Settings.Set(SettingKeys.AuditRetentionDays, days.ToString());
        if (_token.Text.Length > 0) { Rt.Settings.Set(SettingKeys.RelayToken, _token.Text, true); _token.Text = string.Empty; }
        await Rt.Presence.RestartAsync();
        Ui.Info(FindForm(), "Settings saved.");
    }
}

public sealed class AboutView : ViewBase
{
    public AboutView(RdmRuntime rt) : base(rt)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent };
        flow.Controls.Add(Ui.Text("REMOTE DESKTOP MANAGER", 15f, true));
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var p = PlatformInfo.Detect();
        flow.Controls.Add(Ui.Text($"Version {version}, {(p.Is64BitProcess ? "x64" : "x86")} build", 9.5f, false, Theme.TextSecondary));
        flow.Controls.Add(Ui.Text($"Database: {rt.Context.DatabasePath}", 9f, false, Theme.TextMuted));
        flow.Controls.Add(Ui.Text(" "));
        if (!ProductMode.Managed)
        {
            flow.Controls.Add(Ui.Text("Development Status", 10.5f, true));
            foreach (var line in new[]
            {
                "✔ Phase 1: foundation, interface, device and group management, Import/Export",
                "✔ Phase 2: SQLite, encryption, DPAPI, PBKDF2, Key Management, Audit Log, Backup/Restore",
                "✔ Phase 3: allow remote control, device registration, Heartbeat, Online/Offline",
                "✔ Phase 4: E2EE, Remote Desktop, mouse and keyboard over direct connections (Windows Service untested)",
                "✔ Phase 5: data relay, Internet connections, concurrent sessions (TCP hole punching not implemented)",
                "– Phase 6: File Transfer, Chat, Multi-Session (not implemented)",
                "– Phase 7: cross-platform testing, final packaging (in progress)"
            }) flow.Controls.Add(Ui.Text(line, 9.5f, false, Theme.TextSecondary));
            flow.Controls.Add(Ui.Text(" "));
        }
        flow.Controls.Add(Ui.Text("Third-Party Libraries", 10.5f, true));
        flow.Controls.Add(Ui.Text("BouncyCastle.Cryptography, System.Data.SQLite, NLog, ClosedXML", 9.5f, false, Theme.TextSecondary));
        Controls.Add(ViewHelpers.InCard(flow));
    }

    public override Task RefreshAsync() => Task.CompletedTask;
}
