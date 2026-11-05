using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;
using RDM.Data.Repositories;
using RDM.Shared.Common;
using RDM.Shared.Models;

namespace RDM.Data.Services;

public sealed class ImportResult
{
    public int Added { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; } = new List<string>();
}

/// <summary>Import/Export danh sách thiết bị. Không bao giờ xuất credential ra file.</summary>
public sealed class ClientTransferService
{
    public static readonly string[] Columns = { "DeviceName", "RemoteID", "Group", "IPAddress", "MacAddress", "OperatingSystem", "Architecture", "Description" };

    private readonly SQLiteContext _ctx;
    private readonly ClientRepository _clients;
    private readonly GroupRepository _groups;
    private readonly AuditLogger _audit;
    private readonly UserSession _session;

    public ClientTransferService(SQLiteContext ctx, ClientRepository clients, GroupRepository groups, AuditLogger audit, UserSession session)
    {
        _ctx = ctx; _clients = clients; _groups = groups; _audit = audit; _session = session;
    }

    public void Export(string path, IEnumerable<ClientDevice> devices)
    {
        _session.Demand(Permission.ManageClients);
        var rows = devices.Select(d => new[] { d.DeviceName, d.RemoteId, d.GroupName, d.IpAddress, d.MacAddress, d.OperatingSystem, d.Architecture, d.Description }).ToList();
        if (IsExcel(path)) WriteExcel(path, rows); else WriteCsv(path, rows);
        _audit.Write(AuditEvents.ClientsExported, _session.Username, details: $"Exported {rows.Count} devices to {Path.GetFileName(path)}");
    }

    /// <summary>Import trong một transaction. Dòng lỗi được báo lại kèm số dòng, Remote ID đã có thì bỏ qua.</summary>
    public ImportResult Import(string path)
    {
        _session.Demand(Permission.ManageClients);
        var rows = IsExcel(path) ? ReadExcel(path) : ReadCsv(path);
        var result = new ImportResult();
        if (rows.Count == 0) { result.Errors.Add("The file has no data."); return result; }

        var index = rows[0].Select((h, i) => (name: h?.Trim() ?? "", i)).GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var missing = new[] { "DeviceName", "RemoteID" }.Where(r => !index.ContainsKey(r)).ToList();
        if (missing.Count > 0) { result.Errors.Add("Missing required columns: " + string.Join(", ", missing) + "."); return result; }

        string Cell(string[] row, string column) =>
            index.TryGetValue(column, out var i) && i < row.Length ? Clean(row[i]) : null;

        _ctx.InTransaction((c, tx) =>
        {
            var seen = new HashSet<string>();
            for (var n = 1; n < rows.Count; n++)
            {
                var row = rows[n];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                var d = new ClientDevice
                {
                    DeviceName = Cell(row, "DeviceName"), RemoteId = Cell(row, "RemoteID"), IpAddress = Cell(row, "IPAddress"), MacAddress = Cell(row, "MacAddress"),
                    OperatingSystem = Cell(row, "OperatingSystem"), Architecture = Cell(row, "Architecture"), Description = Cell(row, "Description"),
                    DeviceId = Guid.NewGuid().ToString("N"), CreatedDate = DateTime.UtcNow, IsEnabled = true
                };
                try { ClientService.Normalize(d); }
                catch (ValidationException ex) { result.Errors.Add($"Row {n + 1}: {ex.Message}"); continue; }

                if (!seen.Add(d.RemoteId) || c.Scalar("SELECT COUNT(*) FROM Clients WHERE RemoteID=@r", tx, ("@r", d.RemoteId)) > 0)
                {
                    result.Skipped++;
                    continue;
                }
                var group = Cell(row, "Group");
                if (!string.IsNullOrWhiteSpace(group)) d.GroupId = _groups.GetOrCreate(c, tx, group);
                _clients.Insert(c, tx, d);
                result.Added++;
            }
        });
        _audit.Write(AuditEvents.ClientsImported, _session.Username, details: $"{Path.GetFileName(path)}: added {result.Added}, skipped {result.Skipped}, errors {result.Errors.Count}");
        return result;
    }

    private static bool IsExcel(string path) => string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string s) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s; }

    /// <summary>Chống CSV/Formula Injection: ô bắt đầu bằng ký tự công thức được thêm dấu nháy.</summary>
    public static string NeutralizeFormula(string value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        return "=+-@\t\r".IndexOf(value[0]) >= 0 ? "'" + value : value;
    }

    private static void WriteCsv(string path, List<string[]> rows)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(",", Columns));
        foreach (var row in rows) w.WriteLine(string.Join(",", row.Select(v => Quote(NeutralizeFormula(v)))));
    }

    private static string Quote(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>Đọc CSV theo RFC 4180: hỗ trợ dấu phẩy, xuống dòng và nháy kép trong ô.</summary>
    public static List<string[]> ReadCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else cell.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r')); cell.Clear();
                rows.Add(row.ToArray()); row.Clear();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); rows.Add(row.ToArray()); }
        return rows;
    }

    private static void WriteExcel(string path, List<string[]> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Clients");
        for (var c = 0; c < Columns.Length; c++) ws.Cell(1, c + 1).Value = Columns[c];
        ws.Row(1).Style.Font.Bold = true;
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < Columns.Length; c++)
            {
                var cell = ws.Cell(r + 2, c + 1);
                // Ghi dạng chuỗi để Remote ID không bị đổi thành số và không có công thức nào được thực thi.
                cell.Style.NumberFormat.Format = "@";
                cell.SetValue(rows[r][c] ?? string.Empty);
            }
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private static List<string[]> ReadExcel(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        var rows = new List<string[]>();
        if (used == null) return rows;
        foreach (var r in used.Rows())
            rows.Add(r.Cells(1, used.ColumnCount()).Select(c => c.GetFormattedString()).ToArray());
        return rows;
    }
}
