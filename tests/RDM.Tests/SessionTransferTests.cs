using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RDM.Shared.Network;
using RDM.Shared.Protocol;
using Xunit;

namespace RDM.Tests;

public class SessionTransferTests : IDisposable
{
    private readonly string _dirA = Path.Combine(Path.GetTempPath(), "rdm-xferA-" + Guid.NewGuid().ToString("N"));
    private readonly string _dirB = Path.Combine(Path.GetTempPath(), "rdm-xferB-" + Guid.NewGuid().ToString("N"));

    public SessionTransferTests()
    {
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dirA, true); } catch (IOException) { }
        try { Directory.Delete(_dirB, true); } catch (IOException) { }
    }

    // Nối hai bên bằng cách chuyển thẳng thông điệp sang HandleAsync của bên kia, giữ đúng thứ tự như kênh thật.
    private (SessionTransfers a, SessionTransfers b) Link()
    {
        SessionTransfers a = null, b = null;
        a = new SessionTransfers((t, p, c) => b.HandleAsync(t, p), _dirA, CancellationToken.None);
        b = new SessionTransfers((t, p, c) => a.HandleAsync(t, p), _dirB, CancellationToken.None);
        return (a, b);
    }

    [Fact]
    public async Task Chat_DeliversText()
    {
        var (a, b) = Link();
        string got = null;
        b.ChatReceived += m => got = m.Text;
        await a.SendChatAsync("Xin chào", CancellationToken.None);
        Assert.Equal("Xin chào", got);
    }

    [Fact]
    public async Task File_IsReceivedIntact()
    {
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        var src = Path.Combine(_dirA, "payload.bin");
        File.WriteAllBytes(src, bytes);

        var (a, b) = Link();
        FileTransferProgress completed = null;
        b.TransferChanged += p => { if (p.Status == TransferStatus.Completed) completed = p; };

        await a.SendFileAsync(src, CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(_dirB, Path.GetDirectoryName(completed.SavePath));
        Assert.Equal(bytes, File.ReadAllBytes(completed.SavePath));
    }

    [Fact] // Tên file từ bên kia không được dùng để thoát khỏi thư mục nhận.
    public async Task Offer_WithTraversalName_StaysInReceiveDir()
    {
        var (_, b) = Link();
        await b.HandleAsync(SessionMessage.FileOffer, Wire.Build(w => { w.Write(1); w.Str(@"..\..\evil.txt"); w.Write(3L); w.Str(""); }));
        await b.HandleAsync(SessionMessage.FileChunk, Wire.Build(w => { w.Write(1); w.Write(3); w.Write(new byte[] { 1, 2, 3 }); }));
        await b.HandleAsync(SessionMessage.FileEnd, Wire.Build(w => w.Write(1)));

        Assert.True(File.Exists(Path.Combine(_dirB, "evil.txt")));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dirB, @"..\..\evil.txt"))));
    }

    [Fact] // Không ghi đè file đã có: lần nhận thứ hai được đổi tên.
    public async Task SecondFileSameName_DoesNotOverwrite()
    {
        File.WriteAllText(Path.Combine(_dirA, "note.txt"), "one");
        var (a, b) = Link();
        var saved = 0;
        b.TransferChanged += p => { if (p.Status == TransferStatus.Completed) saved++; };
        await a.SendFileAsync(Path.Combine(_dirA, "note.txt"), CancellationToken.None);
        await a.SendFileAsync(Path.Combine(_dirA, "note.txt"), CancellationToken.None);
        Assert.Equal(2, saved);
        Assert.True(File.Exists(Path.Combine(_dirB, "note.txt")));
        Assert.True(File.Exists(Path.Combine(_dirB, "note (1).txt")));
    }

    [Fact] // Ghi đè khi được xác nhận: gửi lại cùng tên với overwrite=true thì thay file cũ, không tạo "(1)".
    public async Task SameName_WithOverwrite_ReplacesFile()
    {
        var (a, b) = Link();
        var done = 0;
        b.TransferChanged += p => { if (p.Status == TransferStatus.Completed) done++; };

        File.WriteAllText(Path.Combine(_dirA, "note.txt"), "first");
        await a.SendFileAsync(Path.Combine(_dirA, "note.txt"), _dirB, true, CancellationToken.None);
        File.WriteAllText(Path.Combine(_dirA, "note.txt"), "second-longer");
        await a.SendFileAsync(Path.Combine(_dirA, "note.txt"), _dirB, true, CancellationToken.None);

        Assert.Equal(2, done);
        Assert.True(File.Exists(Path.Combine(_dirB, "note.txt")));
        Assert.False(File.Exists(Path.Combine(_dirB, "note (1).txt")));
        Assert.Equal("second-longer", File.ReadAllText(Path.Combine(_dirB, "note.txt")));
    }

    [Fact] // Khúc dữ liệu quá lớn bị từ chối, không làm hỏng vòng nhận.
    public async Task OversizedChunk_IsRejected()
    {
        var (_, b) = Link();
        await b.HandleAsync(SessionMessage.FileOffer, Wire.Build(w => { w.Write(1); w.Str("big.bin"); w.Write(1000L); w.Str(""); }));
        await Assert.ThrowsAsync<ProtocolException>(() =>
            b.HandleAsync(SessionMessage.FileChunk, Wire.Build(w => { w.Write(1); w.Write(SessionTransfers.MaxChunk + 1); w.Write(new byte[SessionTransfers.MaxChunk + 1]); })));
    }

    [Fact] // Chat không chỉ định thư mục (ToFolder=false); trình quản lý file có (ToFolder=true).
    public async Task ChatFile_NotToFolder_UploadIsToFolder()
    {
        File.WriteAllText(Path.Combine(_dirA, "c.txt"), "x");
        var (a, b) = Link();
        var flags = new System.Collections.Generic.List<bool>();
        b.TransferChanged += p => { if (p.Status == TransferStatus.Completed) flags.Add(p.ToFolder); };
        await a.SendFileAsync(Path.Combine(_dirA, "c.txt"), CancellationToken.None);
        await a.SendFileAsync(Path.Combine(_dirA, "c.txt"), Path.Combine(_dirB, "into"), CancellationToken.None);
        Assert.Equal(new[] { false, true }, flags);
    }

    [Fact] // Thao tác tạo thư mục / đổi tên / xóa trên máy ở xa.
    public async Task RemoteMutate_NewFolder_Rename_Delete()
    {
        var (a, _) = Link();
        var sub = Path.Combine(_dirB, "x");
        var (ok1, _) = await a.MutateRemoteAsync(FsOp.NewFolder, _dirB, "x", CancellationToken.None);
        Assert.True(ok1);
        Assert.True(Directory.Exists(sub));
        var (ok2, _) = await a.MutateRemoteAsync(FsOp.Rename, sub, "y", CancellationToken.None);
        Assert.True(ok2);
        Assert.True(Directory.Exists(Path.Combine(_dirB, "y")));
        var (ok3, _) = await a.MutateRemoteAsync(FsOp.Delete, Path.Combine(_dirB, "y"), "", CancellationToken.None);
        Assert.True(ok3);
        Assert.False(Directory.Exists(Path.Combine(_dirB, "y")));
    }

    [Fact] // Duyệt thư mục của bên kia trả về đúng thư mục con và file.
    public async Task ListRemote_ReturnsEntries()
    {
        File.WriteAllText(Path.Combine(_dirB, "a.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dirB, "sub"));
        var (a, _) = Link();
        var listing = await a.ListRemoteAsync(_dirB, CancellationToken.None);
        Assert.True(listing.Ok);
        Assert.Contains(listing.Entries, e => e.Name == "a.txt" && !e.IsDirectory);
        Assert.Contains(listing.Entries, e => e.Name == "sub" && e.IsDirectory);
    }

    [Fact] // Upload lưu đúng thư mục đích do bên nhận-thư-mục chỉ định.
    public async Task Upload_SavesToTargetDir()
    {
        File.WriteAllText(Path.Combine(_dirA, "up.txt"), "hello");
        var target = Path.Combine(_dirB, "into");
        var (a, b) = Link();
        FileTransferProgress done = null;
        b.TransferChanged += p => { if (p.Status == TransferStatus.Completed && p.Incoming) done = p; };
        await a.SendFileAsync(Path.Combine(_dirA, "up.txt"), target, CancellationToken.None);
        Assert.NotNull(done);
        Assert.Equal(target, Path.GetDirectoryName(done.SavePath));
        Assert.Equal("hello", File.ReadAllText(done.SavePath));
    }

    [Fact] // Download: máy ở xa gửi file về đúng thư mục local đã chọn.
    public async Task Download_SavesToLocalDir()
    {
        var bytes = new byte[5000];
        new Random(3).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(_dirB, "down.bin"), bytes);
        var saveDir = Path.Combine(_dirA, "got");
        var (a, _) = Link();
        FileTransferProgress done = null;
        a.TransferChanged += p => { if (p.Status == TransferStatus.Completed && p.Incoming) done = p; };
        await a.RequestDownloadAsync(Path.Combine(_dirB, "down.bin"), saveDir, CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (done == null && sw.Elapsed.TotalSeconds < 5) await Task.Delay(20);

        Assert.NotNull(done);
        Assert.Equal(saveDir, Path.GetDirectoryName(done.SavePath));
        Assert.Equal(bytes, File.ReadAllBytes(done.SavePath));
    }
}
