using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NLog;
using NLog.Config;
using NLog.Targets;
using RDM.Client;
using RDM.Controller.Forms;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Data.Services;
using ClientService = RDM.Client.ClientService;

namespace RDM.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1)
        {
            switch (args[0])
            {
                case "--install-service": return ClientService.Manage(true);
                case "--uninstall-service": return ClientService.Manage(false);
                case "--service":
                    System.ServiceProcess.ServiceBase.Run(new ClientService());
                    return 0;
            }
        }

        var store = new ClientStateStore(ClientStateStore.DefaultPath());
        var clientDir = Path.GetDirectoryName(store.Path);
        var dataDir = SQLiteContext.DefaultDataDirectory();
        ConfigureLogging(dataDir);
        var log = LogManager.GetCurrentClassLogger();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Ui.Error(null, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => log.Fatal(e.ExceptionObject as Exception, "Unhandled error");
        Dpi.Init();

        // Theo thư mục dữ liệu: hai tiến trình không được cùng dùng một bộ khóa và Database. SHA-256 để x86 và x64 cho cùng kết quả.
        string mutexName;
        using (var sha = SHA256.Create())
            mutexName = @"Local\RDM." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToLowerInvariant()))).Replace("-", "");
        using var mutex = new Mutex(true, mutexName, out var first);
        if (!first)
        {
            MessageBox.Show("RDM is already running.", AllowControlPanel.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var state = LoadState(store);
        if (state == null) return 1;
        try { RelayDefaults.ApplyEmbedded(store, state); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Không ghi được thì vẫn chạy với cấu hình trong bộ nhớ; lần sau sẽ thử lại.
            log.Warn(ex, "Could not save the default Relay settings");
        }

        RdmRuntime rt;
        try { rt = new RdmRuntime(RdmRuntime.DefaultDatabasePath()); }
        catch (Exception ex)
        {
            log.Fatal(ex, "Could not initialize the database");
            MessageBox.Show("Could not open the database.\n\n" + ex.Message, AllowControlPanel.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using (rt)
        {
            log.Info("RDM started, 64-bit process: {0}", Environment.Is64BitProcess);
            rt.Session.BeginQuickSession(Environment.UserName);
            Ui.Try(null, () =>
            {
                if (rt.Settings.Get(SettingKeys.Theme) == nameof(ThemeMode.Dark)) Theme.Set(ThemeMode.Dark);
                rt.PurgeAudit();
            });
            // Bản Managed: cấu hình Relay đóng gói chỉ áp cho phần client; đồng bộ sang settings của controller
            // để PresenceService kết nối Relay và hiển thị Online/Offline thật (không còn "Unknown").
            // Dùng ApplyManagedRelay (không đòi quyền quản trị) vì lúc này mới chỉ có quick session quyền Viewer:
            // gọi Set sẽ ném UnauthorizedAccessException ("account does not have permission") và bỏ dở việc ghi cấu hình.
            if (RDM.Shared.Common.ProductMode.Managed)
                Ui.Try(null, () => rt.Settings.ApplyManagedRelay(state.RelayHost, state.RelayPort, state.RelayPin, state.EnrollmentToken));

            var activity = new ActivityLog(Path.Combine(clientDir, "activity.log"));
            var auth = new ClientAuthentication(store, state);
            var info = DeviceInfoProvider.Collect();
            info.SessionPort = state.SessionPort;
            using var connection = new ConnectionManager(store, state, info);
            using var sessions = new SessionManager(state, auth, () => new GdiScreenCapture(), () => new SendInputController());
            connection.IncomingSession += sessions.AcceptRelayed;
            RDM.Controller.Services.ConnectFlow.Broker = connection;
            activity.Write("RDM started");
            Application.Run(new HomeForm(rt, new AllowControlPanel(store, state, auth, connection, activity, info, sessions)));
            activity.Write("RDM closed");
            rt.Presence.StopAsync().GetAwaiter().GetResult();
        }
        LogManager.Shutdown();
        return 0;
    }

    /// <summary>File trạng thái hỏng: phải hỏi chủ máy, vì đặt lại sẽ làm đổi Remote ID và khóa định danh.</summary>
    private static ClientState LoadState(ClientStateStore store)
    {
        try { return store.LoadOrCreate(); }
        catch (ClientStateException ex)
        {
            LogManager.GetCurrentClassLogger().Error(ex, "Could not read the computer state");
            var answer = MessageBox.Show(ex.Message + "\n\nReset? The Remote ID, identity key and access password will be replaced, and your administrator must update this computer.",
                AllowControlPanel.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return null;
            store.Delete();
            return store.LoadOrCreate();
        }
    }

    /// <summary>Log không ghi dữ liệu nhạy cảm (Phần 16, mục 20).</summary>
    private static void ConfigureLogging(string dataDir)
    {
        var config = new LoggingConfiguration();
        var file = new FileTarget("file")
        {
            FileName = Path.Combine(dataDir, "logs", "rdm-${shortdate}.log"),
            Layout = "${longdate}|${level:uppercase=true}|${logger:shortName=true}|${message}${onexception:|${exception:format=type,message}}",
            MaxArchiveFiles = 14, ArchiveEvery = FileArchivePeriod.Day
        };
        config.AddRule(LogLevel.Info, LogLevel.Fatal, file);
        LogManager.Configuration = config;
    }
}
