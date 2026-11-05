using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using RDM.Shared.Network;
using RDM.Shared.Protocol;

namespace RDM.Shared.Security;

public static class Hkdf
{
    public static byte[] Derive(byte[] secret, byte[] salt, string info, int length)
    {
        var generator = new HkdfBytesGenerator(new Sha256Digest());
        generator.Init(new HkdfParameters(secret, salt, Encoding.UTF8.GetBytes(info)));
        var output = new byte[length];
        generator.GenerateBytes(output, 0, length);
        return output;
    }
}

/// <summary>
/// Kênh E2EE của phiên điều khiển. Mỗi hướng một khóa. Nonce = hướng (4 byte) | bộ đếm (8 byte), không bao giờ lặp với cùng khóa.
/// Bộ đếm là ngầm định: gói bị phát lại, đảo thứ tự, xóa hoặc sửa đều làm xác thực GCM thất bại và phiên bị đóng.
/// Relay hoặc bất kỳ ai ở giữa chỉ thấy bản mã.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private sealed class Direction : IDisposable
    {
        private readonly bool _encrypt;
        private readonly uint _id;
        private byte[] _key;
        private GcmBlockCipher _gcm;
        private bool _keyLoaded;
        public ulong Counter { get; private set; }
        public long Bytes { get; private set; }
        public DateTime KeyCreated { get; private set; }
        public int RekeyCount { get; private set; }

        public Direction(byte[] key, uint id, bool encrypt)
        {
            _id = id; _encrypt = encrypt;
            Reset((byte[])key.Clone());
        }

        private void Reset(byte[] key)
        {
            _key = key;
            _gcm = new GcmBlockCipher(AesUtilities.CreateEngine());
            _keyLoaded = false;
            Counter = 0; Bytes = 0;
            KeyCreated = DateTime.UtcNow;
        }

        public byte[] Process(byte[] input)
        {
            var nonce = new byte[12];
            nonce[0] = (byte)(_id >> 24); nonce[1] = (byte)(_id >> 16); nonce[2] = (byte)(_id >> 8); nonce[3] = (byte)_id;
            for (var i = 0; i < 8; i++) nonce[4 + i] = (byte)(Counter >> (56 - 8 * i));
            // Chỉ nạp khóa một lần: nạp lại mỗi gói sẽ tính lại bảng GHASH và làm chậm đáng kể.
            _gcm.Init(_encrypt, new AeadParameters(_keyLoaded ? null : new KeyParameter(_key), 128, nonce));
            _keyLoaded = true;
            var output = new byte[_gcm.GetOutputSize(input.Length)];
            try
            {
                var n = _gcm.ProcessBytes(input, 0, input.Length, output, 0);
                _gcm.DoFinal(output, n);
            }
            catch (InvalidCipherTextException ex)
            {
                throw new CryptoFailureException("Session data was modified or replayed.", ex);
            }
            Counter++;
            Bytes += input.Length;
            return output;
        }

        /// <summary>Đổi khóa một chiều: lộ khóa hiện tại không suy ra được khóa cũ.</summary>
        public void Ratchet()
        {
            var next = Hkdf.Derive(_key, null, "rdm-session-rekey", AesGcmCipher.KeySize);
            Array.Clear(_key, 0, _key.Length);
            Reset(next);
            RekeyCount++;
        }

        public void Dispose() { if (_key != null) Array.Clear(_key, 0, _key.Length); }
    }

    private readonly FramedStream _stream;
    private readonly Direction _send, _recv;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly long _rekeyBytes;
    private readonly TimeSpan _rekeyInterval;

    public const long DefaultRekeyBytes = 1L << 30;
    public static readonly TimeSpan DefaultRekeyInterval = TimeSpan.FromMinutes(60);

    public int SendRekeys => _send.RekeyCount;
    public int ReceiveRekeys => _recv.RekeyCount;

    public SecureChannel(FramedStream stream, byte[] sendKey, byte[] receiveKey, bool isHost, long rekeyBytes = DefaultRekeyBytes, TimeSpan? rekeyInterval = null)
    {
        _stream = stream;
        _send = new Direction(sendKey, isHost ? 1u : 2u, true);
        _recv = new Direction(receiveKey, isHost ? 2u : 1u, false);
        _rekeyBytes = rekeyBytes;
        _rekeyInterval = rekeyInterval ?? DefaultRekeyInterval;
    }

    public async Task SendAsync(SessionMessage type, byte[] payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_send.Bytes >= _rekeyBytes || DateTime.UtcNow - _send.KeyCreated >= _rekeyInterval)
            {
                await WriteAsync(SessionMessage.Rekey, null, ct).ConfigureAwait(false);
                _send.Ratchet();
            }
            await WriteAsync(type, payload, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private Task WriteAsync(SessionMessage type, byte[] payload, CancellationToken ct)
    {
        payload ??= new byte[0];
        var plain = new byte[payload.Length + 1];
        plain[0] = (byte)type;
        Buffer.BlockCopy(payload, 0, plain, 1, payload.Length);
        return _stream.SendAsync(MessageType.Sealed, _send.Process(plain), ct);
    }

    public async Task<(SessionMessage Type, byte[] Payload)> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        while (true)
        {
            var frame = await _stream.ReceiveAsync(SessionInfo.MaxFrame, timeout, ct).ConfigureAwait(false);
            if (frame.Type != MessageType.Sealed) throw new ProtocolException("Unencrypted frame in a session.");
            var plain = _recv.Process(frame.Payload);
            if (plain.Length < 1) throw new ProtocolException("Empty session frame.");
            var type = (SessionMessage)plain[0];
            if (type == SessionMessage.Rekey) { _recv.Ratchet(); continue; }
            var payload = new byte[plain.Length - 1];
            Buffer.BlockCopy(plain, 1, payload, 0, payload.Length);
            return (type, payload);
        }
    }

    /// <summary>Hủy khóa phiên trong bộ nhớ và đóng kết nối.</summary>
    public void Dispose()
    {
        _stream.Dispose();
        _send.Dispose();
        _recv.Dispose();
    }
}
