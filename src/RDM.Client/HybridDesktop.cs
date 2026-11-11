using System;
using System.Collections.Generic;
using RDM.Shared.Protocol;

namespace RDM.Client;

/// <summary>
/// Nguồn màn hình + nhận input "lai": bình thường dùng GDI trong phiên người dùng; khi không chụp được
/// (secure desktop: UAC, màn hình khóa) thì lấy khung từ RDM.DesktopAgent (quyền SYSTEM) và chuyển chuột/phím qua đó.
/// Nếu agent không chạy (chưa cài dịch vụ) thì hành xử y hệt GDI + SendInput như trước, không ảnh hưởng gì.
/// Trong lúc dùng agent, khung là TOÀN màn hình ảo nên toạ độ chuột chuẩn hoá khớp trực tiếp; monitor được bỏ qua.
/// </summary>
public sealed class HybridDesktop : IScreenSource, IInputSink
{
    private readonly GdiScreenCapture _gdi = new GdiScreenCapture();
    private readonly SendInputController _local = new SendInputController();
    private readonly AgentChannel _agent;      // null nếu không có agent
    private volatile bool _agentMode;

    public HybridDesktop(AgentChannel agent) => _agent = agent;

    public IReadOnlyList<MonitorInfo> GetMonitors() => _gdi.GetMonitors();

    public RawFrame Capture(int monitor)
    {
        var frame = _gdi.Capture(monitor);
        if (frame != null) { _agentMode = false; return frame; }

        // GDI không chụp được => có thể đang ở secure desktop. Thử lấy từ agent SYSTEM.
        if (_agent != null && _agent.Ok)
        {
            var af = _agent.Capture();
            if (af != null) { _agentMode = true; return af; }
        }
        _agentMode = false;
        return null;
    }

    // Input: đang ở secure desktop (agent cấp khung) thì gửi qua agent; ngược lại tiêm cục bộ.
    public void Mouse(MonitorInfo monitor, MouseInputData m)
    {
        if (_agentMode && _agent != null && _agent.Ok) _agent.Mouse(m);
        else _local.Mouse(monitor, m);
    }

    public void Key(KeyInputData k)
    {
        if (_agentMode && _agent != null && _agent.Ok) _agent.Key(k);
        else _local.Key(k);
    }

    public void Text(string text)
    {
        if (_agentMode && _agent != null && _agent.Ok) _agent.Text(text);
        else _local.Text(text);
    }

    public string Special(SpecialKey key) => _local.Special(key);

    public void ReleaseAll() => _local.ReleaseAll();

    public void Dispose()
    {
        _gdi.Dispose();
        _agent?.Dispose();
    }
}
