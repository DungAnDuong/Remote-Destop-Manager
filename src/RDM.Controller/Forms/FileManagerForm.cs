using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Services;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Shared.Network;
using RDM.Shared.Protocol;

namespace RDM.Controller.Forms;

/// <summary>
/// Trình quản lý file hai khung kiểu AnyDesk: bên trái là máy điều khiển, bên phải là máy ở xa.
/// Upload đưa file đang chọn bên trái vào thư mục đang mở bên phải; Download làm ngược lại.
/// </summary>
public sealed class FileManagerForm : Form
{
    private readonly RemoteSession _session;
    private readonly string _name;
    private readonly ListView _local = NewList();
    private readonly ListView _remote = NewList();
    private readonly TextBox _localPath = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _remotePath = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    private readonly Panel _xfers = new Panel { Dock = DockStyle.Bottom, Height = 0, AutoScroll = true };
    private readonly Dictionary<int, XferRow> _rows = new Dictionary<int, XferRow>();
    private ClipItem _clip;
    private string _localDir = "";
    private string _remoteDir = "";
    private readonly ShellIconCache _icons = new ShellIconCache(Dpi.S(16));

    private sealed class ClipItem { public bool Remote; public System.Collections.Generic.List<string> Paths; }

    public FileManagerForm(RemoteSession session, string name)
    {
        _session = session;
        _name = name ?? "";
        Text = "File Manager - " + name;
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        BackColor = Theme.Page;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(Dpi.S(980), Dpi.S(620));
        MinimumSize = new Size(Dpi.S(720), Dpi.S(420));

        _status.BackColor = Theme.SurfaceAlt;
        _status.ForeColor = Theme.TextSecondary;
        _status.Padding = new Padding(Dpi.S(8), 0, 0, 0);
        _xfers.BackColor = Theme.SurfaceAlt;

        _local.SmallImageList = _icons.Images;
        _remote.SmallImageList = _icons.Images;

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = Dpi.S(6) };
        split.Panel1.Controls.Add(BuildPane("This computer", _local, _localPath, "Upload →", Upload, out _));
        split.Panel2.Controls.Add(BuildPane(name + " (remote)", _remote, _remotePath, "← Download", Download, out _));
        Controls.Add(split);
        Controls.Add(_xfers);
        Controls.Add(_status);
        // Thanh trạng thái bám đáy ngoài cùng, dải tiến độ nằm ngay trên, khung file lấp phần còn lại.
        split.BringToFront();
        _status.SendToBack();

