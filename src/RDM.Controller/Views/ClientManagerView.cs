using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Forms;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Shared.Common;
using RDM.Shared.Models;

namespace RDM.Controller.Views;

/// <summary>Quản lý Client (Phần 4.3). Dùng cho cả My Computers và Favorites.</summary>
public sealed class ClientManagerView : ViewBase
{
        private readonly bool _favoritesOnly;
    private readonly GridView<ClientDevice> _grid = new GridView<ClientDevice>();
    private readonly ModernInput _search = new ModernInput("Search: name, Remote ID, IP, OS, group, tag");
    private readonly ComboBox _groupFilter = Ui.Combo(170);
    private readonly Label _count = Ui.Text("", 9f, false, Theme.TextMuted);
    private readonly Timer _debounce = new Timer { Interval = 250 };
    private List<DeviceGroup> _groups = new List<DeviceGroup>();
    private int _loadTicket;

    public ClientManagerView(RdmRuntime rt, bool favoritesOnly) : base(rt)
    {
        _favoritesOnly = favoritesOnly;
        var canManage = rt.Session.Can(Permission.ManageClients);

        _grid.AddColumn("STT", (d, i) => i + 1, 28)
             .AddColumn("★", d => d.IsFavorite ? "★" : "", 18)
             .AddColumn("Device Name", d => d.DeviceName, 105)
             .AddColumn("Remote ID", d => RemoteIdFormat.Display(d.RemoteId), 75)
             .AddColumn("Group", d => d.GroupName, 75)
             .AddColumn("IP Address", d => d.IpAddress, 70)
             .AddColumn("OS", d => d.OperatingSystem, 80)
             .AddColumn("Status", d => Rt.Presence.StatusText(d), 90)
             .AddColumn("Last Online", d => Ui.When(d.LastOnline), 95);
        _grid.CellDoubleClick += async (s, e) => { if (e.RowIndex >= 0) await ConnectSelectedAsync(); };

        var bar = Toolbar();
        _search.Width = Dpi.S(280);
        bar.Controls.Add(_search);
        bar.Controls.Add(_groupFilter);
        Add(bar, "Add Client", canManage, async () =>
        {
            if (!Show(new ClientEditForm(Rt, null))) return;
            // Thiết bị có thể đã Online từ trước: lấy luôn thông tin Relay đang có, không chờ thay đổi tiếp theo.
            Rt.Presence.ReapplyCached();
            await RefreshAsync();
        }, ButtonKind.Primary);
        Add(bar, "Edit", canManage, async () =>
        {
            var d = Selected(); if (d == null) return;
            if (Show(new ClientEditForm(Rt, d))) await RefreshAsync();
        });
        Add(bar, "Delete", canManage, async () =>
        {
            var d = Selected(); if (d == null) return;
            if (Ui.Confirm(FindForm(), $"Delete device \"{d.DeviceName}\" ({RemoteIdFormat.Display(d.RemoteId)})?\nIts saved credential will also be deleted.")
                && Ui.Try(FindForm(), () => Rt.Clients.Delete(d.Id))) await RefreshAsync();
        });
        Add(bar, "Revoke Password", canManage, async () =>
        {
            var d = Selected(); if (d == null) return;
            if (!d.HasCredential) { Ui.Info(FindForm(), "This device has no saved password."); return; }
            if (Ui.Confirm(FindForm(), $"Revoke the saved password of \"{d.DeviceName}\"?")
                && Ui.Try(FindForm(), () => Rt.Clients.RevokeCredential(d.Id))) await RefreshAsync();
        });
        Add(bar, "Favorite", true, async () =>
        {
            var d = Selected(); if (d == null) return;
            if (Ui.Try(FindForm(), () => Rt.Clients.SetFavorite(d.Id, !d.IsFavorite))) await RefreshAsync();
        });
        Add(bar, "Copy Remote ID", true, () =>
        {
            var d = Selected(); if (d != null) Clipboard.SetText(d.RemoteId);
            return Task.CompletedTask;
        });
        Add(bar, "Import", canManage, ImportAsync);
        Add(bar, "Export", canManage, ExportAsync);
        Add(bar, "Reload", true, RefreshAsync);
        // Các nút cần Network/Relay: hiển thị để biết chức năng sẽ có, nhưng không giả lập hành vi.
        Add(bar, "Connect", rt.Session.Can(Permission.UseCredentials), ConnectSelectedAsync, ButtonKind.Primary);
        Add(bar, "Disconnect", true, () =>
        {
            var d = Selected(); if (d == null) return Task.CompletedTask;
            var session = RDM.Controller.Services.SessionRegistry.Find(d.RemoteId);
            if (session == null) Ui.Info(FindForm(), "This device has no open session.");
            else session.Close();
            return Task.CompletedTask;
        });
        Add(bar, "Remove Key Pin", rt.Session.Can(Permission.ManageSecurity), () =>
        {
            var d = Selected(); if (d == null) return Task.CompletedTask;
            if (Rt.HostKeys.GetPinned(d.RemoteId) == null) { Ui.Info(FindForm(), "This device has no pinned key."); return Task.CompletedTask; }
            if (Ui.Confirm(FindForm(), $"Remove the pinned identity key of \"{d.DeviceName}\"?\n\nOnly do this after verifying with the owner that the device was reinstalled. The next connection must verify the fingerprint again."))
                Ui.Try(FindForm(), () => Rt.HostKeys.RemovePin(d.RemoteId));
            return Task.CompletedTask;
        });
        Add(bar, "Refresh Status", true, async () =>
        {
            await Rt.Presence.RestartAsync();
            await RefreshAsync();
        });
        _count.Margin = new Padding(0, Dpi.S(9), 0, 0);
        bar.Controls.Add(_count);

        var card = new Card { Padding = new Padding(Dpi.S(4)) };
        card.Controls.Add(_grid);
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(bar, 0, 0);
        page.Controls.Add(card, 0, 1);
        Controls.Add(page);

        _debounce.Tick += async (s, e) => { _debounce.Stop(); await RefreshAsync(); };
        _search.Box.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
        _groupFilter.SelectionChangeCommitted += async (s, e) => await RefreshAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }

