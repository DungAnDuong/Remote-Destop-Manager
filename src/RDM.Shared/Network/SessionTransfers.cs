using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RDM.Shared.Protocol;

namespace RDM.Shared.Network;

/// <summary>Một dòng chat trong phiên.</summary>
public sealed class ChatMessage
{
    public string Text { get; set; }
    public bool FromMe { get; set; }
    public DateTime TimeLocal { get; set; } = DateTime.Now;
}

public enum TransferStatus { Active, Paused, Completed, Failed, Canceled }

/// <summary>Trạng thái một lượt truyền file để giao diện hiển thị tiến độ.</summary>
public sealed class FileTransferProgress
{
    public int Id { get; set; }
    public string FileName { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public bool Incoming { get; set; }
    // Truyền có chỉ định thư mục đích (trình quản lý file). Chat thì không: dùng để quyết định nơi hiển thị.
    public bool ToFolder { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Active;
    public string SavePath { get; set; }
    public string Error { get; set; }
}

/// <summary>Một mục trong thư mục của máy ở xa.</summary>
public sealed class FsEntry
{
    public string Name { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsDrive { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

public sealed class FsListing
{
    public bool Ok { get; set; }
    public string Error { get; set; }
    public string Path { get; set; }
    public List<FsEntry> Entries { get; } = new List<FsEntry>();
}

/// <summary>
/// Chat, truyền file và duyệt thư mục trong một phiên, đi trên cùng kênh E2EE với hình ảnh và điều khiển.
/// File nhận được lưu theo thư mục đích do bên kia chọn (trình quản lý file) hoặc một thư mục mặc định (kéo thả);
/// tên file bị cắt chỉ còn phần tên để chặn vượt thư mục, không ghi đè, không tự chạy.
/// Bên nhận tự nhận vì phiên đã xác thực; máy điều khiển thấy tiến độ, máy bị điều khiển không hiển thị gì khi truyền file.
/// </summary>
public sealed class SessionTransfers
{
    public const int ChunkSize = 128 * 1024;
    public const int MaxChunk = 256 * 1024;
    public const long MaxFileSize = 100L * 1024 * 1024 * 1024;
    public const int MaxEntries = 20000;

    private sealed class Incoming
    {
        public FileStream Stream;
        public FileTransferProgress Progress;
        public long LastAck;
    }

    private sealed class OutgoingCtl
    {
        public CancellationTokenSource Cts;
        public volatile bool Paused;
        public FileTransferProgress Progress;
    }

    // Bên nhận báo tiến độ ghi đĩa về bên gửi để hai máy hiển thị phần trăm giống hệt nhau.
    private const long AckEvery = 256 * 1024;

    private readonly Func<SessionMessage, byte[], CancellationToken, Task> _send;
    private readonly string _receiveDir;
    private readonly CancellationToken _sessionCt;
    private readonly ConcurrentDictionary<int, Incoming> _incoming = new ConcurrentDictionary<int, Incoming>();
    private readonly ConcurrentDictionary<int, OutgoingCtl> _outgoing = new ConcurrentDictionary<int, OutgoingCtl>();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<FsListing>> _listWaiters = new ConcurrentDictionary<int, TaskCompletionSource<FsListing>>();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<(bool, string)>> _mutateWaiters = new ConcurrentDictionary<int, TaskCompletionSource<(bool, string)>>();
    private int _nextId;

    public event Action<ChatMessage> ChatReceived;
    public event Action<FileTransferProgress> TransferChanged;

    public SessionTransfers(Func<SessionMessage, byte[], CancellationToken, Task> send, string receiveDir, CancellationToken sessionCt)
    {
        _send = send;
        _receiveDir = receiveDir;
        _sessionCt = sessionCt;
    }

    public Task SendChatAsync(string text, CancellationToken ct) =>
        _send(SessionMessage.Chat, Wire.Build(w => w.Str(text ?? string.Empty)), ct);

    /// <summary>
    /// Gửi một file sang bên kia. targetDir là thư mục nơi bên nhận lưu (rỗng = thư mục mặc định).
    /// overwrite = true thì bên nhận GHI ĐÈ file trùng tên; mặc định false thì tự đổi tên "(1)" để không ghi đè.
    /// </summary>
    public async Task SendFileAsync(string path, string targetDir, bool overwrite, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var info = new FileInfo(path);
        var progress = new FileTransferProgress { Id = id, FileName = info.Name, TotalBytes = info.Length, Incoming = false, ToFolder = !string.IsNullOrWhiteSpace(targetDir) };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCt);
        var ctl = new OutgoingCtl { Cts = cancel, Progress = progress };
        _outgoing[id] = ctl;
        TransferChanged?.Invoke(progress);
        try
        {
            await _send(SessionMessage.FileOffer, Wire.Build(w => { w.Write(id); w.Str(info.Name); w.Write(info.Length); w.Str(targetDir ?? string.Empty); w.Write(overwrite); }), cancel.Token).ConfigureAwait(false);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[ChunkSize];
                long sent = 0;
                int n;
                while ((n = await fs.ReadAsync(buffer, 0, buffer.Length, cancel.Token).ConfigureAwait(false)) > 0)
                {
                    // Tạm ngưng theo yêu cầu người dùng: chờ cho đến khi bỏ tạm ngưng hoặc hủy.
                    while (ctl.Paused && !cancel.IsCancellationRequested)
                    {
                        if (progress.Status != TransferStatus.Paused) { progress.Status = TransferStatus.Paused; TransferChanged?.Invoke(progress); }
                        await Task.Delay(150, cancel.Token).ConfigureAwait(false);
                    }
                    if (progress.Status == TransferStatus.Paused) { progress.Status = TransferStatus.Active; TransferChanged?.Invoke(progress); }
                    var count = n;
                    await _send(SessionMessage.FileChunk, Wire.Build(w => { w.Write(id); w.Write(count); w.Write(buffer, 0, count); }), cancel.Token).ConfigureAwait(false);
                    sent += n;
                    // Tiến độ của bên gửi do bên nhận báo về (FileProgress), không dùng số byte đã gửi,
                    // để phần trăm hai máy khớp nhau.
                }
            }
            await _send(SessionMessage.FileEnd, Wire.Build(w => w.Write(id)), cancel.Token).ConfigureAwait(false);
            progress.TransferredBytes = progress.TotalBytes;
            progress.Status = TransferStatus.Completed;
            TransferChanged?.Invoke(progress);
        }
        catch (OperationCanceledException)
        {
            progress.Status = TransferStatus.Canceled;
            TransferChanged?.Invoke(progress);
            await TrySendAsync(SessionMessage.FileCancel, Wire.Build(w => w.Write(id))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            progress.Error = ex.Message;
            progress.Status = TransferStatus.Failed;
            TransferChanged?.Invoke(progress);
            await TrySendAsync(SessionMessage.FileCancel, Wire.Build(w => w.Write(id))).ConfigureAwait(false);
        }
        finally { _outgoing.TryRemove(id, out _); }
    }

    public Task SendFileAsync(string path, string targetDir, CancellationToken ct) => SendFileAsync(path, targetDir, false, ct);
    public Task SendFileAsync(string path, CancellationToken ct) => SendFileAsync(path, null, false, ct);

    public void CancelOutgoing(int id) { if (_outgoing.TryGetValue(id, out var o)) { try { o.Cts.Cancel(); } catch (ObjectDisposedException) { } } }

    /// <summary>Tạm ngưng một lượt gửi đi (bên gửi ngừng đẩy dữ liệu).</summary>
    public void PauseOutgoing(int id) { if (_outgoing.TryGetValue(id, out var o)) o.Paused = true; }

    /// <summary>Tiếp tục một lượt gửi đã tạm ngưng.</summary>
    public void ResumeOutgoing(int id) { if (_outgoing.TryGetValue(id, out var o)) o.Paused = false; }

    /// <summary>Hủy một lượt truyền, dù đang gửi đi hay đang nhận về.</summary>
    public void CancelTransfer(int id)
    {
        CancelOutgoing(id);
        if (_incoming.TryGetValue(id, out var inc))
        {
            _ = TrySendAsync(SessionMessage.FileCancel, Wire.Build(w => w.Write(id)));
            FailIncoming(id, inc, "Canceled.");
        }
    }

    // --- Trình quản lý file: máy điều khiển gọi các hàm này để duyệt và tải file của máy ở xa ---

    public async Task<FsListing> ListRemoteAsync(string path, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<FsListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listWaiters[id] = tcs;
        try
        {
            await _send(SessionMessage.FsList, Wire.Build(w => { w.Write(id); w.Str(path ?? string.Empty); }), ct).ConfigureAwait(false);
            using (ct.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally { _listWaiters.TryRemove(id, out _); }
    }

    /// <summary>Yêu cầu máy ở xa gửi một file về thư mục localDir của máy điều khiển.</summary>
    public Task RequestDownloadAsync(string remotePath, string localDir, CancellationToken ct) =>
        _send(SessionMessage.FsGet, Wire.Build(w => { w.Write(Interlocked.Increment(ref _nextId)); w.Str(remotePath ?? string.Empty); w.Str(localDir ?? string.Empty); }), ct);

    /// <summary>Thực hiện một thao tác đổi tên/xóa/tạo thư mục/sao chép trên máy ở xa, chờ kết quả.</summary>
    public async Task<(bool Ok, string Error)> MutateRemoteAsync(FsOp op, string path, string arg2, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mutateWaiters[id] = tcs;
        try
        {
            await _send(SessionMessage.FsMutate, Wire.Build(w => { w.Write(id); w.Write((byte)op); w.Str(path ?? string.Empty); w.Str(arg2 ?? string.Empty); }), ct).ConfigureAwait(false);
            using (ct.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally { _mutateWaiters.TryRemove(id, out _); }
    }

    /// <summary>Gọi từ vòng nhận của phiên cho các thông điệp chat/file/duyệt thư mục. Trả về true nếu đã xử lý.</summary>
    public async Task<bool> HandleAsync(SessionMessage type, byte[] payload)
    {
        switch (type)
        {
            case SessionMessage.Chat:
                ChatReceived?.Invoke(new ChatMessage { Text = Wire.Parse(payload, r => r.Str()), FromMe = false });
                return true;
            case SessionMessage.FileOffer: OnOffer(payload); return true;
            case SessionMessage.FileChunk: await OnChunkAsync(payload).ConfigureAwait(false); return true;
            case SessionMessage.FileEnd: OnEnd(payload); return true;
            case SessionMessage.FileCancel: OnCancel(payload); return true;
            case SessionMessage.FileProgress: OnProgressAck(payload); return true;
            case SessionMessage.FsList: OnFsList(payload); return true;
            case SessionMessage.FsListReply: OnFsListReply(payload); return true;
            case SessionMessage.FsGet: OnFsGet(payload); return true;
            case SessionMessage.FsMutate: OnFsMutate(payload); return true;
            case SessionMessage.FsMutateReply: OnFsMutateReply(payload); return true;
            default: return false;
        }
    }

    private string ChooseDir(string targetDir)
    {
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            try
            {
                if (Path.IsPathRooted(targetDir)) { Directory.CreateDirectory(targetDir); return targetDir; }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
        }
        return _receiveDir;
    }

    private void OnOffer(byte[] payload)
    {
        var (id, rawName, size, targetDir, overwrite) = Wire.Parse(payload, r =>
        {
            var tid = r.ReadInt32(); var nm = r.Str(); var sz = r.ReadInt64(); var td = r.Str();
            // Cờ overwrite là tùy chọn: bản cũ không gửi, đọc có điều kiện để tương thích ngược.
            var ow = r.BaseStream.Position < r.BaseStream.Length && r.ReadBoolean();
            return (tid, nm, sz, td, ow);
        });
        var name = SafeName(rawName);
        var progress = new FileTransferProgress { Id = id, FileName = name, TotalBytes = size, Incoming = true, ToFolder = !string.IsNullOrWhiteSpace(targetDir) };
        if (name.Length == 0 || size < 0 || size > MaxFileSize)
        {
            progress.Status = TransferStatus.Failed;
            progress.Error = "The file was refused.";
            TransferChanged?.Invoke(progress);
            _ = TrySendAsync(SessionMessage.FileCancel, Wire.Build(w => w.Write(id)));
            return;
        }
        try
        {
            var dir = ChooseDir(targetDir);
            // Ghi đè khi được yêu cầu (người dùng đã xác nhận ở máy điều khiển); nếu không thì đổi tên để không ghi đè.
            var path = overwrite ? Path.Combine(dir, name) : UniquePath(dir, name);
            progress.SavePath = path;
            _incoming[id] = new Incoming { Stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None), Progress = progress };
            TransferChanged?.Invoke(progress);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            progress.Status = TransferStatus.Failed;
            progress.Error = ex.Message;
            TransferChanged?.Invoke(progress);
            _ = TrySendAsync(SessionMessage.FileCancel, Wire.Build(w => w.Write(id)));
        }
    }

    private async Task OnChunkAsync(byte[] payload)
    {
        var (id, data) = Wire.Parse(payload, r =>
        {
            var tid = r.ReadInt32();
            var len = r.ReadInt32();
            if (len < 0 || len > MaxChunk) throw new ProtocolException("Invalid file chunk size.");
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException();
            return (tid, bytes);
        });
        if (!_incoming.TryGetValue(id, out var inc)) return;
        try
        {
            if (inc.Progress.TransferredBytes + data.Length > MaxFileSize) throw new IOException("The file exceeds the size limit.");
            await inc.Stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            inc.Progress.TransferredBytes += data.Length;
            TransferChanged?.Invoke(inc.Progress);
            // Báo tiến độ ghi đĩa về bên gửi theo từng mốc, để hai máy hiển thị cùng phần trăm.
            if (inc.Progress.TransferredBytes - inc.LastAck >= AckEvery)
            {
                inc.LastAck = inc.Progress.TransferredBytes;
                var written = inc.Progress.TransferredBytes;
                await TrySendAsync(SessionMessage.FileProgress, Wire.Build(w => { w.Write(id); w.Write(written); })).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            FailIncoming(id, inc, ex.Message);
            await TrySendAsync(SessionMessage.FileCancel, Wire.Build(w => w.Write(id))).ConfigureAwait(false);
        }
    }

    private void OnEnd(byte[] payload)
    {
        var id = Wire.Parse(payload, r => r.ReadInt32());
        if (!_incoming.TryRemove(id, out var inc)) return;
        try { inc.Stream.Dispose(); inc.Progress.Status = TransferStatus.Completed; }
        catch (IOException ex) { inc.Progress.Status = TransferStatus.Failed; inc.Progress.Error = ex.Message; }
        TransferChanged?.Invoke(inc.Progress);
    }

    private void OnCancel(byte[] payload)
    {
        var id = Wire.Parse(payload, r => r.ReadInt32());
        CancelOutgoing(id);
        if (_incoming.TryRemove(id, out var inc)) FailIncoming(id, inc, "The other side canceled the transfer.");
    }

    // Bên gửi nhận báo tiến độ ghi đĩa từ bên nhận và hiển thị theo đó.
    private void OnProgressAck(byte[] payload)
    {
        var (id, written) = Wire.Parse(payload, r => (r.ReadInt32(), r.ReadInt64()));
        if (_outgoing.TryGetValue(id, out var o) && o.Progress != null && o.Progress.Status == TransferStatus.Active)
        {
            if (written > o.Progress.TransferredBytes && written <= o.Progress.TotalBytes)
            {
                o.Progress.TransferredBytes = written;
                TransferChanged?.Invoke(o.Progress);
            }
        }
    }

    private void OnFsList(byte[] payload)
    {
        var (id, path) = Wire.Parse(payload, r => (r.ReadInt32(), r.Str()));
        _ = RespondListAsync(id, path);
    }

    private async Task RespondListAsync(int id, string path)
    {
        var listing = await Task.Run(() => Enumerate(path)).ConfigureAwait(false);
        await TrySendAsync(SessionMessage.FsListReply, Wire.Build(w =>
        {
            w.Write(id); w.Write(listing.Ok); w.Str(Clip(listing.Error)); w.Str(Clip(listing.Path)); w.Write(listing.Entries.Count);
            foreach (var e in listing.Entries) { w.Str(Clip(e.Name)); w.Write(e.IsDirectory); w.Write(e.IsDrive); w.Write(e.Size); w.Write(e.Modified.Ticks); }
        })).ConfigureAwait(false);
    }

    private void OnFsListReply(byte[] payload)
    {
        var (id, listing) = Wire.Parse(payload, r =>
        {
            var rid = r.ReadInt32();
            var l = new FsListing { Ok = r.ReadBoolean(), Error = r.Str(), Path = r.Str() };
            var count = r.ReadInt32();
            if (count < 0 || count > MaxEntries) throw new ProtocolException("Invalid directory listing.");
            for (var i = 0; i < count; i++)
                l.Entries.Add(new FsEntry { Name = r.Str(), IsDirectory = r.ReadBoolean(), IsDrive = r.ReadBoolean(), Size = r.ReadInt64(), Modified = new DateTime(Math.Max(0, Math.Min(DateTime.MaxValue.Ticks, r.ReadInt64())), DateTimeKind.Local) });
            return (rid, l);
        });
        if (_listWaiters.TryGetValue(id, out var tcs)) tcs.TrySetResult(listing);
    }

    private void OnFsGet(byte[] payload)
    {
        var (path, saveDir) = Wire.Parse(payload, r => { r.ReadInt32(); return (r.Str(), r.Str()); });
        _ = DownloadToAsync(path, saveDir);
    }

    private async Task DownloadToAsync(string path, string saveDir)
    {
        try { await SendFileAsync(path, saveDir, _sessionCt).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
    }

    private void OnFsMutate(byte[] payload)
    {
        var (id, op, path, arg2) = Wire.Parse(payload, r => (r.ReadInt32(), (FsOp)r.ReadByte(), r.Str(), r.Str()));
        _ = RespondMutateAsync(id, op, path, arg2);
    }

    private async Task RespondMutateAsync(int id, FsOp op, string path, string arg2)
    {
        var (ok, error) = await Task.Run(() => RunMutate(op, path, arg2)).ConfigureAwait(false);
        await TrySendAsync(SessionMessage.FsMutateReply, Wire.Build(w => { w.Write(id); w.Write(ok); w.Str(Clip(error)); })).ConfigureAwait(false);
    }

    // Thực thi thao tác file trên máy nhận. Tên mới bị cắt chỉ còn phần tên để chặn vượt thư mục.
    private static (bool, string) RunMutate(FsOp op, string path, string arg2)
    {
        try
        {
            switch (op)
            {
                case FsOp.NewFolder:
                    if (string.IsNullOrEmpty(path)) return (false, "No folder selected.");
                    Directory.CreateDirectory(Path.Combine(path, SafeName(arg2)));
                    return (true, null);
                case FsOp.Rename:
                    if (string.IsNullOrEmpty(path)) return (false, "No item selected.");
                    var target = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, SafeName(arg2));
                    if (Directory.Exists(path)) Directory.Move(path, target);
                    else File.Move(path, target);
                    return (true, null);
                case FsOp.Delete:
                    if (string.IsNullOrEmpty(path)) return (false, "No item selected.");
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else File.Delete(path);
                    return (true, null);
                case FsOp.Copy:
                    if (string.IsNullOrEmpty(path)) return (false, "No item selected.");
                    var destDir = arg2;
                    if (Directory.Exists(path)) CopyDir(path, Path.Combine(destDir, Path.GetFileName(path.TrimEnd('\\', '/'))));
                    else File.Copy(path, UniquePath(destDir, Path.GetFileName(path)), false);
                    return (true, null);
                default: return (false, "Unsupported operation.");
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException || ex is NotSupportedException)
        {
            return (false, ex.Message);
        }
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), false);
        foreach (var d in Directory.GetDirectories(source)) CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private void OnFsMutateReply(byte[] payload)
    {
        var (id, ok, error) = Wire.Parse(payload, r => (r.ReadInt32(), r.ReadBoolean(), r.Str()));
        if (_mutateWaiters.TryGetValue(id, out var tcs)) tcs.TrySetResult((ok, string.IsNullOrEmpty(error) ? null : error));
    }

    private static FsListing Enumerate(string path)
    {
        var listing = new FsListing { Path = path ?? string.Empty };
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                foreach (var d in DriveInfo.GetDrives())
                    listing.Entries.Add(new FsEntry { Name = d.Name, IsDirectory = true, IsDrive = true });
            }
            else
            {
                foreach (var d in Directory.GetDirectories(path))
                {
                    if (listing.Entries.Count >= MaxEntries) break;
                    var di = new DirectoryInfo(d);
                    listing.Entries.Add(new FsEntry { Name = di.Name, IsDirectory = true, Modified = SafeTime(di) });
                }
                foreach (var f in Directory.GetFiles(path))
                {
                    if (listing.Entries.Count >= MaxEntries) break;
                    var fi = new FileInfo(f);
                    long size = 0;
                    try { size = fi.Length; } catch (IOException) { }
                    listing.Entries.Add(new FsEntry { Name = fi.Name, Size = size, Modified = SafeTime(fi) });
                }
            }
            listing.Ok = true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is ArgumentException)
        {
            listing.Ok = false;
            listing.Error = ex.Message;
        }
        return listing;
    }

    private static DateTime SafeTime(FileSystemInfo i) { try { return i.LastWriteTime; } catch (IOException) { return DateTime.MinValue; } catch (UnauthorizedAccessException) { return DateTime.MinValue; } }

    private void FailIncoming(int id, Incoming inc, string error)
    {
        _incoming.TryRemove(id, out _);
        var path = inc.Progress.SavePath;
        try { inc.Stream.Dispose(); } catch (IOException) { }
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        inc.Progress.Status = TransferStatus.Canceled;
        inc.Progress.Error = error;
        TransferChanged?.Invoke(inc.Progress);
    }

    /// <summary>Đóng mọi lượt đang dở khi phiên kết thúc.</summary>
    public void Dispose()
    {
        foreach (var kv in _incoming)
            if (_incoming.TryRemove(kv.Key, out var inc)) FailIncoming(kv.Key, inc, "The session ended.");
        foreach (var kv in _outgoing) { try { kv.Value.Cts.Cancel(); } catch (ObjectDisposedException) { } }
        foreach (var kv in _listWaiters) kv.Value.TrySetCanceled();
        foreach (var kv in _mutateWaiters) kv.Value.TrySetCanceled();
    }

    private async Task TrySendAsync(SessionMessage type, byte[] payload)
    {
        try { await _send(type, payload, _sessionCt).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException) { }
    }

    private static string Clip(string s) => s != null && s.Length > 1000 ? s.Substring(0, 1000) : (s ?? string.Empty);

    private static string SafeName(string name)
    {
        var bare = Path.GetFileName(name ?? string.Empty);
        foreach (var c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
        return bare.Trim();
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static string DefaultReceiveDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RDM Received Files");
}
