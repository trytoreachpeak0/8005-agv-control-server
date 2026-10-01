using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>派车轮交给充电分配的一辆空闲车，连同这一轮为它读的动态事实。</summary>
public sealed record ChargingCandidate(FleetVehicle Vehicle, DispatchVehicleFacts Facts);

/// <summary>一辆车这一轮的充电分配结论。形成承诺时带着所选的桩与承诺的旅程 id。</summary>
public sealed record ChargingAllocationVerdict(
    string AgvId,
    string VehicleKey,
    string Reason,
    string Detail = "",
    int? StationId = null,
    string? JourneyId = null)
{
    public bool Committed => Reason == ChargingAllocationReasons.Committed;
}

/// <summary>每辆车最近一次的充电分配结论，跨轮次保留（宿主里是单例）。</summary>
/// <remarks>
/// 一辆空闲车每一轮都判一次；结论变了才记一条日志（事件 2241），看板（批次9-10）也可以从这里读。放单例而不是静态字段或作用域实例上：
/// 派车每一轮是一个新的作用域，静态字段会被并行的测试互相清掉。
/// </remarks>
public sealed class ChargingAllocationBoard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Reason, string Detail)> _last =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 记下这辆车这一轮的结论；与上一次不同时答真。第一次见到一辆车时，「不需要充电」不算变化：那是绝大多数车绝大多数时候的结论，
    /// 每次启动为每辆车记一条没有信息。
    /// </summary>
    public bool Record(string agvId, string reason, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        (string, string) now = (reason, detail);
        bool changed = _last.TryGetValue(agvId, out (string Reason, string Detail) before)
            ? before != now
            : reason != ChargingAllocationReasons.NotRequired;
        _last[agvId] = now;
        return changed;
    }

    /// <summary>每辆车最近一次的原因码与细节。</summary>
    public IReadOnlyDictionary<string, (string Reason, string Detail)> Verdicts =>
        _last.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _waiting = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _absentSince =
        new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _said = new(StringComparer.Ordinal);

    /// <summary>
    /// 一辆低于强制充电线的车这一轮为什么分不到桩（<paramref name="conclusion"/>，不带电量——电量每掉一格不该再告警一次）；不再等桩时传空。
    /// 与上一次不同时答真：每车每种结论只告警一次，变了再告警（独立审查 M2(d)）。
    /// </summary>
    public bool RecordWaiting(string agvId, string? conclusion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        if (conclusion is null)
        {
            _waiting.TryRemove(agvId, out _);
            return false;
        }

        bool changed = !_waiting.TryGetValue(agvId, out string? before) || !string.Equals(before, conclusion, StringComparison.Ordinal);
        _waiting[agvId] = conclusion;
        return changed;
    }

    /// <summary>
    /// RIoT 从哪一刻起对这张充电单持续答「查无此单」（独立审查 M2(b)）：第一次读到时记下，之后每次读到都答同一个时刻。
    /// 进程重启后从头计——只会让放弃来得更晚。
    /// </summary>
    public DateTimeOffset AbsentSince(string upperId, DateTimeOffset now) => _absentSince.GetOrAdd(upperId, now);

    /// <summary>这张单这一次读到的不是「查无此单」（读到了别的，或没读到）：连续性断了，重新计时。</summary>
    public void SeenOrUnread(string upperId) => _absentSince.TryRemove(upperId, out _);

    /// <summary>这件事是不是第一次说：同一个键只答一次真，直到 <see cref="Unsay"/>。给「状态持续过久」一类只告警一次的告警用。</summary>
    public bool FirstTime(string key) => _said.TryAdd(key, 0);

    /// <inheritdoc cref="FirstTime"/>
    public void Unsay(string key) => _said.TryRemove(key, out _);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _sampleRun =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 充电中这一个新鲜的电量样本是否接着上一个新鲜样本（批次9-07，control-server#405；<c>REQ-0287</c>）：两次观测时刻相隔不超过
    /// <paramref name="maxGap"/> 才算连续。记下它，答它是否连续——一个周期的第一个样本、遥测断过之后的第一个样本都不连续，只重新开始观察。
    /// 观测时刻没有往前走（同一个读数读了两次）不算新样本，什么也不改、答否。进程重启后从头计：只会让充满判得更晚。
    /// </summary>
    public bool ContinuesSampleRun(string cycleId, DateTimeOffset observedAt, TimeSpan maxGap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleId);
        if (_sampleRun.TryGetValue(cycleId, out DateTimeOffset previous))
        {
            if (observedAt <= previous)
            {
                return false;
            }
            _sampleRun[cycleId] = observedAt;
            return observedAt - previous <= maxGap;
        }
        _sampleRun[cycleId] = observedAt;
        return false;
    }

    /// <summary>电量遥测断了（读不到、过期）或周期已不在充电：下一个新鲜样本重新开始观察。</summary>
    public void BreakSampleRun(string cycleId) => _sampleRun.TryRemove(cycleId, out _);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _firstObserved =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 一件持续成立的事从哪一刻起被看到（批次9-08：充不上的那组事实第一次全部成立的时刻）：第一次记下，之后答同一个时刻，直到
    /// <see cref="ForgetObserved"/>。进程重启后从头记。
    /// </summary>
    public DateTimeOffset FirstObserved(string key, DateTimeOffset at) => _firstObserved.GetOrAdd(key, at);

    /// <inheritdoc cref="FirstObserved"/>
    public void ForgetObserved(string key) => _firstObserved.TryRemove(key, out _);

    /// <summary>「到桩之后失联」已升级告警过的键（事件 2261、2262）：一种失联一次，恢复时 <see cref="Unsay"/>。</summary>
    public static string ChargingLossKey(string journeyId, string code) => $"charging-loss:{code}:{journeyId}";

    /// <summary>「取消后一直证明不了停稳」已告警过的那趟充电旅程的键（事件 2256）。</summary>
    public static string EndNotProvenKey(string journeyId) => EndNotProvenPrefix + journeyId;

    /// <summary>「失败周期留下的桩预占一直放不掉」已告警过的那趟充电旅程的键（事件 2247）。</summary>
    public static string ReservationStuckKey(string journeyId) => ReservationStuckPrefix + journeyId;

    /// <summary>
    /// 丢掉已经了结的事的键（增量审查低项）：旅程不再开着的「证明不了停稳」，桩预占已不在的「放不掉」。旅程或预占走别的路了结时——人工清除
    /// 故障、人工清桩——没有人来 <see cref="Unsay"/>，键会一直留在这块单例板上。
    /// </summary>
    /// <param name="openJourneyIds">此刻还开着的旅程里，有「证明不了停稳」键的那几趟。</param>
    /// <param name="heldReservationJourneyIds">此刻还预占着桩的旅程。</param>
    public void ForgetSettled(IReadOnlySet<string> openJourneyIds, IReadOnlySet<string> heldReservationJourneyIds)
    {
        ArgumentNullException.ThrowIfNull(openJourneyIds);
        ArgumentNullException.ThrowIfNull(heldReservationJourneyIds);
        foreach (string key in _said.Keys)
        {
            if ((key.StartsWith(EndNotProvenPrefix, StringComparison.Ordinal) &&
                 !openJourneyIds.Contains(key[EndNotProvenPrefix.Length..])) ||
                (key.StartsWith(ReservationStuckPrefix, StringComparison.Ordinal) &&
                 !heldReservationJourneyIds.Contains(key[ReservationStuckPrefix.Length..])))
            {
                _said.TryRemove(key, out _);
            }
        }
    }

    /// <summary>有「证明不了停稳」键的旅程：<see cref="ForgetSettled"/> 之前要读它们开没开着。</summary>
    public IReadOnlyList<string> JourneysSaidEndNotProven =>
    [
        .. _said.Keys
            .Where(key => key.StartsWith(EndNotProvenPrefix, StringComparison.Ordinal))
            .Select(key => key[EndNotProvenPrefix.Length..]),
    ];

    private const string EndNotProvenPrefix = "charging-end-not-proven:";
    private const string ReservationStuckPrefix = "charger-reservation-stuck:";
}

