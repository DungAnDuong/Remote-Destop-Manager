using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Forms;
using RDM.Data;
using RDM.Data.Services;
using RDM.Shared.Common;
using RDM.Shared.Models;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using RDM.Shared.Security;

namespace RDM.Controller.Services;

/// <summary>
/// Luồng kết nối của Phần 4.4: kiểm tra Remote ID, kiểm tra trạng thái, xác thực, thiết lập phiên, mở Remote Desktop, ghi lịch sử.
/// Ưu tiên kết nối trực tiếp; không được thì chuyển tiếp qua Relay. Dù đi đường nào, phiên vẫn là E2EE và được xác minh bằng khóa ghim.
/// </summary>
public static class ConnectFlow
{
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Còn đường Relay thì không chờ lâu: thử trực tiếp chỉ là để nhanh hơn.</summary>
    private static readonly TimeSpan DirectTimeoutWithRelay = TimeSpan.FromSeconds(2);

    /// <summary>Kết nối Relay của chính máy này. Máy nào đăng ký với Relay thì kết nối được qua Internet, không cần token Controller.</summary>
    public static IRelayBroker Broker { get; set; }

    private sealed class Route
    {
        public readonly List<(string host, int port)> Direct = new List<(string, int)>();
        public bool CanRelay;
    }

    /// <param name="password">Mật khẩu người dùng nhập; null nghĩa là dùng mật khẩu đã lưu nếu có, nếu không thì hỏi.</param>
    /// <param name="manualAddress">Địa chỉ IP do người dùng nhập, dùng khi không tìm được máy tự động.</param>
    public static async Task ConnectAsync(RdmRuntime rt, IWin32Window owner, string remoteIdInput, string password, string manualAddress = null)
    {
        rt.Session.Demand(Permission.QuickConnect);
        var remoteId = RemoteIdFormat.Normalize(remoteIdInput) ?? throw new ValidationException("The Remote ID must be 9 to 12 digits.");

        var existing = SessionRegistry.Find(remoteId);
        if (existing != null) throw new ValidationException("This device already has an open session.");

        var device = rt.Clients.Query(remoteId, null, false).FirstOrDefault(d => d.RemoteId == remoteId);
        if (device != null && !device.IsEnabled) throw new ValidationException("This device is disabled in the list.");
        var name = device?.DeviceName ?? RemoteIdFormat.Display(remoteId);

        var route = await ResolveRouteAsync(rt, remoteId, device, manualAddress).ConfigureAwait(true);

        // Kết nối nhanh không đăng nhập thì không được đọc mật khẩu đã lưu.
        if (password == null && device?.HasCredential == true && rt.Session.Can(Permission.UseCredentials)) password = rt.Clients.UseCredential(device.Id);
        if (password == null)
        {
            using var prompt = new PasswordPromptForm("Connect to " + name, "Remote computer's access password",
                "This is the RDM password set by the computer's owner, not a Windows password. Leave blank if none is set; the owner must then accept on screen.", false, false, true);
            if (prompt.ShowDialog(owner) != DialogResult.OK) return;
            password = prompt.Password;
        }

        try
        {
            var open = BuildOpener(rt, owner, remoteId, route, password);
            var session = await open().ConfigureAwait(true);
            var historyId = rt.History.Start(remoteId, name, rt.Session.Username, session.ConnectionType, "Connected");
            rt.Audit.Write(AuditEvents.SessionStarted, rt.Session.Username, remoteId: remoteId, deviceName: name, details: session.ConnectionType + " connection");
            var form = new RemoteDesktopForm(rt, remoteId, name, session, open, historyId);
            form.Show();
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            rt.History.Finish(rt.History.Start(remoteId, name, rt.Session.Username, route.CanRelay ? ProductMode.RelayRouteName : "Direct", "Failed"), "Failed");
            rt.Audit.Write(AuditEvents.SessionFailed, rt.Session.Username, false, ex.GetType().Name, remoteId, name, AuditSeverity.Warning);
            throw new ValidationException(Explain(ex));
        }
    }

    private static bool IsConnectionFailure(Exception ex) =>
        ex is SessionRejectedException || ex is HostVerificationException || ex is SocketException || ex is TimeoutException || ex is IOException ||
        ex is ProtocolException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is InvalidOperationException ||
        // Lỗi từ máy chủ trung gian có nội dung kỹ thuật: phải qua Explain, không hiện nguyên văn cho người dùng.
        ex is RelayRejectedException || ex is System.Security.Authentication.AuthenticationException;

