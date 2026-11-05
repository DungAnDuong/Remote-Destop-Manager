extern alias rdmclient;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using RDM.Shared.Protocol;
using RDM.Shared.Security;
using Xunit;
using AccessDecision = rdmclient::RDM.Client.AccessDecision;
using ActivityLog = rdmclient::RDM.Client.ActivityLog;
using ClientAuthentication = rdmclient::RDM.Client.ClientAuthentication;
using ClientState = rdmclient::RDM.Client.ClientState;
using ClientStateException = rdmclient::RDM.Client.ClientStateException;
using ClientStateStore = rdmclient::RDM.Client.ClientStateStore;
using ClientValidationException = rdmclient::RDM.Client.ClientValidationException;
using DeviceInfoProvider = rdmclient::RDM.Client.DeviceInfoProvider;
using StartupManager = rdmclient::RDM.Client.StartupManager;

namespace RDM.Tests;

public sealed class TempStore : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "rdm-client-" + Guid.NewGuid().ToString("N"));
    public ClientStateStore Store { get; }
    public TempStore() => Store = new ClientStateStore(Path.Combine(Dir, "client-state.dat"));
    public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
}

public class ClientStateTests
{
    [Fact]
    public void FirstRun_CreatesId_AndReloadsSameIdentity()
    {
        using var t = new TempStore();
        var a = t.Store.LoadOrCreate();
        Assert.Matches("^[1-9][0-9]{8}$", a.RemoteId);
        var b = t.Store.LoadOrCreate();
        Assert.Equal(a.RemoteId, b.RemoteId);
        Assert.Equal(a.IdentitySeed, b.IdentitySeed);
    }

    [Fact]
    public void AllFields_RoundTrip()
    {
        using var t = new TempStore();
        var s = t.Store.LoadOrCreate();
        s.AccessPasswordHash = "pbkdf2$x"; s.AccessEnabled = false; s.UnattendedEnabled = true; s.RelayHost = "relay.vn"; s.RelayPort = 1234;
        s.RelayPin = new string('a', 64); s.EnrollmentToken = "tok"; s.RegisteredOnce = true; s.FailedAttempts = 3; s.LockedUntilUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        t.Store.Save(s);
        var r = t.Store.LoadOrCreate();
        Assert.Equal(s.AccessPasswordHash, r.AccessPasswordHash);
        Assert.False(r.AccessEnabled);
        Assert.True(r.UnattendedEnabled);
        Assert.Equal("relay.vn", r.RelayHost);
        Assert.Equal(1234, r.RelayPort);
        Assert.Equal(s.RelayPin, r.RelayPin);
        Assert.Equal("tok", r.EnrollmentToken);
        Assert.True(r.RegisteredOnce);
        Assert.Equal(3, r.FailedAttempts);
        Assert.Equal(s.LockedUntilUtc, r.LockedUntilUtc);
    }

    [Fact] // Database Theft phía Client: file không chứa bí mật dạng rõ.
    public void StateFile_HasNoPlaintextSecrets()
    {
        using var t = new TempStore();
        var s = t.Store.LoadOrCreate();
        s.AccessPasswordHash = "pbkdf2-sha256$BIMAT"; s.EnrollmentToken = "TokenBiMatQQQ"; s.RelayHost = "relay.bimat.test";
        t.Store.Save(s);
        var raw = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(t.Store.Path));
        foreach (var secret in new[] { "BIMAT", "TokenBiMatQQQ", "relay.bimat.test", s.RemoteId }) Assert.DoesNotContain(secret, raw);
    }

    [Fact] // Không được âm thầm tạo định danh mới khi file hỏng.
    public void TamperedFile_IsRejected_NotSilentlyRecreated()
    {
        using var t = new TempStore();
        t.Store.LoadOrCreate();
        var bytes = File.ReadAllBytes(t.Store.Path);
        bytes[bytes.Length / 2] ^= 1;
        File.WriteAllBytes(t.Store.Path, bytes);
        Assert.Throws<ClientStateException>(() => t.Store.LoadOrCreate());
        Assert.Equal(bytes, File.ReadAllBytes(t.Store.Path));
    }
}

public class ClientAuthenticationTests
{
    private const string Pw = "Mat-Khau-Truy-Cap-1";
    private static readonly byte[] Transcript = Encoding.UTF8.GetBytes("transcript-phien-1");

    private sealed class Ctx : IDisposable
    {
        public readonly TempStore Temp = new TempStore();
        public readonly ClientState State;
        public readonly ClientAuthentication Auth;
        public DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public Ctx()
        {
            State = Temp.Store.LoadOrCreate();
            Auth = new ClientAuthentication(Temp.Store, State, 1000, () => Now);
        }
        public byte[] Proof(string password, byte[] transcript = null)
        {
            Assert.True(Auth.TryGetProofParameters(out var salt, out var iterations));
            return AccessProof.Compute(password, salt, iterations, transcript ?? Transcript);
        }
        public void Dispose() => Temp.Dispose();
    }

