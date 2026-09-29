using ControlServer.Application;
using ControlServer.Domain;
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
    /// <summary>「结果未知」的本服务端订单意图状态：建单发出过、而服务端还没核实它的结局。</summary>
    /// <summary>
    /// 共用判定里的两个电量码：先让给空闲返回自己的电量检查（读不到或充电中、强制充电线），好答空闲返回的码；那两格都过了而共用判定
    /// 仍是它们之一，照样拒。任何一条路径都不会因为让了一步而多放行一辆车。
    /// </summary>
    private static readonly string[] DeferredBatteryCodes = ["BATTERY_FACT_UNKNOWN", "BATTERY_POLICY_NOT_SATISFIED"];

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
        if (candidates.Count == 0)
        {
            return [];
        }

        // 关着就什么也不读：关掉只挡新承诺，这一句之后没有任何写入的可能。
        if (!_options.Enabled)
        {
            return [.. candidates.Select(candidate => Refuse(candidate.Vehicle, IdleReturnReasons.Disabled, ""))];
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

        // 停在上一次返回的等待点或充电桩上（等离点证据）：它已经在一个等待之处了。持有的是固定公共站（REQ-0204）时不拒——
        // 卸完货、没有下一单的车正该离开那个单车位的公共站去等待点（审查 S4）。充电桩的口径由 B9-07 定，今天保持拒。
        if ((await stations.ListByVehicleAsync(vehicle.VehicleKey, cancellationToken).ConfigureAwait(false))
            .Any(held => held.StationKind is StationExclusivityKinds.WaitingPoint or StationExclusivityKinds.Charger))
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

        // 充电、清桩、维护门禁：批次 8 没有这三种用途，也没有它们的门禁，这一格恒无（不是跳过）。批次 9 加用途时，
        // 那些用途的占有已经由上面「没有任何用途占有」那一格挡住；门禁本身若另有记录，加在这里。

        // RIoT 上没有活动或结果未知的订单：外来的、本服务端结果未知的，各一格；本服务端正在跑的订单由下面动态事实的
        // RIOT_VEHICLE_ORDER_OCCUPIED（RIoT 报的锁与任务号）挡。
        if (heldByForeignOrder.Contains(vehicle.AgvId))
        {
            return IdleReturnReasons.ForeignOrderRunning;
        }

        if (await dbContext.OrderIntents.AsNoTracking()
                .AnyAsync(
                    row => row.VehicleKey == vehicle.VehicleKey && UnknownOutcomeIntentStatuses.Contains(row.Status),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return IdleReturnReasons.OwnOrderResultUnknown;
        }

        // 这辆车此刻能不能承接新用途：与派车共用的车辆侧判定（故障阻断，然后动态事实——安全、在线、绑定、IDLE、地图、新鲜、
        // 停止、RIoT 上没有它的单）。故障那一格曾经只在派车链里，空闲返回漏了它（审查 M1）。新鲜度按此刻算，不按这一轮开头读事实的
        // 时刻：承诺发生在任务循环之后。放在电量线之前：一辆故障车先答故障。
        string readiness = await VehicleNewPurposeReadiness.JudgeAsync(
                faults, candidate.Facts with { ObservedAt = timeProvider.GetUtcNow() }, _runtime, cancellationToken)
            .ConfigureAwait(false);
        if (readiness != DispatchAdmissionChain.Eligible && !DeferredBatteryCodes.Contains(readiness))
        {
            return readiness;
        }

        RiotVehicleObservation observed = candidate.Facts.Vehicle;
        if (observed.BatteryPercent is not int battery ||
            string.IsNullOrWhiteSpace(observed.BatteryState) ||
            string.Equals(observed.BatteryState, "CHARGING", StringComparison.Ordinal))
        {
            return IdleReturnReasons.BatteryUnknownOrCharging;
        }

        if (await chargeLine.IsBelowLineAsync(vehicle.VehicleKey, battery, cancellationToken).ConfigureAwait(false))
        {
            return IdleReturnReasons.BelowMandatoryChargeLine;
        }

        // 让出去的电量码在这里收回：今天两条线是同一个值，走不到这里；批次 9 之后若出现「线上方、门槛下方」的车，仍拒——宁可不动。
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
        foreach (WaitingPointEntry point in (reads.Registration?.Points ?? [])
                     .Where(point => point.MapId == _runtime.MapId)
                     .OrderBy(point => point.StationId))
        {
            string? why = await ExcludeAsync(point, vehicle, origin, currentMap, reads, cancellationToken)
                .ConfigureAwait(false);
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
    private async Task<string?> ExcludeAsync(
        WaitingPointEntry point,
        FleetVehicle vehicle,
        int origin,
        RiotMapStationCatalogSnapshot currentMap,
        RoundReads reads,
        CancellationToken cancellationToken)
    {
        WaitingPointEligibilityDecision decision = WaitingPointEligibility.Judge(
            reads.Registration, reads.FixedTaskStations, currentMap, _runtime.MapId, point.StationId, vehicle.VehicleKey);
        if (!decision.Accepts)
        {
            return decision.Reason;
        }

        if (await stations.ReadAsync(_runtime.MapId, point.StationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return IdleReturnReasons.PointTaken;
        }

        return reads.Graph.Graph!.Traverse(origin, point.StationId).Reachable ? null : IdleReturnReasons.PointUnreachable;
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
/// 看板（批次8-21，control-server#392）也可以从这里读。
/// </summary>
public sealed class IdleReturnVerdictBoard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Reason, string Detail)> _last =
        new(StringComparer.Ordinal);

    /// <summary>记下这辆车这一轮的结论；与上一次不同（或第一次）时答真。</summary>
    public bool Record(string agvId, string reason, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        (string, string) now = (reason, detail);
        bool changed = !_last.TryGetValue(agvId, out (string Reason, string Detail) before) || before != now;
        _last[agvId] = now;
        return changed;
    }

    /// <summary>每辆车最近一次的原因码。</summary>
    public IReadOnlyDictionary<string, string> Reasons =>
        _last.ToDictionary(pair => pair.Key, pair => pair.Value.Reason, StringComparer.Ordinal);
}