    private static string Explain(Exception ex) => ex switch
    {
        SessionRejectedException _ or HostVerificationException _ => ex.Message,
        SocketException _ or TimeoutException _ => "Could not connect to the remote computer. Check that it is online and running RDM, then try again.",
        _ => "The connection was interrupted while setting up the session."
    };

    /// <summary>Địa chỉ nhận được chỉ là gợi ý nơi tìm máy: không được tin, vì danh tính được xác minh bằng khóa trong bắt tay.</summary>
    private static async Task<Route> ResolveRouteAsync(RdmRuntime rt, string remoteId, ClientDevice device, string manualAddress)
    {
        var route = new Route();
        if (!string.IsNullOrWhiteSpace(manualAddress))
        {
            route.Direct.Add(ParseManualAddress(manualAddress));
            return route;
        }

        var broker = Broker;
        if (broker != null && broker.IsOnline)
        {
            RelayTicket info;
            try { info = await broker.RequestSessionAsync(remoteId, false, CancellationToken.None).ConfigureAwait(true); }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                throw new ValidationException("Could not start the connection. Check your Internet connection and try again, or enter the computer's IP address.");
            }
            ThrowIfRefused(info.Code);
            route.CanRelay = true;
            var port = info.SessionPort > 0 ? info.SessionPort : SessionInfo.DefaultPort;
            if (!string.IsNullOrEmpty(info.LocalIp)) route.Direct.Add((info.LocalIp, port));
            if (!string.IsNullOrEmpty(info.PublicIp) && info.PublicIp != info.LocalIp) route.Direct.Add((info.PublicIp, port));
            return route;
        }

        var entry = rt.Presence.Find(remoteId);
        if (entry != null && rt.Presence.Availability == PresenceAvailability.Connected)
        {
            if (!entry.IsOnline) throw new ValidationException("The device is offline.");
            if (!entry.AccessEnabled) throw new ValidationException("The owner has turned off Remote Access on this device.");
            if (!string.IsNullOrEmpty(entry.Info?.LocalIp))
            {
                route.Direct.Add((entry.Info.LocalIp, entry.Info.SessionPort > 0 ? entry.Info.SessionPort : SessionInfo.DefaultPort));
                return route;
            }
        }
        if (!string.IsNullOrWhiteSpace(device?.IpAddress))
        {
            route.Direct.Add((device.IpAddress, SessionInfo.DefaultPort));
            return route;
        }
        var found = await LanDiscovery.FindAsync(remoteId, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        if (found != null)
        {
            route.Direct.Add((found.Address.ToString(), found.Port));
            return route;
        }
        throw new ValidationException(ProductMode.Managed
            ? "This computer was not found. Check your Internet connection and that the computer is running RDM, or enter its IP address."
            : "This computer was not found on the network. It must be running RDM on the same network; on a different subnet, enter its IP address. To connect over the Internet, configure a Relay Server in Settings.");
    }

    /// <summary>Máy đích có thể đổi cổng kết nối trực tiếp trong Settings, nên phải nhập được dạng địa chỉ:cổng. IPv6 có nhiều dấu hai chấm thì không tách.</summary>
    public static (string host, int port) ParseManualAddress(string text)
    {
        var host = text.Trim();
        var port = SessionInfo.DefaultPort;
        var colon = host.IndexOf(':');
        if (colon > 0 && colon == host.LastIndexOf(':'))
        {
            if (!int.TryParse(host.Substring(colon + 1), out port) || port < 1 || port > 65535) throw new ValidationException("Port must be between 1 and 65535.");
            host = host.Substring(0, colon);
        }
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ValidationException("Invalid IP address.");
        return (host, port);
    }

    private static void ThrowIfRefused(TicketCode code)
    {
        switch (code)
        {
            case TicketCode.Ok: return;
            case TicketCode.NotFound: throw new ValidationException("No computer with this Remote ID was found.");
            case TicketCode.Offline: throw new ValidationException("The device is offline.");
            case TicketCode.AccessDisabled: throw new ValidationException("The owner has turned off Remote Access on this device.");
            case TicketCode.RateLimited: throw new ValidationException("Too many connection requests. Wait a minute and try again.");
            case TicketCode.Busy: throw new ValidationException("The service is busy. Try again later.");
            case TicketCode.SameDevice: throw new ValidationException("This is this computer's own Remote ID.");
            default: throw new ValidationException("The connection request was refused.");
        }
    }

