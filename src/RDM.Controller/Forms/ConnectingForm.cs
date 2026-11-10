using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RDM.Controller.Theming;

namespace RDM.Controller.Forms;

/// <summary>
/// Cửa sổ nhỏ "đang kết nối" có vòng xoay, hiện trong lúc thiết lập phiên để người dùng biết đang chạy.
/// Modeless và không chiếm focus (WS_EX_NOACTIVATE) nên không cản hộp thoại mật khẩu/fingerprint.
/// Dùng chung cho mọi điểm kết nối qua ConnectFlow.ConnectAsync.
/// </summary>
public sealed class ConnectingForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private readonly Timer _timer = new Timer { Interval = 70 };
    private readonly Label _label;
    private float _angle;

    private ConnectingForm(string text)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Size = new Size(Dpi.S(300), Dpi.S(92));

        _label = new Label
        {
            Text = text,
            AutoSize = false,
            Font = Theme.Font(10.5f),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
            Bounds = new Rectangle(Dpi.S(64), Dpi.S(10), Width - Dpi.S(76), Height - Dpi.S(20))
        };
        Controls.Add(_label);
        _timer.Tick += (s, e) => { _angle = (_angle + 30f) % 360f; Invalidate(new Rectangle(0, 0, Dpi.S(64), Height)); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= WsExNoActivate; return p; }
    }

    /// <summary>Đổi dòng chữ trạng thái (ví dụ chuyển sang bước bắt tay).</summary>
    public void SetText(string text) { if (!IsDisposed) _label.Text = text; }

    public static ConnectingForm Begin(IWin32Window owner, string text)
    {
        var f = new ConnectingForm(text);
        f.PlaceOver(owner);
        f._timer.Start();
        f.Show(owner);
        return f;
    }

    private void PlaceOver(IWin32Window owner)
    {
        var area = owner is Control c && c.FindForm() is Form host && host.Visible
            ? host.RectangleToScreen(host.ClientRectangle)
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var cx = Dpi.S(32);
        var cy = Height / 2f;
        var outer = Dpi.S(14);
        var inner = Dpi.S(8);
        for (var i = 0; i < 12; i++)
        {
            var a = (_angle + i * 30f) * Math.PI / 180.0;
            var alpha = (int)(40 + 215 * (i / 11.0));
            using var pen = new Pen(Color.FromArgb(alpha, Theme.Accent), Dpi.S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var cos = (float)Math.Cos(a);
            var sin = (float)Math.Sin(a);
            g.DrawLine(pen, cx + cos * inner, cy + sin * inner, cx + cos * outer, cy + sin * outer);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
