using System;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;

namespace RDM.Data;

/// <summary>
/// Cung cấp connection ngắn hạn. Không giữ connection hay transaction qua lời gọi UI/mạng,
/// để không khóa Database kéo dài.
/// </summary>
public sealed class SQLiteContext
{
    public const string DatabaseFileName = "RDM_Database.db";

    public string DatabasePath { get; }
    private readonly string _connectionString;

    public SQLiteContext(string databasePath)
    {
        DatabasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _connectionString = BuildConnectionString(databasePath);
    }

    /// <summary>Thư mục dữ liệu: biến môi trường RDM_DATA_DIR hoặc %LOCALAPPDATA%\RDM.</summary>
    public static string DefaultDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("RDM_DATA_DIR");
        return !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDM");
    }

    public static string BuildConnectionString(string path) => new SQLiteConnectionStringBuilder
    {
        DataSource = path,
        Version = 3,
        JournalMode = SQLiteJournalModeEnum.Wal,
        ForeignKeys = true,
        BusyTimeout = 5000,
        Pooling = true,
        FailIfMissing = false
    }.ToString();

    public SQLiteConnection Open()
    {
        var c = new SQLiteConnection(_connectionString);
        c.Open();
        return c;
    }

    public void InTransaction(Action<SQLiteConnection, SQLiteTransaction> work)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        work(c, tx);
        tx.Commit();
    }

    public T InTransaction<T>(Func<SQLiteConnection, SQLiteTransaction, T> work)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var result = work(c, tx);
        tx.Commit();
        return result;
    }
}

/// <summary>Tiện ích ADO.NET: mọi giá trị đều đi qua tham số, chống SQL Injection.</summary>
internal static class Db
{
    public static SQLiteCommand Cmd(this SQLiteConnection c, string sql, SQLiteTransaction tx = null, params (string name, object value)[] args)
    {
        var cmd = new SQLiteCommand(sql, c, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, Normalize(value));
        return cmd;
    }

    public static int Exec(this SQLiteConnection c, string sql, SQLiteTransaction tx = null, params (string, object)[] args)
    {
        using var cmd = c.Cmd(sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    public static long Scalar(this SQLiteConnection c, string sql, SQLiteTransaction tx = null, params (string, object)[] args)
    {
        using var cmd = c.Cmd(sql, tx, args);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    public static long InsertReturningId(this SQLiteConnection c, string sql, SQLiteTransaction tx = null, params (string, object)[] args)
    {
        c.Exec(sql, tx, args);
        return c.LastInsertRowId;
    }

    private static object Normalize(object value) => value switch
    {
        null => DBNull.Value,
        DateTime d => Iso(d),
        bool b => b ? 1 : 0,
        Enum e => Convert.ToInt32(e, CultureInfo.InvariantCulture),
        _ => value
    };

    /// <summary>Thời gian luôn lưu UTC dạng ISO-8601.</summary>
    public static string Iso(DateTime d) => (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string Str(this IDataRecord r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetValue(i).ToString(); }
    public static long Long(this IDataRecord r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
    public static long? LongN(this IDataRecord r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? (long?)null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
    public static bool Bool(this IDataRecord r, string name) => r.Long(name) != 0;
    public static DateTime Date(this IDataRecord r, string name) => r.DateN(name) ?? DateTime.MinValue;

    public static DateTime? DateN(this IDataRecord r, string name)
    {
        var s = r.Str(name);
        if (s == null) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
    }

    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
