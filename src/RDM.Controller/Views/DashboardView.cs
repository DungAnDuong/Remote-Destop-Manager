using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Forms;
using RDM.Controller.Theming;
using RDM.Data;
using RDM.Shared.Common;
using RDM.Shared.Models;

namespace RDM.Controller.Views;

public sealed class DashboardView : ViewBase
{
    private const int ChartDays = 14;
    private readonly StatCard _total = new StatCard("Total Devices");
    private readonly StatCard _online = new StatCard("Online Devices");
    private readonly StatCard _offline = new StatCard("Offline Devices");
    private readonly StatCard _sessions = new StatCard("Active Sessions");
    private readonly StatCard _connections = new StatCard("Total Connections");
    private readonly PresenceBar _presence = new PresenceBar();
    private readonly BarChart _connectionChart = new BarChart();
    private readonly BarChart _durationChart = new BarChart { AxisFormat = FormatMinutes };
    private readonly GridView<AuditEntry> _recent = new GridView<AuditEntry>();
    private readonly Label _system = Ui.Text("", 9f, false, Theme.TextSecondary);
    private readonly ModernInput _quickId = new ModernInput("Remote ID");
    private readonly ModernInput _quickPass = new ModernInput("Password", true);
    private readonly ModernButton _connect = new ModernButton("CONNECT", ButtonKind.Primary);

    private static string FormatMinutes(double m) => m.ToString("0.#", CultureInfo.InvariantCulture) + " min";

    public DashboardView(RdmRuntime rt) : base(rt)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(116)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(212)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var stats = Grid(5);
        var cards = new[] { _total, _online, _offline, _sessions, _connections };
        for (var i = 0; i < cards.Length; i++) stats.Controls.Add(cards[i], i, 0);
        layout.Controls.Add(stats, 0, 0);

        var middle = Grid(3);
        middle.Controls.Add(BuildQuickConnect(), 0, 0);
        var presenceCard = new Card("Online / Offline");
        presenceCard.Controls.Add(_presence);
        middle.Controls.Add(presenceCard, 1, 0);
        var systemCard = new Card("System Status");
        // Dock.Fill để nhãn nằm trong Padding của thẻ, không đè lên tiêu đề, và tự xuống dòng theo độ rộng thẻ.
        _system.AutoSize = false;
        _system.Dock = DockStyle.Fill;
        systemCard.Controls.Add(_system);
        middle.Controls.Add(systemCard, 2, 0);
        layout.Controls.Add(middle, 0, 1);

