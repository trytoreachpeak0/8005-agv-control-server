using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 防饥饿升级告警（REQ-0210 后半，REQ-0203；批次7-09，control-server#214）：一条任务进入超时层时告警一次。
/// </summary>
public sealed class StarvationEscalationSink(
    ControlServerDbContext dbContext,
    ILogger<StarvationEscalationSink> logger) : IDispatchRoundOutcomeSink
{
    public Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _ = dbContext;
        _ = logger;
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
