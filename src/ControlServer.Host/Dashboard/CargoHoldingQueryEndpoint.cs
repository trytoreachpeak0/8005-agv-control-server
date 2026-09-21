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
/// <b>哪些旅程有一行。</b>装货阶段判过且还没结束的，以及站在取货停靠上的——引擎只在状态变了才写那一列，<c>LOADING</c> 不写，
/// 所以一趟正在装第一批的旅程列是空的，它仍然是「装货中」。还在去第一个取货站路上的、旅程已完成的，不列。已结束的只在车还停在装货停靠上时列，
/// 车一离站就不列（审查 L3，见 <see cref="InLoadingPhase"/>）。
/// </para>
/// <para>
/// <b>持货期限 = 起算点 + 宿主配置的持货期限</b>（<see cref="JourneyRuntimeOptions.CargoHoldingTimeout"/>，宿主上与引擎是同一份
/// <see cref="IOptions{TOptions}"/>），剩余时间按请求时刻算，看板不自己推起算点。<b>期限只在适用持货等单时给</b>：适用性引擎按车能服务的分区
/// 配置现算，看板不重算，按落库的状态推（见 <see cref="HoldingApplies"/>）：装货中一律不给期限。期限已过而一批装货还在执行时，
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
        DateTimeOffset? deadline = LoadingPhaseMachine.Deadline(
            state, journey.CargoHoldingStartedAt, _cargoHoldingTimeout, HoldingApplies(journey, state));
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
            awaitingLoadBatchClosure = deadlinePassed && !closed && LoadBatchInProgress(journey),
            frontFull = SideFull(journey.FullSlotPositionsJson, FrontSide),
            rearFull = SideFull(journey.FullSlotPositionsJson, RearSide),
            yieldedToVehicleKey = yielded ? journey.YieldTriggeredByVehicleKey : null,
            yieldTriggeredAt = yielded ? journey.YieldTriggeredAt : null,
            demands = demands.FactsOf(journey.JourneyId)
        };
    }

    /// <summary>
    /// 有一批装货命令已发、结果未到：期限已过也不结束等单，等它安全闭环（ADR-cross-0057「到期不打断正在进行的仓位操作」）。
    /// </summary>
    /// <remarks>
    /// <b>这是引擎的定义，看板照抄一份，前提由引擎承担</b>：<c>JourneyRuntimeEngine.ReconcileLoadingPhaseAsync</c> 给
    /// <see cref="LoadingPhaseMachine.Facts.LoadBatchInProgress"/> 传的正是 <c>runtime.Stage == JourneyRuntimeStage.AwaitingLoadResult</c>。
    /// 引擎那一句私有、本票不碰引擎，所以不能共用；<c>Batch7CargoHoldingDashboardTests.TheLoadBatchInProgressPremiseIsTheEnginesOwn</c>
    /// 逐字核引擎源码里的那一句，引擎改了判法，那条用例红，指回这里。
    /// </remarks>
    private static bool LoadBatchInProgress(JourneyRuntimeRow journey) =>
        journey.Stage == JourneyRuntimeStage.AwaitingLoadResult;

    /// <summary>序位最小的、还没完成也没被移除的停靠——与推进段的当前停靠同一个定义（<c>JourneyStopCursor.Current</c>）。</summary>
    private static JourneyStopRow? CurrentStop(IEnumerable<JourneyStopRow> stops) => stops
        .Where(stop => stop.Status is not (JourneyStopStatuses.Completed or JourneyStopStatuses.Removed))
        .OrderBy(stop => stop.Sequence)
        .FirstOrDefault();

    /// <summary>
    /// 这趟旅程在不在卡片上：装货阶段判过且还没结束的都在；从没判过的（列为空即装货中）与已结束的，只在车站在装货停靠上时在。
    /// </summary>
    /// <remarks>
    /// 已结束的行车一离站就不列（审查 L3）：结束原因在车离站之前操作员已经看得到，车去卸货的路上一直挂着「已结束」只会把卡片占满。
    /// 限度：持货超时关闭时还有别的装货停靠没装（cs#290 之前不截断），车开到下一个取货站时这一行会再出现，写的仍是持货超时。
    /// </remarks>
    private static bool InLoadingPhase(JourneyRuntimeRow journey, JourneyStopRow? current) =>
        journey.LoadingPhaseState is not (null or LoadingPhaseStates.Closed) || StandsAtPickupStop(journey, current);

    private static bool StandsAtPickupStop(JourneyRuntimeRow journey, JourneyStopRow? current) =>
        current?.StopRole == JourneyStopRoles.Pickup && journey.Stage is
            JourneyRuntimeStage.AwaitingSublot or
            JourneyRuntimeStage.AwaitingLoadResult or
            JourneyRuntimeStage.AwaitingStationDeparture or
            JourneyRuntimeStage.AwaitingDepartureSafety;

    /// <summary>
    /// 看板给不给这趟旅程持货期限：交给 <see cref="LoadingPhaseMachine.Deadline"/> 的那个「适用持货等单」，从落库的状态推。
    /// 引擎的判法（<c>JourneyRuntimeEngine.HoldingApplicableAsync</c>：车能服务的分区里至少有一个允许途中追加）每轮按宿主的车队分区配置与
    /// 当前每区参数现算，看板不重算。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>依赖引擎的三条行为</b>（2026-09-22 在 <c>fp/v2-impl@8ee99549</c> 实读），由装货阶段状态机（批次7-07、7-08）承担；改了其中任何一条，这里要跟着改，
    /// <c>Batch7CargoHoldingDashboardTests.TheDeadlineIsShownOnlyWhereCargoHoldingApplies</c> 逐个状态钉着：
    /// </para>
    /// <list type="number">
    /// <item>不适用的旅程只会是装货中与「计划装货完成」两种（<see cref="LoadingPhaseMachine"/> 第 4 条）；等单、装满，以及因超时／让站／装满
    /// 而结束，只有适用时才会被判出来。</item>
    /// <item>适用的旅程只从「已装满」离开最后一个装货停靠（等单不发离站请求），所以「计划装货完成」只出现在不适用的旅程上
    /// （以及列落地之前就在途的旅程）。</item>
    /// <item>起算点 <c>CargoHoldingStartedAt</c> 在第一批装货落定时写一次，不论适用与否，之后从不清零——所以「起算点有值」说明不了适用。</item>
    /// </list>
    /// <para>
    /// <b>装货中一律不给</b>（审查 M3）。装货中适用与否只能靠引擎现算：有过途中追加之后分区参数又改成禁止追加，引擎就不给期限、
    /// 也不会因超时关闭，而任何落库的事实都说不出这一变化——按追加归属推，看板会照旧给期限，甚至显示「已到期」。所以退一步，
    /// 代价是有追加、仍适用的装货中那一段看板不显示期限（车上看得到），进入等单之后看板照常显示。
    /// </para>
    /// <para>
    /// 仍然推不准的一格：等单、装满或已结束之后，有人把本区参数改成禁止追加，引擎发给车的期限变成空，而这里照旧给（program#94 语义表没有
    /// 覆盖「关闭后适用性变了」这一格，<see cref="LoadingPhaseMachine.Deadline"/>）。只影响显示。
    /// </para>
    /// </remarks>
    private static bool HoldingApplies(JourneyRuntimeRow journey, string state) =>
        state switch
        {
            LoadingPhaseStates.CargoHoldingWait or LoadingPhaseStates.VehicleFull => true,
            LoadingPhaseStates.Closed => journey.LoadingClosedReason is
                LoadingClosedReasons.CargoHoldingTimeout or
                LoadingClosedReasons.WaitingStationYield or
                LoadingClosedReasons.VehicleFull,
            _ => false
        };

    private static bool? SideFull(string? fullSlotPositionsJson, string side) =>
        fullSlotPositionsJson is null
            ? null
            : (JsonSerializer.Deserialize<string[]>(fullSlotPositionsJson) ?? []).Contains(side, StringComparer.Ordinal);
}
