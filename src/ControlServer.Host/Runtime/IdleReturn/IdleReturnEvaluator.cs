using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>派车轮末尾交给空闲返回评估的一辆空闲车。</summary>
/// <param name="Facts">这一轮派车为它读的动态事实。</param>
/// <param name="LeftOverByTransport">这一轮任务优先派车没选中它、它也没中途退出：只有这样的车才评估（<c>REQ-0291</c>）。</param>
public sealed record IdleReturnCandidate(FleetVehicle Vehicle, DispatchVehicleFacts Facts, bool LeftOverByTransport);

/// <summary>一辆车这一轮的空闲返回结论。形成承诺时带着所选等待点、据以判定的登记版本与承诺的旅程 id。</summary>
public sealed record IdleReturnVerdict(
    string AgvId,
    string VehicleKey,
    string Reason,
    int? StationId = null,
    long? WaitingPointVersion = null,
    string? JourneyId = null)
{
    public bool Committed => Reason == IdleReturnReasons.Committed;
}

/// <summary>核验后仍合格的一个等待点，与从车的位置过去的路网代价（毫米）。</summary>
public sealed record IdleReturnPointCandidate(int StationId, long CostMm);

/// <summary>
/// 空闲返回的资格、选点与原子承诺（<c>REQ-0290</c>～<c>0292</c>、<c>REQ-0293</c> 前半句；批次8-18，control-server#389）。
/// </summary>
/// <remarks>
/// <para>
/// <b>在哪评估。</b>派车轮的任务循环结束之后（<see cref="DispatchRoundRunner"/> 收尾处一次调用）。只看空闲车里这一轮没被选中的：
/// 在途车、被外来订单挡住的车、<c>Blocked</c> 的车根本不是空闲车，不会交到这里（「忙碌或受阻车辆不改变当前状态」）。
/// 电量允许时先任务优先派车，只有没有合法搬运用途时才轮到这里——评估排在任务循环之后，这一句就是它。
/// </para>
/// <para>
/// <b>候选算出来不等于拿到。</b>选点读的是此刻的登记、站点独占与路网，逐点核验；真正决定输赢的是承诺那一次保存：
/// <see cref="IVehiclePurposeLedger.TryAcquireAsync"/> 把用途占有（<c>IDLE_RETURN</c>）与等待点预占（<c>RESERVED</c>）连同两条经过记录
/// 放在同一次保存里，车由 <c>VehiclePurposeClaims</c> 的主键仲裁、站由 <c>StationExclusivities</c> 的主键仲裁。任一被拒整笔回滚，
/// 本轮不换点重试，下一轮重评。
/// </para>
/// <para>
/// <b>承诺写什么（调度 Coordinator 9 待定，见 PR）。</b>本票只取得占有与预占，不写旅程行也不写订单意图：
/// 写一行没有需求的旅程，引擎下一轮就会拿搬运状态机去推进它，而推进、建单与执行都在批次8-19（control-server#390）。
/// 承诺的旅程 id（<see cref="IdleReturnIdentity.JourneyIdFor"/>）预先定好，批次8-19 按它物化旅程与订单意图。
/// </para>
/// <para>
/// <b>一辆车的麻烦是它自己的。</b>评估某辆车时抛了异常，它这一轮留下的暂存行全部丢掉、写一条日志、答
/// <see cref="IdleReturnReasons.EvaluationFailed"/>，后面的车照常。暂存行不丢的话，轮次后面任何一次保存都会把半截承诺写下去。
/// </para>
/// </remarks>
public sealed class IdleReturnEvaluator(
    ControlServerDbContext dbContext,
    IVehiclePurposeLedger ledger,
    IStationExclusivityStore stations,
    IVehicleFaultStore faults,
    IChargingPolicyResolver chargingPolicy,
    IWaitingPointRegistry registry,
    ITaskTypeStationBindingStore bindings,
    RouteGraphAccess routeGraph,
    IMandatoryChargeLine chargeLine,
    IOptions<IdleReturnOptions> idleReturnOptions,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    IdleReturnVerdictBoard verdictBoard,
    TimeProvider timeProvider,
    ILogger<IdleReturnEvaluator> logger)
{
    /// <summary>
    /// 共用判定里的两个电量码：先让给空闲返回自己的电量检查（读不到或充电中、强制充电线），好答空闲返回的码；那两格都过了而共用判定
    /// 仍是它们之一，照样拒。任何一条路径都不会因为让了一步而多放行一辆车。
    /// </summary>
    private static readonly string[] DeferredBatteryCodes =
    [
        VehicleDynamicFactsCriterion.BatteryFactUnknownReason,
        VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason,
        DispatchReasonCodes.MandatoryChargeRequired,
    ];

    /// <summary>「结果未知」的本服务端订单意图状态：建单发出过、而服务端还没核实它的结局。</summary>
    internal static readonly string[] UnknownOutcomeIntentStatuses =
        ["CREATE_ATTEMPTED", "RESULT_UNKNOWN", "TERMINAL_RECONCILIATION_REQUIRED"];

    private static readonly Action<ILogger, string, int, long, string, long, string, Exception?> LogCommitted =
        LoggerMessage.Define<string, int, long, string, long, string>(
            LogLevel.Information,
            new EventId(2198, nameof(LogCommitted)),
            "Idle return committed: vehicle {AgvId} reserved waiting point {StationId} (registration version {Version}) " +
            "as journey {JourneyId}, route cost {CostMm} mm; charge line {ChargeLine}.");

    // Information, but only when this vehicle's reason or detail differs from the last round's (IdleReturnVerdictBoard): an
    // idle vehicle is judged every round, and a line per round per vehicle would bury everything else in the log.
    private static readonly Action<ILogger, string, string, string, Exception?> LogNotCommitted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2199, nameof(LogNotCommitted)),
            "Idle return not committed for vehicle {AgvId}: {Reason}. {Detail}");

    private static readonly Action<ILogger, string, Exception?> LogEvaluationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2200, nameof(LogEvaluationFailed)),
            "Idle return evaluation failed for vehicle {AgvId}; it left nothing behind this round and is judged again next round.");

    private static readonly Action<ILogger, string, string, Exception?> LogStoppedAfterRepeatedEnds =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2227, nameof(LogStoppedAfterRepeatedEnds)),
            "Idle return stopped for vehicle {AgvId}: its last two idle returns both ended with their order cancelled, deleted " +
            "or failed within the repeat window ({Detail}). No idle return is committed for it until it has done another " +
            "journey; someone may want it to stay where it is.");

    private readonly IdleReturnOptions _options = idleReturnOptions.Value;
    private readonly JourneyRuntimeOptions _runtime = runtimeOptions.Value;

    /// <summary>评估这一轮交来的每一辆车，依次，前一辆的承诺后一辆看得见。</summary>
    /// <param name="currentMap">这一轮读到的实时站点目录：等待点要在其中、站名一致才接新承诺（批次8-17 的判定函数）。</param>
    public async Task<IReadOnlyList<IdleReturnVerdict>> EvaluateAsync(
        RiotMapStationCatalogSnapshot currentMap,
        IReadOnlyList<IdleReturnCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentMap);
        ArgumentNullException.ThrowIfNull(candidates);
        // control-server#392: every call is one pass, the early returns included, so a vehicle not handed over this round reads
        // as not evaluated rather than as its previous verdict.
        long pass = verdictBoard.BeginPass(timeProvider.GetUtcNow());
        if (candidates.Count == 0)
        {
            verdictBoard.EndPass(pass, timeProvider.GetUtcNow());
            return [];
        }

        // 关着就什么也不读：关掉只挡新承诺，这一句之后没有任何写入的可能。
        if (!_options.Enabled)
        {
            IdleReturnVerdict[] disabled = [.. candidates.Select(candidate => Refuse(candidate.Vehicle, IdleReturnReasons.Disabled, ""))];
            verdictBoard.EndPass(pass, timeProvider.GetUtcNow());
            return disabled;
        }

        // 读在每辆车的 try 里（审查 S5）：它抛了只是这一辆这一轮不评估，不让空闲返回的故障跳过轮次结局的记录。
        HashSet<string>? heldByForeignOrder = null;
        RoundReads? reads = null;
        List<IdleReturnVerdict> verdicts = [];
        foreach (IdleReturnCandidate candidate in candidates)
        {
            try
            {
                heldByForeignOrder ??= await ForeignRunningOrders
                    .HeldAgvIdsAsync(dbContext, cancellationToken).ConfigureAwait(false);
                string? refusal = await QualifyAsync(candidate, heldByForeignOrder, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                {
                    verdicts.Add(Refuse(candidate.Vehicle, refusal, ""));
                    continue;
                }

                // 登记、固定站与路网一次评估读一次：一轮之内它们对每辆车是同一份。站点独占不在这里读，每辆车现读——前一辆刚预占的点
                // 后一辆要看得见。
                reads ??= await ReadRoundAsync(cancellationToken).ConfigureAwait(false);
                verdicts.Add(await SelectAndCommitAsync(candidate, currentMap, reads, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                ForgetStagedCommitment();
                LogEvaluationFailed(logger, candidate.Vehicle.AgvId, error);
                verdictBoard.Record(candidate.Vehicle.AgvId, IdleReturnReasons.EvaluationFailed, error.GetType().Name);
                verdicts.Add(new IdleReturnVerdict(
                    candidate.Vehicle.AgvId, candidate.Vehicle.VehicleKey, IdleReturnReasons.EvaluationFailed));
            }
        }

        verdictBoard.EndPass(pass, timeProvider.GetUtcNow());
        return verdicts;
    }

    /// <summary>
    /// 资格（<c>REQ-0291</c>）：一条不满足就答那一条的码，全满足答空。顺序是先便宜的、先与这辆车身份相关的。
    /// </summary>
    private async Task<string?> QualifyAsync(
        IdleReturnCandidate candidate,
        HashSet<string> heldByForeignOrder,
        CancellationToken cancellationToken)
    {
        FleetVehicle vehicle = candidate.Vehicle;
        if (!candidate.LeftOverByTransport)
        {
            return IdleReturnReasons.NotLeftOverByTransportThisRound;
        }

        // 已结束当前用途：没有任何用途占有。已开始或结果未知的用途保持占有，不被新用途抢（REQ-0290）。
        if (await ledger.ReadClaimAsync(vehicle.VehicleKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            return IdleReturnReasons.VehicleHasPurpose;
        }

        // 服务端持有的人工充电等待（control-server#404）：在等待中的车不接任何新用途、不被移动，电量回升本身不恢复资格——被人充过电的车
        // 电量在线上，下面的电量检查挡不住它。与派车链、充电分配同一个读法。
        if (await VehicleNewPurposeReadiness.ManualChargingHoldVerdictAsync(dbContext, vehicle.VehicleKey, cancellationToken)
                .ConfigureAwait(false) is var hold && hold != DispatchAdmissionChain.Eligible)
        {
            return hold;
        }

        // 停在上一次返回的等待点上（等离点证据）：它已经在一个等待之处了。持有的是固定公共站（REQ-0204）时不拒——
        // 卸完货、没有下一单的车正该离开那个单车位的公共站去等待点（审查 S4）。
        // 充电桩（批次9-07，control-server#405 定的口径）：本周期已充满的车照常评估——空闲返回是它离桩的两种下一用途之一（规格 8.7），
        // 不放开它，没有搬运时充满的车会一直占着桩。别的持桩（还在充、或失败周期等三项确认的）照旧拒。
        IReadOnlyList<StationExclusivity> held = await stations.ListByVehicleAsync(vehicle.VehicleKey, cancellationToken)
            .ConfigureAwait(false);
        if (held.Any(row => row.StationKind == StationExclusivityKinds.WaitingPoint) ||
            (held.Any(row => row.StationKind == StationExclusivityKinds.Charger) &&
             !await ChargingCycleFacts.CompleteOnChargerAsync(dbContext, vehicle.VehicleKey, cancellationToken).ConfigureAwait(false)))
        {
            return IdleReturnReasons.VehicleHoldsStation;
        }

        // 没有下一业务目标：空闲车按定义没有未结束的旅程，这里现读一次，不信开轮时那份划分。
        if (await dbContext.JourneyRuntimes.AsNoTracking()
                .AnyAsync(row => row.AgvId == vehicle.AgvId && row.Stage != JourneyRuntimeStage.Completed, cancellationToken)
                .ConfigureAwait(false))
        {
            return IdleReturnReasons.HasNextBusinessTarget;
        }

        // 上一趟空闲返回的单被人取消、删除或弄成 FAILED：先冷却，再二次就停（与搬运自建单被取消的护栏对等，control-server#390）。
        if (await EndedOrderGuardAsync(vehicle, cancellationToken).ConfigureAwait(false) is { } ended)
        {
            return ended;
        }

        // 充电、清桩、维护门禁：批次 8 没有这三种用途，也没有它们的门禁，这一格恒无（不是跳过）。批次 9 加用途时，
        // 那些用途的占有已经由上面「没有任何用途占有」那一格挡住；门禁本身若另有记录，加在这里。

        // RIoT 上没有活动或结果未知的订单：外来的、本服务端结果未知的，各一格；本服务端正在跑的订单由下面动态事实的
        // RIOT_VEHICLE_ORDER_OCCUPIED（RIoT 报的锁与任务号）挡。
        if (heldByForeignOrder.Contains(vehicle.AgvId))
        {
            return IdleReturnReasons.ForeignOrderRunning;
        }

        // An idle return that ended (control-server#390) proved its vehicle stopped with no order before it closed, so the
        // status its intent was left in -- TERMINAL_RECONCILIATION_REQUIRED after a cancellation, RESULT_UNKNOWN for one never
        // sent -- is settled, not unknown. Counted, it would keep that vehicle from ever being committed again. The same holds
        // for a charging journey that ended before reaching its charger (control-server#404): it closes on the same proof.
        IQueryable<string> settledIdleReturnLegs = dbContext.Set<JourneyStopRow>()
            .Where(stop => (stop.StopRole == JourneyStopRoles.WaitingPoint || stop.StopRole == JourneyStopRoles.Charger) &&
                           dbContext.JourneyRuntimes.Any(journey =>
                               journey.JourneyId == stop.JourneyId && journey.Stage == JourneyRuntimeStage.Completed))
            .Select(stop => stop.MovementLegId);
        if (await dbContext.OrderIntents.AsNoTracking()
                .AnyAsync(
                    row => row.VehicleKey == vehicle.VehicleKey && UnknownOutcomeIntentStatuses.Contains(row.Status) &&
                           !settledIdleReturnLegs.Contains(row.MovementLegId),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return IdleReturnReasons.OwnOrderResultUnknown;
        }

        // 这辆车此刻能不能承接新用途：与派车共用的车辆侧判定（故障阻断，然后投运策略——没有已批准策略的车不被承诺空闲返回，
        // control-server#400——然后动态事实——安全、在线、绑定、IDLE、地图、新鲜、
        // 停止、RIoT 上没有它的单）。故障那一格曾经只在派车链里，空闲返回漏了它（审查 M1）。新鲜度按此刻算，不按这一轮开头读事实的
        // 时刻：承诺发生在任务循环之后。放在电量线之前：一辆故障车先答故障。
        string readiness = await VehicleNewPurposeReadiness.JudgeAsync(
                faults, dbContext, chargingPolicy, candidate.Facts with { ObservedAt = timeProvider.GetUtcNow() }, _runtime, cancellationToken)
            .ConfigureAwait(false);
        if (readiness != DispatchAdmissionChain.Eligible && !DeferredBatteryCodes.Contains(readiness))
        {
            return readiness;
        }

        RiotVehicleObservation observed = candidate.Facts.Vehicle;
        // 报 CHARGING 的车不去等待点，本周期已充满的除外（批次9-07：与派车电量判据同一个放开，读的是这一轮的同一份事实）。
        if (observed.BatteryPercent is not int battery ||
            string.IsNullOrWhiteSpace(observed.BatteryState) ||
            (string.Equals(observed.BatteryState, "CHARGING", StringComparison.Ordinal) &&
             !BatteryEligibility.ChargingVehicleMayTakeWork(observed, candidate.Facts.ChargingCycleComplete)))
        {
            return IdleReturnReasons.BatteryUnknownOrCharging;
        }

        if (await chargeLine.IsBelowLineAsync(vehicle.VehicleKey, battery, cancellationToken).ConfigureAwait(false))
        {
            return IdleReturnReasons.BelowMandatoryChargeLine;
        }

        // 让出去的电量码在这里收回：强制充电线下方的车上一格已拒；线上方、却保不住任务后余量的车（BATTERY_POLICY_NOT_SATISFIED）仍拒——宁可不动
        // （批次9-05，control-server#403）。
        if (DeferredBatteryCodes.Contains(readiness))
        {
            return readiness;
        }

        return observed.CurrentStationId is null ? IdleReturnReasons.VehiclePositionUnknown : null;
    }

    private async Task<RoundReads> ReadRoundAsync(CancellationToken cancellationToken)
    {
        WaitingPointRegistrationVersion? current =
            await registry.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        // 批次8-17 的交接：固定站要用共用的读法取本轮的集合，不能传空集合占位——「站被绑成固定站」只在这里拦。
        IReadOnlySet<int> fixedTaskStations = WaitingPointFixedTaskStations.StationIds(
            await WaitingPointFixedTaskStations.ReadAsync(bindings, _runtime.MapId, cancellationToken).ConfigureAwait(false));
        RouteGraphAvailability graph = routeGraph.Enabled && routeGraph.MapId == _runtime.MapId
            ? await routeGraph.ReadAsync(cancellationToken).ConfigureAwait(false)
            : RouteGraphAvailability.Stale("ROUTE_GRAPH_DISABLED_OR_OTHER_MAP");
        return new RoundReads(current, fixedTaskStations, graph);
    }

    private async Task<IdleReturnVerdict> SelectAndCommitAsync(
        IdleReturnCandidate candidate,
        RiotMapStationCatalogSnapshot currentMap,
        RoundReads reads,
        CancellationToken cancellationToken)
    {
        FleetVehicle vehicle = candidate.Vehicle;
        if (!reads.Graph.IsUsable)
        {
            return Refuse(vehicle, IdleReturnReasons.RouteGraphUnavailable, reads.Graph.StaleReason ?? "");
        }

        List<string> excluded = [];
        List<IdleReturnPointCandidate> eligible = [];
        int origin = candidate.Facts.Vehicle.CurrentStationId!.Value;
        int? failedLastTime = await FailedLastTimeAsync(vehicle, cancellationToken).ConfigureAwait(false);
        foreach (WaitingPointEntry point in (reads.Registration?.Points ?? [])
                     .Where(point => point.MapId == _runtime.MapId)
                     .OrderBy(point => point.StationId))
        {
            string? why = point.StationId == failedLastTime
                ? IdleReturnReasons.PointFailedLastAttempt
                : await ExcludeAsync(point, vehicle, origin, currentMap, reads, cancellationToken).ConfigureAwait(false);
            if (why is not null)
            {
                excluded.Add($"{point.StationId}={why}");
                continue;
            }

            eligible.Add(new IdleReturnPointCandidate(point.StationId, reads.Graph.Graph!.Traverse(origin, point.StationId).TraversalCostMm));
        }

        IdleReturnPointCandidate? chosen = Choose(eligible);
        if (chosen is null)
        {
            return Refuse(
                vehicle,
                IdleReturnReasons.NoWaitingPointAvailable,
                excluded.Count == 0 ? "No waiting point is registered on this Map." : string.Join(", ", excluded));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string journeyId = IdleReturnIdentity.JourneyIdFor(vehicle.VehicleKey, now);
        long version = reads.Registration!.Version;
        VehiclePurposeAcquisitionOutcome outcome = await TryCommitAsync(
                ledger, vehicle.VehicleKey, journeyId, _runtime.MapId, chosen.StationId, version, now, cancellationToken)
            .ConfigureAwait(false);
        if (outcome != VehiclePurposeAcquisitionOutcome.Acquired)
        {
            return Refuse(vehicle, IdleReturnReasons.CommitmentRefused, $"Station {chosen.StationId}: {outcome}.");
        }

        LogCommitted(
            logger, vehicle.AgvId, chosen.StationId, version, journeyId, chosen.CostMm, chargeLine.Describe(vehicle.VehicleKey), null);
        verdictBoard.Record(vehicle.AgvId, IdleReturnReasons.Committed, journeyId);
        return new IdleReturnVerdict(
            vehicle.AgvId, vehicle.VehicleKey, IdleReturnReasons.Committed, chosen.StationId, version, journeyId);
    }

    /// <summary>
    /// 一个等待点能不能接这辆车的新承诺（<c>REQ-0293</c> 前半句）：登记与实时目录（当前地图、专用角色、白名单、站点身份），
    /// 没被别的承诺预占或占用，路网可达。能接答空。
    /// </summary>
    private Task<string?> ExcludeAsync(
        WaitingPointEntry point,
        FleetVehicle vehicle,
        int origin,
        RiotMapStationCatalogSnapshot currentMap,
        RoundReads reads,
        CancellationToken cancellationToken) =>
        ExcludePointAsync(
            reads.Registration, reads.FixedTaskStations, currentMap, _runtime.MapId, reads.Graph.Graph!, point, vehicle.VehicleKey,
            origin, async (stationId, token) => await stations.ReadAsync(_runtime.MapId, stationId, token).ConfigureAwait(false) is not null,
            cancellationToken);

    /// <summary>
    /// 一个等待点能不能接这辆车的新承诺：上面那一个判法本身，抽出来给清桩开往等待点共用（批次9-11，control-server#409：「同一个等待点集合、
    /// 同一个判法」，只改可见性与签名）。<paramref name="isHeld"/> 现读这个站此刻有没有被预占或占用——前一辆刚预占的点后一辆要看得见。
    /// </summary>
    internal static async Task<string?> ExcludePointAsync(
        WaitingPointRegistrationVersion? registration,
        IReadOnlySet<int> fixedTaskStations,
        RiotMapStationCatalogSnapshot currentMap,
        int mapId,
        Domain.RouteGraph graph,
        WaitingPointEntry point,
        string vehicleKey,
        int origin,
        Func<int, CancellationToken, Task<bool>> isHeld,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(isHeld);
        WaitingPointEligibilityDecision decision = WaitingPointEligibility.Judge(
            registration, fixedTaskStations, currentMap, mapId, point.StationId, vehicleKey);
        if (!decision.Accepts)
        {
            return decision.Reason;
        }

        if (await isHeld(point.StationId, cancellationToken).ConfigureAwait(false))
        {
            return IdleReturnReasons.PointTaken;
        }

        return graph.Traverse(origin, point.StationId).Reachable ? null : IdleReturnReasons.PointUnreachable;
    }

    /// <summary>
    /// 这辆车最近一趟旅程若是一次已确认失败的空闲返回，答它的等待点（<c>REQ-0296</c> 末句：已确认失败只可在排除原失败点后建立新的承诺；
    /// control-server#390）。最近一趟是别的（搬运、收敛了的空闲返回、另一种收尾），或没有旅程，答空。
    /// </summary>
    /// <remarks>「最近一趟」按受理时刻在客户端排：SQLite 不接受 <see cref="DateTimeOffset"/> 的 ORDER BY，一辆车的旅程行按条数算。</remarks>
    private async Task<int?> FailedLastTimeAsync(FleetVehicle vehicle, CancellationToken cancellationToken)
    {
        var journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == vehicle.AgvId)
            .Select(row => new { row.JourneyId, row.CreatedAt, row.BlockReasonCode, row.PickupStationRiotId })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var latest = journeys
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.JourneyId, StringComparer.Ordinal)
            .FirstOrDefault();
        return latest is not null &&
               latest.JourneyId.StartsWith(IdleReturnIdentity.JourneyIdPrefix, StringComparison.Ordinal) &&
               latest.BlockReasonCode is { } code && IdleReturnExecutionReasons.ConfirmedFailures.Contains(code)
            ? latest.PickupStationRiotId
            : null;
    }

    /// <summary>
    /// 被取消的空闲返回之后的两道护栏（control-server#390 审查问题 2，与搬运自建单被取消的 control-server#318 对等）。只数这辆车
    /// <b>最近一趟非空闲返回的旅程之后</b>的空闲返回里已确认失败的那些（单被取消、删除，或 FAILED 后故障被人清除）：
    /// 最近一次收尾不到 <see cref="JourneyRuntimeOptions.OwnOrderRebuildDelay"/> 答冷却；以最近一次为准往回
    /// <see cref="JourneyRuntimeOptions.OwnOrderRebuildRepeatWindow"/> 之内有两次或以上答停止——过了窗口也不自动解除，车做了一趟
    /// 别的旅程（被派了搬运）才解除。都不是答空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不按「最近两趟」数（增量审查 L-a）：中间夹一趟不算失败的收尾（例如出发前点被人释放，<c>IDLE_RETURN_WAITING_POINT_LOST</c>），
    /// 链就断了，车在取消之间来回换点。现在夹在中间的收尾不计数，也不打断计数。
    /// </para>
    /// <para>
    /// 收尾时刻取用途占有的释放时刻（与旅程收尾同一次保存）。「车在急停、手动、故障时不动」那一道不在这里：
    /// 故障与动态事实由 <see cref="VehicleNewPurposeReadiness"/> 挡，建单前另有出发安全门。
    /// </para>
    /// </remarks>
    private async Task<string?> EndedOrderGuardAsync(FleetVehicle vehicle, CancellationToken cancellationToken)
    {
        var journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == vehicle.AgvId)
            .Select(row => new { row.JourneyId, row.CreatedAt, row.BlockReasonCode })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // Newest first, up to the latest journey that was not an idle return: only the idle returns since the vehicle last did
        // other work count.
        string[] failedSinceOtherWork =
        [
            .. journeys
                .OrderByDescending(row => row.CreatedAt)
                .ThenByDescending(row => row.JourneyId, StringComparer.Ordinal)
                .TakeWhile(row => row.JourneyId.StartsWith(IdleReturnIdentity.JourneyIdPrefix, StringComparison.Ordinal))
                .Where(row => row.BlockReasonCode is { } code && IdleReturnExecutionReasons.ConfirmedFailures.Contains(code))
                .Select(row => row.JourneyId),
        ];
        if (failedSinceOtherWork.Length == 0)
        {
            return null;
        }

        DateTimeOffset?[] released = await dbContext.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .Where(record => failedSinceOtherWork.Contains(record.JourneyId) && record.ReleasedAt != null)
            .Select(record => record.ReleasedAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset[] endedAt = [.. released.Select(at => at!.Value)];
        if (endedAt.Length == 0)
        {
            return null;
        }

        DateTimeOffset latest = endedAt.Max();
        if (endedAt.Count(at => latest - at <= _runtime.OwnOrderRebuildRepeatWindow) >= 2)
        {
            return IdleReturnReasons.StoppedAfterRepeatedEndedOrders;
        }

        return timeProvider.GetUtcNow() - latest < _runtime.OwnOrderRebuildDelay
            ? IdleReturnReasons.CooldownAfterEndedOrder
            : null;
    }

    /// <summary>
    /// 从核验后仍合格的点里挑一个：路网代价最小的，平手取站号小的。站号是登记里稳定的身份，平手规则因此不随读取顺序变。
    /// </summary>
    public static IdleReturnPointCandidate? Choose(IEnumerable<IdleReturnPointCandidate> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        return eligible.OrderBy(point => point.CostMm).ThenBy(point => point.StationId).FirstOrDefault();
    }

    /// <summary>
    /// 原子承诺（<c>REQ-0292</c>）：同一次保存取得这辆车的 <c>IDLE_RETURN</c> 用途占有与这个等待点的预占，连同两条经过记录。
    /// 不先读后写，输赢由两个主键决定；不是 <see cref="VehiclePurposeAcquisitionOutcome.Acquired"/> 就是什么也没留下。
    /// </summary>
    /// <remarks>单独拿出来，是为了让「两辆车同时挑中同一个点」能绕开选点的预读、直接在这一刻构造出来测。</remarks>
    public static Task<VehiclePurposeAcquisitionOutcome> TryCommitAsync(
        IVehiclePurposeLedger ledger,
        string vehicleKey,
        string journeyId,
        int mapId,
        int stationId,
        long waitingPointVersion,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return ledger.TryAcquireAsync(
            new VehiclePurposeClaim(vehicleKey, VehiclePurposes.IdleReturn, journeyId, committedAt),
            new StationExclusivityRequest(
                mapId, stationId, StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Reserved, waitingPointVersion),
            cancellationToken);
    }

    /// <summary>
    /// 丢掉这辆车没能保存的承诺行。账本在主键冲突时自己丢，别的失败（库忙、连接断）不丢：它们留在共享的变更跟踪里，
    /// 轮次后面的下一次保存会把半截承诺写下去。
    /// </summary>
    private void ForgetStagedCommitment()
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.State == EntityState.Added &&
                                     entry.Entity is VehiclePurposeClaimRow or VehiclePurposeClaimRecordRow
                                         or StationExclusivityRow or StationExclusivityRecordRow)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private IdleReturnVerdict Refuse(FleetVehicle vehicle, string reason, string detail)
    {
        if (verdictBoard.Record(vehicle.AgvId, reason, detail))
        {
            LogNotCommitted(logger, vehicle.AgvId, reason, detail, null);
            if (reason == IdleReturnReasons.StoppedAfterRepeatedEndedOrders)
            {
                LogStoppedAfterRepeatedEnds(logger, vehicle.AgvId, $"repeat window {_runtime.OwnOrderRebuildRepeatWindow}", null);
            }
        }
        return new IdleReturnVerdict(vehicle.AgvId, vehicle.VehicleKey, reason);
    }

    private sealed record RoundReads(
        WaitingPointRegistrationVersion? Registration,
        IReadOnlySet<int> FixedTaskStations,
        RouteGraphAvailability Graph);
}

/// <summary>
/// 每辆车最近一次的空闲返回结论（原因码与细节）。宿主里是单例，跨轮次保留：结论变了才记一条 Information 日志（事件 2199），
/// 看板（批次8-21，control-server#392）从 <see cref="LatestCompletedPass"/> 读。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要分轮次</b>（control-server#392）：只有交给评估器的车才会被改写。读 RIoT 抛了异常、这一轮预算用尽、或者已经在途的车
/// 不是候选，它在「最近一次」里停着上一轮的结论，而且看不出来。看板拿它当现状，就是 REQ-0269 不许的不确定新旧的旧值。
/// </para>
/// <para>
/// <b>怎么切</b>：<see cref="IdleReturnEvaluator.EvaluateAsync"/> 一开头 <see cref="BeginPass"/>，这一轮的结论记进暂存；评估走完
/// （包括开关关着、候选为空的提前返回）<see cref="EndPass"/> 把暂存整份换成「最近一轮已完成的评估」。看板只读已完成的那一份，所以
/// 读取正好落在一轮中途时看到的是上一轮完整的结论，不会一轮刚开始就所有车都变成「本轮没有评估」。一轮没走完就被取消时不发布，
/// 已完成的仍是上一轮。重启后还没有任何一轮完成，<see cref="LatestCompletedPass"/> 为空。
/// </para>
/// <para>
/// 日志「变了才记」比的仍是这辆车最近一次记下的结论（<see cref="Record"/> 的返回值），与轮次无关，语义不变。
/// </para>
/// </remarks>
public sealed class IdleReturnVerdictBoard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Reason, string Detail)> _last = new(StringComparer.Ordinal);
    private Dictionary<string, IdleReturnBoardVerdict>? _staging;
    private DateTimeOffset _stagingStartedAt;
    private long _stagingToken;
    private long _tokens;
    private long _passes;
    private IdleReturnBoardPass? _completed;

    /// <summary>记下这辆车这一轮的结论；与上一次不同（或第一次）时答真。</summary>
    public bool Record(string agvId, string reason, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        lock (_gate)
        {
            (string, string) now = (reason, detail);
            bool changed = !_last.TryGetValue(agvId, out (string Reason, string Detail) before) || before != now;
            _last[agvId] = now;
            if (_staging is not null)
            {
                _staging[agvId] = new IdleReturnBoardVerdict(reason, detail);
            }
            return changed;
        }
    }

    /// <summary>每辆车最近一次的原因码（不分轮次）。</summary>
    public IReadOnlyDictionary<string, string> Reasons
    {
        get
        {
            lock (_gate)
            {
                return _last.ToDictionary(pair => pair.Key, pair => pair.Value.Reason, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// 一轮评估开始：此后的结论记进这一轮的暂存，已完成的那一份不动。答这一轮的令牌，<see cref="EndPass"/> 凭它提交。
    /// </summary>
    public long BeginPass(DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            _staging = new Dictionary<string, IdleReturnBoardVerdict>(StringComparer.Ordinal);
            _stagingStartedAt = startedAt;
            _stagingToken = ++_tokens;
            return _stagingToken;
        }
    }

    /// <summary>
    /// 一轮评估走完：这一轮评估过的车与结论整份成为「最近一轮已完成的评估」。没评估到的车不在其中。令牌不是当前这一轮的（两轮重叠，
    /// 后开的那一轮已经接管了暂存）时什么也不提交，免得先开的一轮把后一轮的半截暂存当成完整的一轮发布出去。
    /// </summary>
    public void EndPass(long token, DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            if (_staging is null || token != _stagingToken)
            {
                return;
            }
            _completed = new IdleReturnBoardPass(++_passes, _stagingStartedAt, completedAt, _staging);
            _staging = null;
        }
    }

    /// <summary>最近一轮已完成的评估；重启后还没有完成过一轮时为空。</summary>
    public IdleReturnBoardPass? LatestCompletedPass
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }
}

/// <summary>一辆车在一轮评估里的结论。</summary>
public sealed record IdleReturnBoardVerdict(string Reason, string Detail);

/// <summary>一轮已完成的评估：第几轮、何时开始与走完、这一轮评估过的每辆车（按 <c>agvId</c>）与结论。</summary>
public sealed record IdleReturnBoardPass(
    long Number,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyDictionary<string, IdleReturnBoardVerdict> Verdicts);
