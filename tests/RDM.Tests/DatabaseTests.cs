using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RDM.Data;
using RDM.Data.Services;
using RDM.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace RDM.Tests;

public class PlatformTests
{
    [Fact] // Xác nhận test đang chạy đúng kiến trúc được yêu cầu, và SQLite native nạp được.
    public void ProcessArchitectureMatchesBuild_AndSqliteNativeLoads()
    {
#if RDM_x86
        Assert.False(Environment.Is64BitProcess);
#elif RDM_x64
        Assert.True(Environment.Is64BitProcess);
#else
        Assert.Fail("Thiếu hằng RDM_x86 hoặc RDM_x64.");
#endif
        using var db = new TestDb();
        using var c = db.Rt.Context.Open();
        using var cmd = new SQLiteCommand("SELECT sqlite_version()", c);
        Assert.False(string.IsNullOrEmpty(cmd.ExecuteScalar()?.ToString()));
    }
}

public class SchemaTests
{
    private static List<string> Names(SQLiteConnection c, string type)
    {
        var list = new List<string>();
        using var cmd = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type=@t", c);
        cmd.Parameters.AddWithValue("@t", type);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    [Fact]
    public void AutoCreatesAllRequiredTablesAndIndexes()
    {
        using var db = new TestDb();
        using var c = db.Rt.Context.Open();
        var tables = Names(c, "table");
        foreach (var t in new[] { "Clients", "Groups", "ConnectionHistory", "Users", "Settings", "Favorites", "AuditLogs", "SecurityKeys", "SchemaVersion", "Tags", "ClientTags" })
            Assert.Contains(t, tables);
        var indexes = Names(c, "index");
        foreach (var i in new[] { "IX_Clients_GroupID", "IX_Clients_DeviceName", "IX_History_StartTime", "IX_Audit_Timestamp", "IX_Keys_PurposeStatus" })
            Assert.Contains(i, indexes);
        Assert.Equal(DatabaseMigration.LatestVersion, DatabaseMigration.CurrentVersion(c));
    }

    [Fact]
    public void FileNameAndPragmasAreAsSpecified()
    {
        using var db = new TestDb();
        Assert.Equal("RDM_Database.db", Path.GetFileName(db.Rt.Context.DatabasePath));
        using var c = db.Rt.Context.Open();
        using (var cmd = new SQLiteCommand("PRAGMA journal_mode", c)) Assert.Equal("wal", cmd.ExecuteScalar()?.ToString());
        using (var cmd = new SQLiteCommand("PRAGMA foreign_keys", c)) Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void ReopeningIsIdempotent_DataSurvives()
    {
        using var db = new TestDb().LoginAdmin();
        db.AddClient("PC", "123456789");
        DatabaseMigration.Migrate(db.Rt.Context);
        Assert.Equal(1, db.Rt.Clients.Count());
    }

    [Fact]
    public void NewerDatabaseVersion_IsRefusedAndUntouched()
    {
        using var db = new TestDb();
        using (var c = db.Rt.Context.Open())
        using (var cmd = new SQLiteCommand("PRAGMA user_version = 999", c)) cmd.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => DatabaseMigration.Migrate(db.Rt.Context));
        using var c2 = db.Rt.Context.Open();
        Assert.Equal(999, DatabaseMigration.CurrentVersion(c2));
    }
}

public class ClientRepositoryTests
{
    private readonly ITestOutputHelper _out;
    public ClientRepositoryTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Search_FindsByEachColumn_AndWildcardsAreLiteral()
    {
        using var db = new TestDb().LoginAdmin();
        var g = db.Rt.Groups.Add("Nhà máy A", null);
        db.Rt.Clients.Add(new ClientDevice { DeviceName = "PC_01", RemoteId = "111111111", IpAddress = "10.0.0.5", OperatingSystem = "Windows 7", GroupId = g }, null, new[] { "kho" });
        db.AddClient("PCX01", "222222222");
        Assert.Single(db.Rt.Clients.Query("PC_", null, false));
        Assert.Single(db.Rt.Clients.Query("10.0.0.5", null, false));
        Assert.Single(db.Rt.Clients.Query("Windows 7", null, false));
        Assert.Single(db.Rt.Clients.Query("Nhà máy", null, false));
        Assert.Single(db.Rt.Clients.Query("kho", null, false));
        Assert.Equal(2, db.Rt.Clients.Query("PC", null, false).Count);
        Assert.Single(db.Rt.Clients.Query(null, g, false));
    }