        _local.ItemActivate += (s, e) => { if (_local.SelectedItems.Count > 0) EnterLocal(_local.SelectedItems[0]); };
        _remote.ItemActivate += (s, e) => { if (_remote.SelectedItems.Count > 0) EnterRemote(_remote.SelectedItems[0]); };
        _local.ContextMenuStrip = BuildContextMenu(false);
        _remote.ContextMenuStrip = BuildContextMenu(true);
        _local.KeyDown += (s, e) => OnListKey(false, e);
        _remote.KeyDown += (s, e) => OnListKey(true, e);
        _session.TransferChanged += OnTransfer;
        FormClosed += (s, e) => { _session.TransferChanged -= OnTransfer; _icons.Dispose(); };
        Shown += async (s, e) =>
        {
            // Nhớ lại đường dẫn phiên trước: local toàn cục, remote theo từng máy. Đường dẫn cũ không còn thì về gốc.
            var saved = LoadSavedPaths();
            LoadLocal(saved.local);
            if (!await LoadRemoteAsync(saved.remote) && saved.remote.Length != 0) await LoadRemoteAsync("");
            SplitAfterShown(split);
        };
    }

    private static void SplitAfterShown(SplitContainer split) { try { split.SplitterDistance = split.Width / 2; } catch (InvalidOperationException) { } }

    private Control BuildPane(string title, ListView list, TextBox path, string actionText, Action action, out Button actionButton)
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        var header = new Label { Dock = DockStyle.Top, Height = Dpi.S(30), Text = title, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dpi.S(8), 0, 0, 0), BackColor = Theme.Sidebar, ForeColor = Color.White, Font = Theme.Font(9.5f, true), UseMnemonic = false };

        var bar = new Panel { Dock = DockStyle.Top, Height = Dpi.S(32), BackColor = Theme.Surface, Padding = new Padding(Dpi.S(2)) };
        var up = new Button { Dock = DockStyle.Left, Width = Dpi.S(36), Text = "↑", FlatStyle = FlatStyle.Flat };
        up.FlatAppearance.BorderColor = Theme.Border;
        up.Click += (s, e) => { if (list == _local) UpLocal(); else _ = UpRemoteAsync(); };
        var refresh = new Button { Dock = DockStyle.Left, Width = Dpi.S(36), Text = "↻", FlatStyle = FlatStyle.Flat };
        refresh.FlatAppearance.BorderColor = Theme.Border;
        refresh.Click += (s, e) => { if (list == _local) LoadLocal(_localDir); else _ = LoadRemoteAsync(_remoteDir); };
        // Nhập đường dẫn rồi Enter để nhảy đến (dán được).
        path.KeyDown += (s, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            var target = path.Text.Trim().Trim('"');
            if (list == _local) GoLocal(target); else _ = LoadRemoteAsync(target);
        };
        bar.Controls.Add(path);
        bar.Controls.Add(refresh);
        bar.Controls.Add(up);

        actionButton = new Button { Dock = DockStyle.Bottom, Height = Dpi.S(34), Text = actionText, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(9.5f, true) };
        actionButton.FlatAppearance.BorderSize = 0;
        actionButton.Click += (s, e) => action();

        root.Controls.Add(list);
        root.Controls.Add(actionButton);
        root.Controls.Add(bar);
        root.Controls.Add(header);
        return root;
    }

    private static ListView NewList()
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, MultiSelect = true };
        list.Columns.Add("Name", Dpi.S(260));
        list.Columns.Add("Size", Dpi.S(90), HorizontalAlignment.Right);
        list.Columns.Add("Modified", Dpi.S(130));
        list.BackColor = Theme.Surface;
        list.ForeColor = Theme.Text;
        return list;
    }

    // --- Local (máy điều khiển) ---

    private void LoadLocal(string dir)
    {
        _localDir = dir ?? "";
        _localPath.Text = _localDir.Length == 0 ? "This PC (drives)" : _localDir;
        _local.BeginUpdate();
        _local.Items.Clear();
        try
        {
            if (_localDir.Length == 0)
            {
                foreach (var d in DriveInfo.GetDrives()) AddRow(_local, d.Name, true, true, 0, DateTime.MinValue, d.Name);
            }
            else
            {
                AddRow(_local, "..", true, false, 0, DateTime.MinValue, null);
                foreach (var d in Directory.GetDirectories(_localDir)) { var di = new DirectoryInfo(d); AddRow(_local, di.Name, true, false, 0, SafeTime(di), d); }
                foreach (var f in Directory.GetFiles(_localDir)) { var fi = new FileInfo(f); AddRow(_local, fi.Name, false, false, SafeSize(fi), SafeTime(fi), f); }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { SetStatus("Local: " + ex.Message); }
        _local.EndUpdate();
        SavePaths();
    }

    private void EnterLocal(ListViewItem item)
    {
        var tag = (RowTag)item.Tag;
        if (!tag.IsDir) return;
        if (item.Text == "..") { UpLocal(); return; }
        LoadLocal(tag.FullPath);
    }

    private void UpLocal()
    {
        if (_localDir.Length == 0) return;
        var parent = Directory.GetParent(_localDir.TrimEnd(Path.DirectorySeparatorChar));
        LoadLocal(parent?.FullName ?? "");
    }

    // --- Remote (máy ở xa) ---

    private async Task<bool> LoadRemoteAsync(string dir)
    {
        SetStatus("Loading remote folder...");
        FsListing listing;
        try { listing = await _session.ListRemoteAsync(dir ?? ""); }
        catch (Exception) { SetStatus("Could not read the remote folder."); return false; }
        if (!listing.Ok) { SetStatus("Remote: " + (listing.Error ?? "access denied")); return false; }

        _remoteDir = dir ?? "";
        _remotePath.Text = _remoteDir.Length == 0 ? "This PC (drives)" : _remoteDir;
        _remote.BeginUpdate();
        _remote.Items.Clear();
        if (_remoteDir.Length != 0) AddRow(_remote, "..", true, false, 0, DateTime.MinValue, null);
        foreach (var e in listing.Entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            AddRow(_remote, e.Name, e.IsDirectory, e.IsDrive, e.Size, e.Modified, e.IsDrive ? e.Name : RemoteCombine(_remoteDir, e.Name));
        _remote.EndUpdate();
        SetStatus("Ready");
        SavePaths();
        return true;
    }

    private void EnterRemote(ListViewItem item)
    {
        var tag = (RowTag)item.Tag;
        if (!tag.IsDir) return;
        if (item.Text == "..") { _ = UpRemoteAsync(); return; }
        _ = LoadRemoteAsync(tag.FullPath);
    }

    private async Task UpRemoteAsync()
    {
        if (_remoteDir.Length == 0) return;
        var trimmed = _remoteDir.TrimEnd('\\', '/');
        var parent = trimmed.Length <= 2 ? "" : (Path.GetDirectoryName(trimmed) ?? "");
        await LoadRemoteAsync(parent);
    }

    private static string RemoteCombine(string dir, string name)
    {
        if (dir.Length == 0) return name;
        return dir.EndsWith("\\") || dir.EndsWith("/") ? dir + name : dir + "\\" + name;
    }

    // --- Nhớ đường dẫn phiên trước ---
    // File filemanager-paths.txt trong thư mục dữ liệu. Mỗi dòng cách nhau bằng tab (ký tự không hợp lệ trong đường dẫn
    // và tên máy của Windows, nên an toàn làm dấu phân cách). local lưu toàn cục; remote lưu theo từng tên máy.

    private static string PathsFile() => Path.Combine(SQLiteContext.DefaultDataDirectory(), "filemanager-paths.txt");

    private (string local, string remote) LoadSavedPaths()
    {
        string local = "", remote = "";
        try
        {
            foreach (var line in File.ReadAllLines(PathsFile()))
            {
                var p = line.Split('\t');
                if (p.Length == 2 && p[0] == "local") local = p[1];
                else if (p.Length == 3 && p[0] == "remote" && p[1] == _name) remote = p[2];
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        if (local.Length != 0 && !Directory.Exists(local)) local = "";
        return (local, remote);
    }

    private void SavePaths()
    {
        try
        {
            var lines = new List<string>();
            bool localDone = false, remoteDone = false;
            try
            {
                foreach (var line in File.ReadAllLines(PathsFile()))
                {
                    var p = line.Split('\t');
                    if (p.Length == 2 && p[0] == "local") { lines.Add("local\t" + _localDir); localDone = true; }
                    else if (p.Length == 3 && p[0] == "remote" && p[1] == _name) { lines.Add("remote\t" + _name + "\t" + _remoteDir); remoteDone = true; }
                    else lines.Add(line);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException) { }
            if (!localDone) lines.Add("local\t" + _localDir);
            if (!remoteDone) lines.Add("remote\t" + _name + "\t" + _remoteDir);
            Directory.CreateDirectory(Path.GetDirectoryName(PathsFile()));
            File.WriteAllLines(PathsFile(), lines);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
    }

    // --- Truyền ---

    private void Upload()
    {
        if (_remoteDir.Length == 0) { SetStatus("Open a remote folder first."); return; }
        var files = Selected(_local).Where(t => !t.IsDir && t.FullPath != null).ToList();
        if (files.Count == 0) { SetStatus("Select file(s) on the left to upload."); return; }
        // Trùng tên ở thư mục đích thì hỏi xác nhận; Yes = ghi đè, No = bỏ qua file đó, Cancel = dừng.
        var existing = RemoteFileNames();
        var started = 0;
        foreach (var f in files)
        {
            var name = Path.GetFileName(f.FullPath);
            var overwrite = false;
            if (existing.Contains(name))
            {
                var answer = MessageBox.Show(this, $"\"{name}\" already exists in the remote folder.\n\nOverwrite it?",
                    "File Manager", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (answer == DialogResult.Cancel) break;
                if (answer == DialogResult.No) continue;
                overwrite = true;
            }
            _ = Quiet(_session.UploadAsync(f.FullPath, _remoteDir, overwrite));
            started++;
        }
        if (started > 0) SetStatus($"Uploading {started} file(s)...");
    }

    /// <summary>Tên các file (không phải thư mục) đang có trong khung máy ở xa, để phát hiện trùng tên khi upload.</summary>
    private HashSet<string> RemoteFileNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem it in _remote.Items)
            if (it.Tag is RowTag tag && !tag.IsDir) set.Add(it.Text);
        return set;
    }

    private void Download()
    {
        if (_localDir.Length == 0) { SetStatus("Open a local folder first."); return; }
        var files = Selected(_remote).Where(t => !t.IsDir && t.FullPath != null).ToList();
        if (files.Count == 0) { SetStatus("Select file(s) on the right to download."); return; }
        foreach (var f in files) _ = Quiet(_session.RequestDownloadAsync(f.FullPath, _localDir));
        SetStatus($"Downloading {files.Count} file(s)...");
    }

    // --- Điều hướng bằng ô địa chỉ ---

    private void GoLocal(string target)
    {
        if (string.IsNullOrEmpty(target)) { LoadLocal(""); return; }
        if (Directory.Exists(target)) LoadLocal(target);
        else if (File.Exists(target)) LoadLocal(Path.GetDirectoryName(target));
        else SetStatus("Folder not found: " + target);
    }

    // --- Menu chuột phải và phím tắt kiểu Windows Explorer ---

    private ContextMenuStrip BuildContextMenu(bool remote)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (s, e) => OpenSelected(remote));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("New Folder", null, async (s, e) => await NewFolderAsync(remote));
        menu.Items.Add("Rename", null, async (s, e) => await RenameAsync(remote));
        menu.Items.Add("Delete", null, async (s, e) => await DeleteAsync(remote));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy", null, (s, e) => Copy(remote));
        menu.Items.Add("Paste", null, async (s, e) => await PasteAsync(remote));
        return menu;
    }

    private void OnListKey(bool remote, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2) { _ = RenameAsync(remote); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { _ = DeleteAsync(remote); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.C) { Copy(remote); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.V) { _ = PasteAsync(remote); e.Handled = true; }
        else if (e.KeyCode == Keys.F5) { if (remote) _ = LoadRemoteAsync(_remoteDir); else LoadLocal(_localDir); e.Handled = true; }
    }

    private void OpenSelected(bool remote)
    {
        var list = remote ? _remote : _local;
        if (list.SelectedItems.Count > 0) { if (remote) EnterRemote(list.SelectedItems[0]); else EnterLocal(list.SelectedItems[0]); }
    }

    private async Task NewFolderAsync(bool remote)
    {
        var dir = remote ? _remoteDir : _localDir;
        if (dir.Length == 0) { SetStatus("Open a folder first."); return; }
        var name = Prompt("New Folder", "Folder name:", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (remote) { await DoRemoteAsync(FsOp.NewFolder, dir, name); await LoadRemoteAsync(_remoteDir); }
        else
        {
            try { Directory.CreateDirectory(Path.Combine(dir, SafeLeaf(name))); LoadLocal(_localDir); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { SetStatus(ex.Message); }
        }
    }

    private async Task RenameAsync(bool remote)
    {
        var sel = Selected(remote ? _remote : _local).Where(t => t.FullPath != null).ToList();
        if (sel.Count != 1) { SetStatus("Select one item to rename."); return; }
        var item = sel[0];
        var current = Path.GetFileName(item.FullPath.TrimEnd('\\', '/'));
        var name = Prompt("Rename", "New name:", current);
        if (string.IsNullOrWhiteSpace(name) || name == current) return;
        if (remote) { await DoRemoteAsync(FsOp.Rename, item.FullPath, name); await LoadRemoteAsync(_remoteDir); }
        else
        {
            try
            {
                var dest = Path.Combine(Path.GetDirectoryName(item.FullPath) ?? "", SafeLeaf(name));
                if (item.IsDir) Directory.Move(item.FullPath, dest); else File.Move(item.FullPath, dest);
                LoadLocal(_localDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { SetStatus(ex.Message); }
        }
    }

    private async Task DeleteAsync(bool remote)
    {
        var sel = Selected(remote ? _remote : _local).Where(t => t.FullPath != null).ToList();
        if (sel.Count == 0) { SetStatus("Select item(s) to delete."); return; }
        if (MessageBox.Show(this, $"Delete {sel.Count} item(s)? This cannot be undone.", "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (remote)
        {
            foreach (var t in sel) await DoRemoteAsync(FsOp.Delete, t.FullPath, "");
            await LoadRemoteAsync(_remoteDir);
        }
        else
        {
            foreach (var t in sel)
                try { if (t.IsDir) Directory.Delete(t.FullPath, true); else File.Delete(t.FullPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { SetStatus(ex.Message); }
            LoadLocal(_localDir);
        }
    }

    private void Copy(bool remote)
    {
        var paths = Selected(remote ? _remote : _local).Where(t => t.FullPath != null).Select(t => t.FullPath).ToList();
        if (paths.Count == 0) { SetStatus("Nothing selected to copy."); return; }
        _clip = new ClipItem { Remote = remote, Paths = paths };
        SetStatus($"Copied {paths.Count} item(s). Paste into a folder.");
    }

    private async Task PasteAsync(bool toRemote)
    {
        if (_clip == null || _clip.Paths.Count == 0) { SetStatus("Clipboard is empty."); return; }
        var destDir = toRemote ? _remoteDir : _localDir;
        if (destDir.Length == 0) { SetStatus("Open a folder first."); return; }
        foreach (var src in _clip.Paths)
        {
            if (!_clip.Remote && !toRemote) LocalCopy(src, destDir);                 // máy này -> máy này
            else if (!_clip.Remote && toRemote) _ = Quiet(_session.UploadAsync(src, destDir));       // máy này -> máy ở xa
            else if (_clip.Remote && !toRemote) _ = Quiet(_session.RequestDownloadAsync(src, destDir)); // máy ở xa -> máy này
            else await DoRemoteAsync(FsOp.Copy, src, destDir);                        // máy ở xa -> máy ở xa
        }
        if (!_clip.Remote && !toRemote) LoadLocal(_localDir);
        else if (_clip.Remote && toRemote) await LoadRemoteAsync(_remoteDir);
        else SetStatus("Pasting...");
    }

    private void LocalCopy(string src, string destDir)
    {
        try
        {
            if (Directory.Exists(src))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(src.TrimEnd('\\', '/')));
                CopyDirLocal(src, dest);
            }
            else if (File.Exists(src)) File.Copy(src, UniqueLocal(destDir, Path.GetFileName(src)), false);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { SetStatus(ex.Message); }
    }

    private static void CopyDirLocal(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), false);
        foreach (var d in Directory.GetDirectories(source)) CopyDirLocal(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static string UniqueLocal(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return p;
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        for (var i = 1; i < 10000; i++) { var c = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!File.Exists(c)) return c; }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private async Task DoRemoteAsync(FsOp op, string path, string arg2)
    {
        try
        {
            var (ok, error) = await _session.MutateRemoteAsync(op, path, arg2);
            if (!ok) SetStatus("Remote: " + (error ?? "operation failed"));
        }
        catch (Exception) { SetStatus("The operation could not be sent to the remote computer."); }
    }

    private static string SafeLeaf(string name)
    {
        var bare = Path.GetFileName(name ?? "");
        foreach (var c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
        return bare.Trim();
    }

    // Hộp nhập tên đơn giản (cho New Folder / Rename).
    private string Prompt(string title, string label, string initial)
    {
        using var dlg = new Form { Text = title, Icon = Theme.AppIcon, Font = Theme.Font(), BackColor = Theme.Page, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Dpi.S(360), Dpi.S(130)) };
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(Dpi.S(14), Dpi.S(14)), ForeColor = Theme.Text, UseMnemonic = false };
        var box = new TextBox { Text = initial, Location = new Point(Dpi.S(14), Dpi.S(40)), Width = Dpi.S(332), BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(Dpi.S(176), Dpi.S(84)), Width = Dpi.S(80), FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(Dpi.S(266), Dpi.S(84)), Width = Dpi.S(80), FlatStyle = FlatStyle.Flat };
        cancel.FlatAppearance.BorderColor = Theme.Border;
        dlg.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        box.SelectAll();
        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    private void OnTransfer(FileTransferProgress p)
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            UpdateRow(p);
            switch (p.Status)
            {
                case TransferStatus.Active:
                    var pct = p.TotalBytes > 0 ? (int)(p.TransferredBytes * 100 / p.TotalBytes) : 0;
                    SetStatus($"{(p.Incoming ? "Downloading" : "Uploading")} {p.FileName} - {pct}%");
                    break;
                case TransferStatus.Completed:
                    SetStatus($"Finished {p.FileName}");
                    if (p.Incoming) LoadLocal(_localDir); else _ = LoadRemoteAsync(_remoteDir);
                    break;
                default:
                    SetStatus($"{p.FileName}: {p.Error ?? p.Status.ToString()}");
                    break;
            }
        }));
    }

    // --- Dải tiến độ: mỗi file một hàng có thanh phần trăm, tự ẩn sau khi xong ---

    private void UpdateRow(FileTransferProgress p)
    {
        var row = EnsureRow(p);
        int permille = p.TotalBytes > 0 ? (int)Math.Min(1000, Math.Max(0, p.TransferredBytes * 1000 / p.TotalBytes)) : 0;
        if (p.Status == TransferStatus.Completed) permille = 1000;
        row.Bar.Value = Math.Min(row.Bar.Maximum, permille);
        int pct = p.TotalBytes > 0 ? (int)Math.Min(100, p.TransferredBytes * 100 / p.TotalBytes) : (p.Status == TransferStatus.Completed ? 100 : 0);
        string arrow = p.Incoming ? "↓" : "↑";
        string state;
        switch (p.Status)
        {
            case TransferStatus.Completed: state = "Done"; break;
            case TransferStatus.Failed: state = "Failed"; break;
            case TransferStatus.Canceled: state = "Canceled"; break;
            default: state = pct + "%"; break;
        }
        row.Caption.Text = $"{arrow} {p.FileName}  -  {state}  ({FormatSize(p.TransferredBytes)} / {FormatSize(p.TotalBytes)})";
        if (p.Status != TransferStatus.Active) ScheduleRemove(p.Id);
    }

    private XferRow EnsureRow(FileTransferProgress p)
    {
        if (_rows.TryGetValue(p.Id, out var existing)) return existing;
        var bar = new ProgressBar { Dock = DockStyle.Bottom, Height = Dpi.S(8), Maximum = 1000, Style = ProgressBarStyle.Continuous };
        var cap = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true, ForeColor = Theme.Text };
        var root = new Panel { Dock = DockStyle.Top, Height = Dpi.S(34), Padding = new Padding(Dpi.S(8), Dpi.S(4), Dpi.S(8), Dpi.S(2)), BackColor = Theme.SurfaceAlt };
        root.Controls.Add(cap);
        root.Controls.Add(bar);
        bar.SendToBack();
        cap.BringToFront();
        _xfers.Controls.Add(root);
        root.BringToFront();
        var row = new XferRow { Root = root, Caption = cap, Bar = bar };
        _rows[p.Id] = row;
        ResizeXfers();
        return row;
    }

    private void ScheduleRemove(int id)
    {
        Task.Delay(3000).ContinueWith(_ =>
        {
            if (IsDisposed) return;
            try { BeginInvoke(new Action(() => RemoveRow(id))); } catch (Exception) { }
        });
    }

    private void RemoveRow(int id)
    {
        if (IsDisposed) return;
        if (_rows.TryGetValue(id, out var row))
        {
            _rows.Remove(id);
            _xfers.Controls.Remove(row.Root);
            row.Root.Dispose();
            ResizeXfers();
        }
    }

    private void ResizeXfers()
    {
        int rh = Dpi.S(34);
        _xfers.Height = _rows.Count == 0 ? 0 : Math.Min(_rows.Count, 4) * rh + Dpi.S(4);
    }

    private sealed class XferRow { public Panel Root; public Label Caption; public ProgressBar Bar; }

    private IEnumerable<RowTag> Selected(ListView list)
    {
        foreach (ListViewItem i in list.SelectedItems) yield return (RowTag)i.Tag;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void AddRow(ListView list, string name, bool isDir, bool isDrive, long size, DateTime modified, string fullPath)
    {
        var remote = ReferenceEquals(list, _remote);
        // Local: lấy icon thật theo đường dẫn. Remote: lấy icon theo phần mở rộng/loại (không có file thật ở máy này).
        var img = _icons.Index(remote ? name : (fullPath ?? name), isDir, isDrive, remote);
        var item = new ListViewItem(name) { Tag = new RowTag { IsDir = isDir, FullPath = fullPath }, ImageIndex = img };
        item.SubItems.Add(isDir ? "" : FormatSize(size));
        item.SubItems.Add(modified == DateTime.MinValue ? "" : modified.ToString("yyyy-MM-dd HH:mm"));
        list.Items.Add(item);
    }

    private sealed class RowTag { public bool IsDir; public string FullPath; }

    /// <summary>
    /// Kho icon hệ thống (giống Windows Explorer) cho ổ đĩa, thư mục và từng loại file.
    /// Local lấy icon thật theo đường dẫn; remote lấy icon theo phần mở rộng (SHGFI_USEFILEATTRIBUTES).
    /// Cache theo khoá (ổ đĩa / thư mục / ".ext") để không gọi shell lặp lại và ImageList không phình.
    /// </summary>
    private sealed class ShellIconCache : IDisposable
    {
        public ImageList Images { get; }
        private readonly int _size;
        private readonly Dictionary<string, int> _map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private const uint SHGFI_ICON = 0x000000100, SHGFI_SMALLICON = 0x000000001, SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010, FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        public ShellIconCache(int size)
        {
            _size = size;
            Images = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
            Images.Images.Add(GenericFile(size)); // chỉ số 0 = dự phòng nếu không lấy được icon hệ thống
            _map["::fallback"] = 0;
        }

        public int Index(string nameOrPath, bool isDir, bool isDrive, bool remote)
        {
            string key;
            if (isDrive) key = remote ? "::drive" : "drv:" + nameOrPath;
            else if (isDir) key = "::dir";
            else { var ext = Path.GetExtension(nameOrPath ?? string.Empty); key = string.IsNullOrEmpty(ext) ? "::file" : "ext:" + ext.ToLowerInvariant(); }

            if (_map.TryGetValue(key, out var idx)) return idx;
            int result = 0;
            try
            {
                using var bmp = Fetch(nameOrPath, isDir, isDrive, remote);
                if (bmp != null) { Images.Images.Add((Bitmap)bmp.Clone()); result = Images.Images.Count - 1; }
            }
            catch (Exception) { }
            _map[key] = result;
            return result;
        }

        private Bitmap Fetch(string nameOrPath, bool isDir, bool isDrive, bool remote)
        {
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;
            uint attr = 0;
            string path;
            if (isDrive)
            {
                // Ổ đĩa: local dùng icon thật của ổ; remote dùng icon ổ của hệ thống (lấy từ ổ hệ điều hành).
                path = !remote && nameOrPath != null && Directory.Exists(nameOrPath) ? nameOrPath : SystemDrive();
            }
            else if (!remote && nameOrPath != null && (File.Exists(nameOrPath) || Directory.Exists(nameOrPath)))
            {
                path = nameOrPath; // icon thật của file/thư mục đang có
            }
            else
            {
                flags |= SHGFI_USEFILEATTRIBUTES; // không cần file thật: lấy icon theo loại
                if (isDir) { attr = FILE_ATTRIBUTE_DIRECTORY; path = "folder"; }
                else { var ext = Path.GetExtension(nameOrPath ?? string.Empty); attr = FILE_ATTRIBUTE_NORMAL; path = string.IsNullOrEmpty(ext) ? "file" : "file" + ext; }
            }

            var sfi = new SHFILEINFO();
            var res = SHGetFileInfo(path, attr, ref sfi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
            if (res == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
            try
            {
                using var ico = Icon.FromHandle(sfi.hIcon);
                using var src = ico.ToBitmap();
                var bmp = new Bitmap(_size, _size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, new Rectangle(0, 0, _size, _size));
                }
                return bmp;
            }
            finally { DestroyIcon(sfi.hIcon); }
        }

        private static string SystemDrive() => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";

        private static Bitmap GenericFile(int s)
        {
            var bmp = new Bitmap(s, s);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = s / 16f;
            var r = new RectangleF(3.5f * f, 1.5f * f, 9f * f, 13f * f);
            using var b = new SolidBrush(Color.White);
            using var pen = new Pen(Color.FromArgb(150, 160, 170), Math.Max(1f, f));
            g.FillRectangle(b, r);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            return bmp;
        }

        public void Dispose() => Images.Dispose();
    }

    private static long SafeSize(FileInfo f) { try { return f.Length; } catch (IOException) { return 0; } }
    private static DateTime SafeTime(FileSystemInfo i) { try { return i.LastWriteTime; } catch (IOException) { return DateTime.MinValue; } catch (UnauthorizedAccessException) { return DateTime.MinValue; } }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    private static async Task Quiet(Task t) { try { await t.ConfigureAwait(false); } catch (Exception) { } }
}
