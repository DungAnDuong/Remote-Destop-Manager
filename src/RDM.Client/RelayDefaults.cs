using System;
using System.IO;
using System.Reflection;
using System.Text;
using RDM.Shared.Common;
using RDM.Shared.Protocol;

namespace RDM.Client;

/// <summary>
/// Cấu hình máy chủ đóng gói sẵn trong bản phát hành, để người dùng cài xong là kết nối được.
/// File nguồn deploy\relay\client-defaults.txt do build\relay-init.ps1 sinh và không đưa vào git;
/// thiếu file thì bản build không ở chế độ Managed và vẫn có ô cấu hình Relay.
/// </summary>
public sealed class RelayDefaults
{
    public const string ResourceName = "RDM.Client.relay-defaults.txt";
    public const string OptOutVariable = "RDM_NO_RELAY_DEFAULTS";

    public string Host { get; private set; }
    public int Port { get; private set; } = ProtocolInfo.DefaultRelayPort;
    public string Pin { get; private set; }
    public string EnrollmentToken { get; private set; }

    /// <summary>Mỗi dòng dạng key=value. Sai định dạng thì báo lỗi để lộ ra ngay khi kiểm tra bản build, không âm thầm bỏ qua.</summary>
    public static RelayDefaults Parse(string text)
    {
        var d = new RelayDefaults();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new InvalidDataException("Invalid defaults line.");
            var value = line.Substring(eq + 1).Trim();
            switch (line.Substring(0, eq).Trim())
            {
                case "host": d.Host = value; break;
                case "port":
                    if (!int.TryParse(value, out var port) || port < 1 || port > 65535) throw new InvalidDataException("Invalid defaults port.");
                    d.Port = port;
                    break;
                case "pin": d.Pin = value; break;
                case "enrollment": d.EnrollmentToken = value; break;
                default: throw new InvalidDataException("Unknown defaults key.");
            }
        }
        if (string.IsNullOrEmpty(d.Host)) throw new InvalidDataException("Defaults have no host.");
        return d;
    }

    /// <summary>Đọc cấu hình đóng gói trong RDM.Client.dll; bản build không có thì trả về null.</summary>
    public static RelayDefaults LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Luôn dùng cấu hình đóng gói: bản Managed không có ô sửa, nên giá trị cũ sai sẽ không còn cách nào chữa,
    /// và bản cài mới đổi được máy chủ cho mọi máy. Trả về true nếu trạng thái đã đổi.
    /// </summary>
    public bool ApplyTo(ClientState state)
    {
        var pin = string.IsNullOrEmpty(Pin) ? null : Pin;
        var token = string.IsNullOrEmpty(EnrollmentToken) ? null : EnrollmentToken;
        if (state.RelayHost == Host && state.RelayPort == Port && state.RelayPin == pin && state.EnrollmentToken == token) return false;
        state.RelayHost = Host;
        state.RelayPort = Port;
        state.RelayPin = pin;
        state.EnrollmentToken = token;
        return true;
    }

    /// <summary>Gọi lúc khởi động. Script kiểm thử đặt RDM_NO_RELAY_DEFAULTS=1 để không bao giờ chạm vào máy chủ thật.</summary>
    public static void ApplyEmbedded(ClientStateStore store, ClientState state)
    {
        if (Environment.GetEnvironmentVariable(OptOutVariable) == "1") return;
        var defaults = LoadEmbedded();
        if (defaults == null) return;
        ProductMode.Managed = true;
        if (defaults.ApplyTo(state)) store.Save(state);
    }
}
