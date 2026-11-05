extern alias rdmclient;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RDM.Data.Services;
using RDM.Shared.Models;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;
using Xunit;
using ClientStateStore = rdmclient::RDM.Client.ClientStateStore;
using ConnectionManager = rdmclient::RDM.Client.ConnectionManager;

namespace RDM.Tests;

/// <summary>Chạy RDM.Server.exe thật với chứng thư tự ký và thư mục tạm. Kiểm thử đi qua TCP và TLS thật.</summary>
public sealed class RelayServer : IDisposable
{
    public const string ControllerToken = "controller-token-bi-mat-QQQ";
    public const string EnrollmentToken = "enrollment-token-bi-mat-WWW";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdm-relay-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _extra;
    private readonly StringBuilder _output = new StringBuilder();
    private Process _process;

    public int Port { get; private set; }
    public string Pin { get; }
    public string Output { get { lock (_output) return _output.ToString(); } }

    public static string ServerExe()
    {
        // Thư mục test: tests\RDM.Tests\bin\<nền tảng>\<cấu hình>. Server luôn là x64.
        var cfg = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        var root = cfg.Parent.Parent.Parent.Parent.Parent.FullName;
        var exe = Path.Combine(root, "src", "RDM.Server", "bin", "x64", cfg.Name, "RDM.Server.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Chưa build RDM.Server x64: " + exe);
        return exe;
    }

    public RelayServer(Dictionary<string, string> extra = null)
    {
        _extra = extra ?? new Dictionary<string, string>();
        Directory.CreateDirectory(_dir);
        Port = FreePort();
        var generate = Psi("--generate-cert \"" + Path.Combine(_dir, "relay.pfx") + "\" localhost 30");
        generate.RedirectStandardOutput = true;
        generate.RedirectStandardError = true;
        string errors;
        using (var p = Process.Start(generate))
        {
            var stderr = p.StandardError.ReadToEndAsync();
            Pin = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            errors = stderr.Result;
        }
        Assert.True(Pin.Length == 64, "Không sinh được chứng thư: " + errors);
        // Cổng trống có thể bị tiến trình khác chiếm trước khi Server kịp dùng: chỉ trường hợp đó mới thử lại, lỗi khác vẫn báo.
        for (var attempt = 1; ; attempt++)
        {
            try { Start(); break; }
            catch (InvalidOperationException ex) when (attempt < 3 && ex.Message.Contains("address already in use"))
            {
                Stop();
                Port = FreePort();
            }
        }
    }

    private ProcessStartInfo Psi(string args)
    {
        var psi = new ProcessStartInfo(ServerExe(), args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir };
        // Tiến trình test x86 vẫn phải trỏ tới dotnet x64 cho Server.
        psi.EnvironmentVariables["DOTNET_ROOT"] = Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetEnvironmentVariable("ProgramFiles"), "dotnet");
        psi.EnvironmentVariables["RDM_CERT_PASSWORD"] = "test-pfx-password";
        return psi;
    }

    public void Start()
    {
        var psi = Psi("");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        // Cổng 0: Kestrel tự chọn cổng trống, không có khoảng trống để bị chiếm mất.
        psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        psi.EnvironmentVariables["Relay__Port"] = Port.ToString();
        psi.EnvironmentVariables["Relay__BindAddress"] = "127.0.0.1";
        psi.EnvironmentVariables["Relay__CertificatePath"] = Path.Combine(_dir, "relay.pfx");
        psi.EnvironmentVariables["Relay__DatabasePath"] = Path.Combine(_dir, "relay.db");
        psi.EnvironmentVariables["Relay__ControllerTokenSha256"] = Sha(ControllerToken);
        psi.EnvironmentVariables["Relay__HeartbeatIntervalSeconds"] = "1";
        psi.EnvironmentVariables["Relay__HeartbeatTimeoutSeconds"] = "3";
        psi.EnvironmentVariables["Relay__MaxAuthFailures"] = "100000";
        foreach (var kv in _extra) psi.EnvironmentVariables[kv.Key] = kv.Value;
        _process = new Process { StartInfo = psi };
        void Append(object s, DataReceivedEventArgs e) { lock (_output) _output.AppendLine(e.Data); }
        _process.OutputDataReceived += Append;
        _process.ErrorDataReceived += Append;
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            try { using var c = new TcpClient(); c.Connect("127.0.0.1", Port); return; }
            catch (SocketException) { Thread.Sleep(100); }
            if (_process.HasExited) throw new InvalidOperationException("Server thoát sớm:\n" + Output);
        }
        throw new TimeoutException("Server không khởi động:\n" + Output);
    }

    public void Stop()
    {
        if (_process == null) return;
        try { if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(5000); } }
        finally { _process.Dispose(); _process = null; }
    }

    public RelayEndpoint Endpoint() => new RelayEndpoint { Host = "localhost", Port = Port, PinnedKeySha256 = Pin };

    public static string Sha(string token)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(token)).Select(b => b.ToString("x2")));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        Stop();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Wait
{
    public static async Task Until(Func<Task<bool>> condition, string what, int seconds = 15)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.True(await condition(), "Hết thời gian chờ: " + what);
    }

    public static async Task Until(Func<bool> condition, string what, int seconds = 15)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "Hết thời gian chờ: " + what);
    }
}

