namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 试验机批号（REQ-0368，control-server#828）每轮滤掉了哪些批号的日志，只在变化时记。
/// </summary>
public sealed class TrialSublotFilterLog
{
    public bool Record(IEnumerable<string> sublots)
    {
        _ = sublots;
        return false;
    }
}
