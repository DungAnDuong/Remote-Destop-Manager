using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RDM.App;

/// <summary>
/// Lưu thông tin đăng nhập quản lý khi người dùng chọn "Remember password".
/// Bọc DPAPI theo tài khoản Windows hiện tại: tài khoản khác hoặc máy khác không giải mã được.
/// </summary>
internal static class RememberStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RDM|login-remember|v1");

    private static string PathFor(string dataDir) => Path.Combine(dataDir, "login-remember.dat");

    public static (string user, string pass)? Load(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var text = Encoding.UTF8.GetString(plain);
                var nl = text.IndexOf('\n');
                return nl < 0 ? ((string, string)?)null : (text.Substring(0, nl), text.Substring(nl + 1));
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string dataDir, string user, string pass)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var plain = Encoding.UTF8.GetBytes((user ?? string.Empty) + "\n" + (pass ?? string.Empty));
            try { File.WriteAllBytes(PathFor(dataDir), ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
    }

    public static void Clear(string dataDir)
    {
        try { var p = PathFor(dataDir); if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
    }
}
