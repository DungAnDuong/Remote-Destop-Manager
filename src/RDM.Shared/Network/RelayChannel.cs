using System;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using RDM.Shared.Common;
using RDM.Shared.Protocol;

namespace RDM.Shared.Network;

/// <summary>Luồng khung tin: độ dài (4 byte, big-endian) | loại (1 byte) | nội dung. Dùng chung cho Client, Controller, Server.</summary>
public sealed class FramedStream : IDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable _owner;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly byte[] _header = new byte[5];
    private int _disposed;

    public FramedStream(Stream stream, IDisposable owner = null) { _stream = stream; _owner = owner; }

    public async Task SendAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        payload ??= new byte[0];
        var buffer = new byte[5 + payload.Length];
        var length = payload.Length + 1;
        buffer[0] = (byte)(length >> 24); buffer[1] = (byte)(length >> 16); buffer[2] = (byte)(length >> 8); buffer[3] = (byte)length;
        buffer[4] = (byte)type;
        Buffer.BlockCopy(payload, 0, buffer, 5, payload.Length);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using (ct.Register(Dispose))
            {
                await _stream.WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Đọc một khung. Khi có hủy, luồng bị đóng vì SslStream trên .NET Framework không tôn trọng CancellationToken lúc đang đọc.</summary>
    public async Task<Frame> ReceiveAsync(int maxFrame, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            using (linked.Token.Register(Dispose))
            {
                await ReadExactAsync(_header, 5, linked.Token).ConfigureAwait(false);
                var length = (_header[0] << 24) | (_header[1] << 16) | (_header[2] << 8) | _header[3];
                if (length < 1 || length > maxFrame) throw new ProtocolException("Invalid frame size.");
                var type = (MessageType)_header[4];
                var payload = new byte[length - 1];
                await ReadExactAsync(payload, payload.Length, linked.Token).ConfigureAwait(false);
                return new Frame(type, payload);
            }
        }
        catch (Exception ex) when (linked.IsCancellationRequested && !(ex is ProtocolException))
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new TimeoutException("Timed out waiting for data.", ex);
        }
    }

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var n = await _stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("The connection was closed.");
            offset += n;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stream.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        try { _owner?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
    }
}

/// <summary>Địa chỉ Relay. PinnedKeySha256 là neo tin cậy tường minh cho Relay dùng chứng thư nội bộ.</summary>
public sealed class RelayEndpoint
{
    public string Host { get; set; }
    public int Port { get; set; } = ProtocolInfo.DefaultRelayPort;
    public string PinnedKeySha256 { get; set; }
}

public static class TlsConnector
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public static async Task<FramedStream> ConnectAsync(RelayEndpoint endpoint, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream ssl = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            using (timeout.Token.Register(() => { tcp.Close(); ssl?.Dispose(); }))
            {
                try
                {
                    await tcp.ConnectAsync(endpoint.Host, endpoint.Port).ConfigureAwait(false);
                    ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, errors) => ValidateCertificate(endpoint.PinnedKeySha256, cert, errors));
                    // Có ghim khóa thì tin vào khóa, không cần kiểm tra thu hồi; không ghim thì bắt buộc kiểm tra.
                    var checkRevocation = string.IsNullOrEmpty(endpoint.PinnedKeySha256);
                    await ssl.AuthenticateAsClientAsync(endpoint.Host, null, AllowedProtocols(), checkRevocation).ConfigureAwait(false);
                }
                catch (Exception ex) when (timeout.IsCancellationRequested && !(ex is AuthenticationException))
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw new TimeoutException("Timed out connecting to the Relay.", ex);
                }
            }
            return new FramedStream(ssl, tcp);
        }
        catch
        {
            ssl?.Dispose();
            tcp.Close();
            throw;
        }
    }

    /// <summary>TLS 1.2 trở lên. Chỉ thêm TLS 1.3 khi hệ điều hành hỗ trợ; không bao giờ hạ xuống dưới TLS 1.2.</summary>
    public static SslProtocols AllowedProtocols()
    {
        const SslProtocols tls13 = (SslProtocols)12288; // netstandard2.0 chưa có SslProtocols.Tls13.
        return PlatformInfo.Detect().SupportsTls13 ? SslProtocols.Tls12 | tls13 : SslProtocols.Tls12;
    }

    /// <summary>
    /// Mọi lỗi chứng thư đều bị từ chối, không có công tắc bỏ qua.
    /// Khóa ghim thay thế chuỗi CA (cho Relay nội bộ) nhưng vẫn phải đúng khóa, đúng hostname và còn hạn.
    /// </summary>
    public static bool ValidateCertificate(string pinnedKey, X509Certificate certificate, SslPolicyErrors errors)
    {
        if (certificate == null) return false;
        if (string.IsNullOrEmpty(pinnedKey)) return errors == SslPolicyErrors.None;

        using var cert = new X509Certificate2(certificate);
        if (!string.Equals(PublicKeyPin(cert), pinnedKey.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        var now = DateTime.Now;
        return now >= cert.NotBefore && now <= cert.NotAfter;
    }

    public static string PublicKeyPin(X509Certificate2 cert)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(cert.GetPublicKey()).Select(b => b.ToString("x2")));
    }
}
