using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Client;

/// <summary>Trạng thái bền vững của Client. Toàn bộ nằm trong một file được bọc DPAPI.</summary>
public sealed class ClientState
{
    public string RemoteId { get; set; }
    public byte[] IdentitySeed { get; set; }
    public bool RegisteredOnce { get; set; }
    public string AccessPasswordHash { get; set; }
    public bool AccessEnabled { get; set; } = true;
    public bool UnattendedEnabled { get; set; }
    public string RelayHost { get; set; }
    public int RelayPort { get; set; } = ProtocolInfo.DefaultRelayPort;
    public int SessionPort { get; set; } = SessionInfo.DefaultPort;
    public string RelayPin { get; set; }
    public string EnrollmentToken { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
}

public sealed class ClientStateException : Exception
{
    public ClientStateException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Đọc và ghi file trạng thái. DPAPI CurrentUser vừa mã hóa vừa phát hiện sửa đổi.
/// Không tự tạo lại khi đọc lỗi: làm vậy sẽ âm thầm đổi Remote ID và khóa định danh.
/// Giai đoạn 4 thêm Windows Service sẽ cần phạm vi LocalMachine cho chế độ Unattended.
/// </summary>
public sealed class ClientStateStore
{
    private const int FormatVersion = 1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RDM|client-state|v1");
    private readonly object _gate = new object();

    public string Path { get; }
    public ClientStateStore(string path) => Path = path;

    public static string DefaultPath()
    {
        var overrideDir = Environment.GetEnvironmentVariable("RDM_CLIENT_DATA_DIR");
        var dir = !string.IsNullOrWhiteSpace(overrideDir) ? overrideDir
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDM", "Client");
        return System.IO.Path.Combine(dir, "client-state.dat");
    }

    /// <summary>Đọc trạng thái; lần đầu thì sinh Remote ID và khóa định danh mới.</summary>
    public ClientState LoadOrCreate()
    {
        lock (_gate)
        {
            if (File.Exists(Path)) return Read();
            var identity = DeviceIdentity.Generate();
            var state = new ClientState { RemoteId = RemoteIdGenerator.Generate(), IdentitySeed = identity.ExportSeed() };
            identity.Dispose();
            Save(state);
            return state;
        }
    }

    private ClientState Read()
    {
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                using var r = new BinaryReader(new MemoryStream(plain), Encoding.UTF8);
                if (r.ReadInt32() != FormatVersion) throw new InvalidDataException("Unsupported file version.");
                var state = new ClientState { RemoteId = r.ReadString(), IdentitySeed = r.ReadBytes(r.ReadInt32()), RegisteredOnce = r.ReadBoolean() };
                state.AccessPasswordHash = NullIfEmpty(r.ReadString());
                state.AccessEnabled = r.ReadBoolean();
                state.UnattendedEnabled = r.ReadBoolean();
                state.RelayHost = NullIfEmpty(r.ReadString());
                state.RelayPort = r.ReadInt32();
                state.RelayPin = NullIfEmpty(r.ReadString());
                state.EnrollmentToken = NullIfEmpty(r.ReadString());
                state.FailedAttempts = r.ReadInt32();
                var locked = r.ReadInt64();
                state.LockedUntilUtc = locked == 0 ? (DateTime?)null : new DateTime(locked, DateTimeKind.Utc);
                // File từ bản trước chưa có cổng phiên: dùng mặc định.
                if (r.BaseStream.Position < r.BaseStream.Length) state.SessionPort = r.ReadInt32();
                // Bản 0.6.1 và 0.6.2 ghi thêm một byte cờ ở cuối, không còn dùng: bỏ qua.
                return state;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        catch (Exception ex) when (ex is CryptographicException || ex is EndOfStreamException || ex is InvalidDataException)
        {
            throw new ClientStateException("The state file could not be read: it belongs to another Windows account, or it was modified.", ex);
        }
    }

    /// <summary>Ghi nguyên tử: mất điện giữa chừng không làm hỏng file đang có.</summary>
    public void Save(ClientState s)
    {
        lock (_gate)
        {
            byte[] plain;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(FormatVersion);
                w.Write(s.RemoteId); w.Write(s.IdentitySeed.Length); w.Write(s.IdentitySeed); w.Write(s.RegisteredOnce);
                w.Write(s.AccessPasswordHash ?? ""); w.Write(s.AccessEnabled); w.Write(s.UnattendedEnabled);
                w.Write(s.RelayHost ?? ""); w.Write(s.RelayPort); w.Write(s.RelayPin ?? ""); w.Write(s.EnrollmentToken ?? "");
                w.Write(s.FailedAttempts); w.Write(s.LockedUntilUtc?.Ticks ?? 0L); w.Write(s.SessionPort);
                w.Flush();
                plain = ms.ToArray();
            }
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                var temp = Path + ".tmp";
                File.WriteAllBytes(temp, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
                if (File.Exists(Path)) File.Replace(temp, Path, null); else File.Move(temp, Path);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
    }

    /// <summary>Xóa khi chủ máy xác nhận đặt lại. Remote ID và khóa định danh sẽ đổi.</summary>
    public void Delete()
    {
        lock (_gate) { if (File.Exists(Path)) File.Delete(Path); }
    }

    private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