public class RelayEndToEndTests : IClassFixture<RelayEndToEndTests.Shared>
{
    public sealed class Shared : IDisposable
    {
        public RelayServer Server { get; } = new RelayServer();
        public void Dispose() => Server.Dispose();
    }

    private readonly RelayServer _server;
    public RelayEndToEndTests(Shared shared) => _server = shared.Server;

    private static DeviceInfo Info(string name = "PC-E2E") => new DeviceInfo { Name = name, OperatingSystem = "Windows 10", Architecture = "x64", LocalIp = "10.1.2.3", MacAddress = "AA:BB" };

    private DeviceRelayClient Device(DeviceIdentity identity, string id, bool access = true, RelayEndpoint endpoint = null) =>
        new DeviceRelayClient(endpoint ?? _server.Endpoint(), identity, id, Info(), access) { InitialBackoff = TimeSpan.FromMilliseconds(100) };

    private sealed class Watcher : IDisposable
    {
        public readonly ConcurrentDictionary<string, PresenceEntry> Entries = new ConcurrentDictionary<string, PresenceEntry>();
        public readonly PresenceRelayClient Client;
        public volatile bool SnapshotReceived;
        public Watcher(RelayServer server, string token = RelayServer.ControllerToken, RelayEndpoint endpoint = null)
        {
            Client = new PresenceRelayClient(endpoint ?? server.Endpoint(), token) { InitialBackoff = TimeSpan.FromMilliseconds(100) };
            Client.EntriesReceived += (list, snapshot) =>
            {
                if (snapshot) { Entries.Clear(); SnapshotReceived = true; }
                foreach (var e in list) Entries[e.RemoteId] = e;
            };
            Client.Start();
        }
        public bool IsOnline(string id) => Entries.TryGetValue(id, out var e) && e.IsOnline;
        public void Dispose() => Client.StopAsync().GetAwaiter().GetResult();
    }

    [Fact] // Tiêu chí Online/Offline: đăng ký, Heartbeat giữ Online, ngắt thì Offline.
    public async Task Device_RegistersThenGoesOnlineThenOffline()
    {
        var id = RemoteIdGenerator.Generate();
        using var watcher = new Watcher(_server);
        await Wait.Until(() => watcher.SnapshotReceived, "Controller nhận ảnh chụp");
        using var identity = DeviceIdentity.Generate();
        var device = Device(identity, id);
        device.Start();
        await Wait.Until(() => device.State == RelayState.Online, "thiết bị Online");
        await Wait.Until(() => watcher.IsOnline(id), "Controller thấy Online");
        Assert.Equal("PC-E2E", watcher.Entries[id].Info.Name);

        await Task.Delay(TimeSpan.FromSeconds(4)); // Lâu hơn timeout 3 giây: Heartbeat phải giữ được Online.
        Assert.True(watcher.IsOnline(id));

        await device.StopAsync();
        await Wait.Until(() => !watcher.IsOnline(id), "Controller thấy Offline");
    }