        var bottom = Grid(3);
        bottom.Controls.Add(Wrap("Connections per Day", _connectionChart), 0, 0);
        bottom.Controls.Add(Wrap("Control Time (minutes)", _durationChart), 1, 0);
        _recent.AddColumn("Time", a => a.Timestamp == DateTime.MinValue ? "–" : DateTime.SpecifyKind(a.Timestamp, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm"), 60)
               .AddColumn("Active", a => a.EventType, 80);
        bottom.Controls.Add(Wrap("Recent Activities", _recent), 2, 0);
        layout.Controls.Add(bottom, 0, 2);

        Controls.Add(layout);
    }

    private static TableLayoutPanel Grid(int columns)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
        for (var i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return t;
    }

    private static Card Wrap(string title, Control content)
    {
        var card = new Card(title);
        card.Controls.Add(content);
        return card;
    }

    private Card BuildQuickConnect()
    {
        var card = new Card("Quick Connect");
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        void Fit(Control c) => c.Width = Math.Max(Dpi.S(120), flow.ClientSize.Width - Dpi.S(4));
        flow.Resize += (s, e) => { Fit(_quickId); Fit(_quickPass); Fit(_connect); };
        _connect.Margin = new Padding(0, 0, 0, Dpi.S(4));
        var note = Ui.Text(ProductMode.Managed ? "Connects over your local network or the Internet." : "Direct when possible, otherwise through the Relay Server.", 8.5f, false, Theme.TextMuted);
        _connect.Enabled = Rt.Session.Can(Permission.UseCredentials);
        _connect.Click += async (s, e) =>
        {
            _connect.Enabled = false;
            try
            {
                var password = _quickPass.Text;
                await RDM.Controller.Services.ConnectFlow.ConnectAsync(Rt, FindForm(), _quickId.Text, password.Length == 0 ? null : password);
                _quickPass.Text = string.Empty;
            }
            catch (Exception ex) { Ui.Error(FindForm(), ex); }
            finally { if (!IsDisposed) _connect.Enabled = true; }
        };
        flow.Controls.AddRange(new Control[] { _quickId, _quickPass, _connect, note });
        card.Controls.Add(flow);
        return card;
    }

    public override bool ShowsPresence => true;

    public override Task RefreshAsync() => LoadAsync(() => new
    {
        Stats = Rt.GetDashboardStats(),
        Presence = Rt.Presence.Count(Rt.Clients.Query(null, null, false).Select(c => c.RemoteId)),
        PresenceText = Rt.Presence.DescribeAvailability(),
        RelayStatus = Rt.Presence.Availability,
        Daily = Rt.History.Daily(ChartDays),
        Audit = Rt.AuditLogs.Query(null, 30),
        Platform = PlatformInfo.Detect()
    }, data =>
    {
        _total.SetValue(data.Stats.TotalDevices.ToString(CultureInfo.CurrentCulture));
        _sessions.SetValue(RDM.Controller.Services.SessionRegistry.All.Count.ToString(CultureInfo.CurrentCulture));
        _connections.SetValue(data.Stats.TotalConnections.ToString(CultureInfo.CurrentCulture));
        _online.SetValue(data.Presence?.online.ToString(CultureInfo.CurrentCulture) ?? "–", data.PresenceText);
        _offline.SetValue(data.Presence?.offline.ToString(CultureInfo.CurrentCulture) ?? "–", data.PresenceText);
        _presence.SetData(data.Presence?.online, data.Presence?.offline, data.Stats.TotalDevices);
        _connectionChart.SetData(data.Daily.Select(d => new ChartPoint
        {
            Label = d.Day.ToString("dd/MM", CultureInfo.InvariantCulture), Value = d.Connections, Tooltip = $"{d.Day:dd/MM}: {d.Connections} connections"
        }));
        _durationChart.SetData(data.Daily.Select(d => new ChartPoint
        {
            Label = d.Day.ToString("dd/MM", CultureInfo.InvariantCulture), Value = d.DurationSeconds / 60.0, Tooltip = $"{d.Day:dd/MM}: {Ui.Duration(d.DurationSeconds)}"
        }));
        _recent.SetItems(data.Audit);
        _system.Text = string.Join("\n", SystemLines(data.Stats, data.Platform, data.RelayStatus, data.PresenceText));
    });

    /// <summary>Trạng thái dùng ký hiệu cùng nhãn chữ, không dựa vào màu.</summary>
    private static IEnumerable<string> SystemLines(DashboardStats s, PlatformInfo p, RDM.Data.Services.PresenceAvailability relay, string relayText)
    {
        yield return $"✔ Database: running, schema v{s.SchemaVersion}";
        yield return $"✔ Storage encryption: {s.ActiveKeys} active keys";
        yield return (s.FailedLogins24h > 0 ? "▲ " : "✔ ") + $"Failed sign-ins (24 h): {s.FailedLogins24h}";
        yield return (p.IsEndOfLifeOs ? "▲ " : "✔ ") + p.Note;
        if (ProductMode.Managed) yield break;
        yield return (relay == RDM.Data.Services.PresenceAvailability.Connected ? "✔ " : relay == RDM.Data.Services.PresenceAvailability.NotConfigured ? "– " : "▲ ") + "Relay Server: " + (relay == RDM.Data.Services.PresenceAvailability.Connected ? "connected" : relayText);
    }
}
