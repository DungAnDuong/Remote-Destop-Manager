using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using RDM.Data.Services;
using RDM.Shared.Common;
using RDM.Shared.Models;
using RDM.Shared.Security;
using Xunit;
using Xunit.Abstractions;

namespace RDM.Tests;

public class CryptoTests
{
    private readonly ITestOutputHelper _out;
    public CryptoTests(ITestOutputHelper output) => _out = output;

    private static byte[] Hex(string s) => Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();

    [Fact] // Test case 14 của tài liệu đặc tả GCM (McGrew & Viega).
    public void AesGcm_KnownAnswerVector()
    {
        var sealedData = new byte[12].Concat(Hex("cea7403d4d606b6e074ec5d3baf39d18")).Concat(Hex("d0d1c8a799996bf0265b98b5d48ab919")).ToArray();
        Assert.Equal(new byte[16], AesGcmCipher.Open(new byte[32], sealedData));
    }

    [Fact]
    public void AesGcm_RoundTrip_WithAad()
    {
        var key = CryptoRandom.GetBytes(32);
        var aad = Encoding.UTF8.GetBytes("ctx");
        var plain = Encoding.UTF8.GetBytes("dữ liệu bí mật");
        Assert.Equal(plain, AesGcmCipher.Open(key, AesGcmCipher.Seal(key, plain, aad), aad));
    }

    [Fact]
    public void AesGcm_DetectsTamperingAtEveryPosition()
    {
        var key = CryptoRandom.GetBytes(32);
        var sealedData = AesGcmCipher.Seal(key, new byte[8]);
        for (var i = 0; i < sealedData.Length; i++)
        {
            var bad = (byte[])sealedData.Clone();
            bad[i] ^= 1;
            Assert.Throws<CryptoFailureException>(() => AesGcmCipher.Open(key, bad));
        }
    }

    [Fact]
    public void AesGcm_RejectsWrongKeyWrongAadAndTruncation()
    {
        var key = CryptoRandom.GetBytes(32);
        var aad = new byte[] { 1 };
        var s = AesGcmCipher.Seal(key, new byte[8], aad);
        Assert.Throws<CryptoFailureException>(() => AesGcmCipher.Open(CryptoRandom.GetBytes(32), s, aad));
        Assert.Throws<CryptoFailureException>(() => AesGcmCipher.Open(key, s, new byte[] { 2 }));
        Assert.Throws<CryptoFailureException>(() => AesGcmCipher.Open(key, s.Take(s.Length - 1).ToArray(), aad));
    }

    [Fact]
    public void AesGcm_NonceNeverRepeatsInSample()
    {
        var key = CryptoRandom.GetBytes(32);
        var seen = new HashSet<string>();
        for (var i = 0; i < 20000; i++)
            Assert.True(seen.Add(Convert.ToBase64String(AesGcmCipher.Seal(key, new byte[1]).Take(12).ToArray())));
    }

    [Theory] // Vector PBKDF2-HMAC-SHA256 đã công bố (RFC 7914 dùng cùng thuật toán; vector từ bộ kiểm thử phổ biến).
    [InlineData(1, "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b")]
    [InlineData(4096, "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a")]
    public void Pbkdf2_KnownAnswerVectors(int iterations, string expected)
    {
        Assert.Equal(Hex(expected), PasswordHasher.Derive("password", Encoding.ASCII.GetBytes("salt"), iterations, 32));
    }

    [Fact]
    public void Password_SaltIsUnique_AndVerifies()
    {
        var h = new PasswordHasher(1000);
        var a = h.Hash("mật khẩu đúng");
        Assert.NotEqual(a, h.Hash("mật khẩu đúng"));
        Assert.True(h.Verify("mật khẩu đúng", a, out var rehash));
        Assert.False(rehash);
        Assert.False(h.Verify("mật khẩu sai", a, out _));
        Assert.False(h.Verify("x", "rác$không$hợp$lệ", out _));
    }