    [Fact]
    public async Task Controller_SnapshotIncludesOfflineRegisteredDevices()
    {
        var id = RemoteIdGenerator.Generate();
        using var identity = DeviceIdentity.Generate();
        var device = Device(identity, id);
        device.Start();
        await Wait.Until(() => device.State == RelayState.Online, "thiết bị Online");
        await device.StopAsync();
        await Task.Delay(300);
        using var watcher = new Watcher(_server);
        await Wait.Until(() => watcher.SnapshotReceived, "ảnh chụp");
        Assert.True(watcher.Entries.ContainsKey(id));
        // Máy bận thì Relay có thể chưa kịp thấy thiết bị ngắt lúc chụp ảnh; khi đó tin Offline phải tới ngay sau.
        await Wait.Until(() => !watcher.Entries[id].IsOnline, "thiết bị Offline");
    }

    [Fact]
    public async Task AccessFlag_PropagatesToController()
    {
        var id = RemoteIdGenerator.Generate();
        using var watcher = new Watcher(_server);
        using var identity = DeviceIdentity.Generate();
        var device = Device(identity, id);
        device.Start();
        await Wait.Until(() => watcher.IsOnline(id) && watcher.Entries[id].AccessEnabled, "Online có quyền");
        device.SetAccessEnabled(false);
        await Wait.Until(() => !watcher.Entries[id].AccessEnabled, "đã tắt truy cập");
        await device.StopAsync();
    }

    [Fact] // Chống chiếm Remote ID.
    public async Task RemoteId_BoundToFirstKey_SecondKeyRejected()
    {
        var id = RemoteIdGenerator.Generate();
        using var owner = DeviceIdentity.Generate();
        using var attacker = DeviceIdentity.Generate();
        var a = Device(owner, id);
        a.Start();
        await Wait.Until(() => a.State == RelayState.Online, "chủ Online");
        await a.StopAsync();
        var b = Device(attacker, id);
        b.Start();
        await Wait.Until(() => b.State == RelayState.Rejected, "kẻ giả mạo bị từ chối");
        Assert.Equal(AuthCode.RemoteIdConflict, b.LastRejection);
        await b.StopAsync();
        var again = Device(owner, id);
        again.Start();
        await Wait.Until(() => again.State == RelayState.Online, "chủ vẫn dùng được");
        await again.StopAsync();
    }

    private async Task<FramedStream> RawAsync() => await TlsConnector.ConnectAsync(_server.Endpoint(), CancellationToken.None);

    private static async Task<byte[]> HelloAsync(FramedStream c, PeerRole role)
    {
        await c.SendAsync(MessageType.Hello, Wire.Build(w => { w.Write(ProtocolInfo.Version); w.Write((byte)role); }), CancellationToken.None);
        var f = await c.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(MessageType.Challenge, f.Type);
        return Wire.Parse(f.Payload, r => r.Blob());
    }

    private static Task SendDeviceAuth(FramedStream c, string id, byte[] pub, byte[] sig) =>
        c.SendAsync(MessageType.DeviceAuth, Wire.Build(w => { w.Str(id); w.Blob(pub); w.Blob(sig); w.Str(null); w.Info(Info()); w.Write(true); }), CancellationToken.None);