    [Fact]
    public void DuplicateRemoteId_Rejected_InvalidInputRejected()
    {
        using var db = new TestDb().LoginAdmin();
        db.AddClient("A", "123456789");
        Assert.Throws<ValidationException>(() => db.AddClient("B", "123 456 789"));
        Assert.Throws<ValidationException>(() => db.AddClient("C", "abc"));
        Assert.Throws<ValidationException>(() => db.AddClient("   ", "987654321"));
    }

    [Fact]
    public void DeletingGroup_KeepsClients_DeletingClient_RemovesLinks()
    {
        using var db = new TestDb().LoginAdmin();
        var g = db.Rt.Groups.Add("G", null);
        var id = db.Rt.Clients.Add(new ClientDevice { DeviceName = "PC", RemoteId = "123456789", GroupId = g }, null, new[] { "t1" });
        db.Rt.Clients.SetFavorite(id, true);
        Assert.Equal(1, db.Rt.Groups.GetAll().Single().DeviceCount);
        db.Rt.Groups.Delete(g, "G");
        Assert.Null(db.Rt.Clients.Query(null, null, false).Single().GroupId);
        db.Rt.Clients.Delete(id);
        using var c = db.Rt.Context.Open();
        using var cmd = new SQLiteCommand("SELECT (SELECT COUNT(*) FROM Favorites) + (SELECT COUNT(*) FROM ClientTags)", c);
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Favorites_ArePerUser()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.AddClient("PC", "123456789");
        db.Rt.Clients.SetFavorite(id, true);
        Assert.True(db.Rt.Clients.Query(null, null, true).Single().IsFavorite);
        db.LoginAs("nv01", "Mat-Khau-Nhan-Vien1", UserRole.Viewer);
        Assert.Empty(db.Rt.Clients.Query(null, null, true));
        Assert.False(db.Rt.Clients.Query(null, null, false).Single().IsFavorite);
    }

    [Fact] // Yêu cầu "hàng nghìn thiết bị".
    public void Performance_FiveThousandDevices()
    {
        using var db = new TestDb().LoginAdmin();
        var transfer = Path.Combine(db.Dir, "big.csv");
        File.WriteAllLines(transfer, new[] { "DeviceName,RemoteID,Group" }.Concat(Enumerable.Range(0, 5000).Select(i => $"PC-{i:0000},{100000000 + i},Nhóm {i % 20}")));
        var sw = Stopwatch.StartNew();
        var result = db.Rt.Transfer.Import(transfer);
        var importMs = sw.ElapsedMilliseconds;
        Assert.Equal(5000, result.Added);
        sw.Restart();
        var all = db.Rt.Clients.Query(null, null, false);
        var queryMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var found = db.Rt.Clients.Query("PC-4999", null, false);
        var searchMs = sw.ElapsedMilliseconds;
        _out.WriteLine($"Import 5000: {importMs} ms; đọc tất cả: {queryMs} ms; tìm kiếm: {searchMs} ms");
        Assert.Equal(5000, all.Count);
        Assert.Single(found);
        Assert.True(queryMs < 3000, $"Đọc danh sách chậm: {queryMs} ms");
        Assert.True(searchMs < 1000, $"Tìm kiếm chậm: {searchMs} ms");
    }