    [Fact] // Mặc định an toàn: chưa đặt mật khẩu thì chủ máy phải chấp nhận.
    public void Default_RequiresOwnerApproval()
    {
        using var c = new Ctx();
        Assert.Equal(AccessDecision.RequireOwnerApproval, c.Auth.Evaluate(Transcript, null));
        Assert.False(c.State.UnattendedEnabled);
    }

    [Fact]
    public void CorrectPassword_StillRequiresApproval_WithoutUnattended()
    {
        using var c = new Ctx();
        c.Auth.SetPassword(null, Pw);
        Assert.Equal(AccessDecision.RequireOwnerApproval, c.Auth.Evaluate(Transcript, c.Proof(Pw)));
    }

    [Fact]
    public void Unattended_RequiresPassword_ThenAllowsWithoutApproval()
    {
        using var c = new Ctx();
        Assert.Throws<ClientValidationException>(() => c.Auth.SetUnattended(true));
        c.Auth.SetPassword(null, Pw);
        c.Auth.SetUnattended(true);
        Assert.Equal(AccessDecision.Allowed, c.Auth.Evaluate(Transcript, c.Proof(Pw)));
        Assert.Equal(AccessDecision.DeniedBadPassword, c.Auth.Evaluate(Transcript, c.Proof("sai-mat-khau-123")));
    }

    [Fact] // Kill switch của chủ máy thắng mọi thứ, kể cả Unattended và mật khẩu đúng.
    public void AccessDisabled_DeniesEverything()
    {
        using var c = new Ctx();
        c.Auth.SetPassword(null, Pw);
        c.Auth.SetUnattended(true);
        c.Auth.SetAccessEnabled(false);
        Assert.Equal(AccessDecision.DeniedDisabled, c.Auth.Evaluate(Transcript, c.Proof(Pw)));
    }

    [Fact] // Replay: bằng chứng của phiên này không dùng được cho phiên khác.
    public void Proof_IsBoundToTranscript()
    {
        using var c = new Ctx();
        c.Auth.SetPassword(null, Pw);
        c.Auth.SetUnattended(true);
        var captured = c.Proof(Pw);
        Assert.Equal(AccessDecision.DeniedBadPassword, c.Auth.Evaluate(Encoding.UTF8.GetBytes("transcript-phien-2"), captured));
    }

    [Fact] // Brute Force: khóa 15 phút, bền vững qua lần khởi động lại, rồi tự mở.
    public void BruteForce_Locks_SurvivesRestart_ThenExpires()
    {
        using var c = new Ctx();
        c.Auth.SetPassword(null, Pw);
        c.Auth.SetUnattended(true);
        for (var i = 0; i < ClientAuthentication.MaxFailedAttempts - 1; i++)
            Assert.Equal(AccessDecision.DeniedBadPassword, c.Auth.Evaluate(Transcript, new byte[32]));
        Assert.Equal(AccessDecision.DeniedLocked, c.Auth.Evaluate(Transcript, new byte[32]));
        Assert.Equal(AccessDecision.DeniedLocked, c.Auth.Evaluate(Transcript, c.Proof(Pw)));

        var reloaded = c.Temp.Store.LoadOrCreate();
        var restarted = new ClientAuthentication(c.Temp.Store, reloaded, 1000, () => c.Now);
        Assert.Equal(AccessDecision.DeniedLocked, restarted.Evaluate(Transcript, c.Proof(Pw)));

        c.Now += ClientAuthentication.LockoutDuration + TimeSpan.FromSeconds(1);
        Assert.Equal(AccessDecision.Allowed, c.Auth.Evaluate(Transcript, c.Proof(Pw)));
    }

    [Fact]
    public void ChangePassword_RequiresCurrent_OldProofDies()
    {
        using var c = new Ctx();
        c.Auth.SetPassword(null, Pw);
        Assert.Throws<ClientValidationException>(() => c.Auth.SetPassword("sai", "Mat-Khau-Moi-12345"));
        Assert.Throws<ClientValidationException>(() => c.Auth.SetPassword(Pw, "ngan"));
        c.Auth.SetPassword(Pw, "Mat-Khau-Moi-12345");
        c.Auth.SetUnattended(true);
        Assert.Equal(AccessDecision.DeniedBadPassword, c.Auth.Evaluate(Transcript, c.Proof(Pw)));
        Assert.Equal(AccessDecision.Allowed, c.Auth.Evaluate(Transcript, c.Proof("Mat-Khau-Moi-12345")));
    }