    private static async Task<AuthCode> ReadCode(FramedStream c)
    {
        var f = await c.ReceiveAsync(ProtocolInfo.MaxHandshakeFrame, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(MessageType.AuthResult, f.Type);
        return Wire.Parse(f.Payload, r => (AuthCode)r.ReadByte());
    }

    [Fact] // Replay Attack ở bước xác thực: chữ ký gắn với nonce của từng kết nối.
    public async Task ReplayedSignature_IsRejected()
    {
        var id = RemoteIdGenerator.Generate();
        using var identity = DeviceIdentity.Generate();
        byte[] captured;
        using (var first = await RawAsync())
        {
            var nonce = await HelloAsync(first, PeerRole.Device);
            captured = identity.Sign(Wire.DeviceAuthMessage(nonce, id, identity.PublicKey));
            await SendDeviceAuth(first, id, identity.PublicKey, captured);
            Assert.Equal(AuthCode.Ok, await ReadCode(first));
        }
        using var replay = await RawAsync();
        await HelloAsync(replay, PeerRole.Device);
        await SendDeviceAuth(replay, id, identity.PublicKey, captured);
        Assert.Equal(AuthCode.BadSignature, await ReadCode(replay));
    }

    [Fact]
    public async Task ForgedSignature_IsRejected()
    {
        using var identity = DeviceIdentity.Generate();
        using var c = await RawAsync();
        await HelloAsync(c, PeerRole.Device);
        await SendDeviceAuth(c, RemoteIdGenerator.Generate(), identity.PublicKey, new byte[DeviceIdentity.SignatureSize]);
        Assert.Equal(AuthCode.BadSignature, await ReadCode(c));
    }

    [Theory] // Chưa xác thực thì không nhận thêm được gì.
    [InlineData("abc")]
    [InlineData("'; DROP TABLE Devices; --")]
    [InlineData("123")]
    public async Task InvalidRemoteId_IsRejected(string id)
    {
        using var identity = DeviceIdentity.Generate();
        using var c = await RawAsync();
        var nonce = await HelloAsync(c, PeerRole.Device);
        await SendDeviceAuth(c, id, identity.PublicKey, identity.Sign(Wire.DeviceAuthMessage(nonce, id, identity.PublicKey)));
        Assert.Equal(AuthCode.ProtocolError, await ReadCode(c));
    }

    [Fact] // Heartbeat: thiết bị im lặng phải bị coi là Offline.
    public async Task SilentDevice_GoesOfflineAfterTimeout()
    {
        var id = RemoteIdGenerator.Generate();
        using var watcher = new Watcher(_server);
        using var identity = DeviceIdentity.Generate();
        using var c = await RawAsync();
        var nonce = await HelloAsync(c, PeerRole.Device);
        await SendDeviceAuth(c, id, identity.PublicKey, identity.Sign(Wire.DeviceAuthMessage(nonce, id, identity.PublicKey)));
        Assert.Equal(AuthCode.Ok, await ReadCode(c));
        await Wait.Until(() => watcher.IsOnline(id), "Online");
        await Wait.Until(() => !watcher.IsOnline(id), "Offline do hết hạn Heartbeat", 10);
    }

    [Fact] // Chống cấp phát bộ nhớ lớn trước khi xác thực.
    public async Task OversizedPreAuthFrame_ClosesConnection()
    {
        using var tcp = new TcpClient("localhost", _server.Port);
        using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (a, b, d, e) => true);
        await ssl.AuthenticateAsClientAsync("localhost", null, SslProtocols.Tls12, false);
        await ssl.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 1 }, 0, 5);
        await ssl.FlushAsync();
        ssl.ReadTimeout = 5000;
        Assert.Equal(0, await ssl.ReadAsync(new byte[1], 0, 1));
    }

    [Fact] // TLS cũ phải bị Server từ chối.
    public async Task Tls11_IsRefusedByServer()
    {
        using var tcp = new TcpClient("localhost", _server.Port);
        using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (a, b, d, e) => true);
        await Assert.ThrowsAnyAsync<Exception>(() => ssl.AuthenticateAsClientAsync("localhost", null, SslProtocols.Tls11, false));
    }

    [Fact]
    public async Task Controller_WrongToken_IsRejected()
    {
        using var watcher = new Watcher(_server, "token-sai");
        await Wait.Until(() => watcher.Client.State == RelayState.Rejected, "bị từ chối");
        Assert.Equal(AuthCode.BadToken, watcher.Client.LastRejection);
        Assert.Empty(watcher.Entries);
    }

    [Fact] // MITM: chứng thư tự ký không ghim không được chấp nhận.
    public async Task Certificate_UntrustedWithoutPin_NeverGoesOnline()
    {
        using var identity = DeviceIdentity.Generate();
        var device = Device(identity, RemoteIdGenerator.Generate(), true, new RelayEndpoint { Host = "localhost", Port = _server.Port });
        device.Start();
        await Wait.Until(() => device.State == RelayState.Offline, "bị từ chối TLS");
        Assert.Contains("certificate", device.LastError);
        await device.StopAsync();
    }

    [Fact] // MITM: khóa ghim khác nghĩa là không phải Relay thật.
    public async Task Certificate_WrongPin_NeverGoesOnline()
    {
        using var identity = DeviceIdentity.Generate();
        var device = Device(identity, RemoteIdGenerator.Generate(), true, new RelayEndpoint { Host = "localhost", Port = _server.Port, PinnedKeySha256 = new string('a', 64) });
        device.Start();
        await Wait.Until(() => device.State == RelayState.Offline, "bị từ chối TLS");
        Assert.Contains("certificate", device.LastError);
        await device.StopAsync();
    }

    [Fact] // Hostname: chứng thư chỉ cấp cho localhost, kết nối bằng 127.0.0.1 phải thất bại dù đúng khóa ghim.
    public async Task Certificate_HostnameMismatch_RejectedEvenWithCorrectPin()
    {
        using var identity = DeviceIdentity.Generate();
        var device = Device(identity, RemoteIdGenerator.Generate(), true, new RelayEndpoint { Host = "127.0.0.1", Port = _server.Port, PinnedKeySha256 = _server.Pin });
        device.Start();
        await Wait.Until(() => device.State == RelayState.Offline, "bị từ chối TLS");
        Assert.Contains("certificate", device.LastError);
        await device.StopAsync();
    }

    [Fact] // CVE-2025-6965: Relay mở ra mạng nên không được dùng SQLite cũ hơn 3.50.2.
    public async Task Server_RunsPatchedSqlite()
    {
        await Wait.Until(() => _server.Output.Contains("SQLite "), "Server ghi phiên bản SQLite");
        var match = System.Text.RegularExpressions.Regex.Match(_server.Output, @"SQLite (\d+\.\d+\.\d+)");
        Assert.True(match.Success, _server.Output);
        var version = new Version(match.Groups[1].Value);
        Assert.True(version >= new Version(3, 50, 2), "SQLite cũ: " + version);
    }

    [Fact] // Không ghi bí mật vào Log.
    public async Task ServerLog_ContainsNoToken()
    {
        using (var bad = new Watcher(_server, "token-sai-BIMAT-EEE"))
            await Wait.Until(() => bad.Client.State == RelayState.Rejected, "bị từ chối");
        using (var good = new Watcher(_server))
            await Wait.Until(() => good.SnapshotReceived, "ảnh chụp");
        Assert.DoesNotContain("BIMAT-EEE", _server.Output);
        Assert.DoesNotContain(RelayServer.ControllerToken, _server.Output);
    }
}

