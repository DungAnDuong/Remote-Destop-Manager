using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RDM.DesktopAgent;

/// <summary>
/// Chụp "input desktop" đang hoạt động — kể cả secure desktop của UAC và màn hình khóa (Winlogon).
/// Chỉ chụp được secure desktop khi tiến trình chạy quyền SYSTEM; chạy quyền người dùng thường thì
/// SetThreadDesktop vào secure desktop sẽ bị từ chối (đúng theo cơ chế bảo mật của Windows).
/// Phải gọi trên một luồng CHƯA tạo cửa sổ nào (yêu cầu của SetThreadDesktop).
/// </summary>
public static class DesktopCapture
{
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79, SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Tên desktop đang nhập (ví dụ "Default", "Winlogon", "Screen-saver"), để phát hiện secure desktop.</summary>
    public static string InputDesktopName()
    {
        var hDesk = OpenInputDesktop(0, false, MAXIMUM_ALLOWED);
        if (hDesk == IntPtr.Zero) return null;
        try { return DesktopName(hDesk); }
        finally { CloseDesktop(hDesk); }
    }

    /// <summary>Chụp toàn bộ màn hình ảo của input desktop hiện tại. Ném Win32Exception nếu không đủ quyền.</summary>
    public static Bitmap CaptureInputDesktop(out string desktopName)
    {
        string name = null;
        Bitmap bmp = null;
        RunOnInputDesktop(n => { name = n; bmp = CaptureVirtualScreen(); });
        desktopName = name;
        return bmp;
    }

    /// <summary>
    /// Gắn luồng hiện tại vào input desktop đang hoạt động (kể cả Winlogon/secure desktop khi chạy SYSTEM),
    /// chạy action rồi trả luồng về desktop cũ. Phải gọi trên luồng CHƯA tạo cửa sổ nào (yêu cầu của SetThreadDesktop).
    /// Dùng cho cả chụp màn hình và tiêm chuột/phím vào đúng desktop đang nhập.
    /// </summary>
    public static void RunOnInputDesktop(Action<string> action)
    {
        var hDesk = OpenInputDesktop(0, true, MAXIMUM_ALLOWED);
        if (hDesk == IntPtr.Zero) hDesk = OpenDesktop("Default", 0, true, MAXIMUM_ALLOWED);
        if (hDesk == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenInputDesktop failed");
        var name = DesktopName(hDesk);
        var previous = GetThreadDesktop(GetCurrentThreadId());
        var switched = false;
        try
        {
            if (!SetThreadDesktop(hDesk))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadDesktop failed (the secure desktop requires running as SYSTEM)");
            switched = true;
            action(name);
        }
        finally
        {
            if (switched && previous != IntPtr.Zero) SetThreadDesktop(previous);
            CloseDesktop(hDesk);
        }
    }

    private static Bitmap CaptureVirtualScreen()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) { x = 0; y = 0; w = GetSystemMetrics(SM_CXSCREEN); h = GetSystemMetrics(SM_CYSCREEN); }
        if (w <= 0 || h <= 0) { w = 1920; h = 1080; }
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, byte[] info, int length, out int needed);

    private static string DesktopName(IntPtr hDesk)
    {
        const int UOI_NAME = 2;
        var buffer = new byte[256];
        return GetUserObjectInformation(hDesk, UOI_NAME, buffer, buffer.Length, out var needed)
            ? System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2))
            : null;
    }
}