    [Fact]
    public void Password_FlagsRehashWhenIterationsIncreased()
    {
        var stored = new PasswordHasher(1000).Hash("abc");
        Assert.True(new PasswordHasher(2000).Verify("abc", stored, out var rehash));
        Assert.True(rehash);
    }

    [Fact] // Ghi lại chi phí thật để đánh giá ảnh hưởng tới đăng nhập.
    public void Password_DefaultIterations_Timing()
    {
        var sw = Stopwatch.StartNew();
        new PasswordHasher().Hash("đo-thời-gian");
        sw.Stop();
        _out.WriteLine($"PBKDF2 {PasswordHasher.DefaultIterations} vòng: {sw.ElapsedMilliseconds} ms, 64-bit={Environment.Is64BitProcess}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("password=abc123", "abc123")]
    [InlineData("Token: eyJhbGciOi", "eyJhbGciOi")]
    [InlineData("session_key=\"deadbeef cafe\"", "deadbeef")]
    [InlineData("mật khẩu=BiMat", "BiMat")]
    public void LogRedactor_RemovesSecrets(string input, string secret) => Assert.DoesNotContain(secret, LogRedactor.Redact(input));

    [Theory]
    [InlineData("123 456 789", "123456789")]
    [InlineData("123-456-789", "123456789")]
    [InlineData("12345678", null)]
    [InlineData("12345678a", null)]
    [InlineData("'; DROP--", null)]
    public void RemoteId_Normalize(string input, string expected) => Assert.Equal(expected, RemoteIdFormat.Normalize(input));
}

public class KeyManagerTests
{
    [Fact]
    public void Dpapi_RealRoundTrip_BoundToPurposeAndVersion()
    {
        var p = new DpapiKeyProtector();
        var key = CryptoRandom.GetBytes(32);
        var wrapped = p.Protect(key, KeyPurpose.Database, 1);
        Assert.False(wrapped.Length == key.Length);
        Assert.Equal(key, p.Unprotect(wrapped, KeyPurpose.Database, 1));
        Assert.Throws<CryptoFailureException>(() => p.Unprotect(wrapped, KeyPurpose.Database, 2));
        Assert.Throws<CryptoFailureException>(() => p.Unprotect(wrapped, KeyPurpose.Authentication, 1));
        wrapped[wrapped.Length / 2] ^= 1;
        Assert.Throws<CryptoFailureException>(() => p.Unprotect(wrapped, KeyPurpose.Database, 1));
    }

    [Fact]
    public void KeysAreSeparatedByPurpose_AndCreatedOnce()
    {
        using var db = new TestDb();
        var a = db.Rt.Keys.GetCurrentKey(KeyPurpose.Database, out var v1);
        var b = db.Rt.Keys.GetCurrentKey(KeyPurpose.Authentication, out _);
        Assert.NotEqual(a, b);
        Assert.Equal(a, db.Rt.Keys.GetCurrentKey(KeyPurpose.Database, out var v2));
        Assert.Equal(v1, v2);
    }

    [Fact]
    public void SessionAndFileKeys_CannotBePersisted()
    {
        using var db = new TestDb();
        Assert.Throws<ArgumentException>(() => db.Rt.Keys.GetCurrentKey(KeyPurpose.Session, out _));
        Assert.Throws<ArgumentException>(() => db.Rt.Keys.PrepareRotation(KeyPurpose.FileTransfer, out _));
    }

    [Fact]
    public void ListKeys_DoesNotExposeWrappedKeyMaterial()
    {
        using var db = new TestDb();
        db.Rt.Keys.GetCurrentKey(KeyPurpose.Database, out _);
        Assert.All(db.Rt.Keys.ListKeys(), k => Assert.Null(k.WrappedKey));
    }

    [Fact]
    public void DestroyedCallerCopy_DoesNotAffectManager()
    {
        using var db = new TestDb();
        var a = db.Rt.Keys.GetCurrentKey(KeyPurpose.Database, out _);
        var copy = (byte[])a.Clone();
        KeyManager.Destroy(a);
        Assert.Equal(copy, db.Rt.Keys.GetCurrentKey(KeyPurpose.Database, out _));
    }
}

public class FieldProtectorTests
{
    [Fact]
    public void CiphertextCannotBeMovedBetweenRows()
    {
        using var db = new TestDb();
        var stored = db.Rt.Protector.Encrypt("pw", "Clients|EncryptedCredential|A");
        Assert.Equal("pw", db.Rt.Protector.Decrypt(stored, "Clients|EncryptedCredential|A"));
        Assert.Throws<CryptoFailureException>(() => db.Rt.Protector.Decrypt(stored, "Clients|EncryptedCredential|B"));
    }

    [Theory]
    [InlineData("rác")]
    [InlineData("v1.1.@@@")]
    [InlineData("v9.1.AAAA")]
    [InlineData("v1.999.AAAA")]
    public void RejectsMalformedValues(string value)
    {
        using var db = new TestDb();
        Assert.Throws<CryptoFailureException>(() => db.Rt.Protector.Decrypt(value, "x"));
    }
}

/// <summary>Kiểm thử bảo mật Phần 8.9 cho phạm vi đã lập trình.</summary>
public class AttackTests
{
    [Fact] // Database Theft
    public void DatabaseTheft_RawFileContainsNoSecrets()
    {
        using var db = new TestDb().LoginAdmin();
        db.Rt.Users.Create("nguoidungbimat", "Mat-Khau-Nguoi-Dung-9", "HoTenBiMatXYZ", "email.bimat@example.test", UserRole.Operator);
        db.AddClient("PC-01", "123456789", "CredentialBiMatQQQ");
        db.Rt.Settings.Set(SettingKeys.RelayToken, "RelayTokenBiMatWWW", true);
        var raw = db.RawDatabaseAsLatin1();
        foreach (var secret in new[] { "CredentialBiMatQQQ", "RelayTokenBiMatWWW", "Mat-Khau-Nguoi-Dung-9", TestDb.AdminPassword, "pbkdf2-sha256", "HoTenBiMatXYZ", "email.bimat@example.test" })
            Assert.DoesNotContain(secret, raw);
    }

    [Fact] // Database Theft: có DB nhưng không có DPAPI của máy gốc thì không mở được khóa.
    public void DatabaseTheft_OtherMachineCannotUnwrapKeys()
    {
        using var db = new TestDb(new FakeMachineProtector(1)).LoginAdmin();
        var id = db.AddClient("PC-01", "123456789", "bi-mat");
        SQLiteConnection.ClearAllPools();
        using var attacker = new RDM.Data.RdmRuntime(db.DbPath, 1000, new FakeMachineProtector(2));
        Assert.ThrowsAny<Exception>(() => attacker.Users.Login("admin", TestDb.AdminPassword));
    }

    [Theory] // SQL Injection
    [InlineData("'; DROP TABLE Clients; --")]
    [InlineData("' OR '1'='1")]
    [InlineData("\" OR 1=1 --")]
    [InlineData("%")]
    [InlineData("_")]
    public void SqlInjection_SearchTreatsInputAsLiteral(string payload)
    {
        using var db = new TestDb().LoginAdmin();
        db.AddClient("PC-01", "123456789");
        Assert.Empty(db.Rt.Clients.Query(payload, null, false));
        Assert.Equal(1, db.Rt.Clients.Count());
    }

    [Fact]
    public void SqlInjection_StoredAsLiteralInFields()
    {
        using var db = new TestDb().LoginAdmin();
        const string payload = "x'); DROP TABLE Users; --";
        db.AddClient(payload, "123456789");
        db.Rt.Groups.Add(payload, payload);
        Assert.Equal(payload, db.Rt.Clients.Query(null, null, false).Single().DeviceName);
        Assert.Equal(LoginOutcome.Success, db.Rt.Users.Login("admin", TestDb.AdminPassword));
    }

    [Fact] // Brute Force
    public void BruteForce_LocksEvenForCorrectPasswordAfterFiveFailures()
    {
        using var db = new TestDb().LoginAdmin();
        db.Rt.Users.Logout();
        for (var i = 0; i < UserService.MaxFailedAttempts; i++)
            Assert.Equal(LoginOutcome.Failed, db.Rt.Users.Login("admin", "sai-mat-khau-" + i));
        Assert.Equal(LoginOutcome.Failed, db.Rt.Users.Login("admin", TestDb.AdminPassword));
        Assert.Null(db.Rt.Session.Current);
        Assert.Contains(db.Rt.AuditLogs.Query(null, 50), a => a.EventType == AuditEvents.LoginFailed && a.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public void Login_FewerThanLimitThenSuccess_ResetsCounter()
    {
        using var db = new TestDb().LoginAdmin();
        db.Rt.Users.Logout();
        for (var i = 0; i < UserService.MaxFailedAttempts - 1; i++) db.Rt.Users.Login("admin", "sai");
        Assert.Equal(LoginOutcome.Success, db.Rt.Users.Login("admin", TestDb.AdminPassword));
        Assert.Equal(0, db.Rt.Session.Current.FailedAttempts);
    }

    [Fact] // Không ghi chuỗi đã nhập: người dùng có thể gõ nhầm mật khẩu vào ô tên.
    public void Audit_UnknownUserAttempt_DoesNotRecordTypedText()
    {
        using var db = new TestDb();
        db.Rt.Users.Login("MatKhauGoNham123", "x");
        var e = db.Rt.AuditLogs.Query(null, 10).Single(a => a.EventType == AuditEvents.LoginFailed);
        Assert.Null(e.Username);
        Assert.DoesNotContain("MatKhauGoNham123", db.RawDatabaseAsLatin1(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_DetailsAreRedacted()
    {
        using var db = new TestDb();
        db.Rt.Audit.Write("Test", "u", details: "lỡ tay password=SieuBiMat777");
        Assert.DoesNotContain("SieuBiMat777", db.Rt.AuditLogs.Query(null, 10).Single(a => a.EventType == "Test").Details);
    }

    [Fact] // Unauthorized Access: gọi trực tiếp Service, không qua UI.
    public void Unauthorized_ViewerBlockedAtServiceLayer_AndAudited()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.AddClient("PC-01", "123456789", "bi-mat");
        db.LoginAs("viewer", "Viewer-Password-1", UserRole.Viewer);
        var rt = db.Rt;
        Assert.Throws<UnauthorizedAccessException>(() => rt.Clients.UseCredential(id));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Clients.Delete(id));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Users.GetAll());
        Assert.Throws<UnauthorizedAccessException>(() => rt.KeyRotation.Rotate(KeyPurpose.Database));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Backup.Backup(Path.Combine(db.Dir, "x.rdmbak"), "Backup-Password-1"));
        Assert.Throws<UnauthorizedAccessException>(() => rt.Settings.Set(SettingKeys.RelayHost, "evil"));
        Assert.Contains(rt.AuditLogs.Query(null, 50), a => a.EventType == AuditEvents.AccessDenied);
        Assert.False(File.Exists(Path.Combine(db.Dir, "x.rdmbak")));
    }

    [Fact]
    public void Unauthorized_NotLoggedIn_IsDenied()
    {
        using var db = new TestDb();
        Assert.Throws<UnauthorizedAccessException>(() => db.AddClient("PC", "123456789"));
    }

    [Fact]
    public void Operator_CanManageClients_ButNotUsersOrSecurity()
    {
        using var db = new TestDb().LoginAdmin();
        db.LoginAs("oper", "Operator-Password-1", UserRole.Operator);
        var id = db.AddClient("PC-01", "123456789", "bi-mat");
        Assert.Equal("bi-mat", db.Rt.Clients.UseCredential(id));
        Assert.Throws<UnauthorizedAccessException>(() => db.Rt.Users.GetAll());
        Assert.Throws<UnauthorizedAccessException>(() => db.Rt.KeyRotation.Rotate(KeyPurpose.Database));
    }
}

public class UserServiceTests
{
    [Fact]
    public void NoDefaultAccount_AndFirstAdminOnlyOnce()
    {
        using var db = new TestDb();
        Assert.False(db.Rt.Users.HasAnyUser());
        Assert.Equal(LoginOutcome.Failed, db.Rt.Users.Login("admin", "admin"));
        db.Rt.Users.CreateFirstAdministrator("admin", TestDb.AdminPassword, null);
        Assert.Throws<ValidationException>(() => db.Rt.Users.CreateFirstAdministrator("hacker", TestDb.AdminPassword, null));
    }

    [Theory]
    [InlineData("ngan")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("administrator")]
    public void WeakPasswordsRejected(string password)
    {
        using var db = new TestDb();
        Assert.Throws<ValidationException>(() => db.Rt.Users.CreateFirstAdministrator("administrator", password, null));
    }

    [Fact]
    public void LastAdministrator_IsProtected()
    {
        using var db = new TestDb().LoginAdmin();
        var me = db.Rt.Session.Current.Id;
        Assert.Throws<ValidationException>(() => db.Rt.Users.Update(me, null, null, UserRole.Viewer, true));
        Assert.Throws<ValidationException>(() => db.Rt.Users.Update(me, null, null, UserRole.Administrator, false));
        Assert.Throws<ValidationException>(() => db.Rt.Users.Delete(me));
    }

    [Fact]
    public void ChangePassword_RequiresCurrentPassword()
    {
        using var db = new TestDb().LoginAdmin();
        Assert.Throws<ValidationException>(() => db.Rt.Users.ChangeOwnPassword("sai", "Mat-Khau-Moi-12345"));
        db.Rt.Users.ChangeOwnPassword(TestDb.AdminPassword, "Mat-Khau-Moi-12345");
        db.Rt.Users.Logout();
        Assert.Equal(LoginOutcome.Failed, db.Rt.Users.Login("admin", TestDb.AdminPassword));
        Assert.Equal(LoginOutcome.Success, db.Rt.Users.Login("admin", "Mat-Khau-Moi-12345"));
    }

    [Fact]
    public void ResetPassword_ForcesChange_DisabledUserCannotLogin()
    {
        using var db = new TestDb().LoginAdmin();
        db.Rt.Users.Create("nv01", "Mat-Khau-Ban-Dau-1", "Nhân Viên", "nv@example.test", UserRole.Operator);
        var u = db.Rt.Users.GetAll().Single(x => x.Username == "nv01");
        Assert.Equal("Nhân Viên", u.FullName);
        Assert.True(u.MustChangePassword);
        db.Rt.Users.Update(u.Id, u.FullName, u.Email, u.Role, false);
        Assert.Equal(LoginOutcome.Failed, db.Rt.Users.Login("nv01", "Mat-Khau-Ban-Dau-1"));
    }

    [Fact]
    public void Username_IsCaseInsensitive_AndDuplicateRejected()
    {
        using var db = new TestDb().LoginAdmin();
        Assert.Throws<ValidationException>(() => db.Rt.Users.Create("ADMIN", "Mat-Khau-Khac-1234", null, null, UserRole.Viewer));
        db.Rt.Users.Logout();
        Assert.Equal(LoginOutcome.Success, db.Rt.Users.Login(" Admin ", TestDb.AdminPassword));
    }
}

public class KeyRotationTests
{
    [Fact] // Key Rotation
    public void RotateDatabaseKey_AllDataStillReadable_OldKeyRetired()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.AddClient("PC-01", "123456789", "bi-mat-1");
        db.Rt.Settings.Set(SettingKeys.RelayToken, "token-1", true);

        var newVersion = db.Rt.KeyRotation.Rotate(KeyPurpose.Database);

        Assert.Equal(2, newVersion);
        Assert.Equal("bi-mat-1", db.Rt.Clients.UseCredential(id));
        Assert.Equal("token-1", db.Rt.Settings.Get(SettingKeys.RelayToken));
        Assert.Equal("Quản Trị Viên Bí Mật", db.Rt.Users.GetAll().Single().FullName);
        var keys = db.Rt.Keys.ListKeys().Where(k => k.Purpose == KeyPurpose.Database).ToList();
        Assert.Equal(KeyStatus.Retired, keys.Single(k => k.Version == 1).Status);
        Assert.Equal(KeyStatus.Active, keys.Single(k => k.Version == 2).Status);
        db.Rt.Users.Logout();
        Assert.Equal(LoginOutcome.Success, db.Rt.Users.Login("admin", TestDb.AdminPassword));
    }

    [Fact] // Sau khi xoay, có thể thu hồi khóa cũ vì không còn dữ liệu nào phụ thuộc.
    public void RevokeOldKey_DataRemainsReadable_OldCiphertextIsDead()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.AddClient("PC-01", "123456789", "bi-mat-1");
        var oldCipher = db.Rt.Protector.Encrypt("cu", "ctx");
        db.Rt.KeyRotation.Rotate(KeyPurpose.Database);
        db.Rt.KeyRotation.Revoke(KeyPurpose.Database, 1);
        Assert.Equal("bi-mat-1", db.Rt.Clients.UseCredential(id));
        Assert.Throws<CryptoFailureException>(() => db.Rt.Protector.Decrypt(oldCipher, "ctx"));
    }

    [Fact]
    public void CannotRevokeActiveKey()
    {
        using var db = new TestDb().LoginAdmin();
        var active = db.Rt.Keys.ListKeys().Single(k => k.Purpose == KeyPurpose.Database && k.Status == KeyStatus.Active);
        Assert.Throws<InvalidOperationException>(() => db.Rt.KeyRotation.Revoke(KeyPurpose.Database, active.Version));
    }

    [Fact] // Hỏng giữa chừng phải rollback hoàn toàn.
    public void Rotation_FailureMidway_RollsBackEverything()
    {
        using var db = new TestDb().LoginAdmin();
        var good = db.AddClient("PC-01", "111111111", "bi-mat-tot");
        var bad = db.AddClient("PC-02", "222222222", "bi-mat-hong");
        using (var c = db.Rt.Context.Open())
        using (var cmd = new SQLiteCommand("UPDATE Clients SET EncryptedCredential='v1.1.AAAA' WHERE ID=" + bad, c))
            cmd.ExecuteNonQuery();

        Assert.Throws<CryptoFailureException>(() => db.Rt.KeyRotation.Rotate(KeyPurpose.Database));

        var keys = db.Rt.Keys.ListKeys().Where(k => k.Purpose == KeyPurpose.Database).ToList();
        Assert.Single(keys);
        Assert.Equal(KeyStatus.Active, keys[0].Status);
        Assert.Equal("bi-mat-tot", db.Rt.Clients.UseCredential(good));
    }

    [Fact]
    public void RotateAuthenticationKey_LoginStillWorks()
    {
        using var db = new TestDb().LoginAdmin();
        db.Rt.KeyRotation.Rotate(KeyPurpose.Authentication);
        db.Rt.Users.Logout();
        Assert.Equal(LoginOutcome.Success, db.Rt.Users.Login("admin", TestDb.AdminPassword));
    }

    [Fact]
    public void Credential_RevokeRemovesIt()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.AddClient("PC-01", "123456789", "bi-mat");
        db.Rt.Clients.RevokeCredential(id);
        Assert.Null(db.Rt.Clients.UseCredential(id));
        Assert.False(db.Rt.Clients.Query(null, null, false).Single().HasCredential);
    }
}

