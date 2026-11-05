using System;
using System.Collections.Generic;

namespace RDM.Shared.Protocol;

public static class SessionInfo
{
    public const int DefaultPort = 7444;
    public const int MaxFrame = 32 * 1024 * 1024;
    public const int MaxTiles = 4096;
    public const int NormalizedMax = 65535;
    public const string HostSignContext = "RDM-SESSION-HOST-v1";
    public const string ControllerSignContext = "RDM-SESSION-CTRL-v1";
}

public enum SessionMessage : byte
{
    Rekey = 1, Auth = 2, AuthResult = 3,
    ScreenInfo = 10, Frame = 11, FrameAck = 12, SetMonitor = 13, SetQuality = 14, Notice = 15,
    Mouse = 20, Key = 21, Text = 22, SpecialKey = 23,
    Ping = 30, Pong = 31, Disconnect = 32
}

public enum SessionDecline : byte { None = 0, AccessDisabled = 1, Locked = 2, BadPassword = 3, OwnerRejected = 4, Busy = 5, BadSignature = 6 }

public enum MouseAction : byte { Move = 0, LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, MiddleDown = 5, MiddleUp = 6, Wheel = 7 }

public enum SpecialKey : byte { CtrlAltDel = 1, WindowsKey = 2, AltTab = 3, CtrlEsc = 4, PrintScreen = 5 }

public sealed class SessionRejectedException : Exception
{
    public SessionDecline Reason { get; }
    public SessionRejectedException(SessionDecline reason) : base(Describe(reason)) => Reason = reason;

    public static string Describe(SessionDecline reason) => reason switch
    {
        SessionDecline.AccessDisabled => "The owner has turned off Remote Access.",
        SessionDecline.Locked => "The remote computer is temporarily locked after too many wrong passwords.",
        SessionDecline.BadPassword => "Incorrect access password.",
        SessionDecline.OwnerRejected => "The owner rejected or did not answer the connection request.",
        SessionDecline.Busy => "The remote computer already has another session.",
        SessionDecline.BadSignature => "Controller authentication failed.",
        _ => "The connection was refused."
    };
}

public sealed class MonitorInfo
{
    public int Index { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }
    public string Label => $"Monitor {Index + 1} ({Width}x{Height}{(IsPrimary ? ", primary" : "")})";
}

public sealed class FrameTile
{
    public int X, Y, Width, Height;
    public byte[] Data;
}

public sealed class FrameMessage
{
    public long Sequence;
    public int Monitor, Width, Height, Quality;
    public List<FrameTile> Tiles = new List<FrameTile>();
}

public struct MouseInputData { public MouseAction Action; public int X, Y, WheelDelta; }
public struct KeyInputData { public int VirtualKey, ScanCode; public bool Extended, Down; }

/// <summary>Mã hóa thông điệp phiên. Mọi giá trị đọc từ mạng đều được kiểm tra biên.</summary>
public static class SessionWire
{
    public static byte[] Frame(FrameMessage f) => Wire.Build(w =>
    {
        w.Write(f.Sequence); w.Write(f.Monitor); w.Write(f.Width); w.Write(f.Height); w.Write(f.Quality); w.Write(f.Tiles.Count);
        foreach (var t in f.Tiles) { w.Write(t.X); w.Write(t.Y); w.Write(t.Width); w.Write(t.Height); w.Write(t.Data.Length); w.Write(t.Data); }
    });

    public static FrameMessage ReadFrame(byte[] payload) => Wire.Parse(payload, r =>
    {
        var f = new FrameMessage { Sequence = r.ReadInt64(), Monitor = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32(), Quality = r.ReadInt32() };
        if (f.Width <= 0 || f.Height <= 0 || f.Width > 32768 || f.Height > 32768) throw new ProtocolException("Invalid screen size.");
        var n = r.ReadInt32();
        if (n < 0 || n > SessionInfo.MaxTiles) throw new ProtocolException("Invalid image region count.");
        for (var i = 0; i < n; i++)
        {
            var t = new FrameTile { X = r.ReadInt32(), Y = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32() };
            // Cộng ở kiểu long: với int, X lớn gần giới hạn sẽ tràn thành số âm và lọt qua kiểm tra.
            if (t.X < 0 || t.Y < 0 || t.Width <= 0 || t.Height <= 0 || (long)t.X + t.Width > f.Width || (long)t.Y + t.Height > f.Height) throw new ProtocolException("An image region is outside the screen.");
            var length = r.ReadInt32();
            if (length <= 0 || length > payload.Length) throw new ProtocolException("Invalid image length.");
            t.Data = r.ReadBytes(length);
            if (t.Data.Length != length) throw new System.IO.EndOfStreamException();
            f.Tiles.Add(t);
        }
        return f;
    });

    public static byte[] Monitors(IReadOnlyList<MonitorInfo> list, int current) => Wire.Build(w =>
    {
        w.Write(current); w.Write(list.Count);
        foreach (var m in list) { w.Write(m.Index); w.Write(m.X); w.Write(m.Y); w.Write(m.Width); w.Write(m.Height); w.Write(m.IsPrimary); }
    });

    public static List<MonitorInfo> ReadMonitors(byte[] payload, out int current)
    {
        var active = 0;
        var result = Wire.Parse(payload, r =>
        {
            active = r.ReadInt32();
            var n = r.ReadInt32();
            if (n < 0 || n > 64) throw new ProtocolException("Invalid monitor count.");
            var list = new List<MonitorInfo>(n);
            for (var i = 0; i < n; i++) list.Add(new MonitorInfo { Index = r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32(), IsPrimary = r.ReadBoolean() });
            return list;
        });
        current = active;
        return result;
    }

    public static byte[] Mouse(MouseInputData m) => Wire.Build(w => { w.Write((byte)m.Action); w.Write(m.X); w.Write(m.Y); w.Write(m.WheelDelta); });

    public static MouseInputData ReadMouse(byte[] p) => Wire.Parse(p, r =>
    {
        var m = new MouseInputData { Action = (MouseAction)r.ReadByte(), X = r.ReadInt32(), Y = r.ReadInt32(), WheelDelta = r.ReadInt32() };
        if (m.Action > MouseAction.Wheel || m.X < 0 || m.X > SessionInfo.NormalizedMax || m.Y < 0 || m.Y > SessionInfo.NormalizedMax) throw new ProtocolException("Invalid mouse command.");
        return m;
    });

    public static byte[] Key(KeyInputData k) => Wire.Build(w => { w.Write(k.VirtualKey); w.Write(k.ScanCode); w.Write(k.Extended); w.Write(k.Down); });

    public static KeyInputData ReadKey(byte[] p) => Wire.Parse(p, r =>
    {
        var k = new KeyInputData { VirtualKey = r.ReadInt32(), ScanCode = r.ReadInt32(), Extended = r.ReadBoolean(), Down = r.ReadBoolean() };
        if (k.VirtualKey < 0 || k.VirtualKey > 255 || k.ScanCode < 0 || k.ScanCode > 0xFFFF) throw new ProtocolException("Invalid key command.");
        return k;
    });

    public static byte[] Quality(int quality, int maxFps, int scalePercent) => Wire.Build(w => { w.Write(quality); w.Write(maxFps); w.Write(scalePercent); });
}
