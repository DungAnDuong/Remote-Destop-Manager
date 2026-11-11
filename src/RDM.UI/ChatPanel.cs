using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RDM.Controller.Theming;
using RDM.Shared.Network;

namespace RDM.Controller.Controls;

/// <summary>
/// Bảng chat và truyền file dùng chung cho cả hai phía, kiểu bong bóng tin nhắn.
/// Chỉ lo hiển thị và nhập liệu; việc gửi/nhận thật do phiên xử lý qua các sự kiện.
/// </summary>
public sealed class ChatPanel : UserControl
{
    private sealed class Bubble : Control
    {
        private readonly bool _mine;
        public bool Mine => _mine;
        public Bubble(string text, bool mine)
        {
            _mine = mine;
            Text = text;
            Font = Theme.Font(9.5f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void Measure(int maxWidth)
        {
            var pad = Dpi.S(10);
            var size = TextRenderer.MeasureText(Text, Font, new Size(maxWidth - pad * 2, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Size = new Size(Math.Min(maxWidth, size.Width + pad * 2 + Dpi.S(2)), size.Height + pad * 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            var radius = Dpi.S(12);
            using var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, radius, radius, 180, 90);
            path.AddArc(r.Right - radius, r.Y, radius, radius, 270, 90);
            path.AddArc(r.Right - radius, r.Bottom - radius, radius, radius, 0, 90);
            path.AddArc(r.X, r.Bottom - radius, radius, radius, 90, 90);
            path.CloseFigure();
            using (var b = new SolidBrush(_mine ? Theme.Accent : Theme.SurfaceAlt)) g.FillPath(b, path);
            var pad = Dpi.S(10);
            var text = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2);
            TextRenderer.DrawText(g, Text, Font, text, _mine ? Color.White : Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    private readonly FlowLayoutPanel _list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly Label _title = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, ForeColor = Color.White, Padding = new Padding(12, 0, 0, 0) };
    private readonly TextBox _input = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly Dictionary<int, FileEntry> _fileEntries = new Dictionary<int, FileEntry>();
    private static readonly Image FileGlyph = BuildFileGlyph();

    private readonly Button _detach;

    public string PeerName { get; set; } = "Peer";
    public string Title { set { _title.Text = value; } }
    public bool AllowDetach { set { _detach.Visible = value; } }

    public event Action<string> ChatEntered;
    public event Action SendFileRequested;
    public event Action CloseRequested;
    public event Action DetachRequested;
    public event Action<int> PauseRequested;
    public event Action<int> ResumeRequested;
    public event Action<int> CancelRequested;

    public ChatPanel()
    {
        BackColor = Theme.Surface;

        var header = new Panel { Dock = DockStyle.Top, Height = Dpi.S(38), BackColor = Theme.Sidebar };
        _title.Font = Theme.Font(10f, true);
        var close = new Button { Dock = DockStyle.Right, Width = Dpi.S(40), Text = "✕", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Theme.Sidebar, Font = Theme.Font(11f) };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Theme.Critical;
        close.Click += (s, e) => CloseRequested?.Invoke();
        _detach = new Button { Dock = DockStyle.Right, Width = Dpi.S(40), Text = "⧉", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Theme.Sidebar, Font = Theme.Font(11f), Visible = false };
        _detach.FlatAppearance.BorderSize = 0;
        _detach.FlatAppearance.MouseOverBackColor = Theme.Accent;
        _detach.Click += (s, e) => DetachRequested?.Invoke();
        header.Controls.Add(_title);
        header.Controls.Add(close);
        header.Controls.Add(_detach);

        _list.BackColor = Theme.Surface;
        _list.Padding = new Padding(Dpi.S(8));
        _list.ClientSizeChanged += (s, e) => Reflow();

        _input.Font = Theme.Font(10.5f);
        _input.BorderStyle = BorderStyle.None;
        _input.ForeColor = Theme.Text;
        _input.BackColor = Theme.Surface;
        _input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Enter(); } };

        // Hàng nhập: ô chữ bên trái; Send rồi tới mặt cười / đính kèm / like đều nằm BÊN PHẢI nút Send.
        var send = IconButton(DrawSend(Dpi.S(22)));
        send.Dock = DockStyle.Right;
        send.Click += (s, e) => Enter();
        var emoji = IconButton(DrawSmiley(Dpi.S(22)));
        emoji.Dock = DockStyle.Right;
        emoji.Click += (s, e) => ShowEmojiPicker(emoji);
        var attach = IconButton(DrawAttach(Dpi.S(22)));
        attach.Dock = DockStyle.Right;
        attach.Click += (s, e) => SendFileRequested?.Invoke();
        var like = IconButton(DrawLike(Dpi.S(22)));
        like.Dock = DockStyle.Right;
        like.Click += (s, e) => ChatEntered?.Invoke("\U0001F44D");

        var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Dpi.S(8), Dpi.S(13), Dpi.S(4), 0) };
        wrap.Controls.Add(_input);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(48), BackColor = Theme.Surface, Padding = new Padding(Dpi.S(4), Dpi.S(3), Dpi.S(4), Dpi.S(3)) };
        // Thêm theo thứ tự far-right -> gần input: like, attach, emoji, send; cuối cùng là ô chữ (fill).
        bottom.Controls.Add(like);
        bottom.Controls.Add(attach);
        bottom.Controls.Add(emoji);
        bottom.Controls.Add(send);
        bottom.Controls.Add(wrap);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(header);
    }

