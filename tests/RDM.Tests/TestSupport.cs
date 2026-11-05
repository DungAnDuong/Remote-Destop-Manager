using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using RDM.Data;
using RDM.Shared.Models;
using RDM.Shared.Security;
using SQLiteContext = RDM.Data.SQLiteContext;

namespace RDM.Tests;

/// <summary>
/// Chụp màn hình và SendInput thật: cần một phiên Windows đang có màn hình, và chuột sẽ di chuyển, nên tách riêng.
/// Bộ test thường không chạy nhóm này; chạy có chủ đích bằng build\real-desktop-test.ps1.
/// </summary>
public static class TestCategories
{
    public const string RealDesktop = "RealDesktop";
}

/// <summary>Database tạm cho mỗi test. Số vòng PBKDF2 thấp để test chạy nhanh; riêng test tốc độ dùng giá trị thật.</summary>
public sealed class TestDb : IDisposable
{
    public const string AdminPassword = "Admin-Pass-Secret-1";
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "rdm-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, SQLiteContext.DatabaseFileName);
    public RdmRuntime Rt { get; private set; }

    public TestDb(IKeyProtector protector = null)
    {
        Directory.CreateDirectory(Dir);
        Rt = new RdmRuntime(DbPath, 1000, protector);
    }

    public TestDb LoginAdmin()
    {
        Rt.Users.CreateFirstAdministrator("admin", AdminPassword, "Quản Trị Viên Bí Mật");
        Rt.Users.Login("admin", AdminPassword);
        return this;
    }

    public void LoginAs(string username, string password, UserRole role)
    {
        Rt.Users.Create(username, password, null, null, role);
        Rt.Users.Logout();
        Rt.Users.Login(username, password);
    }

    public long AddClient(string name, string remoteId, string credential = null) =>
        Rt.Clients.Add(new ClientDevice { DeviceName = name, RemoteId = remoteId, IsEnabled = true }, credential, null);

    /// <summary>Tệp DB thô cùng WAL, như kẻ trộm file nhìn thấy.</summary>
    public string RawDatabaseAsLatin1()
    {
        SQLiteConnection.ClearAllPools();
        // SQLite có thể gộp WAL vào file chính rồi xóa WAL ngay giữa lúc đang đọc. Khi đó đọc lại từ đầu,
        // không bỏ qua file đã mất: nội dung của nó có thể vừa chuyển sang file đã đọc qua, và test bảo mật sẽ quét thiếu.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var f in Directory.GetFiles(Dir, SQLiteContext.DatabaseFileName + "*"))
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var b = new byte[fs.Length];
                        fs.Read(b, 0, b.Length);
                        sb.Append(Encoding.GetEncoding(28591).GetString(b)).Append(Encoding.Unicode.GetString(b));
                    }
                return sb.ToString();
            }
            catch (FileNotFoundException) when (attempt < 10)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        Rt.Dispose();
        SQLiteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Giả lập DPAPI của một máy khác trong test, không dùng trong sản phẩm.</summary>
public sealed class FakeMachineProtector : IKeyProtector
{
    private readonly byte[] _secret;
    public FakeMachineProtector(byte seed) => _secret = Enumerable.Repeat(seed, 32).ToArray();
    public string Scheme => "FAKE";
    public byte[] Protect(byte[] key, KeyPurpose p, int v) => AesGcmCipher.Seal(_secret, key, BitConverter.GetBytes((int)p * 1000 + v));
    public byte[] Unprotect(byte[] w, KeyPurpose p, int v) => AesGcmCipher.Open(_secret, w, BitConverter.GetBytes((int)p * 1000 + v));
}
