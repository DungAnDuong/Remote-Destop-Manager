using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace RDM.Controller.Theming;

/// <summary>Quy đổi kích thước theo DPI hệ thống (lấy một lần khi khởi động).</summary>
public static class Dpi
{
    private static float _scale = 1f;

    public static void Init()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        _scale = g.DpiX / 96f;
    }

    public static int S(int value) => (int)Math.Round(value * _scale);
}

public enum ThemeMode { Light, Dark }

/// <summary>
/// Toàn bộ màu của ứng dụng nằm ở đây. Sidebar xanh đậm ở cả hai chế độ.
/// Màu biểu đồ được chọn riêng cho từng chế độ và đã kiểm tra độ tương phản (xem docs/06-GiaoDien.md).
/// </summary>
public static class Theme
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.Light;
    public static event Action Changed;

    public static void Set(ThemeMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Changed?.Invoke();
    }

    private static bool D => Mode == ThemeMode.Dark;
    private static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public static Color Sidebar => C("#0F2A4A");
    public static Color SidebarHover => C("#1A3B63");
    public static Color SidebarActive => C("#24507F");
    public static Color SidebarText => C("#E6EDF5");
    public static Color SidebarMuted => C("#9DB2C9");

    public static Color Page => D ? C("#101826") : C("#F3F5F8");
    public static Color Surface => D ? C("#1A2434") : C("#FFFFFF");
    public static Color SurfaceAlt => D ? C("#212D40") : C("#F7F8FA");
    public static Color Border => D ? C("#2F3D52") : C("#DDE2E8");
    public static Color Text => D ? C("#F0F2F5") : C("#14202E");
    public static Color TextSecondary => D ? C("#C3CBD6") : C("#4C5866");
    public static Color TextMuted => D ? C("#8D99A8") : C("#6B7684");
    public static Color Accent => D ? C("#3987E5") : C("#2A78D6");
    public static Color AccentHover => D ? C("#5598E7") : C("#256ABF");
    public static Color Selection => D ? C("#24507F") : C("#CDE2FB");
    public static Color SelectionText => D ? C("#FFFFFF") : C("#14202E");
    public static Color Disabled => D ? C("#2A3546") : C("#E4E7EB");
    public static Color DisabledText => D ? C("#7A8696") : C("#7D8792");

    // Màu trạng thái: chỉ dùng khi màu mang nghĩa tốt/xấu, luôn đi kèm icon và nhãn.
    public static Color Good => C("#0CA30C");
    public static Color Warning => C("#FAB219");
    public static Color Critical => C("#D03B3B");
    public static Color CriticalHover => C("#B73030");

    public static Color ChartSeries => Accent;
    public static Color ChartGrid => D ? C("#2C3849") : C("#E8EAEE");
    public static Color ChartBaseline => D ? C("#414F63") : C("#C3C9D1");
    public static Color ChartNeutral => D ? C("#55637A") : C("#A9B2BE");

    private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

    public static Font Font(float size = 9.5f, bool bold = false) => Cached("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);

    /// <summary>Segoe UI Symbol có từ Windows 7; Segoe MDL2 Assets chỉ có từ Windows 10 nên không dùng.</summary>
    public static Font Icon(float size = 12f) => Cached("Segoe UI Symbol", size, FontStyle.Regular);

    private static Font Cached(string family, float size, FontStyle style)
    {
        var key = $"{family}|{size}|{style}";
        if (!Fonts.TryGetValue(key, out var font)) Fonts[key] = font = new Font(family, size, style);
        return font;
    }

    private static System.Drawing.Icon _appIcon;

    /// <summary>Icon RDM nhúng trong chương trình. Mỗi cửa sổ WinForms phải tự đặt, nếu không sẽ hiện icon mặc định của .NET.</summary>
    public static System.Drawing.Icon AppIcon
    {
        get
        {
            if (_appIcon != null) return _appIcon;
            using var stream = typeof(Theme).Assembly.GetManifestResourceStream("RDM.ico");
            if (stream != null) _appIcon = new System.Drawing.Icon(stream);
            return _appIcon;
        }
    }

    /// <summary>Màu nền thật phía sau một control: bỏ qua các control cha có nền Transparent (Clear bằng Transparent sẽ ra màu đen).</summary>
    public static Color BackgroundBehind(System.Windows.Forms.Control control)
    {
        for (var p = control.Parent; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Page;
    }

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
