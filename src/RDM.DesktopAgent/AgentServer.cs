using System;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RDM.DesktopAgent;

/// <summary>
/// Máy chủ named pipe của agent: phục vụ khung hình của input desktop (kể cả secure desktop khi chạy SYSTEM)
/// và nhận chuột/phím để tiêm vào đúng desktop đang nhập. Máy điều khiển (RDM.exe trong phiên người dùng) là client.
/// Pipe do SYSTEM tạo nhưng cho phép người dùng tương tác kết nối. Mỗi kết nối xử lý trên một luồng chưa tạo cửa sổ
/// (điều kiện của SetThreadDesktop). Khung hình gửi dạng PNG để nhẹ đường truyền; đổi khung mới gửi lại.
/// </summary>
internal static class AgentServer
{
    public const string DefaultPipe = "RDM.DesktopAgent";
    private const byte OpGetFrame = 1, OpMouse = 2, OpKey = 3, OpText = 4, OpSpecial = 5, OpDesktopName = 6;

    public static int Serve(string pipeName)
    {
        pipeName = string.IsNullOrEmpty(pipeName) ? DefaultPipe : pipeName;
        StartSelfProbe();
        PipeSecurity security = null;
        try
        {
            security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch (Exception) { security = null; }

        while (true)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
            }
            catch (Exception) { return 1; }

            try { pipe.WaitForConnection(); }
            catch (Exception) { pipe.Dispose(); continue; }

            var worker = new Thread(() => Handle(pipe)) { IsBackground = false, Name = "RDM.DesktopAgent.Client" };
            worker.Start();
        }
    }

    /// <summary>
    /// Luồng tự kiểm chứng: mỗi 2s thử chụp input desktop và ghi tên desktop + OK/FAIL vào desktop-probe.log.
    /// KHÔNG lưu ảnh. Để xác minh agent (SYSTEM) chụp được secure desktop (Winlogon) trong lúc UAC/khóa máy.
    /// </summary>
    private static void StartSelfProbe()
    {
        var log = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RDM", "desktop-probe.log");
        var t = new Thread(() =>
        {
            while (true)
            {
                string line;
                try
                {
                    using var bmp = DesktopCapture.CaptureInputDesktop(out var name);
                    line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  desktop={name}  result=OK {bmp.Width}x{bmp.Height}  user={Environment.UserName}";
                }
                catch (Exception ex)
                {
                    string name = null;
                    try { name = DesktopCapture.InputDesktopName(); } catch (Exception) { }
                    line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  desktop={name}  result=FAILED  {ex.Message}  user={Environment.UserName}";
                }
                try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)); System.IO.File.AppendAllText(log, line + Environment.NewLine); }
                catch (Exception) { }
                Thread.Sleep(2000);
            }
        })
        { IsBackground = true, Name = "RDM.DesktopAgent.Probe" };
        t.Start();
    }

    private static void Handle(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            using (var r = new BinaryReader(pipe))
            using (var w = new BinaryWriter(pipe))
            {
                while (pipe.IsConnected)
                {
                    byte op;
                    try { op = r.ReadByte(); }
                    catch (Exception) { break; }

                    switch (op)
                    {
                        case OpGetFrame:
                            SendFrame(r, w);
                            break;
                        case OpMouse:
                        {
                            int px = r.ReadInt32(), py = r.ReadInt32();
                            byte action = r.ReadByte();
                            int wheel = r.ReadInt32();
                            try { AgentInject.Mouse(px, py, action, wheel); } catch (Exception) { }
                            w.Write((byte)1); w.Flush();
                            break;
                        }
                        case OpKey:
                        {
                            int vk = r.ReadInt32(), scan = r.ReadInt32();
                            bool ext = r.ReadBoolean(), down = r.ReadBoolean();
                            try { AgentInject.Key(vk, scan, ext, down); } catch (Exception) { }
                            w.Write((byte)1); w.Flush();
                            break;
                        }
                        case OpText:
                        {
                            var text = ReadString(r);
                            try { AgentInject.Text(text); } catch (Exception) { }
                            w.Write((byte)1); w.Flush();
                            break;
                        }
                        case OpSpecial:
                            r.ReadByte();
                            w.Write((byte)1); w.Flush();
                            break;
                        case OpDesktopName:
                            WriteString(w, SafeDesktopName());
                            w.Flush();
                            break;
                        default:
                            return;
                    }
                }
            }
        }
        catch (Exception) { }
    }

    private static void SendFrame(BinaryReader r, BinaryWriter w)
    {
        var clientSeq = r.ReadInt32();
        byte[] png = null; string name = ""; int width = 0, height = 0; int seq = clientSeq;
        try
        {
            using var bmp = DesktopCapture.CaptureInputDesktop(out name);
            width = bmp.Width; height = bmp.Height;
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
            seq = unchecked((int)Fnv(png));
        }
        catch (Exception) { png = null; }

        w.Write(seq);
        w.Write(width);
        w.Write(height);
        WriteString(w, name ?? "");
        if (png == null || seq == clientSeq) w.Write(0);
        else { w.Write(png.Length); w.Write(png); }
        w.Flush();
    }

    private static string SafeDesktopName()
    {
        try { return DesktopCapture.InputDesktopName() ?? ""; } catch (Exception) { return ""; }
    }

    private static uint Fnv(byte[] data)
    {
        uint h = 2166136261;
        // Lấy mẫu để nhanh với ảnh lớn; đủ để phát hiện khung đổi.
        var step = Math.Max(1, data.Length / 65536);
        for (var i = 0; i < data.Length; i += step) { h ^= data[i]; h *= 16777619; }
        h ^= (uint)data.Length;
        return h;
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static string ReadString(BinaryReader r)
    {
        var len = r.ReadInt32();
        if (len < 0 || len > 1 << 20) throw new InvalidDataException("string too long");
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    /// <summary>Client test an toàn riêng tư: kết nối pipe và chỉ hỏi TÊN input desktop (không chụp, không lưu ảnh).</summary>
    public static int Ping(string pipeName)
    {
        pipeName = string.IsNullOrEmpty(pipeName) ? DefaultPipe : pipeName;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            using var r = new BinaryReader(pipe);
            using var w = new BinaryWriter(pipe);
            w.Write(OpDesktopName); w.Flush();
            var name = ReadString(r);
            Console.WriteLine($"connected  input-desktop={name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ping failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Client test: kết nối pipe, lấy một khung và lưu PNG, in tên desktop + kích thước.</summary>
    public static int PullFrame(string pipeName, string outPng)
    {
        pipeName = string.IsNullOrEmpty(pipeName) ? DefaultPipe : pipeName;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            using var r = new BinaryReader(pipe);
            using var w = new BinaryWriter(pipe);
            w.Write(OpGetFrame); w.Write(0); w.Flush();
            var seq = r.ReadInt32();
            var width = r.ReadInt32();
            var height = r.ReadInt32();
            var name = ReadString(r);
            var len = r.ReadInt32();
            if (len <= 0) { Console.Error.WriteLine($"No frame (desktop={name})"); return 2; }
            var png = r.ReadBytes(len);
            File.WriteAllBytes(outPng, png);
            Console.WriteLine($"desktop={name}  {width}x{height}  {len} bytes  -> {outPng}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PullFrame failed: " + ex.Message);
            return 1;
        }
    }
}
