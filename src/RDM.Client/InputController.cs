using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using RDM.Shared.Protocol;

namespace RDM.Client;

public interface IInputSink
{
    void Mouse(MonitorInfo monitor, MouseInputData m);
    void Key(KeyInputData k);
    void Text(string text);
    /// <summary>Trả về thông báo nếu không thực hiện được, null nếu thành công.</summary>
    string Special(SpecialKey key);
    /// <summary>Thả mọi phím và nút chuột còn giữ khi phiên kết thúc, tránh kẹt phím trên máy người dùng.</summary>
    void ReleaseAll();
}

/// <summary>Chuột và bàn phím thật qua SendInput (Phần 5.2, 5.3). Chỉ được dùng trong phiên đã được chủ máy cấp quyền.</summary>
public sealed class SendInputController : IInputSink
{
    private const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010, MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, WHEEL = 0x0800, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    private const uint KEYUP = 0x0002, EXTENDED = 0x0001, UNICODE = 0x0004, SCANCODE = 0x0008;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int Dx, Dy; public int Data; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public int Type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private readonly HashSet<(int vk, int scan, bool ext)> _heldKeys = new HashSet<(int, int, bool)>();
    private readonly HashSet<MouseAction> _heldButtons = new HashSet<MouseAction>();
    private readonly object _gate = new object();

    public void Mouse(MonitorInfo monitor, MouseInputData m)
    {
        lock (_gate)
        {
            // Tọa độ chuẩn hóa theo monitor đang xem, đổi sang tọa độ của toàn bộ màn hình ảo cho SendInput.
            var px = monitor.X + (long)m.X * (monitor.Width - 1) / SessionInfo.NormalizedMax;
            var py = monitor.Y + (long)m.Y * (monitor.Height - 1) / SessionInfo.NormalizedMax;
            var vx = GetSystemMetrics(SM_XVIRTUALSCREEN); var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)); var vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
            var input = new MOUSEINPUT
            {
                Dx = (int)((px - vx) * 65535 / (vw - 1)), Dy = (int)((py - vy) * 65535 / (vh - 1)), Flags = MOVE | ABSOLUTE | VIRTUALDESK
            };
            switch (m.Action)
            {
                case MouseAction.LeftDown: input.Flags |= LEFTDOWN; _heldButtons.Add(MouseAction.LeftUp); break;
                case MouseAction.LeftUp: input.Flags |= LEFTUP; _heldButtons.Remove(MouseAction.LeftUp); break;
                case MouseAction.RightDown: input.Flags |= RIGHTDOWN; _heldButtons.Add(MouseAction.RightUp); break;
                case MouseAction.RightUp: input.Flags |= RIGHTUP; _heldButtons.Remove(MouseAction.RightUp); break;
                case MouseAction.MiddleDown: input.Flags |= MIDDLEDOWN; _heldButtons.Add(MouseAction.MiddleUp); break;
                case MouseAction.MiddleUp: input.Flags |= MIDDLEUP; _heldButtons.Remove(MouseAction.MiddleUp); break;
                case MouseAction.Wheel: input.Flags |= WHEEL; input.Data = m.WheelDelta; break;
            }
            Send(new INPUT { Type = INPUT_MOUSE, U = new InputUnion { Mouse = input } });
        }
    }

    public void Key(KeyInputData k)
    {
        lock (_gate)
        {
            SendKey(k.VirtualKey, k.ScanCode, k.Extended, k.Down);
            var id = (k.VirtualKey, k.ScanCode, k.Extended);
            if (k.Down) _heldKeys.Add(id); else _heldKeys.Remove(id);
        }
    }

    /// <summary>Ký tự Unicode: gõ tiếng Việt qua Unikey/IME ở máy Controller ra đúng chữ, không phụ thuộc bố cục bàn phím máy đích.</summary>
    public void Text(string text)
    {
        lock (_gate)
        {
            foreach (var ch in text)
            {
                Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Scan = ch, Flags = UNICODE } } });
                Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Scan = ch, Flags = UNICODE | KEYUP } } });
            }
        }
    }

    public string Special(SpecialKey key)
    {
        const int VK_MENU = 0x12, VK_TAB = 0x09, VK_LWIN = 0x5B, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_SNAPSHOT = 0x2C;
        switch (key)
        {
            case SpecialKey.CtrlAltDel: return SasClient.Request();
            case SpecialKey.WindowsKey: Chord(VK_LWIN); return null;
            case SpecialKey.AltTab: Chord(VK_MENU, VK_TAB); return null;
            case SpecialKey.CtrlEsc: Chord(VK_CONTROL, VK_ESCAPE); return null;
            case SpecialKey.PrintScreen: Chord(VK_SNAPSHOT); return null;
            default: return "Invalid special key.";
        }
    }

    private void Chord(params int[] keys)
    {
        lock (_gate)
        {
            foreach (var k in keys) SendKey(k, 0, k == 0x5B || k == 0x2C, true);
            for (var i = keys.Length - 1; i >= 0; i--) SendKey(keys[i], 0, keys[i] == 0x5B || keys[i] == 0x2C, false);
        }
    }

    private static void SendKey(int vk, int scan, bool extended, bool down)
    {
        var flags = (down ? 0u : KEYUP) | (extended ? EXTENDED : 0u) | (scan != 0 ? SCANCODE : 0u);
        // Dùng scan code khi có: giữ đúng phím vật lý, nhất là với game và ứng dụng đọc scan code.
        Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = (ushort)(scan != 0 ? 0 : vk), Scan = (ushort)scan, Flags = flags } } });
    }

    private static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));

    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var (vk, scan, ext) in _heldKeys) SendKey(vk, scan, ext, false);
            _heldKeys.Clear();
            foreach (var up in _heldButtons)
            {
                var flag = up == MouseAction.LeftUp ? LEFTUP : up == MouseAction.RightUp ? RIGHTUP : MIDDLEUP;
                Send(new INPUT { Type = INPUT_MOUSE, U = new InputUnion { Mouse = new MOUSEINPUT { Flags = flag } } });
            }
            _heldButtons.Clear();
        }
    }
}

/// <summary>Ctrl+Alt+Del không giả lập được bằng SendInput; phải nhờ Windows Service gọi SendSAS.</summary>
public static class SasClient
{
    public const string PipeName = "RDM.Client.SAS";

    public static string Request()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(500);
            pipe.WriteByte(1);
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            return "Ctrl+Alt+Del requires the RDM service running on the remote computer.";
        }
    }
}
