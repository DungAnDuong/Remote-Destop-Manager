using System;
using System.Runtime.InteropServices;

namespace RDM.DesktopAgent;

/// <summary>
/// Tiêm chuột/phím vào input desktop đang hoạt động (kể cả Winlogon/secure desktop khi chạy SYSTEM).
/// Mỗi thao tác được chạy qua DesktopCapture.RunOnInputDesktop để gắn đúng desktop rồi SendInput.
/// </summary>
internal static class AgentInject
{
    private const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010,
        MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, WHEEL = 0x0800, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    private const uint KEYUP = 0x0002, EXTENDED = 0x0001, UNICODE = 0x0004, SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int Dx, Dy; public int Data; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public int Type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    // Mã thao tác chuột khớp với RDM.Shared.Protocol.MouseAction (giữ đồng bộ bằng số, không tham chiếu project để agent gọn).
    private const byte Move = 0, LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, MiddleDown = 5, MiddleUp = 6, WheelAct = 7;

    /// <summary>
    /// nx,ny la toa do CHUAN HOA 0..65535 so voi KHUNG agent gui (toan bo man hinh ao). Vi co MOUSEEVENTF_VIRTUALDESK,
    /// gia tri absolute 0..65535 cung map vao toan bo man hinh ao, nen nx/ny chinh la toa do absolute can dung.
    /// </summary>
    public static void Mouse(int nx, int ny, byte action, int wheel)
    {
        DesktopCapture.RunOnInputDesktop(_ =>
        {
            var input = new MOUSEINPUT
            {
                Dx = Math.Max(0, Math.Min(65535, nx)),
                Dy = Math.Max(0, Math.Min(65535, ny)),
                Flags = MOVE | ABSOLUTE | VIRTUALDESK
            };
            switch (action)
            {
                case LeftDown: input.Flags |= LEFTDOWN; break;
                case LeftUp: input.Flags |= LEFTUP; break;
                case RightDown: input.Flags |= RIGHTDOWN; break;
                case RightUp: input.Flags |= RIGHTUP; break;
                case MiddleDown: input.Flags |= MIDDLEDOWN; break;
                case MiddleUp: input.Flags |= MIDDLEUP; break;
                case WheelAct: input.Flags |= WHEEL; input.Data = wheel; break;
            }
            Send(new INPUT { Type = INPUT_MOUSE, U = new InputUnion { Mouse = input } });
        });
    }

    public static void Key(int vk, int scan, bool extended, bool down)
    {
        DesktopCapture.RunOnInputDesktop(_ =>
        {
            var flags = (down ? 0u : KEYUP) | (extended ? EXTENDED : 0u) | (scan != 0 ? SCANCODE : 0u);
            Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = (ushort)(scan != 0 ? 0 : vk), Scan = (ushort)scan, Flags = flags } } });
        });
    }

    public static void Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        DesktopCapture.RunOnInputDesktop(_ =>
        {
            foreach (var ch in text)
            {
                Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Scan = ch, Flags = UNICODE } } });
                Send(new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Scan = ch, Flags = UNICODE | KEYUP } } });
            }
        });
    }

    private static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
}
