using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图的持货等单数据面（批次7-12，control-server#217；REQ-0354、REQ-0355，规格第 3.3 节第 12 项）：每趟处于装货阶段的旅程一行
/// ——车、站、装货阶段、持货期限与剩余时间、两侧满没满、结束原因、让站由哪辆车触发、这趟旅程的全部需求。
/// </summary>
/// <remarks>
/// <para>
/// <b>全部从服务端自己的库读，不经车载端。</b>装货阶段三列与起算点由批次7-07 写（<see cref="JourneyRuntimeRow.LoadingPhaseState"/>、
/// <see cref="JourneyRuntimeRow.LoadingClosedReason"/>、<see cref="JourneyRuntimeRow.CargoHoldingStartedAt"/>），两侧满没满是它落库的判定
/// （<see cref="JourneyRuntimeRow.FullSlotPositionsJson"/>：只因本车货物占侧、或该侧已无空仓，与 REQ-0354 同一口径），让站触发由批次7-08 写。
/// 看板不另算一套：车载端断线时这一行照旧在，服务端重启之后读出来一样。
/// </para>
/// <para>
/// <b>哪些旅程有一行。</b>装货阶段判过的（列非空），以及站在取货停靠上的——引擎只在状态变了才写那一列，<c>LOADING</c> 不写，
/// 所以一趟正在装第一批的旅程列是空的，它仍然是「装货中」。还在去第一个取货站路上的、旅程已完成的，不列。结束了的继续列到旅程完成，
/// 让现场看得到它为什么不再等单。
/// </para>
/// <para>
/// <b>持货期限 = 起算点 + 宿主配置的持货期限</b>（<see cref="JourneyRuntimeOptions.CargoHoldingTimeout"/>，宿主上与引擎是同一份
/// <see cref="IOptions{TOptions}"/>），剩余时间按请求时刻算，看板不自己推起算点。<b>期限只在适用持货等单时给</b>：适用性引擎按车能服务的分区
/// 配置现算，看板不重算，按落库的事实推（见 <see cref="HoldingApplies"/>），与车上收到的那一份一致。期限已过而一批装货还在执行时，
/// 剩余给 0 并标 <c>awaitingLoadBatchClosure</c>，卡片写「已到期，等待本次装货闭环」，从不给负数。
/// </para>
/// <para>
/// <b>让站以结束原因为准</b>（票面评论，cs#213 审查）：持货期限先到、让站晚到时结束原因是持货超时，而
/// <see cref="JourneyRuntimeRow.YieldTriggeredByVehicleKey"/> 仍然有值——这里只在结束原因是让站时给出触发的车，其余情形不给。
/// </para>
/// <para>
/// <b>不开读事务，容忍中间态。</b>这个库的 <c>BeginTransaction</c> 是 <c>BEGIN IMMEDIATE</c>，看板 2 秒一刷就要 2 秒拿一次写锁、与引擎抢。
/// 旅程、停靠、归属、需求分几次读，可能读到引擎关旅程的半途（停靠都完成了、旅程行还没写完成），或者两次读之间追加进来一条归属、
/// 需求行没读到——这些情形这一行照样给出，站或业务键为空，不抛；下一次刷新就是完整的
/// （<c>Batch7CargoHoldingDashboardTests.AJourneyCaughtHalfwayThroughClosingStillReadsWithoutThrowing</c>）。
/// </para>
/// </remarks>
internal sealed class CargoHoldingQueryEndpoint : IDashboardQueryEndpoint
{
    private const string FrontSide = "FRONT";
    private const string RearSide = "REAR";

    private readonly TimeSpan _cargoHoldingTimeout;
    private readonly TimeProvider _clock;

    /// <summary>只供不带宿主的发现（列目录的测试）：持货期限取 <see cref="JourneyRuntimeOptions"/> 的默认值。</summary>
    public CargoHoldingQueryEndpoint()
        : this(new JourneyRuntimeOptions().CargoHoldingTimeout, TimeProvider.System)
    {
    }