    private void Enter()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        ChatEntered?.Invoke(text);
    }

    public void FocusInput() => _input.Focus();

    private static Button IconButton(Image img)
    {
        var b = new Button { Width = Dpi.S(40), FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, Image = img, ImageAlign = ContentAlignment.MiddleCenter, TabStop = false };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.SurfaceAlt;
        b.FlatAppearance.MouseDownBackColor = Theme.SurfaceAlt;
        return b;
    }

    private void ShowEmojiPicker(Control anchor)
    {
        var menu = new ContextMenuStrip { Font = new Font("Segoe UI Emoji", 13f), ShowImageMargin = false };
        foreach (var em in new[] { "\U0001F600", "\U0001F601", "\U0001F602", "\U0001F60D", "\U0001F60E", "\U0001F44D", "❤️", "\U0001F64F", "\U0001F622", "\U0001F62E", "\U0001F389", "✅" })
        {
            var item = new ToolStripMenuItem(em);
            var value = em;
            item.Click += (s, e) => InsertEmoji(value);
            menu.Items.Add(item);
        }
        menu.Show(anchor, new Point(0, -menu.Height));
    }

    private void InsertEmoji(string em)
    {
        var pos = Math.Max(0, Math.Min(_input.Text.Length, _input.SelectionStart));
        _input.Text = _input.Text.Insert(pos, em);
        _input.SelectionStart = pos + em.Length;
        _input.Focus();
    }

    // --- Icon vẽ phẳng kiểu Zalo cho hàng nhập ---

    private static Image DrawSend(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float f = s / 22f;
        using var b = new SolidBrush(Theme.Accent);
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(3 * f, 4 * f), new PointF(19 * f, 11 * f), new PointF(3 * f, 18 * f), new PointF(7 * f, 11 * f) });
        g.FillPath(b, path);
        return bmp;
    }

    private static Image DrawAttach(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float f = s / 22f;
        using var pen = new Pen(Theme.TextSecondary, Math.Max(1.4f, 1.7f * f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, 6.5f * f, 3.5f * f, 9f * f, 8f * f, 180, 180);
        g.DrawLine(pen, 6.5f * f, 7.5f * f, 6.5f * f, 15f * f);
        g.DrawLine(pen, 15.5f * f, 7.5f * f, 15.5f * f, 14f * f);
        g.DrawArc(pen, 6.5f * f, 11f * f, 9f * f, 7f * f, 0, 180);
        g.DrawLine(pen, 10f * f, 6.5f * f, 10f * f, 14.5f * f);
        return bmp;
    }

    private static Image DrawSmiley(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float f = s / 22f;
        using var pen = new Pen(Theme.TextSecondary, Math.Max(1.4f, 1.6f * f));
        using var eye = new SolidBrush(Theme.TextSecondary);
        g.DrawEllipse(pen, 2.5f * f, 2.5f * f, 17f * f, 17f * f);
        g.FillEllipse(eye, 7f * f, 8f * f, 2f * f, 2f * f);
        g.FillEllipse(eye, 13f * f, 8f * f, 2f * f, 2f * f);
        g.DrawArc(pen, 6.5f * f, 8.5f * f, 9f * f, 7.5f * f, 20, 140);
        return bmp;
    }

    private static Image DrawLike(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float f = s / 22f;
        using var b = new SolidBrush(Theme.Accent);
        // Nắm tay + ngón cái (thumbs up) đơn giản, phẳng.
        using (var palm = RoundRect(new Rectangle((int)(8 * f), (int)(9 * f), (int)(9 * f), (int)(9 * f)), (int)(2f * f))) g.FillPath(b, palm);
        using (var cuff = RoundRect(new Rectangle((int)(3.5f * f), (int)(10 * f), (int)(3.5f * f), (int)(8 * f)), (int)(1.2f * f))) g.FillPath(b, cuff);
        using (var thumb = new GraphicsPath())
        {
            thumb.AddEllipse(8.5f * f, 3.5f * f, 5.5f * f, 7f * f);
            g.FillPath(b, thumb);
        }
        return bmp;
    }

    public void AddOutgoing(string text) => AddBubble(text, true);
    public void AddIncoming(string text) => AddBubble(text, false);

    public void AddSystem(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => AddSystem(text))); return; }
        var label = new Label { Text = text, AutoSize = false, Height = Dpi.S(20), Width = _list.ClientSize.Width - Dpi.S(24), ForeColor = Theme.TextMuted, Font = Theme.Font(8.5f), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, Dpi.S(2), 0, Dpi.S(2)) };
        _list.Controls.Add(label);
        _list.ScrollControlIntoView(label);
    }

    private void AddBubble(string text, bool mine)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => AddBubble(text, mine))); return; }
        // Kiểu Zalo: không ghi tên trong bong bóng; bên phải là của mình, bên trái là của người kia.
        var bubble = new Bubble(text, mine);
        _list.Controls.Add(bubble);
        PlaceBubble(bubble);
        _list.ScrollControlIntoView(bubble);
    }

    private int MaxBubbleWidth() => Math.Max(Dpi.S(120), (int)(_list.ClientSize.Width * 0.72));

    // Tin của mình nằm sát phải, tin của bên kia nằm sát trái.
    private void PlaceBubble(Bubble bubble)
    {
        bubble.Measure(MaxBubbleWidth());
        var left = bubble.Mine ? Math.Max(Dpi.S(8), _list.ClientSize.Width - bubble.Width - Dpi.S(20)) : Dpi.S(4);
        bubble.Margin = new Padding(left, Dpi.S(3), 0, Dpi.S(3));
    }

    private void Reflow()
    {
        foreach (Control c in _list.Controls)
        {
            if (c is Bubble b) PlaceBubble(b);
            else if (c is FileEntry f) PlaceFileEntry(f);
        }
    }

    // Thẻ file canh như bong bóng: của mình sát phải, của người kia sát trái.
    private void PlaceFileEntry(FileEntry f)
    {
        f.Width = Math.Min(Dpi.S(300), MaxBubbleWidth());
        var left = f.Mine ? Math.Max(Dpi.S(8), _list.ClientSize.Width - f.Width - Dpi.S(20)) : Dpi.S(4);
        f.Margin = new Padding(left, Dpi.S(3), 0, Dpi.S(3));
    }

    /// <summary>Hiển thị một lượt truyền file ngay trong dòng chat: icon theo loại file, tên, kích thước và tiến trình.</summary>
    public void UpdateTransfer(FileTransferProgress p)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => UpdateTransfer(p))); return; }
        if (!_fileEntries.TryGetValue(p.Id, out var entry))
        {
            entry = new FileEntry(p.Id, p.Incoming, PeerName,
                id => PauseRequested?.Invoke(id), id => ResumeRequested?.Invoke(id), id => CancelRequested?.Invoke(id));
            _fileEntries[p.Id] = entry;
            _list.Controls.Add(entry);
            PlaceFileEntry(entry);
        }
        entry.Update(p);
        _list.ScrollControlIntoView(entry);
    }

    private static Image BuildFileGlyph()
    {
        int s = Dpi.S(22);
        float f = s / 22f;
        var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4f * f, 2f * f, 14f * f, 18f * f);
            float fold = 5f * f;
            using (var path = new GraphicsPath())
            {
                path.AddLine(r.X, r.Y, r.Right - fold, r.Y);
                path.AddLine(r.Right - fold, r.Y, r.Right, r.Y + fold);
                path.AddLine(r.Right, r.Y + fold, r.Right, r.Bottom);
                path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
                path.CloseFigure();
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(120, 140, 170), Math.Max(1f, 1.3f * f))) g.DrawPath(pen, path);
            }
            using (var b2 = new SolidBrush(Color.FromArgb(200, 212, 228)))
            using (var tri = new GraphicsPath())
            {
                tri.AddLine(r.Right - fold, r.Y, r.Right - fold, r.Y + fold);
                tri.AddLine(r.Right - fold, r.Y + fold, r.Right, r.Y + fold);
                tri.CloseFigure();
                g.FillPath(b2, tri);
            }
        }
        return bmp;
    }

    // --- Icon hệ thống theo loại file (giống Windows/Zalo), cache theo phần mở rộng ---
    private static readonly Dictionary<string, Image> IconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

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

    private static Image FileIconFor(string fileName, int target)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        var key = (string.IsNullOrEmpty(ext) ? "::file" : ext.ToLowerInvariant()) + "@" + target;
        if (IconCache.TryGetValue(key, out var img)) return img;
        img = FetchShellIcon(string.IsNullOrEmpty(ext) ? "file" : "file" + ext, target) ?? FileGlyph;
        IconCache[key] = img;
        return img;
    }

    private static Image FetchShellIcon(string pseudoName, int target)
    {
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
        var sfi = new SHFILEINFO();
        var res = SHGetFileInfo(pseudoName, FILE_ATTRIBUTE_NORMAL, ref sfi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        if (res == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
        try
        {
            using var ico = Icon.FromHandle(sfi.hIcon);
            using var src = ico.ToBitmap();
            var bmp = new Bitmap(target, target);
            using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, new Rectangle(0, 0, target, target)); }
            return bmp;
        }
        catch (Exception) { return null; }
        finally { DestroyIcon(sfi.hIcon); }
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb(
            (int)(a.R * t + b.R * (1 - t)),
            (int)(a.G * t + b.G * (1 - t)),
            (int)(a.B * t + b.B * (1 - t)));

    /// <summary>Thẻ file kiểu Zalo: icon theo loại, tên, kích thước + trạng thái và thanh tiến trình; hủy/tạm ngưng khi đang truyền.</summary>
    private sealed class FileEntry : Control
    {
        private readonly int _id;
        private readonly bool _incoming;
        private readonly Action<int> _onPause, _onResume, _onCancel;
        private readonly Button _pause, _cancel, _open, _folder;
        private Image _icon;
        private string _name = "";
        private string _status = "";
        private string _savePath;
        private float _progress;
        private bool _done, _paused, _active;

        public bool Mine => !_incoming;
        private bool HasButtons => _cancel.Visible || _pause.Visible || _open.Visible || _folder.Visible;

        public FileEntry(int id, bool incoming, string peer, Action<int> onPause, Action<int> onResume, Action<int> onCancel)
        {
            _id = id; _incoming = incoming; _onPause = onPause; _onResume = onResume; _onCancel = onCancel;
            Height = Dpi.S(64);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;

            _pause = IconButton("❚❚");
            _pause.Click += (s, e) =>
            {
                if (_done) return;
                if (_paused) { _paused = false; _onResume?.Invoke(_id); _pause.Text = "❚❚"; }
                else { _paused = true; _onPause?.Invoke(_id); _pause.Text = "▶"; }
            };
            _cancel = IconButton("✕");
            _cancel.Click += (s, e) => { if (!_done) _onCancel?.Invoke(_id); };
            _open = IconButton("↗");          // mở file
            _open.Click += (s, e) => OpenFile();
            _folder = IconButton("\U0001F4C1");    // mở thư mục chứa
            _folder.Click += (s, e) => OpenFolder();
            Controls.Add(_pause);
            Controls.Add(_cancel);
            Controls.Add(_open);
            Controls.Add(_folder);
            Resize += (s, e) => LayoutButtons();
        }

        private static Button IconButton(string text)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Font = Theme.Font(8.5f), BackColor = Theme.Surface, ForeColor = Theme.TextSecondary, Visible = false, TabStop = false };
            b.FlatAppearance.BorderColor = Theme.Border;
            return b;
        }

        private void OpenFile()
        {
            if (string.IsNullOrEmpty(_savePath)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_savePath) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        private void OpenFolder()
        {
            if (string.IsNullOrEmpty(_savePath)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _savePath + "\""); }
            catch (Exception) { }
        }

        // Đặt các nút đang hiện từ phải sang trái (ưu tiên: hủy / mở ở ngoài cùng, tạm ngưng / thư mục kế bên).
        private void LayoutButtons()
        {
            int bw = Dpi.S(26), bh = Dpi.S(24), pad = Dpi.S(8), gap = Dpi.S(4), y = (Height - bh) / 2;
            int x = Width - pad - bw;
            foreach (var b in new[] { _cancel, _open, _pause, _folder })
            {
                if (!b.Visible) continue;
                b.SetBounds(x, y, bw, bh);
                x -= bw + gap;
            }
        }

        public void Update(FileTransferProgress p)
        {
            if (_icon == null) { _name = p.FileName; _icon = FileIconFor(p.FileName, Dpi.S(34)); }
            if (!string.IsNullOrEmpty(p.SavePath)) _savePath = p.SavePath;
            _progress = p.TotalBytes > 0 ? Math.Min(1f, Math.Max(0f, (float)p.TransferredBytes / p.TotalBytes)) : (p.Status == TransferStatus.Completed ? 1f : 0f);
            int pct = (int)Math.Round(_progress * 100);
            string sent = FormatSize(p.TransferredBytes), total = FormatSize(p.TotalBytes);
            switch (p.Status)
            {
                case TransferStatus.Completed: _status = (_incoming ? "Received" : "Sent") + "  ✓  " + total; _done = true; _active = false; _progress = 1f; break;
                case TransferStatus.Failed: _status = "Failed" + (string.IsNullOrEmpty(p.Error) ? "" : "  -  " + p.Error); _done = true; _active = false; break;
                case TransferStatus.Canceled: _status = "Canceled"; _done = true; _active = false; break;
                case TransferStatus.Paused: _status = $"Paused  {pct}%  ({sent} / {total})"; _active = true; break;
                default: _status = $"{pct}%  ({sent} / {total})"; _active = true; break;
            }
            _pause.Visible = _active && !_incoming;
            _cancel.Visible = _active;
            // Xong và còn file trên đĩa: cho mở file / mở thư mục chứa (cả bên nhận lẫn bên gửi).
            var canOpen = p.Status == TransferStatus.Completed && !string.IsNullOrEmpty(_savePath);
            _open.Visible = canOpen;
            _folder.Visible = canOpen;
            LayoutButtons();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundRect(r, Dpi.S(12)))
            using (var b = new SolidBrush(Mine ? Blend(Theme.Accent, Theme.Surface, 0.16f) : Theme.SurfaceAlt))
                g.FillPath(b, path);

            int iconSize = Dpi.S(34), pad = Dpi.S(10);
            if (_icon != null) g.DrawImage(_icon, new Rectangle(pad, (Height - iconSize) / 2, iconSize, iconSize));

            int textLeft = pad + iconSize + Dpi.S(8);
            int rightPad = HasButtons ? Dpi.S(72) : Dpi.S(10);
            var nameRect = new Rectangle(textLeft, Dpi.S(9), Math.Max(Dpi.S(20), Width - textLeft - rightPad), Dpi.S(18));
            TextRenderer.DrawText(g, _name, Theme.Font(9.5f, true), nameRect, Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
            var statRect = new Rectangle(textLeft, Dpi.S(28), Math.Max(Dpi.S(20), Width - textLeft - rightPad), Dpi.S(16));
            TextRenderer.DrawText(g, _status, Theme.Font(8.5f), statRect, Theme.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left);

            if (!_done)
            {
                int bh = Dpi.S(4), by = Height - Dpi.S(12), bx = textLeft, bw = Math.Max(Dpi.S(20), Width - textLeft - Dpi.S(10));
                using (var track = new SolidBrush(Blend(Theme.TextMuted, Theme.SurfaceAlt, 0.45f)))
                using (var tp = RoundRect(new Rectangle(bx, by, bw, bh), bh)) g.FillPath(track, tp);
                int fw = (int)(bw * _progress);
                if (fw > bh)
                {
                    using var fill = new SolidBrush(Theme.Accent);
                    using var fp = RoundRect(new Rectangle(bx, by, fw, bh), bh);
                    g.FillPath(fill, fp);
                }
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes; var i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return $"{v:0.#} {units[i]}";
        }
    }
}
