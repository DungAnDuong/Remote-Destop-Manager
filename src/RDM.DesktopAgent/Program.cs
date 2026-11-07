using System;
using System.Drawing.Imaging;
using System.Threading;

namespace RDM.DesktopAgent;

/// <summary>
/// Agent chụp màn hình input desktop. Dự kiến được dịch vụ SYSTEM khởi chạy trong phiên đang hoạt động.
/// Bước 1 chỉ có chế độ tự kiểm (--selftest) để xác minh chụp được secure desktop khi chạy quyền SYSTEM.
/// Các chế độ phục vụ khung hình qua named pipe và nhận input sẽ thêm ở các bước sau.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--desktop-name")
        {
            Console.WriteLine(DesktopCapture.InputDesktopName() ?? "(unknown)");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--probe-loop")
        {
            // Lặp chụp input desktop và ghi log (KHÔNG lưu ảnh), để kiểm chứng chụp được secure desktop khi chạy SYSTEM.
            var log = args[1];
            var seconds = args.Length >= 3 && int.TryParse(args[2], out var s) ? Math.Max(1, s) : 2;
            ProbeLoop(log, seconds);
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            try
            {
                // Chạy trên một luồng riêng chưa tạo cửa sổ nào (điều kiện của SetThreadDesktop).
                int code = 0;
                string message = null;
                var t = new Thread(() =>
                {
                    try
                    {
                        using var bmp = DesktopCapture.CaptureInputDesktop(out var name);
                        bmp.Save(args[1], ImageFormat.Png);
                        message = $"Captured input desktop '{name}' {bmp.Width}x{bmp.Height} -> {args[1]}";
                    }
                    catch (Exception ex) { code = 1; message = "Capture failed: " + ex.Message; }
                });
                t.IsBackground = false;
                t.Start();
                t.Join();
                if (code == 0) Console.WriteLine(message); else Console.Error.WriteLine(message);
                return code;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        Console.Error.WriteLine("RDM.DesktopAgent");
        Console.Error.WriteLine("  --selftest <out.png>          Capture the current input desktop (run as SYSTEM to capture UAC/lock screen).");
        Console.Error.WriteLine("  --desktop-name                Print the name of the current input desktop.");
        Console.Error.WriteLine("  --probe-loop <log> [seconds]  Log capture results of the input desktop over time (no image saved).");
        return 64;
    }

    private static void ProbeLoop(string logFile, int seconds)
    {
        var done = new System.Threading.ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            while (!done.IsSet)
            {
                string line;
                try
                {
                    using var bmp = DesktopCapture.CaptureInputDesktop(out var name);
                    line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  desktop={name}  result=OK {bmp.Width}x{bmp.Height}  user={Environment.UserName}";
                }
                catch (Exception ex)
                {
                    line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  desktop={DesktopCapture.InputDesktopName()}  result=FAILED  {ex.Message}  user={Environment.UserName}";
                }
                try { System.IO.File.AppendAllText(logFile, line + Environment.NewLine); } catch (Exception) { }
                done.Wait(TimeSpan.FromSeconds(seconds));
            }
        }) { IsBackground = false };
        worker.Start();
        worker.Join();
    }
}
