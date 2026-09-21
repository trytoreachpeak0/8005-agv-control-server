namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 轮末的几个汇总，按列表次序各调一次（批次7-09，control-server#214）：结构性阻断在前，防饥饿升级在后。
/// </summary>
/// <remarks>
/// 派车轮只认一个 <see cref="IDispatchRoundOutcomeSink"/>，这个组合让第二个汇总加进来时不必改派车轮。
/// 一个抛出就不再调后面的，与只有一个时「汇总抛出即整轮抛出」相同。
/// </remarks>
public sealed class DispatchRoundOutcomeSinks(IReadOnlyList<IDispatchRoundOutcomeSink> sinks)
    : IDispatchRoundOutcomeSink
{
    private readonly IDispatchRoundOutcomeSink[] _sinks = [.. sinks];

    public async Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        foreach (IDispatchRoundOutcomeSink sink in _sinks)
        {
            await sink.RecordAsync(outcome, cancellationToken).ConfigureAwait(false);
        }
    }
}
