using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RDM.Controller.Theming;

namespace RDM.Controller.Controls;

public enum ButtonKind { Primary, Secondary, Danger, Ghost }

/// <summary>Nút bo góc có hiệu ứng hover. Kế thừa Button nên vẫn dùng được bàn phím, Enter và AcceptButton.</summary>
public class ModernButton : Button
{
    private bool _hover, _pressed;
    public ButtonKind Kind { get; set; }

    public ModernButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        Text = text;
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Font(9.5f, kind == ButtonKind.Primary);
        Height = Dpi.S(34);
        Width = Math.Max(Dpi.S(84), TextRenderer.MeasureText(text, Font).Width + Dpi.S(28));
        Margin = new Padding(0, 0, Dpi.S(8), Dpi.S(8));
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackgroundBehind(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color back, fore, border;
        var active = _hover || _pressed;
        if (!Enabled) { back = Theme.Disabled; fore = Theme.DisabledText; border = back; }
        else switch (Kind)
        {
            case ButtonKind.Primary: back = active ? Theme.AccentHover : Theme.Accent; fore = Color.White; border = back; break;
            case ButtonKind.Danger: back = active ? Theme.CriticalHover : Theme.Critical; fore = Color.White; border = back; break;
            case ButtonKind.Ghost: back = active ? Theme.SurfaceAlt : Theme.BackgroundBehind(this); fore = Theme.Text; border = back; break;
            default: back = active ? Theme.SurfaceAlt : Theme.Surface; fore = Theme.Text; border = active ? Theme.Accent : Theme.Border; break;
        }
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Rounded(rect, Dpi.S(6)))
        using (var brush = new SolidBrush(back))
        using (var pen = new Pen(Focused && Enabled ? Theme.AccentHover : border, Focused && Enabled ? 2 : 1))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Thẻ nền bo góc, có tiêu đề tùy chọn.</summary>
public class Card : Panel
{
    public string Title { get; set; }

    public Card(string title = null)
    {
        Title = title;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Margin = new Padding(Dpi.S(6));
        var top = title == null ? 14 : 44;
        Padding = new Padding(Dpi.S(14), Dpi.S(top), Dpi.S(14), Dpi.S(12));
        Dock = DockStyle.Fill;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackgroundBehind(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Dpi.S(8)))
        using (var brush = new SolidBrush(Theme.Surface))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        if (Title != null)
            TextRenderer.DrawText(g, Title, Theme.Font(10.5f, true), new Rectangle(Dpi.S(14), Dpi.S(12), Width - Dpi.S(28), Dpi.S(24)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Nút điều hướng ở Sidebar.</summary>
public class SidebarButton : Button
{
    private bool _hover;
    public string Glyph { get; }
    public bool Selected { get; private set; }
    public void Select(bool selected) { Selected = selected; Invalidate(); }

    public SidebarButton(string glyph, string text)
    {
        Glyph = glyph;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = Dpi.S(38);
        Dock = DockStyle.Top;
        Font = Theme.Font(9.5f);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Selected ? Theme.SidebarActive : _hover && Enabled ? Theme.SidebarHover : Theme.Sidebar;
        g.Clear(back);
        if (Selected) using (var accent = new SolidBrush(Theme.Accent)) g.FillRectangle(accent, 0, 0, Dpi.S(3), Height);
        var fore = !Enabled ? Theme.SidebarMuted : Selected ? Color.White : Theme.SidebarText;
        TextRenderer.DrawText(g, Glyph, Theme.Icon(11f), new Rectangle(Dpi.S(12), 0, Dpi.S(26), Height), fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Text, Selected ? Theme.Font(9.5f, true) : Font, new Rectangle(Dpi.S(44), 0, Width - Dpi.S(48), Height), fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(2, 2, Width - 4, Height - 4), fore, back);
    }
}

/// <summary>Ô nhập liệu bo góc, không dùng viền mặc định của WinForms.</summary>
public class ModernInput : UserControl
{
    private const int EM_SETCUEBANNER = 0x1501;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public TextBox Box { get; }
    private string _placeholder;

    public ModernInput(string placeholder = null, bool password = false, bool multiline = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        _placeholder = placeholder;
        Height = multiline ? Dpi.S(70) : Dpi.S(34);
        Width = Dpi.S(220);
        Margin = new Padding(0, 0, Dpi.S(8), Dpi.S(8));
        Padding = new Padding(Dpi.S(10), multiline ? Dpi.S(6) : Dpi.S(8), Dpi.S(8), Dpi.S(4));
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = Theme.Font(9.5f), BackColor = Theme.Surface, ForeColor = Theme.Text,
            UseSystemPasswordChar = password, Multiline = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
        };
        Box.GotFocus += (s, e) => Invalidate();
        Box.LostFocus += (s, e) => Invalidate();
        Box.HandleCreated += (s, e) => ApplyPlaceholder();
        Controls.Add(Box);
    }

    private void ApplyPlaceholder()
    {
        if (!string.IsNullOrEmpty(_placeholder)) SendMessage(Box.Handle, EM_SETCUEBANNER, (IntPtr)1, _placeholder);
    }

    [Browsable(true)]
    public override string Text { get => Box.Text; set => Box.Text = value; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackgroundBehind(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Dpi.S(6));
        using var brush = new SolidBrush(Theme.Surface);
        using var pen = new Pen(Box.Focused ? Theme.Accent : Theme.Border, Box.Focused ? 2 : 1);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    protected override void OnEnabledChanged(EventArgs e) { Box.Enabled = Enabled; base.OnEnabledChanged(e); }
}

/// <summary>Ô thống kê: một con số lớn kèm nhãn. Dùng cỡ số tỷ lệ, không tô màu theo series.</summary>
public class StatCard : Card
{
    public string Label { get; set; }
    public string Value { get; set; }
    public string Hint { get; set; }

    public StatCard(string label, string value = "–", string hint = null)
    {
        Label = label; Value = value; Hint = hint;
        Padding = new Padding(Dpi.S(14));
    }

    public void SetValue(string value, string hint = null) { Value = value; if (hint != null) Hint = hint; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var x = Dpi.S(14);
        var w = Width - x * 2;
        TextRenderer.DrawText(g, Label, Theme.Font(9f), new Rectangle(x, Dpi.S(10), w, Dpi.S(20)), Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Value, Theme.Font(20f, true), new Rectangle(x - Dpi.S(3), Dpi.S(28), w, Dpi.S(38)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrEmpty(Hint))
            TextRenderer.DrawText(g, Hint, Theme.Font(8f), new Rectangle(x, Dpi.S(66), w, Height - Dpi.S(68)), Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