public class BackupTests
{
    private const string Pw = "Mat-Khau-Backup-1";

    [Fact] // Backup/Restore Security
    public void BackupFile_ContainsNoReadableSecrets()
    {
        using var db = new TestDb().LoginAdmin();
        db.AddClient("TenThietBiNhinThayDuoc", "123456789", "CredentialBiMatQQQ");
        var file = Path.Combine(db.Dir, "a.rdmbak");
        db.Rt.Backup.Backup(file, Pw);
        var raw = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(file));
        // File backup được mã hóa toàn bộ nên cả metadata cũng không đọc được.
        foreach (var s in new[] { "CredentialBiMatQQQ", "TenThietBiNhinThayDuoc", "SQLite format", Pw })
            Assert.DoesNotContain(s, raw);
    }

    [Fact]
    public void Restore_RevertsToBackedUpState()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.AddClient("PC-01", "111111111", "bi-mat");
        var file = Path.Combine(db.Dir, "a.rdmbak");
        db.Rt.Backup.Backup(file, Pw);
        db.AddClient("PC-MOI", "222222222");
        db.Rt.Clients.SetCredential(id, "da-doi");

        db.Rt.Backup.Restore(file, Pw);

        Assert.Equal(1, db.Rt.Clients.Count());
        Assert.Equal("bi-mat", db.Rt.Clients.UseCredential(id));
    }

    [Fact]
    public void Restore_WrongPassword_LeavesDatabaseUntouched()
    {
        using var db = new TestDb().LoginAdmin();
        var file = Path.Combine(db.Dir, "a.rdmbak");
        db.Rt.Backup.Backup(file, Pw);
        db.AddClient("PC-SAU-BACKUP", "222222222");
        Assert.Throws<CryptoFailureException>(() => db.Rt.Backup.Restore(file, "Sai-Mat-Khau-Backup"));
        Assert.Equal(1, db.Rt.Clients.Count());
    }

    [Theory] // Lật 1 bit ở header và ở thân file
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(-5)]
    public void Restore_TamperedFile_IsRejected(int position)
    {
        using var db = new TestDb().LoginAdmin();
        var file = Path.Combine(db.Dir, "a.rdmbak");
        db.Rt.Backup.Backup(file, Pw);
        var bytes = File.ReadAllBytes(file);
        bytes[position >= 0 ? position : bytes.Length + position] ^= 1;
        File.WriteAllBytes(file, bytes);
        Assert.ThrowsAny<Exception>(() => db.Rt.Backup.Restore(file, Pw));
        Assert.Equal(0, db.Rt.Clients.Count());
    }

    [Fact]
    public void Restore_GarbageFile_IsRejected()
    {
        using var db = new TestDb().LoginAdmin();
        var file = Path.Combine(db.Dir, "rac.rdmbak");
        File.WriteAllBytes(file, new byte[500]);
        Assert.Throws<InvalidDataException>(() => db.Rt.Backup.Restore(file, Pw));
    }

    [Fact] // Chuyển sang máy khác: khóa phải được bọc lại bằng DPAPI của máy mới.
    public void Restore_OnAnotherMachine_RewrapsKeys()
    {
        string file;
        long id;
        var keep = Path.Combine(Path.GetTempPath(), "rdm-" + Guid.NewGuid().ToString("N") + ".rdmbak");
        using (var a = new TestDb(new FakeMachineProtector(1)).LoginAdmin())
        {
            id = a.AddClient("PC-01", "123456789", "bi-mat-may-a");
            a.Rt.Backup.Backup(keep, Pw);
            file = keep;
        }
        try
        {
            using var b = new TestDb(new FakeMachineProtector(2)).LoginAdmin();
            b.Rt.Backup.Restore(file, Pw);
            b.Rt.Keys.ClearCache();
            Assert.Equal(LoginOutcome.Success, b.Rt.Users.Login("admin", TestDb.AdminPassword));
            Assert.Equal("bi-mat-may-a", b.Rt.Clients.UseCredential(id));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Backup_WeakPassword_Rejected()
    {
        using var db = new TestDb().LoginAdmin();
        Assert.Throws<ValidationException>(() => db.Rt.Backup.Backup(Path.Combine(db.Dir, "x.rdmbak"), "ngan"));
    }
}
