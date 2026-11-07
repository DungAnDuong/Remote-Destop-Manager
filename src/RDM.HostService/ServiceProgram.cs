using System;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace RDM.HostService;

/// <summary>
/// Dịch vụ quyền SYSTEM giúp chụp được secure desktop (UAC / màn hình khóa).
/// Dịch vụ chạy ở Session 0 nên không tự chụp được desktop của người dùng; nó khởi chạy RDM.DesktopAgent
/// (quyền SYSTEM) trong phiên console đang hoạt động và giám sát, chạy lại khi agent thoát hoặc khi đổi phiên.
/// Bước 2: agent chạy chế độ --probe-loop ghi log để kiểm chứng; các bước sau sẽ cho agent phục vụ khung hình
/// và nhận input qua named pipe để tích hợp vào phiên điều khiển.
/// </summary>
public sealed class RdmHostService : ServiceBase
{
    public const string ServiceNameConst = "RDMHostAgent";
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RDM");
    private static string ServiceLog => Path.Combine(DataDir, "host-service.log");
    private static string ProbeLog => Path.Combine(DataDir, "desktop-probe.log");

    private Thread _supervisor;
    private volatile bool _stopping;

    public RdmHostService()
    {
        ServiceName = ServiceNameConst;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        Log("Service starting");
        _stopping = false;
        _supervisor = new Thread(SuperviseLoop) { IsBackground = true };
        _supervisor.Start();
    }

    protected override void OnStop() { _stopping = true; Log("Service stopping"); }
    protected override void OnShutdown() { _stopping = true; }

    private void SuperviseLoop()
    {
        Process agent = null;
        uint agentSession = 0xFFFFFFFF;
        var agentPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RDM.DesktopAgent.exe");
        while (!_stopping)
        {
            try
            {
                var session = SessionLauncher.ActiveSessionId();
                var alive = agent != null && !agent.HasExited;
                if (session != 0xFFFFFFFF && (!alive || session != agentSession))
                {
                    if (alive) { try { agent.Kill(); } catch (Exception) { } }
                    if (!File.Exists(agentPath)) { Log("Agent not found: " + agentPath); }
                    else
                    {
                        var pid = SessionLauncher.LaunchAsSystemInActiveSession(agentPath, $"--probe-loop \"{ProbeLog}\" 2");
                        agentSession = session;
                        agent = pid != 0 ? SafeGetProcess(pid) : null;
                        Log($"Launched agent pid={pid} in session {session}");
                    }
                }
            }
            catch (Exception ex) { Log("Supervisor error: " + ex.Message); }
            Thread.Sleep(3000);
        }
        if (agent != null && !agent.HasExited) { try { agent.Kill(); } catch (Exception) { } }
    }

    private static Process SafeGetProcess(uint pid) { try { return Process.GetProcessById((int)pid); } catch (Exception) { return null; } }

    private static void Log(string message)
    {
        try { File.AppendAllText(ServiceLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}"); }
        catch (Exception) { }
    }

    // --- Điểm vào: chạy như dịch vụ, hoặc cài/gỡ/chạy console để kiểm thử ---
    public static int Main(string[] args)
    {
        if (args.Length >= 1)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--install": return Install(false);
                case "--uninstall": return Install(true);
                case "--console":
                    var svc = new RdmHostService();
                    svc.OnStart(Array.Empty<string>());
                    Console.WriteLine("Running in console mode. Press Enter to stop.");
                    Console.ReadLine();
                    svc.OnStop();
                    return 0;
            }
        }
        ServiceBase.Run(new RdmHostService());
        return 0;
    }

    private static int Install(bool uninstall)
    {
        try
        {
            using var inst = new AssemblyInstaller(typeof(RdmHostService).Assembly, null) { UseNewContext = true };
            var state = new System.Collections.Hashtable();
            if (uninstall) inst.Uninstall(state);
            else { inst.Install(state); inst.Commit(state); }
            Console.WriteLine(uninstall ? "Service uninstalled." : "Service installed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}

/// <summary>Cài dịch vụ chạy quyền LocalSystem, tự khởi động cùng Windows.</summary>
[RunInstaller(true)]
public sealed class HostServiceInstaller : Installer
{
    public HostServiceInstaller()
    {
        var process = new ServiceProcessInstaller { Account = ServiceAccount.LocalSystem };
        var service = new ServiceInstaller
        {
            ServiceName = RdmHostService.ServiceNameConst,
            DisplayName = "RDM Host Agent",
            Description = "Lets RDM show the UAC prompt and the lock screen of this computer during a remote session.",
            StartType = ServiceStartMode.Automatic
        };
        Installers.Add(process);
        Installers.Add(service);
    }
}
