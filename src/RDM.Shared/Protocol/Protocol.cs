using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RDM.Shared.Protocol;

public static class ProtocolInfo
{
    public const byte Version = 1;
    public const int DefaultRelayPort = 7443;
    /// <summary>Giới hạn khung trước khi xác thực: chặn kẻ chưa xác thực bắt Server cấp phát bộ nhớ lớn.</summary>
    public const int MaxHandshakeFrame = 8 * 1024;
    public const int MaxFrame = 1024 * 1024;
    public const int SnapshotChunk = 500;
    public const string DeviceAuthContext = "RDM-DEVICE-AUTH-v1";
}

public enum MessageType : byte
{
    Hello = 1, Challenge = 2, DeviceAuth = 3, ControllerAuth = 4, AuthResult = 5,
    Heartbeat = 6, HeartbeatAck = 7, SubscribeAll = 8, PresenceSnapshot = 9, PresenceChanged = 10,
    SessionRequest = 11, SessionTicket = 12, IncomingSession = 13, RelayJoin = 14, RelayReady = 15,
    SessionHello = 32, SessionHelloReply = 33, Sealed = 34
}

public enum PeerRole : byte { Device = 1, Controller = 2, RelayPeer = 3 }

/// <summary>Kết quả xin phiên từ Relay.</summary>
public enum TicketCode : byte { Ok = 0, NotFound = 1, Offline = 2, AccessDisabled = 3, RateLimited = 4, Busy = 5, SameDevice = 6 }

/// <summary>Trả lời của Relay: địa chỉ để thử kết nối trực tiếp, và vé nếu đã xin chuyển tiếp.</summary>
public sealed class RelayTicket
{
    public TicketCode Code { get; set; }
    public byte[] Token { get; set; } = new byte[0];
    public string LocalIp { get; set; }
    public int SessionPort { get; set; }
    public string PublicIp { get; set; }
}

public enum AuthCode : byte
{
    Ok = 0, BadSignature = 1, RemoteIdConflict = 2, EnrollmentRequired = 3, BadToken = 4,
    Revoked = 5, VersionMismatch = 6, RateLimited = 7, ServerBusy = 8, ProtocolError = 9
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message, Exception inner = null) : base(message, inner) { }
}

public sealed class Frame
{
    public MessageType Type { get; }
    public byte[] Payload { get; }
    public Frame(MessageType type, byte[] payload) { Type = type; Payload = payload ?? new byte[0]; }
}

/// <summary>Thông tin thiết bị tự khai báo. Chỉ để hiển thị, không dùng để quyết định bảo mật.</summary>
public sealed class DeviceInfo
{
    public string Name { get; set; }
    public string OperatingSystem { get; set; }
    public string Architecture { get; set; }
    public string LocalIp { get; set; }
    public string MacAddress { get; set; }
    /// <summary>Cổng nhận phiên trực tiếp; 0 là không nhận (đã tắt Remote Access).</summary>
    public int SessionPort { get; set; }
}

public sealed class PresenceEntry
{
    public string RemoteId { get; set; }
    public bool IsOnline { get; set; }
    public bool AccessEnabled { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DeviceInfo Info { get; set; } = new DeviceInfo();
}

/// <summary>Mã hóa nhị phân. Mọi độ dài đều được kiểm tra khi đọc vì dữ liệu đến từ mạng.</summary>
public static class Wire
{
    private const int MaxString = 1024;
    private const int MaxBytes = 4096;

    public static byte[] Build(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, new UTF8Encoding(false));
        write(w);
        w.Flush();
        return ms.ToArray();
    }

