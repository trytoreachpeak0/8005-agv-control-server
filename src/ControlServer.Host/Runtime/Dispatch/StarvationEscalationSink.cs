using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 防饥饿升级告警（REQ-0210 后半，REQ-0203；批次7-09，control-server#214）：一条任务进入超时层时告警一次。
/// </summary>
/// <remarks>
/// <para>
/// <b>「是否超时」与排序读的是同一个判断。</b>这里用 <see cref="TaskStarvation.Assess"/> 按本轮的时刻、本轮读一次的分区归属表
/// 与每区参数重算——正是派车轮排序前给每条任务算的那一份，输入相同，结论相同。轮中导入的新版本因此本轮既不改排序、
/// 也不触发告警，下一轮一起生效。
/// </para>
/// <para>
/// <b>告警的载体是积压行上的 <c>StarvationEscalatedAt</c> 与 <c>StarvationEscalationParameterVersion</c></b>
/// （批次7-01，control-server#206 留的两列）。积压行按需求一行，需求离开目录只改原因码、不删行，所以同一条需求
/// 重启之后、离开目录再回来之后都看得到自己已经告过警。标记与所用版本在同一次保存里落库；日志在提交之后才写，
/// 所以崩在保存上时库里没有标记、日志里也没有告警，下一轮补发一次。反过来「提交了、日志还没写就崩」会丢一条日志行，
/// 但告警本身在库里（看板读的是这两列，批次7-12），不会重发。
/// </para>
/// <para>
/// <b>只对还在等的任务告警</b>：本轮已经接走的不算（它不再等了），积压行还没有的也不算（没有车判过它，下一轮有了行再判）。
/// 告警只进服务端日志与积压行，不经 <c>blockingFacts</c>：它说的是「这条任务等太久了」，不是哪辆车出了故障。
/// </para>
/// <para>
/// <b>阈值未配置（未批准）时什么都不做</b>——那是上线时现场实际走的路径（REQ-0203 降级：继续计龄、不升级）。
/// </para>
/// </remarks>
public sealed class StarvationEscalationSink(
    ControlServerDbContext dbContext,
    ILogger<StarvationEscalationSink> logger) : IDispatchRoundOutcomeSink
{
    private static readonly Action<ILogger, string, string, string, long, long, long, Exception?> LogEscalated =
        LoggerMessage.Define<string, string, string, long, long, long>(
            LogLevel.Warning,
            new EventId(2161, nameof(LogEscalated)),
            "Starvation escalation: demand {DemandId} ({TransportDemandKey}) in dispatch zone {DispatchZone} has waited " +
            "{WaitingSeconds} s since it was created locally, past the zone's {ThresholdSeconds} s anti-starvation " +
            "threshold (dispatch zone parameter version {ParameterVersion}). It now goes ahead of every task that is not " +
            "overdue; hard gates still apply.");

    public async Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        DispatchRoundFacts round = outcome.Round;
        if (round.ZoneParameters is null)
        {
            return;
        }

        Dictionary<string, (AcceptedDemandSnapshot Demand, TaskStarvationStanding Standing)> overdue = new(StringComparer.Ordinal);
        foreach (AcceptedDemandSnapshot demand in round.Catalog.Items)
        {
            if (StillWaiting(round, demand.DemandId) &&
                TaskStarvation.Assess(demand, round.Now, round.AreaAssignments, round.ZoneParameters) is { Overdue: true } standing)
            {
                overdue.TryAdd(demand.DemandId, (demand, standing));
            }
        }
        if (overdue.Count == 0)
        {
            return;
        }

        string[] demandIds = [.. overdue.Keys];
        List<JourneyBacklogRow> raised = [];
        foreach (JourneyBacklogRow row in await dbContext.JourneyBacklog
                     .Where(row => demandIds.Contains(row.DemandId))
                     .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            if (row.StarvationEscalatedAt is not null || row.AcceptedAt is not null)
            {
                continue;
            }
            row.StarvationEscalatedAt = round.Now;
            row.StarvationEscalationParameterVersion = overdue[row.DemandId].Standing.ParameterVersion;
            raised.Add(row);
        }
        if (raised.Count == 0)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (JourneyBacklogRow row in raised)
        {
            (AcceptedDemandSnapshot demand, TaskStarvationStanding standing) = overdue[row.DemandId];
            LogEscalated(
                logger,
                demand.DemandId,
                demand.TransportDemandKey,
                standing.DispatchZone!,
                (long)standing.WaitingAge.TotalSeconds,
                standing.ThresholdSeconds!.Value,
                standing.ParameterVersion!.Value,
                null);
        }
    }

    /// <summary>
    /// 本轮结束时它是否还在等：没有被本轮接走。受理被拒的认领（control-server#242）留在 <c>AcceptedDemandIds</c> 里，
    /// 但这条需求其实没人接，仍在等。
    /// </summary>
    private static bool StillWaiting(DispatchRoundFacts round, string demandId) =>
        !round.AcceptedDemandIds.Contains(demandId) || round.ClaimsIntakeRefused.Contains(demandId);
}