    [Fact] // Xóa mật khẩu không được để lại Unattended không có gì bảo vệ.
    public void ClearPassword_AlsoDisablesUnattended()
    {
        using var c = new Ctx();
        c.Auth.SetPassword(null, Pw);
        c.Auth.SetUnattended(true);
        Assert.Throws<ClientValidationException>(() => c.Auth.ClearPassword("sai"));
        c.Auth.ClearPassword(Pw);
        Assert.False(c.State.UnattendedEnabled);
        Assert.Equal(AccessDecision.RequireOwnerApproval, c.Auth.Evaluate(Transcript, null));
    }
}

public class IdentityAndWireTests
{
    [Fact] // RFC 8032, test 1.
    public void Ed25519_KnownAnswerVector()
    {
        static byte[] Hex(string s) => Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();
        using var id = DeviceIdentity.FromSeed(Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));
        Assert.Equal(Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), id.PublicKey);
        Assert.Equal(Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"), id.Sign(new byte[0]));
    }

    [Fact]
    public void Ed25519_RejectsTamperingAndBadLengths()
    {
        using var id = DeviceIdentity.Generate();
        var msg = Encoding.UTF8.GetBytes("nội dung");
        var sig = id.Sign(msg);
        Assert.True(DeviceIdentity.Verify(id.PublicKey, msg, sig));
        var bad = (byte[])sig.Clone(); bad[0] ^= 1;
        Assert.False(DeviceIdentity.Verify(id.PublicKey, msg, bad));
        Assert.False(DeviceIdentity.Verify(id.PublicKey, Encoding.UTF8.GetBytes("nội dung khác"), sig));
        Assert.False(DeviceIdentity.Verify(new byte[5], msg, sig));
        Assert.False(DeviceIdentity.Verify(id.PublicKey, msg, new byte[10]));
    }

    [Fact]
    public void RemoteId_FormatAndUniqueness()
    {
        var ids = Enumerable.Range(0, 5000).Select(_ => RemoteIdGenerator.Generate()).ToList();
        Assert.All(ids, id => Assert.Matches("^[1-9][0-9]{8}$", id));
        Assert.True(ids.Distinct().Count() >= 4990);
        // Chữ số đầu phân bố gần đều trong 1-9 (kỳ vọng ~555 mỗi chữ số).
        Assert.All(ids.GroupBy(i => i[0]), g => Assert.InRange(g.Count(), 400, 720));
    }

    [Fact]
    public void Entries_RoundTrip_WithVietnamese()
    {
        var e = new PresenceEntry { RemoteId = "123456789", IsOnline = true, AccessEnabled = false, LastSeenUtc = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc), Info = new DeviceInfo { Name = "Máy Kế Toán", OperatingSystem = "Windows 10", LocalIp = "10.0.0.1" } };
        var back = Wire.ReadEntries(Wire.Entries(new[] { e }, true), out var last).Single();
        Assert.True(last);
        Assert.Equal("Máy Kế Toán", back.Info.Name);
        Assert.True(back.IsOnline);
        Assert.False(back.AccessEnabled);
        Assert.Equal(e.LastSeenUtc, back.LastSeenUtc);
    }

    [Fact] // Dữ liệu mạng rác chỉ được gây ProtocolException, không lỗi nào khác và không treo.
    public void Parser_RandomGarbage_OnlyThrowsProtocolException()
    {
        var random = new Random(12345);
        for (var i = 0; i < 3000; i++)
        {
            var b = new byte[random.Next(0, 300)];
            random.NextBytes(b);
            try { Wire.ReadEntries(b, out _); }
            catch (ProtocolException) { }
            try { Wire.Parse(b, r => r.Info()); }
            catch (ProtocolException) { }
        }
    }

    [Fact]
    public void Parser_HugeDeclaredCount_IsRejectedWithoutAllocating()
    {
        var payload = Wire.Build(w => { w.Write(true); w.Write(int.MaxValue); });
        Assert.Throws<ProtocolException>(() => Wire.ReadEntries(payload, out _));
    }
}

