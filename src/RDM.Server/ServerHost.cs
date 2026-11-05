using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RDM.Shared.Network;

namespace RDM.Server;

/// <summary>Lắng nghe TCP/TLS cho thiết bị và Controller.</summary>
public sealed class ServerHost : BackgroundService
{
    private readonly RelayOptions _options;
    private readonly ConnectionHandler _handler;
    private readonly ILogger<ServerHost> _log;
    private readonly ConcurrentDictionary<Task, byte> _active = new ConcurrentDictionary<Task, byte>();

    public int ConnectionCount => _active.Count;

    public ServerHost(IOptions<RelayOptions> options, ConnectionHandler handler, ILogger<ServerHost> log)
    {
        _options = options.Value; _handler = handler; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var certificate = CertificateTools.Load(_options.CertificatePath);
        _log.LogInformation("Relay certificate, pinned key: {Pin}, expires: {Expiry:yyyy-MM-dd}", TlsConnector.PublicKeyPin(certificate), certificate.NotAfter);
        var listener = new TcpListener(IPAddress.Parse(_options.BindAddress), _options.Port);
        listener.Start(1024);
        _log.LogInformation("Relay listening on {Address}:{Port}", _options.BindAddress, _options.Port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                if (_active.Count >= _options.MaxConnections)
                {
                    _log.LogWarning("Reached the limit of {Max} connections; refusing new connections.", _options.MaxConnections);
                    tcp.Dispose();
                    continue;
                }
                tcp.NoDelay = true;
                var task = Task.Run(() => _handler.HandleAsync(tcp, certificate, ct), CancellationToken.None);
                _active[task] = 0;
                _ = task.ContinueWith(t =>
                {
                    _active.TryRemove(t, out _);
                    if (t.Exception != null) _log.LogError(t.Exception.GetBaseException(), "Connection handling error");
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            await Task.WhenAll(_active.Keys.ToArray()).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}

/// <summary>Dọn thiết bị quá hạn Heartbeat. Lớp bảo vệ thứ hai ngoài timeout đọc của từng kết nối.</summary>
public sealed class HeartbeatManager : BackgroundService
{
    private readonly RelayOptions _options;
    private readonly PresenceHub _hub;
    private readonly ILogger<HeartbeatManager> _log;

    public HeartbeatManager(IOptions<RelayOptions> options, PresenceHub hub, ILogger<HeartbeatManager> log)
    {
        _options = options.Value; _hub = hub; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        var period = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatTimeoutSeconds / 3));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(period, ct);
                foreach (var s in _hub.Sessions)
                    if (DateTime.UtcNow - new DateTime(Volatile.Read(ref s.LastHeartbeatTicks), DateTimeKind.Utc) > timeout)
                    {
                        _log.LogInformation("Device {RemoteId} missed its Heartbeat deadline.", s.RemoteId);
                        s.Channel.Dispose();
                    }
            }
        }
        catch (OperationCanceledException) { }
    }
}

public static class CertificateTools
{
    private const string PasswordVariable = "RDM_CERT_PASSWORD";

    public static X509Certificate2 Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Relay:CertificatePath is not configured. The Relay does not run without TLS.");
        return X509CertificateLoader.LoadPkcs12FromFile(path, Environment.GetEnvironmentVariable(PasswordVariable));
    }

    /// <summary>Sinh chứng thư ECDSA P-256 tự ký cho Relay nội bộ; ECDSA để Windows 7 dùng được bộ mã GCM.</summary>
    public static string Generate(string path, string hostname, int days)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(hostname, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(hostname);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(days));
        System.IO.File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, Environment.GetEnvironmentVariable(PasswordVariable)));
        return TlsConnector.PublicKeyPin(cert);
    }
}