/// <summary>
/// 充电分配、排队与原子承诺（<c>REQ-0170</c>～<c>0173</c>、<c>REQ-0290</c> 的充电半边；批次9-06，control-server#404），以及名册为空时
/// 退化到服务端持有的人工充电等待（<c>REQ-0171</c>，规格 8.6）。
/// </summary>
/// <remarks>
/// <para>
/// <b>在哪评估。</b>派车轮里、搬运的任务循环之前（<see cref="DispatchRoundRunner"/> 一次调用）：强制充电先于普通搬运。交来的是这一轮的
/// 空闲车与这一轮为它们读定的事实；在途车（搬运、空闲返回、还没结束的充电）不交过来——已开始的用途不被打断，用途结束后才进（<c>REQ-0281</c>）。
/// </para>
/// <para>
/// <b>谁进待充电集合</b>：这一轮读定的那一版策略下电量低于强制充电线（<see cref="BatteryEligibility.Judge"/>），没有用途占有，不在服务端的
/// 人工充电等待里，车辆充电资格没有暂停，自己没有还占着的充电桩，并且过了与派车、空闲返回共用的车辆侧判定
/// （<see cref="VehicleNewPurposeReadiness.JudgeForChargingAsync"/>：故障、投运、强制充电线与救命线的关系、会话、出发安全、在线、绑定、
/// 空闲、地图、新鲜、停稳、RIoT 上没有它的单）。哪一条不满足就答哪一条的码，不分配、也不置人工充电等待。
/// </para>
/// <para>
/// <b>排队</b>（<c>REQ-0172</c>）：进了集合的车按当前电量升序，平手按 <c>VehicleKey</c>。充电失败过的车与普通待充车同一队，失败不给任何优先。
/// 电量未知的车进不了集合（<c>BATTERY_FACT_UNKNOWN</c>），所以不参与排序，也不挡别人。<b>电量优先只在还没有预占的车之间比</b>：
/// 取得了预占的车持有 <c>CHARGING</c> 用途占有，不在交来的车里，它的桩在候选链里是「已被预占」（<c>REQ-0173</c>）。
/// </para>
/// <para>
/// <b>候选链</b>（<c>REQ-0170</c>，按这个顺序，<see cref="ExcludeAsync"/>）：名册里这辆车可用的桩（本图、车辆范围含它或不设范围；名册之外的站
/// 一个都不进）→ 排除这辆车刚失败的桩 → 排除分配暂停着的桩 → 在 RIoT 当前地图上、站名与登记一致 → 本服务端没有它的独占行 →
/// 占用事实可确认空闲（<see cref="ChargerOccupancyReader"/>，读不到即未知、未知即不是候选）→ 路网可达。最近优先只在过滤后的集合里算，
/// 平手取站号小的；不从全图或失败的桩回填。
/// </para>
/// <para>
/// <b>「刚失败」按车冷却</b>（独立审查 M3，与空闲返回 <c>IdleReturnEvaluator.EndedOrderGuardAsync</c> 同形）：这辆车最近一次充电以已确认失败
/// 结束（单被取消或删除、FAILED 后故障由人清除、或发出后 RIoT 一直查无此单而被放弃）之后，<see cref="JourneyRuntimeOptions.OwnOrderRebuildDelay"/>
/// 之内不给它承诺任何充电桩——不只是失败的那一个：人刚在 RIoT 里取消了它的单，它不该几秒后就开往另一个桩。桩对所有车不可用是「分配暂停」，
/// 由批次9-08、9-09 写。
/// </para>
/// <para>
/// <b>两次即停</b>：只数这辆车自最近一趟非充电旅程之后、且自上一次人工充电等待解除之后的已确认失败。以最近一次为基准往回
/// <see cref="JourneyRuntimeOptions.OwnOrderRebuildRepeatWindow"/> 之内有两次，且最近一次距今也在窗口之内，就不再自动分配，置人工充电等待
/// （<see cref="ManualChargingHoldReasons.ChargingRepeatedlyFailed"/>）由人处理——与搬运自建单「再次出问题即停」对等。要求最近一次也在窗口内，
/// 是为了不让很久以前的两次失败在这辆车下一次需要充电时直接把它送进等待。
/// </para>
/// <para>
/// <b>原子承诺</b>（<c>REQ-0173</c>，<see cref="ChargingCommitment.TryCommitAsync"/>）：周期行、<c>CHARGING</c> 用途占有、<c>CHARGER</c> 预占、
/// 承载它的旅程行与停靠、待建的订单意图同一次保存。候选算出来不等于拿到：输赢由用途占有与站点独占的主键决定，任一被拒整笔回滚，
/// 本轮不换桩重试，下一轮重评。RIoT 单不在这里建：由引擎下一轮过出发前安全门之后建（<c>JourneyRuntimeEngine.Charging.cs</c>）。
/// </para>
/// <para>
/// <b>名册为空</b>：名册里没有这辆车可用的桩（从没导入过、当前版本置空、或没有一条在本图且范围含它）时，进了集合的车不分配，置人工充电等待
/// （<see cref="ManualChargingHoldReasons.RosterEmpty"/>），告警一次，给车发 <c>manualChargingHold=true</c>。已取得预占的周期不在这里——它们
/// 按自己记下的名册版本继续，这里既不读也不动。等待的出口只有「充电后返回服务」（用户 2026-09-29 定）：名册重新启用不解除它。
/// </para>
/// <para>
/// <b>不需要充电的车几乎什么也不读、什么也不写。</b>对每辆空闲车只查它有没有人工充电等待（两次本库的读）；「要不要充电」用这一轮已经
/// 读好的事实在内存里判；时钟、RIoT 的占用事实、名册与路网都只在真有车进了集合（或有预占等着释放）时才读。
/// </para>
/// <para>
/// <b>一辆车的麻烦是它自己的。</b>评估某辆车时抛了异常，它这一轮留下的暂存行全部丢掉、写一条日志、答
/// <see cref="ChargingAllocationReasons.EvaluationFailed"/>，别的车照常；分配器自己不向派车轮抛（取消除外）。
/// </para>
/// </remarks>
public sealed class ChargingAllocator(
    ControlServerDbContext dbContext,
    IVehiclePurposeLedger ledger,
    IStationExclusivityStore stations,
    IVehicleFaultStore faults,
    IChargerRoster roster,
    IChargingCycleStore cycles,
    IChargingHoldStore holds,
    IManualChargingHoldStore manualHolds,
    ChargerOccupancyReader occupancy,
    RouteGraphAccess routeGraph,
    OnboardDispatchFactsReader onboardFacts,
    IRiotVehicleSafetyFacts vehicleSafety,
    OnboardJourneyPublisher publisher,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    ChargingAllocationBoard board,
    TimeProvider timeProvider,
    ILogger<ChargingAllocator> logger)
{
    private const string BusinessStateType = "VehicleBusinessStateSnapshot";

    private static readonly Action<ILogger, string, int, string, long, int, string, Exception?> LogCommitted =
        LoggerMessage.Define<string, int, string, long, int, string>(
            LogLevel.Information,
            new EventId(2240, nameof(LogCommitted)),
            "Charging committed: vehicle {AgvId} reserved charger {StationId} as journey {JourneyId}, route cost {CostMm} mm, " +
            "battery {Battery}%. {Detail}");

    // Information, but only when this vehicle's reason or detail differs from the last round's (ChargingAllocationBoard).
    private static readonly Action<ILogger, string, string, string, Exception?> LogNotAllocated =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2241, nameof(LogNotAllocated)),
            "Charging not allocated for vehicle {AgvId}: {Reason}. {Detail}");

    private static readonly Action<ILogger, string, string, string, DateTimeOffset, Exception?> LogManualChargingHold =
        LoggerMessage.Define<string, string, string, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2242, nameof(LogManualChargingHold)),
            "Vehicle {AgvId} ({VehicleKey}) is on manual charging hold: {Reason}, since {Since}. It is not allocated a charger, " +
            "takes no transport and no idle return, and is not moved; charge it by hand, then have an administrator send " +
            "'return to service after charging' from the vehicle. Nothing else lifts the hold -- not the battery rising, not the " +
            "charger roster being enabled again.");

    private static readonly Action<ILogger, string, Exception?> LogEvaluationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2243, nameof(LogEvaluationFailed)),
            "Charging allocation failed for vehicle {AgvId}; it left nothing behind this round and is judged again next round.");

    private static readonly Action<ILogger, int, int, string, string, Exception?> LogReservationReleased =
        LoggerMessage.Define<int, int, string, string>(
            LogLevel.Information,
            new EventId(2244, nameof(LogReservationReleased)),
            "Charger {MapId}/{StationId} released: the charging cycle of vehicle {VehicleKey} (journey {JourneyId}) ended in a " +
            "confirmed failure, and charging has stopped, the vehicle is not on the charger and the charger is confirmed free.");

    private static readonly Action<ILogger, int, int, string, string, Exception?> LogReleasedOnDeparture =
        LoggerMessage.Define<int, int, string, string>(
            LogLevel.Information,
            new EventId(2248, nameof(LogReleasedOnDeparture)),
            "Charger {MapId}/{StationId} released: vehicle {VehicleKey} was full (journey {JourneyId}) and has left it -- charging " +
            "has stopped, the vehicle is not on the charger and the charger is confirmed free. Its charging cycle is closed.");

    private static readonly Action<ILogger, Exception?> LogSweepFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2245, nameof(LogSweepFailed)),
            "The sweep that releases charger reservations left by failed charging cycles failed this round; every reservation " +
            "stays as it is and the next round tries again.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogWaitingForCharger =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2246, nameof(LogWaitingForCharger)),
            "Vehicle {AgvId} is below its mandatory charge line and no charger can be allocated to it: {Reason}. {Detail} " +
            "It stays queued, takes no transport and no idle return, and its battery goes on falling. Check on site: is the " +
            "charger free and is nothing standing on it (CHARGER_RESERVED_OR_OCCUPIED, CHARGER_OCCUPIED_BY_VEHICLE, " +
            "CHARGER_TARGETED_BY_RUNNING_ORDER); can RIoT read every fleet vehicle and its unfinished orders " +
            "(CHARGER_OCCUPANCY_UNKNOWN names what could not be read); is the route graph engine enabled and fresh " +
            "(CHARGING_ROUTE_GRAPH_UNAVAILABLE, CHARGER_UNREACHABLE); is the charger on allocation hold or renamed on the Map " +
            "(CHARGER_ALLOCATION_HELD, CHARGER_NOT_ON_CURRENT_MAP); can the vehicle set off at all (CHARGING_DEPARTURE_NOT_PROVEN_AT_ALLOCATION " +
            "names what the departure gate still lacks: a slot not locked, a safety reason, RIoT not reading it stopped). Said " +
            "once per vehicle per conclusion.");

    private static readonly Action<ILogger, int, int, string, DateTimeOffset, string, Exception?> LogReservationStuck =
        LoggerMessage.Define<int, int, string, DateTimeOffset, string>(
            LogLevel.Warning,
            new EventId(2247, nameof(LogReservationStuck)),
            "Charger {MapId}/{StationId} is still reserved by vehicle {VehicleKey}, whose charging cycle ended in a confirmed " +
            "failure at {EndedAt}: {Missing}. It is released only once charging has stopped, the vehicle is read off the " +
            "charger and the charger is confirmed free, so no other vehicle can be allocated it until then. Someone has to " +
            "look at the vehicle and the charger (the manual release of a charger is control-server#406).");

    private static readonly Action<ILogger, int, int, string, DateTimeOffset, string, Exception?> LogDepartureNotConfirmed =
        LoggerMessage.Define<int, int, string, DateTimeOffset, string>(
            LogLevel.Warning,
            new EventId(2249, nameof(LogDepartureNotConfirmed)),
            "Charger {MapId}/{StationId} is still occupied by vehicle {VehicleKey}, which was charged full at {CompletedAt} and " +
            "has not been confirmed off it since: {Missing}. It is released only once charging has stopped, the vehicle is " +
            "read off the charger and the charger is confirmed free, so no other vehicle can be allocated it until then. Said " +
            "once; someone may want to look at the vehicle (switched off on the charger, or simply given no work).");

    private static readonly Action<ILogger, int, int, string, string, int, Exception?> LogHandedOverForRecharge =
        LoggerMessage.Define<int, int, string, string, int>(
            LogLevel.Information,
            new EventId(2250, nameof(LogHandedOverForRecharge)),
            "Charger {MapId}/{StationId}: vehicle {VehicleKey} was charged full in journey {JourneyId}, never left it, and is " +
            "below its mandatory charge line again ({Battery}%). That cycle is ended and the charger released for the vehicle's " +
            "next charge, which is allocated by the ordinary chain in this round.");

    private readonly JourneyRuntimeOptions _runtime = runtimeOptions.Value;

    /// <summary>跨轮次保留的那块板（宿主里是单例）：引擎的充电分支也经这里记「只告警一次」与「查无此单从何时起」。</summary>
    public ChargingAllocationBoard Board => board;

    /// <summary>读「桩是否被占」的那一个读者；引擎放弃一张查无此单的充电单之前，经它核这辆车名下没有未完成的单。</summary>
    public ChargerOccupancyReader Occupancy => occupancy;

    /// <summary>评估这一轮交来的每一辆车。</summary>
    /// <param name="currentMap">这一轮读到的实时站点目录：桩要在其中、站名与名册登记一致才是候选。</param>
    public async Task<IReadOnlyList<ChargingAllocationVerdict>> AllocateAsync(
        RiotMapStationCatalogSnapshot currentMap,
        IReadOnlyList<ChargingCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentMap);
        ArgumentNullException.ThrowIfNull(candidates);
        RoundReads reads = new();
        await ReleaseReservationsOfFailedCyclesAsync(reads, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return [];
        }

        Dictionary<string, ChargingAllocationVerdict> verdicts = new(StringComparer.Ordinal);
        List<ChargingCandidate> queue = [];
        foreach (ChargingCandidate candidate in candidates)
        {
            try
            {
                string? refusal = await QualifyAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (refusal is null)
                {
                    queue.Add(candidate);
                }
                else
                {
                    verdicts[candidate.Vehicle.AgvId] = Refuse(candidate.Vehicle, refusal, "");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                verdicts[candidate.Vehicle.AgvId] = Failed(candidate.Vehicle, error);
            }
        }

        // REQ-0172: one queue, lowest battery first; a vehicle whose charging failed before gets no place of its own in it.
        foreach (ChargingCandidate candidate in queue
                     .OrderBy(item => item.Facts.Vehicle.BatteryPercent)
                     .ThenBy(item => item.Vehicle.VehicleKey, StringComparer.Ordinal))
        {
            try
            {
                verdicts[candidate.Vehicle.AgvId] =
                    await SelectAndCommitAsync(candidate, currentMap, reads, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                verdicts[candidate.Vehicle.AgvId] = Failed(candidate.Vehicle, error);
            }
        }

        return [.. candidates.Select(candidate => verdicts[candidate.Vehicle.AgvId])];
    }

    /// <summary>
    /// 这辆车进不进待充电集合：进答空，否则答挡住它的那一条的码。人工充电等待的告警与给车的快照也在这里对齐（每一轮，幂等）。
    /// </summary>
    private async Task<string?> QualifyAsync(ChargingCandidate candidate, CancellationToken cancellationToken)
    {
        FleetVehicle vehicle = candidate.Vehicle;
        if (await SettleManualChargingHoldAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return ChargingAllocationReasons.ManualChargingHoldActive;
        }

        // Whether it needs charging at all, from the facts this round already read: nothing else is read for a vehicle that
        // does not. The policy is the one this round fixed for it -- the resolver is not asked again (REQ-0282).
        string battery = BatteryEligibility.Judge(
            candidate.Facts.Vehicle, candidate.Facts.BatteryPolicy, tasksToCover: 1, _runtime.WaitingJourneyRescueBatteryPercent);
        if (battery != DispatchReasonCodes.MandatoryChargeRequired)
        {
            // Above the line (with or without the margin for another task), or being charged already: not this queue's.
            // No policy, a policy whose line is not above the rescue line, or no battery reading: refused under that code.
            return battery == DispatchAdmissionChain.Eligible ||
                   battery == VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason
                ? ChargingAllocationReasons.NotRequired
                : battery;
        }

        // REQ-0290: a purpose already started, or one whose result is unknown, keeps its claim and is not taken over.
        if (await ledger.ReadClaimAsync(vehicle.VehicleKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ChargingAllocationReasons.VehicleHasPurpose;
        }

        string readiness = await VehicleNewPurposeReadiness
            .JudgeForChargingAsync(faults, candidate.Facts, _runtime, cancellationToken).ConfigureAwait(false);
        if (readiness != DispatchAdmissionChain.Eligible)
        {
            return readiness;
        }

        if ((await holds.ListActiveVehicleHoldsAsync(vehicle.VehicleKey, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return ChargingAllocationReasons.VehicleEligibilityHeld;
        }

        // A charger this vehicle still holds from a cycle that ended: until the three confirmations release it (the sweep at
        // the top of this round), sending the vehicle anywhere as a charging vehicle would send a second charge action to a
        // charger it may still be standing on.
        StationExclusivity[] heldChargers =
        [
            .. (await stations.ListByVehicleAsync(vehicle.VehicleKey, cancellationToken).ConfigureAwait(false))
                .Where(held => held.StationKind == StationExclusivityKinds.Charger),
        ];
        if (heldChargers.Length > 0 &&
            !(heldChargers.Length == 1 &&
              await HandOverForRechargeAsync(heldChargers[0], candidate, cancellationToken).ConfigureAwait(false)))
        {
            return ChargingAllocationReasons.VehicleStillHoldsCharger;
        }

        return candidate.Facts.Vehicle.CurrentStationId is null ? ChargingAllocationReasons.VehiclePositionUnknown : null;
    }

    private async Task<ChargingAllocationVerdict> SelectAndCommitAsync(
        ChargingCandidate candidate,
        RiotMapStationCatalogSnapshot currentMap,
        RoundReads reads,
        CancellationToken cancellationToken)
    {
        FleetVehicle vehicle = candidate.Vehicle;
        DispatchBatteryPolicy policy = candidate.Facts.BatteryPolicy!;
        int battery = candidate.Facts.Vehicle.BatteryPercent!.Value;
        string batteryNote = string.Create(CultureInfo.InvariantCulture, $"battery={battery}");
        DateTimeOffset now = timeProvider.GetUtcNow();

        FailedCycles failed = await FailedCyclesAsync(vehicle, cancellationToken).ConfigureAwait(false);
        if (failed.RepeatedWithin(_runtime.OwnOrderRebuildRepeatWindow, now))
        {
            await PlaceManualChargingHoldAsync(
                candidate, ManualChargingHoldReasons.ChargingRepeatedlyFailed, now, cancellationToken).ConfigureAwait(false);
            return Refuse(vehicle, ChargingAllocationReasons.RepeatedlyFailedManualHold, batteryNote);
        }

        // Independent review M3: the cooldown is the vehicle's, not one charger's. Somebody has just ended its charging order
        // by hand; for this long it is committed to no charger at all.
        if (failed.CoolingDown(_runtime.OwnOrderRebuildDelay, now))
        {
            return Refuse(vehicle, ChargingAllocationReasons.CooldownAfterFailedCycle, batteryNote);
        }

        reads.Roster ??= new RosterRead(await roster.ReadCurrentAsync(cancellationToken).ConfigureAwait(false));
        ChargerRosterEntry[] usable =
        [
            .. (reads.Roster.Current?.Chargers ?? [])
                .Where(charger => UsableBy(charger, vehicle.VehicleKey))
                .OrderBy(charger => charger.StationId),
        ];
        if (usable.Length == 0)
        {
            await PlaceManualChargingHoldAsync(candidate, ManualChargingHoldReasons.RosterEmpty, now, cancellationToken)
                .ConfigureAwait(false);
            return Refuse(vehicle, ChargingAllocationReasons.RosterEmptyManualHold, batteryNote);
        }

        reads.Graph ??= routeGraph.Enabled && routeGraph.MapId == _runtime.MapId
            ? await routeGraph.ReadAsync(cancellationToken).ConfigureAwait(false)
            : RouteGraphAvailability.Stale("ROUTE_GRAPH_DISABLED_OR_OTHER_MAP");
        if (!reads.Graph.IsUsable)
        {
            return Refuse(
                vehicle, ChargingAllocationReasons.RouteGraphUnavailable, $"{batteryNote}; {reads.Graph.StaleReason}",
                waiting: reads.Graph.StaleReason ?? "");
        }

        reads.Occupancy ??= await occupancy.ReadAsync(cancellationToken).ConfigureAwait(false);
        int origin = candidate.Facts.Vehicle.CurrentStationId!.Value;
        List<string> excluded = [];
        List<(ChargerRosterEntry Charger, long CostMm)> eligible = [];
        foreach (ChargerRosterEntry charger in usable)
        {
            string? why = await ExcludeAsync(
                    charger, vehicle.VehicleKey, origin, currentMap, reads, ownReservation: false, cancellationToken)
                .ConfigureAwait(false);
            if (why is not null)
            {
                excluded.Add($"{charger.StationId}={why}");
                continue;
            }

            eligible.Add((charger, reads.Graph.Graph!.Traverse(origin, charger.StationId).TraversalCostMm));
        }

        string detail = excluded.Count == 0 ? batteryNote : $"{batteryNote}; {string.Join(", ", excluded)}";
        if (reads.Occupancy.Unknown.Count > 0)
        {
            detail += $"; unknown: {string.Join(", ", reads.Occupancy.Unknown)}";
        }
        if (eligible.Count == 0)
        {
            return Refuse(
                vehicle, ChargingAllocationReasons.NoChargerAvailable, detail,
                waiting: string.Join(", ", excluded.Concat(reads.Occupancy.Unknown)));
        }

        // Review S-a: what the departure gate checks on top of the dispatch facts, checked here with the same reads. Committing
        // a vehicle the gate would hold back only has it wait out the withdrawal limit and be committed again, round after round.
        List<string> departureGaps =
        [
            .. await NonBusinessDepartureGate.SessionGapsAsync(onboardFacts, vehicle.AgvId, cancellationToken).ConfigureAwait(false),
        ];
        try
        {
            departureGaps.AddRange(await NonBusinessDepartureGate
                .RiotStandstillGapsAsync(vehicleSafety, vehicle.VehicleKey, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (NonBusinessDepartureGate.IsUnreadable(error, cancellationToken))
        {
            departureGaps.Add(NonBusinessDepartureGate.RiotSafetyUnreadable);
        }
        if (departureGaps.Count > 0)
        {
            string gaps = string.Join(", ", departureGaps.Distinct(StringComparer.Ordinal));
            return Refuse(vehicle, ChargingAllocationReasons.DepartureNotProven, $"{detail}; departure: {gaps}", waiting: gaps);
        }

        // Nearest first, over what the chain left; the lower station number on a tie. The station number is the roster's
        // stable identity, so the tie does not turn with the order the roster happens to be read in.
        (ChargerRosterEntry chosen, long costMm) = eligible.OrderBy(item => item.CostMm).ThenBy(item => item.Charger.StationId).First();
        string journeyId = ChargingIdentity.JourneyIdFor(vehicle.VehicleKey, now);
        (JourneyRuntimeRow runtime, JourneyStopRow stop, OrderIntent intent) = ChargingJourneyShape.Build(
            journeyId, vehicle, chosen, policy.Version,
            BatteryEligibility.Project(candidate.Facts.Vehicle, policy, observationFresh: true), _runtime, now);
        ChargingCycleStartOutcome outcome = await ChargingCommitment.TryCommitAsync(
                dbContext,
                cycles,
                new ChargingCycleStart(
                    ChargingIdentity.CycleIdFor(journeyId), vehicle.VehicleKey, journeyId, chosen.MapId, chosen.StationId,
                    reads.Roster.Current!.Version, policy.Version, now),
                runtime,
                stop,
                intent,
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome != ChargingCycleStartOutcome.Started)
        {
            return Refuse(vehicle, ChargingAllocationReasons.CommitmentRefused, $"{batteryNote}; station {chosen.StationId}: {outcome}");
        }

        LogCommitted(logger, vehicle.AgvId, chosen.StationId, journeyId, costMm, battery, detail, null);
        board.Record(vehicle.AgvId, ChargingAllocationReasons.Committed, detail);
        board.RecordWaiting(vehicle.AgvId, null);
        return new ChargingAllocationVerdict(
            vehicle.AgvId, vehicle.VehicleKey, ChargingAllocationReasons.Committed, detail, chosen.StationId, journeyId);
    }

    /// <summary>
    /// 名册上的一个桩此刻能不能分给这辆车（<c>REQ-0170</c> 候选链第 2～4 步，「刚失败」由调用方先判）。能答空，否则答那一条的码。
    /// </summary>
    /// <param name="ownReservation">
    /// 这辆车自己已经预占着这个桩（出发前复核）：本服务端的独占行那一步跳过——那一行就是它自己的，是不是它的由调用方先核。
    /// </param>
    private async Task<string?> ExcludeAsync(
        ChargerRosterEntry charger,
        string vehicleKey,
        int origin,
        RiotMapStationCatalogSnapshot currentMap,
        RoundReads reads,
        bool ownReservation,
        CancellationToken cancellationToken)
    {
        if ((await holds.ListActiveStationHoldsAsync(charger.MapId, charger.StationId, cancellationToken).ConfigureAwait(false))
            .Count > 0)
        {
            return ChargingAllocationReasons.ChargerAllocationHeld;
        }

        // Identity on RIoT's current Map: the station number is there under the name the roster registered it with. A station
        // renamed since is not the station somebody approved as a charger.
        if (currentMap.MapId != charger.MapId ||
            !currentMap.Stations.Any(station =>
                station.StationId == charger.StationId &&
                string.Equals(station.StationName, charger.StationName, StringComparison.Ordinal)))
        {
            return ChargingAllocationReasons.ChargerNotOnCurrentMap;
        }

        // REQ-0173: a charger this server has reserved or occupied is not taken by anyone, whatever their battery. Read now,
        // per vehicle: a charger the vehicle before this one in the queue has just reserved has to be seen.
        if (!ownReservation &&
            await stations.ReadAsync(charger.MapId, charger.StationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ChargingAllocationReasons.ChargerReservedOrOccupied;
        }

        if (reads.Occupancy!.Judge(charger.MapId, charger.StationId, vehicleKey) is { } occupied)
        {
            return occupied;
        }

        return reads.Graph!.Graph!.Traverse(origin, charger.StationId).Reachable ? null : ChargingAllocationReasons.ChargerUnreachable;
    }

    private bool UsableBy(ChargerRosterEntry charger, string vehicleKey) =>
        charger.MapId == _runtime.MapId &&
        (charger.VehicleScope.Count == 0 || charger.VehicleScope.Contains(vehicleKey, StringComparer.Ordinal));

    /// <summary>
    /// 出发前复核（独立审查 M1）：一辆已经预占了这个桩、单还从没发出过的车，此刻还能不能被派去这个桩。能答空，否则答不成立的那一条的码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 承诺与出发之间可以隔很久——出发前安全门没过时承诺一直等着。这段时间里名册可以被置空（关窗：窗口外不自动充电，别的系统可能正要用这个桩）、
    /// 桩可以被置分配暂停、地图上可以改名、别的车可以停上去、路可以断。所以建单之前对这<b>一个</b>桩重跑分配时的那条候选链
    /// （同一个 <see cref="ExcludeAsync"/>，不另写一份），只少「本服务端的独占行」那一步：那一行是它自己的。
    /// </para>
    /// <para>
    /// 名册按此刻生效的版本读，不按周期记下的版本：已经在路上的周期按自己记下的版本继续（票面 8.2），还没出发的不算在路上。
    /// 读不到、读不全一律按不成立答——这里没有「不知道所以照旧出发」。
    /// </para>
    /// </remarks>
    public async Task<string?> WhyCommittedChargerNoLongerStandsAsync(
        string vehicleKey,
        int mapId,
        int stationId,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentNullException.ThrowIfNull(currentMap);
        ChargerRosterEntry? charger = ((await roster.ReadCurrentAsync(cancellationToken).ConfigureAwait(false))?.Chargers ?? [])
            .FirstOrDefault(entry => entry.MapId == mapId && entry.StationId == stationId && UsableBy(entry, vehicleKey));
        if (charger is null)
        {
            return ChargingAllocationReasons.ChargerNotInRoster;
        }

        RoundReads reads = new()
        {
            Graph = routeGraph.Enabled && routeGraph.MapId == _runtime.MapId
                ? await routeGraph.ReadAsync(cancellationToken).ConfigureAwait(false)
                : RouteGraphAvailability.Stale("ROUTE_GRAPH_DISABLED_OR_OTHER_MAP"),
        };
        if (!reads.Graph.IsUsable)
        {
            return ChargingAllocationReasons.RouteGraphUnavailable;
        }

        reads.Occupancy = await occupancy.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!reads.Occupancy.Vehicles.TryGetValue(vehicleKey, out RiotVehicleObservation? seen) ||
            seen.CurrentStationId is not int origin)
        {
            return ChargingAllocationReasons.ChargerOccupancyUnknown;
        }

        return await ExcludeAsync(charger, vehicleKey, origin, currentMap, reads, ownReservation: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 已以确认失败结束的充电周期留下的 <c>CHARGER</c> 预占，三项都确认了才释放（<c>REQ-0173</c>）：充电已停（这辆车的电池状态读得到、
    /// 不是充电中），原车不在桩上（它的位置读得到、新鲜、是别的站），桩位可确认空闲（占用事实读全了，没有车停在上面、没有在跑的单以它为目标）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 失败收尾（引擎、人工清除故障）只结束周期、释放用途占有，<b>不放桩</b>：那一刻车可能就停在桩上，或者还在滑行。放桩在这里，凭证据，
    /// 每一轮开头一次；缺任何一项就这一轮不放，没有按时间放的分支。
    /// </para>
    /// <para>
    /// 只管以 <see cref="ChargingExecutionReasons.ConfirmedFailures"/> 结束的周期。还没结束的周期、以及充满离桩那条正常收尾的释放
    /// （批次9-07）不在这里；充不上、清桩（批次9-08）有它们自己的释放。
    /// </para>
    /// </remarks>
    private async Task ReleaseReservationsOfFailedCyclesAsync(RoundReads reads, CancellationToken cancellationToken)
    {
        try
        {
            StationExclusivityRow[] held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
                .Where(row => row.StationKind == StationExclusivityKinds.Charger)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            await ForgetSettledAsync(held, cancellationToken).ConfigureAwait(false);
            foreach (StationExclusivityRow row in held)
            {
                ChargingCycleRow? cycle = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
                    .SingleOrDefaultAsync(item => item.JourneyId == row.JourneyId, cancellationToken).ConfigureAwait(false);
                if (cycle is { Phase: ChargingCyclePhases.Active, WireState: ChargingCycleWireStates.Complete })
                {
                    // Batch 9-07: full, its purpose released, the charger still its occupancy until it has left.
                    await ReleaseOnDepartureAsync(row, cycle, reads, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (cycle is not { Phase: ChargingCyclePhases.Ended, EndReason: { } reason } ||
                    !ChargingExecutionReasons.ConfirmedFailures.Contains(reason))
                {
                    continue;
                }

                reads.Occupancy ??= await occupancy.ReadAsync(cancellationToken).ConfigureAwait(false);
                string stuckKey = ChargingAllocationBoard.ReservationStuckKey(row.JourneyId);
                string? missing = WhyNotYetReleasable(row, reads.Occupancy);
                if (missing is not null)
                {
                    // Independent review M2(c): nothing releases it but the three confirmations, and a vehicle switched off or
                    // towed away never gives them. Past the repeat window somebody is told, once; the manual release of a
                    // charger is control-server#406's.
                    if (cycle.EndedAt is { } endedAt &&
                        timeProvider.GetUtcNow() - endedAt > _runtime.OwnOrderRebuildRepeatWindow &&
                        board.FirstTime(stuckKey))
                    {
                        LogReservationStuck(logger, row.MapId, row.StationId, row.VehicleKey, endedAt, missing, null);
                    }
                    continue;
                }

                if (await stations.ReleaseAsync(
                        row.MapId, row.StationId, row.JourneyId, timeProvider.GetUtcNow(),
                        ChargingExecutionReasons.ReservationReleasedAfterEndedCycle, cancellationToken).ConfigureAwait(false))
                {
                    board.Unsay(stuckKey);
                    LogReservationReleased(logger, row.MapId, row.StationId, row.VehicleKey, row.JourneyId, null);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ForgetStaged(agvId: null);
            LogSweepFailed(logger, error);
        }
    }

    /// <summary>
    /// <c>REQ-0173</c> 的三项确认缺哪一项（都在答空）：充电已停止（新鲜读数不是 <c>CHARGING</c>）、原车已离开（当前站不是这个桩）、
    /// 桩位可确认空闲（与分配同一个占用判法）。车读不到也不算离开。失败周期与充满离桩用同一个判法。
    /// </summary>
    private string? WhyNotYetReleasable(StationExclusivityRow row, ChargerOccupancySnapshot occupancyFacts) =>
        !occupancyFacts.Vehicles.TryGetValue(row.VehicleKey, out RiotVehicleObservation? vehicle)
            ? "the vehicle cannot be read from RIoT (offline, or its position is missing)"
        : string.IsNullOrWhiteSpace(vehicle.BatteryState)
            ? "the vehicle's battery state cannot be read, so charging is not known to have stopped"
        : string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal)
            ? "the vehicle reports it is charging"
        : StandsOn(vehicle, row)
            ? "the vehicle is read standing on the charger"
        : occupancyFacts.Judge(row.MapId, row.StationId, askingVehicleKey: null) is { } occupied
            ? $"the charger is not confirmed free ({occupied})"
        : null;

    /// <summary>
    /// 充满的车离桩（批次9-07，control-server#405；<c>REQ-0173</c>）：三项确认都在，才在<b>一个事务</b>里删独占行、把释放写进它的经过、
    /// 周期收尾（阶段 <c>ENDED</c>、线上回 <c>NOT_CHARGING</c>、记离桩与释放时刻）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>下达下一单时不释放</b>：那一刻车还停在桩上，第二项不成立。RIoT 在下一单队首自动插 <c>act(78,2,0)</c>，本服务端不发任何离桩命令；
    /// 队首有没有那个动作也不当作离桩证据（调度 09-29，PR #414 审查）。
    /// </para>
    /// <para>
    /// <b>同一个事务</b>：「写释放经过、删独占行」与「周期收尾」之间崩掉，事务回滚，桩仍是占用、周期仍是 <c>COMPLETE</c>，下一轮整笔重来——
    /// 不会留下「桩已空而周期未收尾」。删行与周期更新都带着读到时的条件（持有者与经过、周期版本），另一处已经动过就一样都不写。
    /// </para>
    /// <para>
    /// 先于这一轮的分配做（<see cref="AllocateAsync"/> 开头），所以排队的车在释放提交之后才能取得这个桩，同一轮里也是。
    /// </para>
    /// </remarks>
    private async Task ReleaseOnDepartureAsync(
        StationExclusivityRow row,
        ChargingCycleRow cycle,
        RoundReads reads,
        CancellationToken cancellationToken)
    {
        reads.Occupancy ??= await occupancy.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (WhyNotYetReleasable(row, reads.Occupancy) is { } missing)
        {
            // Independent review S1: nothing releases it but the three confirmations, and a vehicle switched off on the charger,
            // or one simply given no work, never gives them. Past the repeat window from the completion somebody is told,
            // once, like the reservation of a failed cycle (event 2247).
            if (cycle.CompletedAt is { } completedAt &&
                timeProvider.GetUtcNow() - completedAt > _runtime.OwnOrderRebuildRepeatWindow &&
                board.FirstTime(ChargingAllocationBoard.ReservationStuckKey(row.JourneyId)))
            {
                LogDepartureNotConfirmed(logger, row.MapId, row.StationId, row.VehicleKey, completedAt, missing, null);
            }
            return;
        }

        if (await CloseCompletedCycleAsync(
                row, cycle, ChargingExecutionReasons.ChargerReleasedOnDeparture, ChargingExecutionReasons.Departed, departed: true,
                cancellationToken).ConfigureAwait(false))
        {
            board.Unsay(ChargingAllocationBoard.ReservationStuckKey(row.JourneyId));
            LogReleasedOnDeparture(logger, row.MapId, row.StationId, row.VehicleKey, row.JourneyId, null);
        }
    }

    /// <summary>
    /// 充满之后一直留在桩上的车又需要强制充电（独立审查 S6）：这辆车持有的这个桩来自一个 <c>COMPLETE</c> 的周期、它此刻就停在这个桩上，
    /// 才在一个事务里把那一轮收尾（<see cref="ChargingExecutionReasons.RechargedOnHeldCharger"/>）、把桩以
    /// <see cref="ChargingExecutionReasons.ChargerReleasedForRecharge"/> 释放，答真——它随后按正常分配链进队，原桩离它最近（它就停在上面），
    /// 别的车也分不到（占用判法读到它停在上面）。否则什么也不动，答假。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 调用方已经判过「需要强制充电」：报 <c>CHARGING</c> 的车不进这里，桩上还在充着的车不会被收尾。没有离桩，所以不记离桩时刻。
    /// </para>
    /// <para>
    /// <b>先放桩、后重新承诺，中间有一个空窗</b>（增量审查 S-c）：这里提交之后，同一轮的 <see cref="SelectAndCommitAsync"/> 才为这辆车重新承诺。
    /// 那一步可能不成（安全门没过、路网不可用、预占那一条写失败……），那一轮结束时这个桩在本服务端没有独占行，车却还停在上面。
    /// 兜住它的是占用判法，不是独占行：<see cref="ChargerOccupancySnapshot.Judge"/> 读到有车停在桩上，对别的车答
    /// <c>CHARGER_OCCUPIED_BY_VEHICLE</c>，只对停在上面的这辆车不算占用——所以同一轮里排在队里的别的车分不到它，下一轮这辆车按正常链重新预占。
    /// 用例 <c>WhenTheRechargeCommitmentFailsTheChargerLeftWithoutARowStillGoesToNobodyElseAndIsRetakenNextRound</c> 钉住这一点。
    /// 车读不到时这个兜底不成立，但车读不到也到不了这里：资格判定读的就是它这一轮的读数。
    /// </para>
    /// </remarks>
    private async Task<bool> HandOverForRechargeAsync(
        StationExclusivity claim,
        ChargingCandidate candidate,
        CancellationToken cancellationToken)
    {
        StationExclusivityRow? held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MapId == claim.MapId && row.StationId == claim.StationId && row.JourneyId == claim.JourneyId,
                cancellationToken)
            .ConfigureAwait(false);
        if (held is null)
        {
            return false;
        }
        ChargingCycleRow? cycle = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.JourneyId == held.JourneyId, cancellationToken).ConfigureAwait(false);
        RiotVehicleObservation vehicle = candidate.Facts.Vehicle;
        if (cycle is not { Phase: ChargingCyclePhases.Active, WireState: ChargingCycleWireStates.Complete } ||
            !StandsOn(vehicle, held) ||
            string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal))
        {
            return false;
        }

        if (!await CloseCompletedCycleAsync(
                held, cycle, ChargingExecutionReasons.ChargerReleasedForRecharge, ChargingExecutionReasons.RechargedOnHeldCharger,
                departed: false, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        board.Unsay(ChargingAllocationBoard.ReservationStuckKey(held.JourneyId));
        LogHandedOverForRecharge(
            logger, held.MapId, held.StationId, held.VehicleKey, held.JourneyId, vehicle.BatteryPercent ?? -1, null);
        return true;
    }

    /// <summary>
    /// 一个 <c>COMPLETE</c> 周期的桩释放与周期收尾，<b>一个事务</b>：删独占行（带读到的持有者与经过）、把释放原因写进经过，周期回
    /// <c>NOT_CHARGING</c>、<c>ENDED</c>。删行与周期更新都带着读到时的条件——周期按版本号（另一处已经动过这个周期，就一样都不写、事务回滚）。
    /// 答两半是否都写成了。
    /// </summary>
    private async Task<bool> CloseCompletedCycleAsync(
        StationExclusivityRow row,
        ChargingCycleRow cycle,
        string releaseReason,
        string endReason,
        bool departed,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await FixedStationExclusivity.ReleaseAsReadAsync(dbContext, row, now, releaseReason, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }
        DateTimeOffset? departedAt = departed ? now : null;
        int closed = await dbContext.Set<ChargingCycleRow>()
            .Where(item => item.CycleId == cycle.CycleId && item.Version == cycle.Version)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Phase, ChargingCyclePhases.Ended)
                    .SetProperty(item => item.WireState, ChargingCycleWireStates.NotCharging)
                    .SetProperty(item => item.DepartedAt, departedAt)
                    .SetProperty(item => item.ReleasedAt, now)
                    .SetProperty(item => item.EndedAt, now)
                    .SetProperty(item => item.EndReason, endReason)
                    .SetProperty(item => item.Version, cycle.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (closed == 0)
        {
            // The cycle moved since it was read: neither half is written.
            return false;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>板上只告警一次的键里，事情已经走别的路了结的，丢掉（<see cref="ChargingAllocationBoard.ForgetSettled"/>）。没有那种键时不读库。</summary>
    private async Task ForgetSettledAsync(StationExclusivityRow[] held, CancellationToken cancellationToken)
    {
        string[] said = [.. board.JourneysSaidEndNotProven];
        HashSet<string> open = said.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                await dbContext.JourneyRuntimes.AsNoTracking()
                    .Where(row => said.Contains(row.JourneyId) && row.Stage != JourneyRuntimeStage.Completed)
                    .Select(row => row.JourneyId)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false),
                StringComparer.Ordinal);
        board.ForgetSettled(open, new HashSet<string>(held.Select(row => row.JourneyId), StringComparer.Ordinal));
    }

    private bool StandsOn(RiotVehicleObservation vehicle, StationExclusivityRow charger) =>
        vehicle.CurrentStationId == charger.StationId &&
        (string.IsNullOrEmpty(vehicle.CurrentMap) ||
         string.Equals(vehicle.CurrentMap, _runtime.MapIdentity, StringComparison.Ordinal));

    /// <summary>
    /// 这辆车已确认失败的充电周期（单被取消或删除、FAILED 后故障由人清除、发出后一直查无此单而被放弃）的收尾时刻：只数它最近一趟非充电旅程
    /// 之后的，且只数上一次人工充电等待解除之后的。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>自最近一趟非充电旅程之后</b>（与空闲返回 <c>EndedOrderGuardAsync</c> 同一个口径）：车做过别的事，之前的充电失败就不再算进「两次即停」。
    /// </para>
    /// <para>
    /// <b>从上一次解除算起</b>：解除是一个人在车前核对过之后做的，它之前的失败那个人已经处理了；不从那里截断，解除之后第一次分配就会又被置回等待。
    /// </para>
    /// <para>
    /// 时刻在客户端比：SQLite 不接受 <see cref="DateTimeOffset"/> 的比较与排序，一辆车的旅程与周期按条数算。
    /// </para>
    /// </remarks>
    private async Task<FailedCycles> FailedCyclesAsync(FleetVehicle fleetVehicle, CancellationToken cancellationToken)
    {
        string vehicleKey = fleetVehicle.VehicleKey;
        var ended = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey && row.Phase == ChargingCyclePhases.Ended && row.EndReason != null)
            .Select(row => new { row.StationId, row.EndedAt, row.EndReason })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (ended.Length == 0)
        {
            return new FailedCycles([]);
        }

        DateTimeOffset[] otherWork = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == fleetVehicle.AgvId && !row.JourneyId.StartsWith(ChargingIdentity.JourneyIdPrefix))
            .Select(row => row.CreatedAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset?[] released = await dbContext.Set<ManualChargingHoldRecordRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey && row.ReleasedAt != null)
            .Select(row => row.ReleasedAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset since = released.Select(at => at!.Value).Concat(otherWork)
            .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        return new FailedCycles(
        [
            .. ended
                .Where(row => row.EndedAt is { } at && at > since &&
                              ChargingExecutionReasons.ConfirmedFailures.Contains(row.EndReason!))
                .Select(row => (row.StationId, EndedAt: row.EndedAt!.Value))
                .OrderByDescending(row => row.EndedAt),
        ]);
    }

    // ---- 人工充电等待 ----------------------------------------------------------------------------------------------------

    private async Task PlaceManualChargingHoldAsync(
        ChargingCandidate candidate, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string holdId = string.Create(
            CultureInfo.InvariantCulture,
            $"manual-charging-hold:{candidate.Vehicle.VehicleKey}:{now.UtcDateTime:yyyyMMdd'T'HHmmssfffffff'Z'}");
        await manualHolds.PlaceAsync(holdId, candidate.Vehicle.VehicleKey, reason, now, cancellationToken).ConfigureAwait(false);
        // Placed or already held by another writer: either way the vehicle is held now, and what follows is the same.
        await SettleManualChargingHoldAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把这辆车的人工充电等待与它该有的两件事对齐，答它此刻是否在等待中：在等待中而还没告警的，告警一次并记下时刻；给车的
    /// <c>manualChargingHold</c> 快照（置上一张、解除一张）还没排进发件箱的排进去，排了没确认的在车听得到时补发。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>每一步都按库里的事实派生，不靠「刚才做过」</b>：告警看 <c>WarnedAt</c>，快照看发件箱里有没有那个消息 id（由等待的 id 派生）。
    /// 所以崩在置上与告警之间、车当时没有会话、解除是入站事务做的而不是这里做的，下一轮都照样补齐，且只补一次。
    /// </para>
    /// <para>
    /// <b>只对空闲车。</b>持有用途的车不在交来的车里：搬运中的车既不会被置等待，也收不到这里发的任何快照（车载端录入门只看
    /// <c>manualChargingHold</c>，hmi#220）。
    /// </para>
    /// </remarks>
    private async Task<bool> SettleManualChargingHoldAsync(ChargingCandidate candidate, CancellationToken cancellationToken)
    {
        FleetVehicle vehicle = candidate.Vehicle;
        ManualChargingHold? hold = await manualHolds.ReadAsync(vehicle.VehicleKey, cancellationToken).ConfigureAwait(false);
        if (hold is not null)
        {
            if (hold.WarnedAt is null)
            {
                LogManualChargingHold(logger, vehicle.AgvId, vehicle.VehicleKey, hold.Reason, hold.Since, null);
                await manualHolds.MarkWarnedAsync(vehicle.VehicleKey, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }

            await PublishHoldSnapshotAsync(candidate, PlacedMessageId(hold.HoldId), held: true, supersedes: null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // Lifted since: the vehicle is told once, by the snapshot derived from the hold that was lifted. Only a hold the
        // vehicle was told about is taken back -- one it never heard of needs no retraction.
        var lifted = (await dbContext.Set<ManualChargingHoldRecordRow>().AsNoTracking()
                .Where(row => row.VehicleKey == vehicle.VehicleKey && row.ReleasedAt != null)
                .Select(row => new { row.HoldId, row.ReleasedAt })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(row => row.ReleasedAt)
            .ThenByDescending(row => row.HoldId, StringComparer.Ordinal)
            .FirstOrDefault();
        string? toldAbout = lifted is null ? null : PlacedMessageId(lifted.HoldId);
        if (lifted is not null &&
            await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == toldAbout, cancellationToken).ConfigureAwait(false))
        {
            await PublishHoldSnapshotAsync(
                    candidate, ReleasedMessageId(lifted.HoldId), held: false, supersedes: toldAbout, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>这一次等待置上时、解除时各发给车一张业务状态；消息 id 由等待的 id 派生，重跑与补发都是同一张。</summary>
    internal static string PlacedMessageId(string holdId) => JourneyPlanBuilder.StableGuid(holdId, "manual-charging-hold-placed");

    /// <inheritdoc cref="PlacedMessageId"/>
    internal static string ReleasedMessageId(string holdId) => JourneyPlanBuilder.StableGuid(holdId, "manual-charging-hold-released");

    /// <summary>
    /// 把这一张排进发件箱并发出（还没排过时），或在车听得到时补发（排了没确认时）。
    /// </summary>
    /// <remarks>
    /// <b>被取代的不补发，退役。</b>补发按先后发没确认的行，而车载端把低于它已采纳修订号的快照当成 <c>SNAPSHOT_REVISION_REGRESSION</c>、
    /// 当场拆会话。所以解除那一张排进去时，置上那一张若还没确认就一并退役（<paramref name="supersedes"/>，同一次保存）；一张排了没确认的快照
    /// 若这条流上已经有更高的修订号（这辆车后来做了别的旅程），也退役、不再发。快照说的是「现在是什么」，更高的那一张已经说了。
    /// </remarks>
    private async Task PublishHoldSnapshotAsync(
        ChargingCandidate candidate, string messageId, bool held, string? supersedes, CancellationToken cancellationToken)
    {
        FleetVehicle vehicle = candidate.Vehicle;
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == vehicle.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            // The vehicle has never had a session with this server: there is no generation to stamp the snapshot with. The
            // next round after it connects stages it.
            return;
        }

        var stored = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageId == messageId)
            .Select(row => new { row.AcknowledgedAt, row.FencedAt, row.PayloadJson })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stored is { AcknowledgedAt: not null } or { FencedAt: not null })
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (stored is null)
        {
            WireToGateStore store = new(dbContext);
            long revision = await NextBusinessStateRevisionAsync(vehicle.AgvId, cancellationToken).ConfigureAwait(false);
            if (supersedes is not null)
            {
                await RetireIfPendingAsync(supersedes, now, cancellationToken).ConfigureAwait(false);
            }
            await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
                store,
                messageId,
                vehicle.AgvId,
                session.SessionGeneration,
                // No purpose, no loading phase, not in a charging cycle: the vehicle stands where it is. batteryState is projected
                // from the facts this round read, once, when the snapshot is first staged; a resend sends the stored line.
                new VehicleBusinessProjection(
                    revision, "READY", null, held,
                    BatteryEligibility.Project(candidate.Facts.Vehicle, candidate.Facts.BatteryPolicy, observationFresh: true),
                    ChargingCycleWireStates.NotCharging, null, []),
                now,
                cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // Not on the line this moment: kept in the outbox, resent below once the vehicle is heard from.
            }
            return;
        }

        long own;
        using (System.Text.Json.JsonDocument envelope = System.Text.Json.JsonDocument.Parse(stored.PayloadJson))
        {
            own = envelope.RootElement.GetProperty("payload").GetProperty("vehicleBusinessStateRevision").GetInt64();
        }
        if (await JourneyClosure.HighestSentRevisionAsync(dbContext, vehicle.AgvId, BusinessStateType, cancellationToken)
                .ConfigureAwait(false) > own)
        {
            await RetireIfPendingAsync(messageId, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await SessionLiveness.HeardFromAsync(dbContext, vehicle.AgvId, session.SessionGeneration, now, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await publisher.ReplayPendingForSessionAsync(
                    vehicle.AgvId, session.SessionGeneration, new HashSet<string>(StringComparer.Ordinal) { messageId },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Not on the line this moment; the next round sends it.
        }
    }

    /// <summary>退役一张还没确认的快照（暂存，调用方保存）：它不再被补发。已确认、已退役或不存在时什么也不做。</summary>
    private async Task RetireIfPendingAsync(string messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(
                item => item.MessageId == messageId && item.AcknowledgedAt == null && item.FencedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            row.FencedAt = now;
        }
    }

    /// <summary>
    /// 这辆车业务状态流上的下一个修订号：比它发过的每一张都大，也比它最近一趟旅程预留的号都大。车载端按消息类型记修订号，回退或同号
    /// 不同内容都会当场拆会话。
    /// </summary>
    /// <remarks>
    /// 按车计数器是下一趟旅程基准的出处（<c>WireToGateStore.SeedSnapshotRevisionsAsync</c>：基准 = 计数器 + 每趟预留量），暂存原语每发一张
    /// 就把它抬到这一号之上（<c>RaiseSnapshotRevisionFloorAsync</c>）——但它对还没有计数器行的车什么也不做。一辆从没接过活的车第一张
    /// 快照就是这里发的，所以这里为它建那一行，否则它的第一趟旅程会从 1 起号、撞上这一张。
    /// </remarks>
    private async Task<long> NextBusinessStateRevisionAsync(string agvId, CancellationToken cancellationToken)
    {
        long sent = await JourneyClosure.HighestSentRevisionAsync(dbContext, agvId, BusinessStateType, cancellationToken)
            .ConfigureAwait(false) ?? 0;
        VehicleSnapshotRevisionRow? counter = await dbContext.Set<VehicleSnapshotRevisionRow>()
            .SingleOrDefaultAsync(row => row.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        long reserved = counter is null ? 0 : counter.VehicleBusinessRevision + WireToGateStore.RevisionsPerJourney - 1;
        long revision = Math.Max(sent, reserved) + 1;
        if (counter is null)
        {
            dbContext.Set<VehicleSnapshotRevisionRow>().Add(new VehicleSnapshotRevisionRow
            {
                AgvId = agvId,
                VehicleBusinessRevision = revision - WireToGateStore.RevisionsPerJourney + 1,
            });
        }
        return revision;
    }

    // ---- 结论与收拾 ------------------------------------------------------------------------------------------------------

    /// <param name="waiting">
    /// 非空：这辆车需要充电、留在队里、而此刻分不到桩（独立审查 M2(d)）——不带电量的结论，变了才告警一次（事件 2246）。别的拒绝传空。
    /// </param>
    private ChargingAllocationVerdict Refuse(FleetVehicle vehicle, string reason, string detail, string? waiting = null)
    {
        if (board.Record(vehicle.AgvId, reason, detail))
        {
            LogNotAllocated(logger, vehicle.AgvId, reason, detail, null);
        }
        if (board.RecordWaiting(vehicle.AgvId, waiting is null ? null : $"{reason}|{waiting}"))
        {
            LogWaitingForCharger(logger, vehicle.AgvId, reason, detail, null);
        }
        return new ChargingAllocationVerdict(vehicle.AgvId, vehicle.VehicleKey, reason, detail);
    }

    private ChargingAllocationVerdict Failed(FleetVehicle vehicle, Exception error)
    {
        ForgetStaged(vehicle.AgvId);
        LogEvaluationFailed(logger, vehicle.AgvId, error);
        board.Record(vehicle.AgvId, ChargingAllocationReasons.EvaluationFailed, error.GetType().Name);
        return new ChargingAllocationVerdict(
            vehicle.AgvId, vehicle.VehicleKey, ChargingAllocationReasons.EvaluationFailed, error.GetType().Name);
    }

    /// <summary>
    /// 丢掉一次没能保存的评估留在变更跟踪里的行。存储在键冲突时自己丢，别的失败（库忙、连接断、注入的崩溃）不丢：留着的话，
    /// 轮次后面任何一次保存都会把半截承诺、半截等待或一张没人要的快照写下去。
    /// </summary>
    private void ForgetStaged(string? agvId)
    {
        if (agvId is not null)
        {
            ChargingCommitment.Forget(dbContext, agvId);
        }
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.State != EntityState.Unchanged &&
                                     entry.Entity is ManualChargingHoldRow or ManualChargingHoldRecordRow
                                         or StationExclusivityRow or StationExclusivityRecordRow or ProtocolOutboxRow)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>一轮里对每辆车都相同的读，读一次：名册、路网、占用事实。都是真有车进了集合（或有预占等着释放）才读。</summary>
    private sealed class RoundReads
    {
        public RosterRead? Roster { get; set; }

        public RouteGraphAvailability? Graph { get; set; }

        public ChargerOccupancySnapshot? Occupancy { get; set; }
    }

    /// <summary>名册的一次读：<see cref="Current"/> 为空即从没导入过，与「读过了」分得开。</summary>
    private sealed record RosterRead(ChargerRosterVersion? Current);

    /// <summary>一辆车已确认失败的充电周期，最近的在前。</summary>
    private sealed record FailedCycles(IReadOnlyList<(int StationId, DateTimeOffset EndedAt)> NewestFirst)
    {
        public (int StationId, DateTimeOffset EndedAt)? Latest => NewestFirst.Count == 0 ? null : NewestFirst[0];

        /// <summary>
        /// 以最近一次为准往回 <paramref name="window"/> 之内有两次或以上，且最近一次距今也不超过 <paramref name="window"/>。
        /// 置上等待之后不因为过了窗口而自动解除：出口是人工充电等待的解除。
        /// </summary>
        public bool RepeatedWithin(TimeSpan window, DateTimeOffset now) =>
            Latest is { } latest && now - latest.EndedAt <= window &&
            NewestFirst.Count(item => latest.EndedAt - item.EndedAt <= window) >= 2;

        /// <summary>最近一次失败距今还不到 <paramref name="delay"/>：这辆车此刻不被承诺任何充电桩。</summary>
        public bool CoolingDown(TimeSpan delay, DateTimeOffset now) => Latest is { } latest && now - latest.EndedAt < delay;
    }
}