    /// <summary>Hàm mở phiên, cũng được dùng lại khi Reconnect. Mỗi lần là một bắt tay mới với khóa phiên mới.</summary>
    private static Func<Task<RemoteSession>> BuildOpener(RdmRuntime rt, IWin32Window owner, string remoteId, Route route, string password) => async () =>
    {
        // Bắt tay chạy ở luồng nền; hộp thoại fingerprint phải quay về luồng UI.
        var ui = SynchronizationContext.Current;
        Exception last = null;
        var timeout = route.CanRelay ? DirectTimeoutWithRelay : DirectTimeout;
        foreach (var (host, port) in route.Direct)
        {
            try
            {
                var stream = await ConnectDirectAsync(host, port, timeout).ConfigureAwait(true);
                return await HandshakeAsync(rt, owner, ui, remoteId, stream, password, "Direct").ConfigureAwait(true);
            }
            // Không tới được hoặc gõ nhầm máy khác (trùng IP nội bộ giữa hai mạng) thì thử đường tiếp theo.
            // Sai khóa, sai mật khẩu, chủ máy từ chối là câu trả lời thật: không được thử đường khác để né.
            catch (Exception ex) when (ex is SocketException || ex is TimeoutException || ex is IOException || ex is ProtocolException || ex is ObjectDisposedException || (ex is HostVerificationException h && h.WrongMachine))
            {
                last = ex;
            }
        }

        if (route.CanRelay)
        {
            var broker = Broker;
            if (broker == null || !broker.IsOnline) throw new IOException("Not connected to the Relay.");
            var ticket = await broker.RequestSessionAsync(remoteId, true, CancellationToken.None).ConfigureAwait(true);
            ThrowIfRefused(ticket.Code);
            var stream = await RelayJoin.ConnectAsync(broker.Endpoint, ticket.Token, CancellationToken.None).ConfigureAwait(true);
            return await HandshakeAsync(rt, owner, ui, remoteId, stream, password, ProductMode.RelayRouteName).ConfigureAwait(true);
        }
        throw last ?? new IOException("No route to the remote computer.");
    };

    private static async Task<FramedStream> ConnectDirectAsync(string host, int port, TimeSpan timeout)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            var connect = tcp.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeout)).ConfigureAwait(true) != connect) throw new TimeoutException();
            await connect.ConfigureAwait(true);
            return new FramedStream(tcp.GetStream(), tcp);
        }
        catch
        {
            tcp.Close();
            throw;
        }
    }

    private static async Task<RemoteSession> HandshakeAsync(RdmRuntime rt, IWin32Window owner, SynchronizationContext ui, string remoteId, FramedStream stream, string password, string connectionType)
    {
        try
        {
            using var identity = rt.HostKeys.GetControllerIdentity();
            var result = await SessionHandshake.ConnectAsync(stream, new ControllerHandshakeOptions
            {
                Identity = identity,
                ExpectedRemoteId = remoteId,
                Username = rt.Session.Username,
                Password = password,
                VerifyHostKey = key => OnUiThread(ui, () => VerifyHostKey(rt, owner, remoteId, key))
            }, CancellationToken.None).ConfigureAwait(true);
            return new RemoteSession(result) { ConnectionType = connectionType };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Khóa đã ghim thì phải khớp tuyệt đối, không có lựa chọn "vẫn tiếp tục". Chưa ghim thì người dùng phải xác nhận fingerprint.</summary>
    private static Task<bool> OnUiThread(SynchronizationContext ui, Func<bool> work)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try { result.SetResult(work()); }
            catch (Exception ex) { result.SetException(ex); }
        }, null);
        return result.Task;
    }

    private static bool VerifyHostKey(RdmRuntime rt, IWin32Window owner, string remoteId, byte[] key)
    {
        var pinned = rt.HostKeys.GetPinned(remoteId);
        if (pinned != null)
        {
            if (PasswordHasher.FixedTimeEquals(pinned, key)) return true;
            rt.HostKeys.ReportMismatch(remoteId, key);
            throw new HostVerificationException(
                "WARNING: this device's identity key differs from the pinned key. This may be a man-in-the-middle attack, or the device was reinstalled.\n\nThe connection was blocked. If the device really was reinstalled, an Administrator can use \"Remove Key Pin\" in My Computers after verifying with the owner.");
        }
        var fingerprint = DeviceIdentity.Fingerprint(key);
        var strict = rt.Settings.Get(SettingKeys.StrictPinning, "true") == "true";
        using var dialog = new FingerprintForm(remoteId, fingerprint, strict);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        rt.HostKeys.Pin(remoteId, key);
        return true;
    }
}
