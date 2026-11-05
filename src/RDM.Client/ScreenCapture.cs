using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RDM.Shared.Protocol;

namespace RDM.Client;

/// <summary>Khung hình thô BGRA 32 bit, từ trên xuống.</summary>
public sealed class RawFrame
{
    public int Width, Height, Stride;
    public byte[] Pixels;
}

public interface IScreenSource : IDisposable
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    /// <summary>Trả về null nếu không chụp được lúc này (ví dụ UAC hoặc màn hình khóa); có thể thử lại sau.</summary>
    RawFrame Capture(int monitor);
}

/// <summary>
/// Chụp màn hình bằng GDI/BitBlt để tương thích Windows 7 SP1 trở lên (Phần 5.1).
/// Chạy trong phiên người dùng, không chạy trong Windows Service (Session 0).
/// Không chụp được Secure Desktop (UAC, màn hình đăng nhập): Capture trả về null và phiên báo rõ tình trạng.
/// </summary>
public sealed class GdiScreenCapture : IScreenSource
{
    private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, DIB_RGB_COLORS = 0, BI_RGB = 0, CURSOR_SHOWING = 1, DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX { public int Size; public RECT Monitor, Work; public int Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int Size, Flags; public IntPtr Cursor; public POINT Position; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool IsIcon; public int HotX, HotY; public IntPtr Mask, Color; }
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr wnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr wnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, int usage, out IntPtr bits, IntPtr section, int offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    private IntPtr _memDc, _dib, _oldBitmap, _bits;
    private int _width, _height;
    private byte[] _buffer;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc proc = (IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (GetMonitorInfo(monitor, ref info))
                list.Add(new MonitorInfo { X = info.Monitor.Left, Y = info.Monitor.Top, Width = info.Monitor.Right - info.Monitor.Left, Height = info.Monitor.Bottom - info.Monitor.Top, IsPrimary = (info.Flags & 1) != 0 });
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        // Monitor chính luôn là Monitor 1.
        var ordered = list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.X).ThenBy(m => m.Y).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Index = i;
        return ordered;
    }

    public RawFrame Capture(int monitor)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;
        var m = monitors[Math.Max(0, Math.Min(monitor, monitors.Count - 1))];
        EnsureBuffers(m.Width, m.Height);
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            // CAPTUREBLT để có cả cửa sổ layered.
            if (!BitBlt(_memDc, 0, 0, m.Width, m.Height, screen, m.X, m.Y, SRCCOPY | CAPTUREBLT)) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        DrawCursor(m);
        Marshal.Copy(_bits, _buffer, 0, _buffer.Length);
        return new RawFrame { Width = m.Width, Height = m.Height, Stride = m.Width * 4, Pixels = _buffer };
    }

    private void DrawCursor(MonitorInfo m)
    {
        var info = new CURSORINFO { Size = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (!GetCursorInfo(ref info) || info.Flags != CURSOR_SHOWING) return;
        if (!GetIconInfo(info.Cursor, out var icon)) return;
        try { DrawIconEx(_memDc, info.Position.X - icon.HotX - m.X, info.Position.Y - icon.HotY - m.Y, info.Cursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
        finally
        {
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
        }
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_dib != IntPtr.Zero && width == _width && height == _height) return;
        Release();
        var screen = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER { Size = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = BI_RGB };
            _dib = CreateDIBSection(screen, ref header, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) throw new InvalidOperationException("Could not create the screen capture buffer.");
            _oldBitmap = SelectObject(_memDc, _dib);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        _width = width; _height = height;
        _buffer = new byte[width * height * 4];
    }

    private void Release()
    {
        if (_memDc != IntPtr.Zero) { SelectObject(_memDc, _oldBitmap); DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
    }

    public void Dispose() => Release();
}

/// <summary>
/// So sánh khung hình theo khối 64x64 và nén vùng thay đổi (Phần 5.5). Không đổi thì không gửi gì.
/// Tách khỏi việc chụp để kiểm thử được bằng dữ liệu tự tạo.
/// </summary>
public sealed class FrameEncoder
{
    public const int TileSize = 64;
    /// <summary>Quá nhiều mảnh thì gửi một ảnh lớn, vừa nhỏ hơn vừa ít đường viền JPEG giữa các khối.</summary>
    private const double FullFrameRatio = 0.6;

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int memcmp(IntPtr a, IntPtr b, UIntPtr count);

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private byte[] _previous;
    private int _width, _height;

    /// <summary>Buộc lần kế tiếp gửi toàn màn hình: khi bắt đầu phiên, đổi monitor, đổi tỷ lệ.</summary>
    public void Reset() => _previous = null;

    /// <summary>Tìm các hình chữ nhật thay đổi; gộp khối liền nhau trên cùng một hàng. Khung đầu tiên trả về toàn màn hình.</summary>
    public List<Rectangle> FindChanges(RawFrame f)
    {
        var result = new List<Rectangle>();
        if (_previous == null || _width != f.Width || _height != f.Height)
        {
            _previous = (byte[])f.Pixels.Clone();
            _width = f.Width; _height = f.Height;
            result.Add(new Rectangle(0, 0, f.Width, f.Height));
            return result;
        }

        var cols = (f.Width + TileSize - 1) / TileSize;
        var rows = (f.Height + TileSize - 1) / TileSize;
        var changedTiles = 0;
        var pinA = GCHandle.Alloc(f.Pixels, GCHandleType.Pinned);
        var pinB = GCHandle.Alloc(_previous, GCHandleType.Pinned);
        try
        {
            var a = pinA.AddrOfPinnedObject();
            var b = pinB.AddrOfPinnedObject();
            for (var row = 0; row < rows; row++)
            {
                var y = row * TileSize;
                var h = Math.Min(TileSize, f.Height - y);
                var runStart = -1;
                for (var col = 0; col <= cols; col++)
                {
                    var changed = false;
                    if (col < cols)
                    {
                        var x = col * TileSize;
                        var bytes = (UIntPtr)(Math.Min(TileSize, f.Width - x) * 4);
                        for (var line = 0; line < h && !changed; line++)
                        {
                            var offset = (long)(y + line) * f.Stride + x * 4;
                            changed = memcmp(new IntPtr(a.ToInt64() + offset), new IntPtr(b.ToInt64() + offset), bytes) != 0;
                        }
                    }
                    if (changed) { changedTiles++; if (runStart < 0) runStart = col; }
                    else if (runStart >= 0)
                    {
                        var startX = runStart * TileSize;
                        result.Add(new Rectangle(startX, y, Math.Min(col * TileSize, f.Width) - startX, h));
                        runStart = -1;
                    }
                }
            }
        }
        finally { pinA.Free(); pinB.Free(); }

        if (result.Count == 0) return result;
        Buffer.BlockCopy(f.Pixels, 0, _previous, 0, f.Pixels.Length);
        if (changedTiles > cols * rows * FullFrameRatio)
        {
            result.Clear();
            result.Add(new Rectangle(0, 0, f.Width, f.Height));
        }
        return result;
    }

    /// <summary>Nén một vùng. Chất lượng 100 dùng PNG (không mất dữ liệu), còn lại dùng JPEG. Tỷ lệ dưới 100 thu nhỏ để tiết kiệm băng thông.</summary>
    public static byte[] Encode(RawFrame f, Rectangle r, int quality, int scalePercent)
    {
        var pin = GCHandle.Alloc(f.Pixels, GCHandleType.Pinned);
        try
        {
            var start = new IntPtr(pin.AddrOfPinnedObject().ToInt64() + (long)r.Y * f.Stride + r.X * 4);
            using var source = new Bitmap(r.Width, r.Height, f.Stride, PixelFormat.Format32bppRgb, start);
            using var ms = new MemoryStream();
            if (scalePercent >= 100) Save(source, ms, quality);
            else
            {
                var w = Math.Max(1, r.Width * scalePercent / 100);
                var h = Math.Max(1, r.Height * scalePercent / 100);
                using var scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(source, 0, 0, w, h);
                }
                Save(scaled, ms, quality);
            }
            return ms.ToArray();
        }
        finally { pin.Free(); }
    }

    private static void Save(Image image, Stream stream, int quality)
    {
        if (quality >= 100) { image.Save(stream, ImageFormat.Png); return; }
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Max(10, quality));
        image.Save(stream, JpegCodec, parameters);
    }
}