public class RelayLifecycleTests
{
    [Fact] // Reconnect: Server khởi động lại thì thiết bị tự nối lại; Remote ID vẫn gắn đúng khóa nhờ registry bền vững.
    public async Task Device_ReconnectsAfterServerRestart_RegistrySurvives()
    {
        using var server = new RelayServer();
        var id = RemoteIdGenerator.Generate();
        using var identity = DeviceIdentity.Generate();
        var device = new DeviceRelayClient(server.Endpoint(), identity, id, new DeviceInfo { Name = "PC" }, true) { InitialBackoff = TimeSpan.FromMilliseconds(100) };
        device.Start();
        await Wait.Until(() => device.State == RelayState.Online, "Online lần đầu");
        server.Stop();
        await Wait.Until(() => device.State != RelayState.Online, "phát hiện mất Server");
        server.Start();
        await Wait.Until(() => device.State == RelayState.Online, "tự nối lại", 30);
        await device.StopAsync();

        using var attacker = DeviceIdentity.Generate();
        var other = new DeviceRelayClient(server.Endpoint(), attacker, id, new DeviceInfo { Name = "X" }, true);
        other.Start();
        await Wait.Until(() => other.State == RelayState.Rejected, "registry nhớ khóa sau khi khởi động lại");
        await other.StopAsync();
    }

    [Fact] // Brute Force phía Relay.
    public async Task BruteForce_BlocksIpAfterTooManyFailures()
    {
        using var server = new RelayServer(new Dictionary<string, string> { ["Relay__MaxAuthFailures"] = "3", ["Relay__AuthBlockMinutes"] = "5" });
        for (var i = 0; i < 3; i++)
        {
            var c = new PresenceRelayClient(server.Endpoint(), "token-sai");
            c.Start();
            await Wait.Until(() => c.State == RelayState.Rejected, "bị từ chối");
            await c.StopAsync();
        }
        // Đúng token nhưng IP đã bị chặn.
        var good = new PresenceRelayClient(server.Endpoint(), RelayServer.ControllerToken) { InitialBackoff = TimeSpan.FromMilliseconds(100) };
        good.Start();
        await Wait.Until(() => good.LastRejection == AuthCode.RateLimited, "bị chặn tạm");
        Assert.NotEqual(RelayState.Online, good.State);
        await good.StopAsync();
    }

