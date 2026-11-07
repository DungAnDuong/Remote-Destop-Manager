using System.Drawing;
using System.Windows.Forms;
using RDM.Controller.Controls;
using RDM.Controller.Theming;

namespace RDM.Client;

/// <summary>Cửa sổ chat và truyền file trên máy được điều khiển. Đóng chỉ ẩn đi, không ảnh hưởng phiên.</summary>
public sealed class ChatWindow : Form
{
    public ChatPanel Panel { get; } = new ChatPanel();

    public ChatWindow(string peer)
    {
        Text = "RDM - Chat & Files";
        Icon = Theme.AppIcon;
        Font = Theme.Font();
        BackColor = Theme.Page;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(Dpi.S(360), Dpi.S(380));
        Size = new Size(Dpi.S(440), Dpi.S(520));
        Panel.Dock = DockStyle.Fill;
        Panel.PeerName = peer;
        Panel.Title = "Chat - " + peer;
        Panel.CloseRequested += Hide;
        Controls.Add(Panel);
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
}
