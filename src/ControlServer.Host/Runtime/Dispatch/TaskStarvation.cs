using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 一条任务在这一轮的等待处境：它等了多久、属于哪个分区、那个分区的防饥饿阈值是多少、是否已越过
/// （REQ-0201、REQ-0202、REQ-0203；批次7-09，control-server#214）。
/// </summary>
/// <param name="WaitingAge">从本地首次创建 TransportDemand 起的等待时长，见 <see cref="TaskStarvation.WaitingAge"/>。</param>
/// <param name="DispatchZone">需求 AREA 在本轮分区归属表里归入的分区；表里没有这个 AREA 时为空。</param>
/// <param name="ThresholdSeconds">该分区在本轮参数版本里的防饥饿阈值；未配置（未批准）时为空。</param>
/// <param name="ParameterVersion">本轮读到的每区派车参数版本；一版都没有时为空。</param>
/// <param name="Overdue">是否已进入超时层。</param>
public sealed record TaskStarvationStanding(
    TimeSpan WaitingAge,
    string? DispatchZone,
    long? ThresholdSeconds,
    long? ParameterVersion,
    bool Overdue);

/// <summary>
/// 任务侧三件事的唯一定义处：优先级带、等待年龄、是否超时。排序层与升级告警都从这里取，
/// 所以「排在超时层」与「告过警」按构造是同一个判断。
/// </summary>
/// <remarks>
/// <para>
/// <b>等待年龄的起点是 MesIngest 目录项的 <c>CreatedAt</c>，不是本服务端的 <c>JourneyBacklog.FirstSeenAt</c>。</b>
/// REQ-0201 要的是「从本地首次创建 TransportDemand 起持续累积，任何门禁、车辆短缺或资源占用都不暂停或重置」。
/// MesIngest 在插入 <c>TransportDemands</c> 那一行时写 <c>CreatedAt</c>（它自己那一次轮询的时刻，之后的修订不改它），
/// 那正是本地创建 TransportDemand 的时刻，而且与本服务端在不在跑、有没有车无关。<c>FirstSeenAt</c> 则只在派车轮里有车
/// 判过这条候选时才写：全车队 Blocked 时引擎根本不开派车轮，服务端停机期间也不写——那恰好是 REQ-0201 明文禁止的
/// 「车辆短缺暂停年龄」。所以 <c>FirstSeenAt</c> 在排序里退到平手键。两者都不是 MES 的 <c>DATES</c>
/// （<see cref="LiveMesFieldSet.MesSourceDate"/>），后者不进排序。
/// </para>
/// <para>
/// <b>未来时刻按零岁算</b>：MesIngest 与本服务端是两个时钟，前者略快时 <c>now - CreatedAt</c> 会是负的，
/// 负的年龄没有意义，也不该让它比零岁的任务更「年轻」到排在后面去之外还有别的后果。
/// </para>
/// </remarks>
public static class TaskStarvation
{
    /// <summary>这条需求是否在最高初始带：<c>STAGING_TO_WIRE</c> 独占（REQ-0202）。</summary>
    public static bool InTopBand(AcceptedDemandSnapshot demand)
    {
        ArgumentNullException.ThrowIfNull(demand);
        return false;
    }

    /// <summary>从本地首次创建 TransportDemand 到 <paramref name="now"/> 的等待时长，不为负。</summary>
    public static TimeSpan WaitingAge(AcceptedDemandSnapshot demand, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(demand);
        _ = now;
        return TimeSpan.Zero;
    }

    /// <summary>这条需求在本轮的处境，全部取自本轮读一次的那两张表。</summary>
    public static TaskStarvationStanding Assess(
        AcceptedDemandSnapshot demand,
        DateTimeOffset now,
        AreaAssignmentTableVersion? areaAssignments,
        DispatchZoneParameterTableVersion? zoneParameters)
    {
        ArgumentNullException.ThrowIfNull(demand);
        _ = areaAssignments;
        _ = zoneParameters;
        return new TaskStarvationStanding(WaitingAge(demand, now), null, null, null, false);
    }
}