    [Fact]
    public async Task Enrollment_RequiredForNewDevices_NotForKnownOnes()
    {
        using var server = new RelayServer(new Dictionary<string, string> { ["Relay__EnrollmentTokenSha256"] = RelayServer.Sha(RelayServer.EnrollmentToken) });
        var id = RemoteIdGenerator.Generate();
        using var identity = DeviceIdentity.Generate();
        var none = new DeviceRelayClient(server.Endpoint(), identity, id, new DeviceInfo(), true);
        none.Start();
        await Wait.Until(() => none.State == RelayState.Rejected, "thiếu mã");
        Assert.Equal(AuthCode.EnrollmentRequired, none.LastRejection);
        await none.StopAsync();
        var ok = new DeviceRelayClient(server.Endpoint(), identity, id, new DeviceInfo(), true, RelayServer.EnrollmentToken);
        ok.Start();
        await Wait.Until(() => ok.State == RelayState.Online, "có mã");
        await ok.StopAsync();
        var later = new DeviceRelayClient(server.Endpoint(), identity, id, new DeviceInfo(), true);
        later.Start();
        await Wait.Until(() => later.State == RelayState.Online, "đã đăng ký thì không cần mã");
        await later.StopAsync();
    }

    [Fact] // Controller ↔ Relay qua dịch vụ thật: trạng thái, LastOnline, tự động đăng ký, và "chưa rõ" khi mất Relay.
    public async Task PresenceService_ReflectsRealStatus_AndNeverGuessesWhenRelayIsGone()
    {
        using var server = new RelayServer();
        using var db = new TestDb().LoginAdmin();
        var rt = db.Rt;
        var known = RemoteIdGenerator.Generate();
        db.AddClient("Máy đã biết", known);
        var device = rt.Clients.Query(known, null, false).Single();
        Assert.Equal("Unknown", rt.Presence.StatusText(device));

        rt.Settings.Set(SettingKeys.RelayHost, "localhost");
        rt.Settings.Set(SettingKeys.RelayPort, server.Port.ToString());
        rt.Settings.Set(SettingKeys.RelayPin, server.Pin);
        rt.Settings.Set(SettingKeys.RelayToken, RelayServer.ControllerToken, true);
        rt.Settings.Set(SettingKeys.AutoRegister, "true");
        rt.Presence.InitialBackoff = TimeSpan.FromMilliseconds(100);
        rt.Presence.Start();
        await Wait.Until(() => rt.Presence.Availability == PresenceAvailability.Connected, "Controller kết nối Relay");
        Assert.Equal("Not registered", rt.Presence.StatusText(device));
        Assert.DoesNotContain(RelayServer.ControllerToken, db.RawDatabaseAsLatin1());

        using var id1 = DeviceIdentity.Generate();
        using var id2 = DeviceIdentity.Generate();
        var newId = RemoteIdGenerator.Generate();
        var d1 = new DeviceRelayClient(server.Endpoint(), id1, known, new DeviceInfo { Name = "Tên từ thiết bị", LocalIp = "10.9.9.9", OperatingSystem = "Windows 7 SP1", Architecture = "x86" }, false);
        var d2 = new DeviceRelayClient(server.Endpoint(), id2, newId, new DeviceInfo { Name = "Máy mới" }, true);
        d1.Start(); d2.Start();
        await Wait.Until(() => rt.Presence.StatusText(device) == "Online (access off)", "Online tắt truy cập");
        await Wait.Until(() => rt.Clients.Query(newId, null, false).Count == 1, "tự động đăng ký");
        Assert.Equal("Máy mới", rt.Clients.Query(newId, null, false).Single().DeviceName);
        await Wait.Until(() => rt.Clients.Query(known, null, false).Single().IpAddress == "10.9.9.9", "cập nhật IP");
        var row = rt.Clients.Query(known, null, false).Single();
        Assert.Equal("Máy đã biết", row.DeviceName); // Relay không được đổi tên do quản trị viên đặt.
        Assert.NotNull(row.LastOnline);
        Assert.Equal((2, 0), rt.Presence.Count(new[] { known, newId }).Value);

        // Thêm thiết bị đã Online từ trước: thông tin phải có ngay, không chờ thay đổi tiếp theo.
        using var id3 = DeviceIdentity.Generate();
        var lateId = RemoteIdGenerator.Generate();
        var d3 = new DeviceRelayClient(server.Endpoint(), id3, lateId, new DeviceInfo { Name = "Máy thêm sau", LocalIp = "10.7.7.7" }, true);
        rt.Settings.Set(SettingKeys.AutoRegister, "false");
        d3.Start();
        await Wait.Until(() => rt.Presence.Find(lateId)?.IsOnline == true, "Relay báo Online");
        db.AddClient("Máy thêm sau", lateId);
        rt.Presence.ReapplyCached();
        Assert.Equal("10.7.7.7", rt.Clients.Query(lateId, null, false).Single().IpAddress);
        await d3.StopAsync();

        await d1.StopAsync();
        await Wait.Until(() => rt.Presence.StatusText(device) == "Offline", "Offline");
        Assert.Equal((1, 1), rt.Presence.Count(new[] { known, newId }).Value);
        await d2.StopAsync();

        server.Stop();
        await Wait.Until(() => rt.Presence.Availability != PresenceAvailability.Connected, "mất Relay");
        Assert.Equal("Unknown", rt.Presence.StatusText(device));
        Assert.Null(rt.Presence.Count(new[] { known }));
        await rt.Presence.StopAsync();
    }

