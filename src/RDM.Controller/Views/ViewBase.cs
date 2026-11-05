using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Forms;
using RDM.Controller.Theming;
using RDM.Data;

namespace RDM.Controller.Views;

/// <summary>Trang nội dung. RefreshAsync đọc dữ liệu ở luồng nền để UI không bị treo.</summary>
public abstract class ViewBase : UserControl
{
    protected RdmRuntime Rt { get; }

    protected ViewBase(RdmRuntime rt)
    {
        Rt = rt;
        BackColor = Theme.Page;
        Dock = DockStyle.Fill;
        Padding = new Padding(Dpi.S(10));
        Font = Theme.Font();
        DoubleBuffered = true;
    }

    public abstract Task RefreshAsync();

    /// <summary>Trang có hiển thị Online/Offline thì trả về true để được làm mới khi trạng thái đổi.</summary>
    public virtual bool ShowsPresence => false;

    /// <summary>Chạy công việc nền rồi cập nhật UI. Bỏ qua kết quả nếu trang đã đóng trong lúc chờ.</summary>
    protected async Task LoadAsync<T>(Func<T> read, Action<T> apply)
    {
        try
        {
            var data = await Task.Run(read);
            if (!IsDisposed) apply(data);
        }
        catch (Exception ex) when (!IsDisposed) { Ui.Error(FindForm(), ex); }
    }

    /// <summary>Chạy thao tác nặng (Import, Backup) với nút bị vô hiệu hóa trong lúc chờ.</summary>
    protected async Task BusyAsync(Control trigger, Func<Task> work)
    {
        if (trigger != null) trigger.Enabled = false;
        UseWaitCursor = true;
        try { await work(); }
        catch (Exception ex) when (!IsDisposed) { Ui.Error(FindForm(), ex); }
        finally
        {
            if (!IsDisposed) { if (trigger != null) trigger.Enabled = true; UseWaitCursor = false; }
        }
    }

    protected static FlowLayoutPanel Toolbar() => new FlowLayoutPanel
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0)
    };
}

/// <summary>
/// Trang cho chức năng thuộc giai đoạn chưa lập trình. Nói rõ trạng thái, không giả lập dữ liệu (Phần 16).
/// </summary>
public sealed class PendingFeatureView : ViewBase
{
    public PendingFeatureView(RdmRuntime rt, string title, string phase, string description) : base(rt)
    {
        var card = new Card();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        panel.Controls.Add(Ui.Text(title, 14f, true));
        panel.Controls.Add(Ui.Text($"Not implemented yet, planned for {phase}.", 10f, false, Theme.TextSecondary));
        var body = Ui.Text(description, 9.5f, false, Theme.TextSecondary);
        body.MaximumSize = new Size(Dpi.S(640), 0);
        panel.Controls.Add(body);
        card.Controls.Add(panel);
        Controls.Add(card);
    }

    public override Task RefreshAsync() => Task.CompletedTask;
}
