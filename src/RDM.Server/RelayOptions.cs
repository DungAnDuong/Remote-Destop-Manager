using RDM.Shared.Protocol;

namespace RDM.Server;

/// <summary>Cấu hình mục "Relay" (appsettings.json hoặc biến môi trường Relay__*).</summary>
public sealed class RelayOptions
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = ProtocolInfo.DefaultRelayPort;

    /// <summary>File PFX. Mật khẩu lấy từ biến môi trường RDM_CERT_PASSWORD, không ghi trong file cấu hình.</summary>
    public string CertificatePath { get; set; }

    /// <summary>SHA-256 (hex) của token Controller. Để trống thì mọi Controller đều bị từ chối.</summary>
    public string ControllerTokenSha256 { get; set; }

    /// <summary>SHA-256 (hex) của mã đăng ký thiết bị mới. Để trống là ai cũng đăng ký được thiết bị mới.</summary>
    public string EnrollmentTokenSha256 { get; set; }

    /// <summary>Mặc định ở ProgramData: thư mục cài trong Program Files không ghi được, và gỡ cài đặt không được làm mất registry thiết bị.</summary>
    public string DatabasePath { get; set; } = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "RDM", "Relay", "rdm-relay.db");
    public int MaxConnections { get; set; } = 10000;
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    public int HeartbeatTimeoutSeconds { get; set; } = 45;
    public int HandshakeTimeoutSeconds { get; set; } = 10;
    public int MaxAuthFailures { get; set; } = 20;
    public int AuthBlockMinutes { get; set; } = 15;
    public int MaxRelaySessions { get; set; } = 1000;
    public int TicketSeconds { get; set; } = 30;
    public int RelayIdleSeconds { get; set; } = 120;
    public int SessionRequestsPerMinute { get; set; } = 30;
}
