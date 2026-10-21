namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 试验机批号（REQ-0368，control-server#828）上一轮滤掉了哪些批号，跨轮保留，形状照
/// <see cref="Criteria.ChargingPolicyCommissioningLog"/>。宿主里是单例：派车轮每一轮新建，状态放在轮上会让同一条日志每轮再记一次。
/// </summary>
public sealed class TrialSublotFilterLog
{
    private readonly object _gate = new();
    private string[] _last = [];

    /// <summary>记下这一轮滤掉的需求的批号（每条需求一个）；与上一轮记下的不同时答真（第一轮滤掉 0 条不算变化）。</summary>
    public bool Record(IEnumerable<string> sublots)
    {
        ArgumentNullException.ThrowIfNull(sublots);
        string[] current = [.. sublots.Order(StringComparer.Ordinal)];
        lock (_gate)
        {
            if (current.SequenceEqual(_last, StringComparer.Ordinal))
            {
                return false;
            }
            _last = current;
            return true;
        }
    }
}