    public static T Parse<T>(byte[] payload, Func<BinaryReader, T> read)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(payload), new UTF8Encoding(false));
            return read(r);
        }
        catch (EndOfStreamException ex) { throw new ProtocolException("The message is truncated.", ex); }
        catch (FormatException ex) { throw new ProtocolException("The message is malformed.", ex); }
        catch (IOException ex) { throw new ProtocolException("The message is malformed.", ex); }
    }

    public static void Str(this BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? string.Empty);
        if (b.Length > MaxString) throw new ProtocolException("The string is too long.");
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    public static string Str(this BinaryReader r)
    {
        var n = r.ReadUInt16();
        if (n > MaxString) throw new ProtocolException("The string is too long.");
        var b = r.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(b);
    }

    public static void Blob(this BinaryWriter w, byte[] b)
    {
        if (b.Length > MaxBytes) throw new ProtocolException("The data is too long.");
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    public static byte[] Blob(this BinaryReader r)
    {
        var n = r.ReadUInt16();
        if (n > MaxBytes) throw new ProtocolException("The data is too long.");
        var b = r.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return b;
    }

    public static void Info(this BinaryWriter w, DeviceInfo i)
    {
        w.Str(i?.Name); w.Str(i?.OperatingSystem); w.Str(i?.Architecture); w.Str(i?.LocalIp); w.Str(i?.MacAddress); w.Write(i?.SessionPort ?? 0);
    }

    public static DeviceInfo Info(this BinaryReader r) => new DeviceInfo
    {
        Name = r.Str(), OperatingSystem = r.Str(), Architecture = r.Str(), LocalIp = r.Str(), MacAddress = r.Str(), SessionPort = r.ReadInt32()
    };

    public static void Entry(this BinaryWriter w, PresenceEntry e)
    {
        w.Str(e.RemoteId); w.Write(e.IsOnline); w.Write(e.AccessEnabled); w.Write(e.LastSeenUtc.ToBinary()); w.Info(e.Info);
    }

    public static PresenceEntry Entry(this BinaryReader r)
    {
        var e = new PresenceEntry { RemoteId = r.Str(), IsOnline = r.ReadBoolean(), AccessEnabled = r.ReadBoolean() };
        e.LastSeenUtc = DateTime.FromBinary(r.ReadInt64()).ToUniversalTime();
        e.Info = r.Info();
        return e;
    }

    public static byte[] Entries(IReadOnlyList<PresenceEntry> entries, bool isLast) => Build(w =>
    {
        w.Write(isLast);
        w.Write(entries.Count);
        foreach (var e in entries) w.Entry(e);
    });

    public static List<PresenceEntry> ReadEntries(byte[] payload, out bool isLast)
    {
        var last = false;
        var list = Parse(payload, r =>
        {
            last = r.ReadBoolean();
            var n = r.ReadInt32();
            if (n < 0 || n > ProtocolInfo.SnapshotChunk * 2) throw new ProtocolException("Invalid element count.");
            var items = new List<PresenceEntry>(n);
            for (var i = 0; i < n; i++) items.Add(r.Entry());
            return items;
        });
        isLast = last;
        return list;
    }

    public static byte[] Ticket(int requestId, RelayTicket t) => Build(w =>
    {
        w.Write(requestId); w.Write((byte)t.Code); w.Blob(t.Token); w.Str(t.LocalIp); w.Write(t.SessionPort); w.Str(t.PublicIp);
    });

    public static RelayTicket ReadTicket(byte[] payload, out int requestId)
    {
        var id = 0;
        var ticket = Parse(payload, r =>
        {
            id = r.ReadInt32();
            return new RelayTicket { Code = (TicketCode)r.ReadByte(), Token = r.Blob(), LocalIp = r.Str(), SessionPort = r.ReadInt32(), PublicIp = r.Str() };
        });
        requestId = id;
        return ticket;
    }

    /// <summary>Nội dung thiết bị ký khi xác thực: gắn với nonce của Server nên không phát lại được.</summary>
    public static byte[] DeviceAuthMessage(byte[] nonce, string remoteId, byte[] publicKey) => Build(w =>
    {
        w.Str(ProtocolInfo.DeviceAuthContext);
        w.Blob(nonce);
        w.Str(remoteId);
        w.Blob(publicKey);
    });
}