    [Fact] // Trùng Remote ID ở lần đăng ký đầu: Client tự đổi ID.
    public async Task Client_FirstRegistrationConflict_GeneratesNewId()
    {
        using var server = new RelayServer();
        var taken = RemoteIdGenerator.Generate();
        using var owner = DeviceIdentity.Generate();
        var first = new DeviceRelayClient(server.Endpoint(), owner, taken, new DeviceInfo(), true);
        first.Start();
        await Wait.Until(() => first.State == RelayState.Online, "chủ Online");
        await first.StopAsync();

        var dir = Path.Combine(Path.GetTempPath(), "rdm-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClientStateStore(Path.Combine(dir, "s.dat"));
            var state = store.LoadOrCreate();
            state.RemoteId = taken;
            state.RelayHost = "localhost"; state.RelayPort = server.Port; state.RelayPin = server.Pin;
            store.Save(state);
            using var manager = new ConnectionManager(store, state, new DeviceInfo { Name = "PC" }) { InitialBackoff = TimeSpan.FromMilliseconds(100) };
            manager.Start();
            await Wait.Until(() => manager.State == RelayState.Online, "Online bằng ID mới", 20);
            Assert.NotEqual(taken, state.RemoteId);
            Assert.True(store.LoadOrCreate().RegisteredOnce);
            Assert.Equal(state.RemoteId, store.LoadOrCreate().RemoteId);
            manager.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact] // Đã đăng ký mà bị báo trùng là bất thường: không được tự đổi ID.
    public async Task Client_AlreadyRegistered_DoesNotChangeIdOnConflict()
    {
        using var server = new RelayServer();
        var taken = RemoteIdGenerator.Generate();
        using var owner = DeviceIdentity.Generate();
        var first = new DeviceRelayClient(server.Endpoint(), owner, taken, new DeviceInfo(), true);
        first.Start();
        await Wait.Until(() => first.State == RelayState.Online, "chủ Online");
        await first.StopAsync();

        var dir = Path.Combine(Path.GetTempPath(), "rdm-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClientStateStore(Path.Combine(dir, "s.dat"));
            var state = store.LoadOrCreate();
            state.RemoteId = taken; state.RegisteredOnce = true;
            state.RelayHost = "localhost"; state.RelayPort = server.Port; state.RelayPin = server.Pin;
            using var manager = new ConnectionManager(store, state, new DeviceInfo { Name = "PC" });
            manager.Start();
            await Wait.Until(() => manager.State == RelayState.Rejected, "bị từ chối");
            Assert.Equal(taken, state.RemoteId);
            manager.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