    /// <summary>挂在宿主上时用这一个：与引擎判持货超时用的是同一份配置（<see cref="DashboardQueryEndpointCatalog.Discover"/>）。</summary>
    [ActivatorUtilitiesConstructor]
    public CargoHoldingQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value.CargoHoldingTimeout, TimeProvider.System)
    {
    }

    internal CargoHoldingQueryEndpoint(TimeSpan cargoHoldingTimeout, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _cargoHoldingTimeout = cargoHoldingTimeout;
        _clock = clock;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "cargo-holding";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = _clock.GetUtcNow();
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken);
        string[] journeyIds = [.. journeys.Select(row => row.JourneyId)];
        ILookup<string, JourneyStopRow> stops = (await dbContext.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => journeyIds.Contains(row.JourneyId))
                .ToArrayAsync(cancellationToken))
            .ToLookup(row => row.JourneyId, StringComparer.Ordinal);
        JourneyDemandList demands = await JourneyDemandList.ReadAsync(dbContext, journeyIds, cancellationToken);

        return new
        {
            journeys = journeys
                .Select(journey => (Journey: journey, Current: CurrentStop(stops[journey.JourneyId])))
                .Where(item => InLoadingPhase(item.Journey, item.Current))
                .OrderBy(item => item.Journey.AgvId, StringComparer.Ordinal)
                .ThenBy(item => item.Journey.JourneyId, StringComparer.Ordinal)
                .Select(item => Fact(item.Journey, item.Current, demands, now))
                .ToArray()
        };
    }

    private object Fact(
        JourneyRuntimeRow journey,
        JourneyStopRow? current,
        JourneyDemandList demands,
        DateTimeOffset now)
    {
        string state = journey.LoadingPhaseState ?? LoadingPhaseStates.Loading;
        bool closed = state == LoadingPhaseStates.Closed;
        bool yielded = closed && journey.LoadingClosedReason == LoadingClosedReasons.WaitingStationYield;
        DateTimeOffset? deadline = HoldingApplies(journey, state, demands.MembershipsOf(journey.JourneyId))
            ? journey.CargoHoldingStartedAt + _cargoHoldingTimeout
            : null;
        bool deadlinePassed = deadline is { } at && now >= at;
        return new
        {
            journeyId = journey.JourneyId,
            agvId = journey.AgvId,
            stationId = current?.StationId,
            loadingPhaseState = state,
            closedReason = closed ? journey.LoadingClosedReason : null,
            cargoHoldingStartedAt = journey.CargoHoldingStartedAt,
            cargoHoldingDeadlineAt = deadline,
            remainingSeconds = !closed && deadline is { } until ? (long?)Math.Max(0, (until - now).TotalSeconds) : null,
            deadlinePassed,
            awaitingLoadBatchClosure = deadlinePassed && !closed && journey.Stage == JourneyRuntimeStage.AwaitingLoadResult,
            frontFull = SideFull(journey.FullSlotPositionsJson, FrontSide),
            rearFull = SideFull(journey.FullSlotPositionsJson, RearSide),
            yieldedToVehicleKey = yielded ? journey.YieldTriggeredByVehicleKey : null,
            yieldTriggeredAt = yielded ? journey.YieldTriggeredAt : null,
            demands = demands.FactsOf(journey.JourneyId)
        };
    }

    /// <summary>序位最小的、还没完成也没被移除的停靠——与推进段的当前停靠同一个定义（<c>JourneyStopCursor.Current</c>）。</summary>
    private static JourneyStopRow? CurrentStop(IEnumerable<JourneyStopRow> stops) => stops
        .Where(stop => stop.Status is not (JourneyStopStatuses.Completed or JourneyStopStatuses.Removed))
        .OrderBy(stop => stop.Sequence)
        .FirstOrDefault();

    private static bool InLoadingPhase(JourneyRuntimeRow journey, JourneyStopRow? current) =>
        journey.LoadingPhaseState is not null ||
        (current?.StopRole == JourneyStopRoles.Pickup && journey.Stage is
            JourneyRuntimeStage.AwaitingSublot or
            JourneyRuntimeStage.AwaitingLoadResult or
            JourneyRuntimeStage.AwaitingStationDeparture or
            JourneyRuntimeStage.AwaitingDepartureSafety);

    /// <summary>
    /// 这趟旅程是否适用持货等单，从落库的事实推，不重算引擎的判法（那要读车能服务的分区配置）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 等单、装满，以及因超时／让站／装满而结束的，只有适用时才会出现（<see cref="LoadingPhaseMachine"/> 第 4 条：不适用的旅程只有装货中与
    /// 计划装货完成两种）。装货中只在有途中追加进来的归属时算适用——追加只在本区允许追加时发生，而允许追加正是适用的定义；
    /// 从等单被追加回装货中的车，引擎发给它的快照带着期限。其余的装货中不给期限：引擎在装货中不发快照，车第一次看到期限是进入等单那一张。
    /// </para>
    /// <para>
    /// 限度：同一站一次受理了几条需求、在装第二条时，期限已经在走而这里不给；关闭之后有人把本区参数改成禁止追加，引擎发给车的期限变成空，
    /// 而这里照旧给（program#94 语义表没有覆盖这一格，<see cref="LoadingPhaseMachine.Deadline"/>）。两种都只影响显示。
    /// </para>
    /// </remarks>
    private static bool HoldingApplies(JourneyRuntimeRow journey, string state, IReadOnlyList<JourneyDemandRow> memberships) =>
        state switch
        {
            LoadingPhaseStates.CargoHoldingWait or LoadingPhaseStates.VehicleFull => true,
            LoadingPhaseStates.Closed => journey.LoadingClosedReason is
                LoadingClosedReasons.CargoHoldingTimeout or
                LoadingClosedReasons.WaitingStationYield or
                LoadingClosedReasons.VehicleFull,
            _ => memberships.Any(row => row.DispatchZoneParameterVersion is not null)
        };

    private static bool? SideFull(string? fullSlotPositionsJson, string side) =>
        fullSlotPositionsJson is null
            ? null
            : (JsonSerializer.Deserialize<string[]>(fullSlotPositionsJson) ?? []).Contains(side, StringComparer.Ordinal);
}
