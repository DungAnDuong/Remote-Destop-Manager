using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using RDM.Controller.Theming;

namespace RDM.Controller.Controls;

public sealed class ChartPoint
{
    public string Label { get; set; }
    public double Value { get; set; }
    public string Tooltip { get; set; }
}

/// <summary>
/// Biểu đồ cột một chuỗi: cột mảnh, đầu cột bo tròn neo vào đường đáy, lưới mảnh, tooltip khi di chuột.
/// Tiêu đề thẻ gọi tên chuỗi nên không cần chú giải. Số liệu đầy đủ xem ở trang Connection History.
/// </summary>
public class BarChart : Control
{
    private List<ChartPoint> _points = new List<ChartPoint>();
    private int _hover = -1;
    // Giữ một chữ số thập phân: trục có vạch giữa (ví dụ 2.5), làm tròn sẽ hiển thị sai giá trị.
    public Func<double, string> AxisFormat { get; set; } = v => v.ToString("0.#", CultureInfo.InvariantCulture);
    public string EmptyText { get; set; } = "No data yet";

    public BarChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        BackColor = Theme.Surface;
    }

    public void SetData(IEnumerable<ChartPoint> points)
    {
        _points = points.ToList();
        _hover = -1;
        Invalidate();
    }

    private Rectangle Plot() => new Rectangle(Dpi.S(34), Dpi.S(6), Math.Max(1, Width - Dpi.S(40)), Math.Max(1, Height - Dpi.S(28)));

    /// <summary>Làm tròn trục lên số đẹp: 1, 2, 5 nhân lũy thừa của 10.</summary>
    public static double NiceMax(double max)
    {
        if (max <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 2, 5, 10 }) if (m * mag >= max) return m * mag;
        return 10 * mag;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = Plot();
        var index = _points.Count == 0 || !plot.Contains(e.Location) ? -1 : Math.Min(_points.Count - 1, (e.X - plot.Left) * _points.Count / plot.Width);
        if (index != _hover) { _hover = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        var plot = Plot();
        var axisFont = Theme.Font(8f);
        var hasData = _points.Any(p => p.Value > 0);
        var niceMax = NiceMax(_points.Count == 0 ? 0 : _points.Max(p => p.Value));

        using (var grid = new Pen(Theme.ChartGrid))
            for (var i = 1; i <= 2; i++)
            {
                var y = plot.Bottom - plot.Height * i / 2;
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                // Chưa có dữ liệu thì không có thang đo thật, nên không in số lên trục.
                if (hasData) TextRenderer.DrawText(g, AxisFormat(niceMax * i / 2), axisFont, new Rectangle(0, y - Dpi.S(8), plot.Left - Dpi.S(4), Dpi.S(16)), Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        using (var baseline = new Pen(Theme.ChartBaseline)) g.DrawLine(baseline, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        if (_points.Count == 0) return;

        var slot = (float)plot.Width / _points.Count;
        var barWidth = Math.Max(Dpi.S(3), Math.Min(Dpi.S(18), (int)slot - Dpi.S(2)));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(Theme.ChartSeries))
            for (var i = 0; i < _points.Count; i++)
            {
                var v = _points[i].Value;
                if (v <= 0) continue;
                var h = Math.Max(Dpi.S(2), (int)(plot.Height * v / niceMax));
                var x = (int)(plot.Left + slot * i + (slot - barWidth) / 2);
                using var path = TopRounded(new Rectangle(x, plot.Bottom - h, barWidth, h), Dpi.S(3));
                g.FillPath(brush, path);
            }
        g.SmoothingMode = SmoothingMode.None;

        // Nhãn trục X chọn lọc để không chồng lấn; luôn có điểm đầu và điểm cuối.
        var labelWidth = Dpi.S(44);
        var step = Math.Max(1, (int)Math.Ceiling(labelWidth / slot));
        for (var i = 0; i < _points.Count; i += step) DrawXLabel(g, i, slot, plot, axisFont, labelWidth);
        if ((_points.Count - 1) % step > step / 2) DrawXLabel(g, _points.Count - 1, slot, plot, axisFont, labelWidth);

        if (!hasData)
            TextRenderer.DrawText(g, EmptyText, Theme.Font(9f), plot, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        if (_hover >= 0 && _hover < _points.Count) DrawTooltip(g, plot, slot);
    }

    private void DrawXLabel(Graphics g, int i, float slot, Rectangle plot, Font font, int labelWidth)
    {
        var cx = (int)(plot.Left + slot * i + slot / 2);
        var x = Math.Max(0, Math.Min(Width - labelWidth, cx - labelWidth / 2));
        TextRenderer.DrawText(g, _points[i].Label, font, new Rectangle(x, plot.Bottom + Dpi.S(3), labelWidth, Dpi.S(16)), Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawTooltip(Graphics g, Rectangle plot, float slot)
    {
        var p = _points[_hover];
        var text = p.Tooltip ?? $"{p.Label}: {AxisFormat(p.Value)}";
        var font = Theme.Font(8.5f);
        var size = TextRenderer.MeasureText(text, font);
        var box = new Rectangle(0, plot.Top, size.Width + Dpi.S(12), size.Height + Dpi.S(8));
        var cx = (int)(plot.Left + slot * _hover + slot / 2);
        box.X = Math.Max(0, Math.Min(Width - box.Width - 1, cx - box.Width / 2));
        using (var guide = new Pen(Theme.ChartBaseline)) g.DrawLine(guide, cx, plot.Top, cx, plot.Bottom);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(box, Dpi.S(4)))
        using (var brush = new SolidBrush(Theme.SurfaceAlt))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, text, font, box, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static GraphicsPath TopRounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Thanh tỷ lệ Online/Offline, thay cho biểu đồ tròn 2 phần. Màu trạng thái luôn đi kèm nhãn và số.
/// Khi chưa kết nối được Relay, hiển thị đúng là "chưa rõ", không sinh số.
/// </summary>
public class PresenceBar : Control
{
    public int? Online { get; private set; }
    public int? Offline { get; private set; }
    public int Total { get; private set; }

    public PresenceBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        BackColor = Theme.Surface;
    }

    public void SetData(int? online, int? offline, int total)
    {
        Online = online; Offline = offline; Total = total;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        var bar = new Rectangle(0, Dpi.S(4), Width - 1, Dpi.S(18));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var known = Online.HasValue && Offline.HasValue && Online + Offline > 0;
        using (var path = Theme.Rounded(bar, Dpi.S(4)))
        using (var neutral = new SolidBrush(Theme.ChartNeutral))
        {
            g.FillPath(neutral, path);
            if (known && Online > 0)
            {
                var clip = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                using var good = new SolidBrush(Theme.Good);
                var w = (int)(bar.Width * (double)Online.Value / (Online.Value + Offline.Value));
                g.FillRectangle(good, bar.X, bar.Y, w, bar.Height);
                using var gap = new SolidBrush(Theme.Surface);
                g.FillRectangle(gap, bar.X + w, bar.Y, Dpi.S(2), bar.Height);
                g.Clip = clip;
            }
        }
        g.SmoothingMode = SmoothingMode.None;

        var flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
        var y = bar.Bottom + Dpi.S(8);
        if (known)
        {
            Legend(g, Theme.Good, $"Online: {Online}", y);
            Legend(g, Theme.ChartNeutral, $"Offline: {Offline}", y + Dpi.S(20));
        }
        else
        {
            TextRenderer.DrawText(g, RDM.Shared.Common.ProductMode.Managed ? $"Status unknown for {Total} devices." : $"Status unknown for {Total} devices.\nConnect to a Relay Server for live data (Settings).", Theme.Font(9f),
                new Rectangle(0, y, Width, Height - y), Theme.TextSecondary, flags);
        }
    }

    /// <summary>Chấm màu mang danh tính; chữ dùng màu chữ thường, không dùng màu của chuỗi.</summary>
    private void Legend(Graphics g, Color color, string text, int y)
    {
        var size = Dpi.S(10);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 0, y + Dpi.S(4), size, size);
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, text, Theme.Font(9f), new Rectangle(size + Dpi.S(6), y, Width - size, Dpi.S(20)), Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}