/// <summary>Công tắc tắt khẩn cấp của chủ máy phải nằm trọn trong vùng nhìn thấy; lỗi này từng xảy ra và test logic không phát hiện được.</summary>
public class ClientUiTests
{
    private static IEnumerable<System.Windows.Forms.Control> Walk(System.Windows.Forms.Control root)
    {
        foreach (System.Windows.Forms.Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Walk(c)) yield return d;
        }
    }

    /// <summary>Control có nằm trọn trong mọi control cha hay không.</summary>
    private static bool FullyVisibleInParents(System.Windows.Forms.Control c)
    {
        var rect = new System.Drawing.Rectangle(System.Drawing.Point.Empty, c.Size);
        for (var child = c; child.Parent != null; child = child.Parent)
        {
            rect.Offset(child.Location);
            if (!new System.Drawing.Rectangle(System.Drawing.Point.Empty, child.Parent.ClientSize).Contains(rect)) return false;
        }
        return true;
    }

    [Fact]
    public void AllButtons_AreFullyVisible()
    {
        Exception failure = null;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                RDM.Controller.Theming.Dpi.Init();
                // Chỉ lắng nghe trên loopback: không mở cổng ra mạng, không làm hiện cảnh báo tường lửa.
                Environment.SetEnvironmentVariable("RDM_SESSION_BIND", "127.0.0.1");
                using var temp = new TempStore();
                var state = temp.Store.LoadOrCreate();
                var auth = new ClientAuthentication(temp.Store, state, 1000);
                var info = DeviceInfoProvider.Collect();
                using var connection = new rdmclient::RDM.Client.ConnectionManager(temp.Store, state, info);
                state.SessionPort = 0;
                var panel = new rdmclient::RDM.Client.AllowControlPanel(temp.Store, state, auth, connection, new ActivityLog(Path.Combine(temp.Dir, "a.log")), info,
                    new rdmclient::RDM.Client.SessionManager(state, auth, () => null, () => null) { DiscoveryPort = 0 });
                // Cỡ tối thiểu của cửa sổ chính, chia đôi: nút vẫn phải nằm trọn trong khung.
                using var form = new System.Windows.Forms.Form { AutoScaleMode = System.Windows.Forms.AutoScaleMode.None, ClientSize = new System.Drawing.Size(RDM.Controller.Theming.Dpi.S(480), RDM.Controller.Theming.Dpi.S(650)) };
                form.Controls.Add(panel);
                form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                form.Location = new System.Drawing.Point(-5000, -5000);
                using var timer = new System.Windows.Forms.Timer { Interval = 500 };
                timer.Tick += (s, e) => { timer.Stop(); form.Close(); };
                var names = new List<string>();
                form.FormClosing += (s, e) =>
                {
                    foreach (var b in Walk(form).OfType<System.Windows.Forms.Button>())
                    {
                        names.Add(b.Text);
                        Assert.True(FullyVisibleInParents(b), "Nút bị cắt: " + b.Text);
                    }
                    panel.Shutdown();
                };
                timer.Start();
                System.Windows.Forms.Application.Run(form);
                foreach (var required in new[] { "Copy ID", "Change Password", "Enable Unattended Access", "Disable Remote Access", "Disconnect All", "Settings" })
                    Assert.Contains(required, names);
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "Giao diện Client bị treo");
        if (failure != null) throw new Exception(failure.ToString(), failure);
    }
}

public class ClientSupportTests
{
    [Fact]
    public void DeviceInfo_CollectsThisMachine()
    {
        var info = DeviceInfoProvider.Collect();
        Assert.Equal(Environment.MachineName, info.Name);
        Assert.StartsWith("Windows", info.OperatingSystem);
    }

    [Fact] // Bản x86 và x64 phải báo cùng một hệ điều hành; bản x86 từng đọc nhầm WOW6432Node.
    public void OsName_ComesFromNativeRegistryView_OnBothArchitectures()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var native = (string)key.GetValue("ProductName");
        var build = Environment.OSVersion.Version.Build;
        Assert.Equal(build >= 22000 ? native.Replace("Windows 10", "Windows 11") : native, DeviceInfoProvider.OsName());
    }

    [Fact] // Ghi vào khóa test riêng trong HKCU, không đụng vào danh sách khởi động thật.
    public void Startup_OptInOnly_AndRemovable()
    {
        var key = @"Software\RDM-Tests\Run-" + Guid.NewGuid().ToString("N");
        try
        {
            var s = new StartupManager(key);
            Assert.False(s.IsEnabled);
            s.Set(true, @"C:\App Thu\RDM.Client.exe");
            Assert.True(s.IsEnabled);
            using (var k = Registry.CurrentUser.OpenSubKey(key)) Assert.Equal("\"C:\\App Thu\\RDM.Client.exe\"", k.GetValue("RDM Client"));
            s.Set(false, null);
            Assert.False(s.IsEnabled);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\RDM-Tests", false); }
    }

    [Fact]
    public void ActivityLog_NewestFirst_AndRedacted()
    {
        using var t = new TempStore();
        var log = new ActivityLog(Path.Combine(t.Dir, "a.log"));
        log.Write("Sự kiện 1");
        log.Write("lỡ tay password=SieuBiMat");
        var lines = log.ReadRecent(10);
        Assert.Equal(2, lines.Length);
        Assert.Contains("password=***", lines[0]);
        Assert.DoesNotContain("SieuBiMat", File.ReadAllText(Path.Combine(t.Dir, "a.log")));
    }
}
