using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        _input.Font = Theme.Font(10f);
        _input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Enter(); } };

        var send = new Button { Text = "Send", Dock = DockStyle.Right, Width = Dpi.S(64), FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White };
        send.FlatAppearance.BorderSize = 0;
        send.Click += (s, e) => Enter();
        var file = new Button { Dock = DockStyle.Right, Width = Dpi.S(40), Text = "\U0001F4CE", FlatStyle = FlatStyle.Flat, BackColor = Theme.SurfaceAlt, ForeColor = Theme.Text, Font = Theme.Font(11f) };
        file.FlatAppearance.BorderColor = Theme.Border;
        file.Click += (s, e) => SendFileRequested?.Invoke();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(40), BackColor = Theme.Surface, Padding = new Padding(Dpi.S(6)) };
        bottom.Controls.Add(_input);
        bottom.Controls.Add(send);
        bottom.Controls.Add(file);

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
        var bubble = new Bubble((mine ? "You" : PeerName) + ":  " + text, mine);
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
            else if (c is FileEntry f) f.Width = Math.Max(Dpi.S(160), _list.ClientSize.Width - Dpi.S(28));
        }
    }

    /// <summary>Hiển thị một lượt truyền file ngay trong dòng chat: icon file, tên và thanh phần trăm.</summary>
    public void UpdateTransfer(FileTransferProgress p)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => UpdateTransfer(p))); return; }
        if (!_fileEntries.TryGetValue(p.Id, out var entry))
        {
            entry = new FileEntry(p.Id, p.Incoming, PeerName,
                id => PauseRequested?.Invoke(id), id => ResumeRequested?.Invoke(id), id => CancelRequested?.Invoke(id))
            { Width = Math.Max(Dpi.S(160), _list.ClientSize.Width - Dpi.S(28)) };
            entry.Margin = new Padding(Dpi.S(4), Dpi.S(3), 0, Dpi.S(3));
            _fileEntries[p.Id] = entry;
            _list.Controls.Add(entry);
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

    /// <summary>Thẻ một file trong dòng chat: icon, tên + trạng thái, thanh phần trăm và nút Pause/Cancel.</summary>
    private sealed class FileEntry : Panel
    {
        private readonly int _id;
        private readonly bool _incoming;
        private readonly string _peer;
        private readonly Label _text;
        private readonly ProgressBar _bar;
        private readonly Button _pause, _cancel;
        private readonly Action<int> _onResume, _onPause;
        private bool _paused;
        private bool _done;

        public FileEntry(int id, bool incoming, string peer, Action<int> onPause, Action<int> onResume, Action<int> onCancel)
        {
            _id = id;
            _incoming = incoming;
            _peer = peer;
            _onPause = onPause;
            _onResume = onResume;
            Height = Dpi.S(56);
            BackColor = Theme.SurfaceAlt;
            Padding = new Padding(Dpi.S(6));
            var icon = new PictureBox { Dock = DockStyle.Left, Width = Dpi.S(30), Image = FileGlyph, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.Transparent };

            var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = Dpi.S(78), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            _pause = SmallButton("❚❚"); // tạm ngưng
            _pause.Visible = !incoming; // chỉ tạm ngưng được lượt gửi đi
            _pause.Click += (s, e) =>
            {
                if (_done) return;
                if (_paused) { _paused = false; _onResume?.Invoke(_id); _pause.Text = "❚❚"; }
                else { _paused = true; _onPause?.Invoke(_id); _pause.Text = "▶"; }
            };
            _cancel = SmallButton("✕"); // hủy
            _cancel.Click += (s, e) => { if (!_done) onCancel?.Invoke(_id); };
            actions.Controls.Add(_pause);
            actions.Controls.Add(_cancel);

            _bar = new ProgressBar { Dock = DockStyle.Bottom, Height = Dpi.S(6), Maximum = 1000, Style = ProgressBarStyle.Continuous };
            _text = new Label { Dock = DockStyle.Fill, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Font = Theme.Font(9f) };
            Controls.Add(_text);
            Controls.Add(actions);
            Controls.Add(_bar);
            Controls.Add(icon);
            _bar.SendToBack();
            _text.BringToFront();
        }

        private static Button SmallButton(string text)
        {
            var b = new Button { Width = Dpi.S(34), Height = Dpi.S(26), Text = text, FlatStyle = FlatStyle.Flat, Font = Theme.Font(9f), BackColor = Theme.Surface, ForeColor = Theme.Text, Margin = new Padding(Dpi.S(2), Dpi.S(4), 0, 0) };
            b.FlatAppearance.BorderColor = Theme.Border;
            return b;
        }

        public void Update(FileTransferProgress p)
        {
            int permille = p.TotalBytes > 0 ? (int)Math.Min(1000, Math.Max(0, p.TransferredBytes * 1000 / p.TotalBytes)) : 0;
            if (p.Status == TransferStatus.Completed) permille = 1000;
            _bar.Value = Math.Min(_bar.Maximum, permille);
            int pct = p.TotalBytes > 0 ? (int)Math.Min(100, p.TransferredBytes * 100 / p.TotalBytes) : (p.Status == TransferStatus.Completed ? 100 : 0);
            string verb = _incoming ? "From " + _peer : "To " + _peer;
            string state;
            switch (p.Status)
            {
                case TransferStatus.Completed: state = _incoming ? "Received" : "Sent"; _done = true; break;
                case TransferStatus.Failed: state = "Failed"; _done = true; break;
                case TransferStatus.Canceled: state = "Canceled"; _done = true; break;
                case TransferStatus.Paused: state = "Paused"; break;
                default: state = pct + "%"; break;
            }
            if (_done) { _bar.Visible = false; _pause.Visible = false; _cancel.Visible = false; }
            _text.Text = $"{p.FileName}\n{verb}  -  {state}  ({FormatSize(p.TransferredBytes)} / {FormatSize(p.TotalBytes)})";
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
