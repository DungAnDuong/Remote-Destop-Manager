using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Text;
using RDM.Shared.Protocol;

namespace RDM.Client;

/// <summary>
/// Kênh tới RDM.DesktopAgent (chạy quyền SYSTEM) qua named pipe. Dùng khi máy bị điều khiển đang ở secure desktop
/// (UAC/màn hình khóa) mà tiến trình trong phiên không chụp/điều khiển được: lấy khung và chuyển chuột/phím qua agent.
/// Mỗi lời gọi là một yêu cầu-đáp nối tiếp (có khóa). Tự coi như mất kết nối nếu pipe hỏng; nơi gọi sẽ bỏ qua agent.
/// </summary>
public sealed class AgentChannel : IDisposable
{
    public const string PipeName = "RDM.DesktopAgent";
    private const byte OpGetFrame = 1, OpMouse = 2, OpKey = 3, OpText = 4, OpSpecial = 5, OpDesktopName = 6;

    private readonly NamedPipeClientStream _pipe;
    private readonly BinaryReader _r;
    private readonly BinaryWriter _w;
    private readonly object _io = new object();
    private bool _broken;

    private AgentChannel(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _r = new BinaryReader(pipe);
        _w = new BinaryWriter(pipe);
    }

    /// <summary>Kết nối nếu agent đang chạy; trả null nếu không có agent (pipe không tồn tại) hoặc lỗi.</summary>
    public static AgentChannel TryConnect(int timeoutMs = 400)
    {
        try
        {
            // Pipe chưa tồn tại thì bỏ qua ngay, không chờ hết timeout (máy không cài dịch vụ vẫn mở phiên nhanh).
            if (!File.Exists(@"\\.\pipe\" + PipeName)) return null;
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            return new AgentChannel(pipe);
        }
        catch (Exception) { return null; }
    }

    public bool Ok => !_broken && _pipe.IsConnected;

    /// <summary>Lấy một khung của input desktop hiện tại (agent trả PNG của toàn màn hình ảo). Null nếu lỗi/không có khung.</summary>
    public RawFrame Capture()
    {
        lock (_io)
        {
            if (_broken) return null;
            try
            {
                _w.Write(OpGetFrame);
                _w.Write(0); // luôn xin khung mới; bên nhận tự so khung để lọc trùng
                _w.Flush();
                _r.ReadInt32();            // seq (bỏ qua)
                var width = _r.ReadInt32();
                var height = _r.ReadInt32();
                ReadString();              // tên desktop (bỏ qua)
                var len = _r.ReadInt32();
                if (len <= 0 || width <= 0 || height <= 0) return null;
                var png = _r.ReadBytes(len);
                if (png.Length != len) { _broken = true; return null; }
                return Decode(png);
            }
            catch (Exception) { _broken = true; return null; }
        }
    }

    public void Mouse(MouseInputData m) => Fire(() => { _w.Write(OpMouse); _w.Write(m.X); _w.Write(m.Y); _w.Write((byte)m.Action); _w.Write(m.WheelDelta); });
    public void Key(KeyInputData k) => Fire(() => { _w.Write(OpKey); _w.Write(k.VirtualKey); _w.Write(k.ScanCode); _w.Write(k.Extended); _w.Write(k.Down); });
    public void Text(string text) => Fire(() => { _w.Write(OpText); WriteString(text ?? string.Empty); });

    private void Fire(Action write)
    {
        lock (_io)
        {
            if (_broken) return;
            try
            {
                write();
                _w.Flush();
                _r.ReadByte(); // ack
            }
            catch (Exception) { _broken = true; }
        }
    }

    private static RawFrame Decode(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = bmp.Width * 4;
            var pixels = new byte[stride * bmp.Height];
            // Chép từng dòng để chuẩn hoá về top-down, stride = width*4 (bất kể stride nguồn).
            for (var y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            return new RawFrame { Width = bmp.Width, Height = bmp.Height, Stride = stride, Pixels = pixels };
        }
        finally { bmp.UnlockBits(data); }
    }

    private void WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
        _w.Write(bytes.Length);
        _w.Write(bytes);
    }

    private string ReadString()
    {
        var len = _r.ReadInt32();
        if (len < 0 || len > 1 << 20) throw new InvalidDataException("string too long");
        return Encoding.UTF8.GetString(_r.ReadBytes(len));
    }

    public void Dispose()
    {
        try { _pipe.Dispose(); } catch (Exception) { }
    }
}