    private void Add(FlowLayoutPanel bar, string text, bool enabled, Func<Task> action, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new ModernButton(text, kind) { Enabled = enabled };
        b.Click += async (s, e) =>
        {
            try { await action(); }
            catch (Exception ex) { Ui.Error(FindForm(), ex); }
        };
        bar.Controls.Add(b);
    }

    private async Task ConnectSelectedAsync()
    {
        var d = Selected();
        if (d == null) return;
        await RDM.Controller.Services.ConnectFlow.ConnectAsync(Rt, FindForm(), d.RemoteId, null);
    }

    private bool Show(Form dialog)
    {
        using (dialog) return dialog.ShowDialog(FindForm()) == DialogResult.OK;
    }

    private ClientDevice Selected()
    {
        var d = _grid.SelectedItem;
        if (d == null) Ui.Info(FindForm(), "Select a device in the table.");
        return d;
    }

    public override bool ShowsPresence => true;

    public override Task RefreshAsync()
    {
        var search = _search.Text;
        long? group = _groupFilter.SelectedIndex > 0 && _groupFilter.SelectedIndex <= _groups.Count ? _groups[_groupFilter.SelectedIndex - 1].Id : (long?)null;
        // Chỉ áp dụng kết quả của lần tìm mới nhất, tránh kết quả cũ về sau ghi đè kết quả mới.
        var ticket = ++_loadTicket;
        return LoadAsync(() => new { Groups = Rt.Groups.GetAll(), Items = Rt.Clients.Query(search, group, _favoritesOnly) }, data =>
        {
            if (ticket != _loadTicket) return;
            RebuildGroups(data.Groups, group);
            _grid.SetItems(data.Items, (a, b) => a.Id == b.Id);
            _count.Text = $"{data.Items.Count} devices";
        });
    }

    private void RebuildGroups(List<DeviceGroup> groups, long? selected)
    {
        _groups = groups;
        _groupFilter.BeginUpdate();
        _groupFilter.Items.Clear();
        _groupFilter.Items.Add("Filter: all groups");
        foreach (var g in groups) _groupFilter.Items.Add(g.Name);
        var index = selected.HasValue ? groups.FindIndex(g => g.Id == selected) : -1;
        _groupFilter.SelectedIndex = index + 1;
        _groupFilter.EndUpdate();
    }

    private async Task ImportAsync()
    {
        using var dlg = new OpenFileDialog { Filter = "CSV or Excel (*.csv;*.xlsx)|*.csv;*.xlsx", Title = "Import Clients" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        var path = dlg.FileName;
        await BusyAsync(null, async () =>
        {
            var r = await Task.Run(() => Rt.Transfer.Import(path));
            var message = $"Added: {r.Added}\nSkipped (Remote ID already exists): {r.Skipped}\nErrors: {r.Errors.Count}";
            if (r.Errors.Count > 0) message += "\n\n" + string.Join("\n", r.Errors.Take(15)) + (r.Errors.Count > 15 ? "\n..." : "");
            Ui.Info(FindForm(), message);
            await RefreshAsync();
        });
    }

    private async Task ExportAsync()
    {
        using var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", Title = "Export Clients", FileName = "RDM_Clients" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        var path = dlg.FileName;
        var items = _grid.Items.ToList();
        await BusyAsync(null, async () =>
        {
            await Task.Run(() => Rt.Transfer.Export(path, items));
            Ui.Info(FindForm(), $"Exported {items.Count} devices. The file contains no passwords.");
        });
    }
}