    [Fact] // Thao tác đồng thời không gây lỗi "database is locked".
    public async Task ConcurrentWritersAndReaders_DoNotFail()
    {
        using var db = new TestDb().LoginAdmin();
        var repo = db.Rt.AuditLogs;
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                repo.Insert(new AuditEntry { Timestamp = DateTime.UtcNow, EventType = "Load", Username = "t" + t });
                db.Rt.History.Start((100000000 + t).ToString(), "PC", "u", "LAN", "Connected");
                db.Rt.Clients.Count();
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(400, db.Rt.History.Count());
    }
}

public class HistoryTests
{
    [Fact]
    public void Lifecycle_ComputesDuration_DailyStatsAndRecent()
    {
        using var db = new TestDb();
        var a = db.Rt.History.Start("111111111", "A", "u", "LAN", "Connected");
        db.Rt.History.Start("111111111", "A", "u", "LAN", "Failed");
        db.Rt.History.Start("222222222", "B", "u", "Relay", "Failed");
        db.Rt.History.Finish(a, "Disconnected");
        var rows = db.Rt.History.Query(null, 10);
        var done = rows.Single(r => r.Id == a);
        Assert.NotNull(done.EndTime);
        Assert.True(done.DurationSeconds >= 0);
        Assert.Equal(2, db.Rt.History.Recent(10).Count);
        var daily = db.Rt.History.Daily(14);
        Assert.Equal(14, daily.Count);
        Assert.Equal(3, daily.Last().Connections);
        Assert.Equal(0, daily.First().Connections);
    }
}

public class ImportExportTests
{
    private static List<ClientDevice> Sample() => new List<ClientDevice>
    {
        new ClientDevice { DeviceName = "Máy, có \"nháy\" tiếng Việt", RemoteId = "012345678", GroupName = "Nhà máy A", IpAddress = "10.0.0.1", Description = "dòng 1\ndòng 2" },
        new ClientDevice { DeviceName = "PC-02", RemoteId = "222222222" }
    };

    [Theory]
    [InlineData("list.csv")]
    [InlineData("list.xlsx")]
    public void RoundTrip_PreservesData_IncludingLeadingZero(string file)
    {
        using var db = new TestDb().LoginAdmin();
        var path = Path.Combine(db.Dir, file);
        db.Rt.Transfer.Export(path, Sample());
        var r = db.Rt.Transfer.Import(path);
        Assert.Empty(r.Errors);
        Assert.Equal(2, r.Added);
        var first = db.Rt.Clients.Query("012345678", null, false).Single();
        Assert.Equal("Máy, có \"nháy\" tiếng Việt", first.DeviceName);
        Assert.Equal("Nhà máy A", first.GroupName);
        Assert.Contains("dòng 2", first.Description);
    }

    [Fact]
    public void Import_ReportsBadRows_SkipsDuplicates_KeepsGoodRows()
    {
        using var db = new TestDb().LoginAdmin();
        db.AddClient("Đã có", "111111111");
        var path = Path.Combine(db.Dir, "x.csv");
        File.WriteAllLines(path, new[] { "DeviceName,RemoteID", "A,111111111", "B,abc", ",333333333", "C,444444444", "D,444444444", "" });
        var r = db.Rt.Transfer.Import(path);
        Assert.Equal(1, r.Added);
        Assert.Equal(2, r.Skipped);
        Assert.Equal(2, r.Errors.Count);
        Assert.Contains(r.Errors, e => e.Contains("Row 3"));
    }

    [Fact]
    public void Import_MissingRequiredColumn_IsReported()
    {
        using var db = new TestDb().LoginAdmin();
        var path = Path.Combine(db.Dir, "x.csv");
        File.WriteAllLines(path, new[] { "Name,Id", "A,1" });
        var error = db.Rt.Transfer.Import(path).Errors.Single();
        Assert.Contains("DeviceName", error);
        Assert.Contains("RemoteID", error);
    }

    [Fact] // CSV Injection
    public void Export_NeutralizesFormulas_AndNeverContainsCredentials()
    {
        using var db = new TestDb().LoginAdmin();
        var id = db.Rt.Clients.Add(new ClientDevice { DeviceName = "=cmd|'/c calc'!A1", RemoteId = "123456789", Description = "@SUM(1)" }, "CredentialBiMat", null);
        var path = Path.Combine(db.Dir, "x.csv");
        db.Rt.Transfer.Export(path, db.Rt.Clients.Query(null, null, false));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(",=cmd", "," + text.Split('\n')[1]);
        Assert.Contains("'=cmd", text);
        Assert.Contains("'@SUM", text);
        Assert.DoesNotContain("CredentialBiMat", text);
    }

    [Fact]
    public void Import_RequiresPermission()
    {
        using var db = new TestDb().LoginAdmin();
        db.LoginAs("viewer", "Viewer-Password-1", UserRole.Viewer);
        Assert.Throws<UnauthorizedAccessException>(() => db.Rt.Transfer.Import(Path.Combine(db.Dir, "x.csv")));
    }
}
