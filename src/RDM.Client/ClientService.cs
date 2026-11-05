using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace RDM.Client;

/// <summary>
/// Windows Service: giữ Agent chạy trong phiên người dùng và thực hiện Ctrl+Alt+Del.
/// Service ở Session 0 nên KHÔNG tự chụp màn hình hay gửi input (Phần 3.2): việc đó luôn do Agent làm.
/// Agent chạy bằng token của người dùng đang đăng nhập, luôn hiện cửa sổ và thanh báo phiên; không có chế độ ẩn.
/// Giới hạn: không có Agent khi chưa ai đăng nhập, và không điều khiển được Secure Desktop.
/// CHƯA ĐƯỢC KIỂM THỬ: xem docs/07-BaoCaoKiemThu.md.
/// </summary>
public sealed class ClientService : ServiceBase
{
    public const string Name = "RDMClientService";
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const uint INVALID_SESSION = 0xFFFFFFFF, CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public short ShowWindow, Reserved2; public IntPtr Reserved3, StdIn, StdOut, StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("sas.dll")] private static extern void SendSAS(bool asUser);

    private CancellationTokenSource _cts;
    private Task _agentLoop, _sasLoop;
    private IntPtr _agent;
    private uint _agentSession = INVALID_SESSION;

    public ClientService()
    {
        ServiceName = Name;
        CanHandleSessionChangeEvent = true;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _agentLoop = Task.Run(() => AgentLoopAsync(_cts.Token));
        _sasLoop = Task.Run(() => SasLoopAsync(_cts.Token));
    }

    protected override void OnStop()
    {
        _cts.Cancel();
        try { Task.WaitAll(new[] { _agentLoop, _sasLoop }, TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        if (_agent != IntPtr.Zero) { CloseHandle(_agent); _agent = IntPtr.Zero; }
    }

    private async Task AgentLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { EnsureAgent(); }
            catch (Win32Exception ex) { Log.Warn("Could not start the Agent yet: {0}", ex.NativeErrorCode); }
            try { await Task.Delay(5000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Khởi chạy Agent trong phiên console đang hoạt động, bằng token của người dùng, nếu chưa có.</summary>
    private void EnsureAgent()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == INVALID_SESSION) return;
        if (_agent != IntPtr.Zero)
        {
            const uint WAIT_TIMEOUT = 258;
            if (session == _agentSession && WaitForSingleObject(_agent, 0) == WAIT_TIMEOUT) return;
            CloseHandle(_agent);
            _agent = IntPtr.Zero;
        }
        // Chưa ai đăng nhập thì lệnh này thất bại: bình thường, thử lại sau.
        if (!WTSQueryUserToken(session, out var token)) return;
        var env = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out env, token, false);
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), Desktop = @"winsta0\default" };
            if (!CreateProcessAsUser(token, exe, "\"" + exe + "\"", IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, env, null, ref si, out var pi))
                throw new Win32Exception();
            CloseHandle(pi.Thread);
            _agent = pi.Process;
            _agentSession = session;
            Log.Info("Started the Agent in session {0}", session);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(token);
        }
    }

    /// <summary>Chỉ người dùng tương tác trên máy (kể cả Agent) mới gửi được yêu cầu Ctrl+Alt+Del.</summary>
    private async Task SasLoopAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.Write | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(SasClient.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16, 16, security);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                if (pipe.ReadByte() == 1)
                {
                    SendSAS(false);
                    Log.Info("Sent Ctrl+Alt+Del");
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is System.IO.IOException || ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Log.Warn("Ctrl+Alt+Del handling error: {0}", ex.GetType().Name);
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Cài hoặc gỡ Service. Cần quyền Administrator; chỉ chạy khi quản trị viên chủ động gọi.</summary>
    public static int Manage(bool install)
    {
        var exe = Process.GetCurrentProcess().MainModule.FileName;
        var args = install
            ? $"create {Name} binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"RDM Client Service\""
            : $"delete {Name}";
        using var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        p.WaitForExit();
        return p.ExitCode;
    }
}
