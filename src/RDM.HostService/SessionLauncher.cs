using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RDM.HostService;

/// <summary>
/// Khởi chạy một tiến trình quyền SYSTEM trong phiên console đang hoạt động.
/// Dịch vụ chạy ở Session 0; agent phải chạy trong phiên của người dùng mới thấy được các desktop của họ
/// (gồm cả secure desktop của UAC/màn hình khóa). Giữ nguyên quyền SYSTEM bằng cách nhân bản token của dịch vụ
/// rồi đổi session id, thay vì dùng token của người dùng (sẽ không chụp được secure desktop).
/// </summary>
internal static class SessionLauncher
{
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_NO_WINDOW = 0x08000000, CREATE_NEW_CONSOLE = 0x00000010;
    private const int TokenSessionId = 12;
    private const uint INVALID_SESSION = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int tokenInformationClass, ref uint info, int length);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string appName, string cmdLine, IntPtr procAttrs, IntPtr threadAttrs, bool inherit, int flags, IntPtr env, string curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

    public static uint ActiveSessionId() => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Chạy exePath (kèm tham số) quyền SYSTEM trong phiên console đang hoạt động, trên desktop mặc định.
    /// Trả về PID, hoặc 0 nếu không có phiên hoạt động. Ném Win32Exception khi API thất bại.
    /// </summary>
    public static uint LaunchAsSystemInActiveSession(string exePath, string arguments)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == INVALID_SESSION) return 0;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID, out var serviceToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
        var env = IntPtr.Zero;
        var primary = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(serviceToken, MAXIMUM_ALLOWED, IntPtr.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            var sid = sessionId;
            if (!SetTokenInformation(primary, TokenSessionId, ref sid, sizeof(uint)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(session) failed");
            CreateEnvironmentBlock(out env, primary, false);

            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = @"winsta0\default" };
            var cmd = "\"" + exePath + "\" " + arguments;
            if (!CreateProcessAsUser(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, null, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(serviceToken);
        }
    }
}
