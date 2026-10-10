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
/// <b>前提：两个钟是同一个钟。</b>年龄 = 本服务端此刻 − MesIngest 写下的 <c>CreatedAt</c>，所以两台机器的钟差会原样变成年龄误差。
/// 今天两者都跑在 factory01 上，是同一个钟；这个前提由部署承担——把 MesIngest 与服务端分到两台机器上的那次变更，
/// 要么保证两台对时（误差远小于阈值），要么先改这里。<b>钟差为负时年龄夹到 0</b>：MesIngest 的钟快过本服务端时
/// <c>now - CreatedAt</c> 是负的，按零算，不会让它提前进超时层；排序比的是 <c>CreatedAt</c> 的先后，不受钟差影响。
/// </para>
/// <para>
/// <b>分区与阈值只取本轮读一次的那两张表</b>，调用方传进来，这里不读库：一轮之内每条任务按同一版判，
/// 轮中导入的新版本下一轮才生效（照分区归属表的做法）。
/// </para>
/// </remarks>
public static class TaskStarvation
{
    /// <summary>这条需求是否在最高初始带：<c>STAGING_TO_WIRE</c> 独占（REQ-0202）。</summary>
    public static bool InTopBand(AcceptedDemandSnapshot demand)
    {
        ArgumentNullException.ThrowIfNull(demand);
        return IsTopBandWorkType(demand.WorkType);
    }

    /// <summary>
    /// 这个任务类型是否在最高初始带。派车排序（<see cref="InTopBand"/>）与看板积压卡片（从业务键读回任务类型，批次7-12）共用这一处。
    /// </summary>
    public static bool IsTopBandWorkType(string? workType) =>
        string.Equals(workType, TransportTaskTypes.StagingToWire, StringComparison.Ordinal);

    /// <summary>
    /// MesIngest 有没有给这条需求的建单时刻。目录项缺 <c>createdAt</c> 时适配器把它留成默认值并告警（审查低 3），
    /// 这里把默认值读作「不知道」。
    /// </summary>
    public static bool HasLocalCreation(AcceptedDemandSnapshot demand)
    {
        ArgumentNullException.ThrowIfNull(demand);
        return demand.CreatedAt != default;
    }

    /// <summary>
    /// 从本地首次创建 TransportDemand 到 <paramref name="now"/> 的等待时长，不为负；不知道建单时刻时为 0——
    /// 从 0001-01-01 算起的两千年会让它立刻越过任何阈值。
    /// </summary>
    public static TimeSpan WaitingAge(AcceptedDemandSnapshot demand, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(demand);
        if (!HasLocalCreation(demand))
        {
            return TimeSpan.Zero;
        }
        TimeSpan age = now - demand.CreatedAt;
        return age > TimeSpan.Zero ? age : TimeSpan.Zero;
    }

    /// <summary>这条需求在本轮的处境，全部取自本轮读一次的那两张表。</summary>
    /// <param name="structurallyBlocked">
    /// 有未解除结构性派车阻断的需求 id（REQ-0210 的另一半）。它们照样计龄，但不进超时层、不做防饥饿升级：
    /// 票面说层只排判据全过的候选，而结构性阻断的需求没有任何车接得了；它是配置错误，已经有自己的告警，
    /// 再报一次饥饿只是噪声，还会让人误以为是排队不公平。批次7-05（control-server#210）起调用方传的是
    /// <see cref="StarvationExclusions"/> 算出的整份「连合格候选都不是」的集合，键被抑制与键已被别的 DemandId 受理的也在内。
    /// </param>
    public static TaskStarvationStanding Assess(
        AcceptedDemandSnapshot demand,
        DateTimeOffset now,
        AreaAssignmentTableVersion? areaAssignments,
        DispatchZoneParameterTableVersion? zoneParameters,
        IReadOnlySet<string>? structurallyBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(demand);
        TimeSpan age = WaitingAge(demand, now);
        string? area = demand.LiveMesFields?.Area;
        // 表里没有这个 AREA 就没有分区（例如共晶类，REQ-0185）：没有分区就没有阈值，不会超时，也就不会告警。
        string? zone = area is not null && areaAssignments?.ByArea.TryGetValue(area, out AreaAssignment? assignment) == true
            ? assignment.DispatchZone
            : null;
        long? threshold = zone is not null && zoneParameters?.Zones.TryGetValue(zone, out DispatchZoneParameters? parameters) == true
            ? parameters.StarvationThresholdSeconds
            : null;
        // 只有普通带会升级（REQ-0202「普通任务达到防饥饿阈值后进入……超时层」）；阈值未配置（未批准）时不升级、只计龄（REQ-0203）。
        // 结构性阻断的需求不进超时层（调度会话 2026-09-21 定）：它连合格候选都不是，已经有自己的结构性告警。
        bool overdue = !InTopBand(demand) &&
            HasLocalCreation(demand) &&
            structurallyBlocked?.Contains(demand.DemandId) != true &&
            threshold is { } seconds && age >= TimeSpan.FromSeconds(seconds);
        return new TaskStarvationStanding(age, zone, threshold, zoneParameters?.Version, overdue);
    }
}
