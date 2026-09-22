using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Release;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图的旅程阻断数据面：每条被阻断的旅程——车、站、阻断码、从何时起、已挂多久、现在归哪一档（control-server#80）。
/// </summary>
/// <remarks>
/// <para>
/// 在这之前阻断码只写在 <c>JourneyRuntimes.BlockReasonCode</c> 一个字段里，没有开始时间、没有端点，现场与脚本只能直查 SQLite，
/// program#55 的升级规程因此没有载体。开始时间是 <see cref="JourneyRuntimeRow.BlockReasonSince"/>，由
/// <see cref="JourneyRuntimeRow.SetBlockReason"/> 维护；这里只读。
/// </para>
/// <para>
/// 已结束的旅程（<see cref="JourneyRuntimeStage.Completed"/>）不列：它的码记的是怎么结束的，不是在等谁，列出来只会永远挂在最高档。
/// </para>
/// <para>
/// <c>ONBOARD_SESSION_NOT_READY</c> 永远等于「详见 <c>SessionRecoveries</c>」（program#25），所以这一种连同会话行的原因码、安全原因码与
/// <c>SafetyUnknownPresent</c> 一起给；分档按 <see cref="BlockedJourneyEscalationOptions.Classify"/>。
/// </para>
/// </remarks>
internal sealed class BlockedJourneysQueryEndpoint : IDashboardQueryEndpoint
{
    internal const string SessionNotReadyReason = "ONBOARD_SESSION_NOT_READY";

    /// <summary>
    /// 车载端静默失联（<see cref="JourneyRuntimeEngine.OnboardSessionLostReason"/>，control-server#234）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SessionNotReadyReason"/> 同样带会话字段，但会话行上那几个安全值的分量不同：车不说话了，
    /// 那一行停在它最后一次在线时的判定，<c>SafetyUnknownPresent = false</c> 在这里不是「安全证据齐全」，
    /// 是一个不确定新旧的旧值——REQ-0269 禁止拿它当现状，车载告警卡片 2026-09-10 正是栽在这里。所以分档时
    /// 这一项按「说不清」传，直接进最高档；会话那一格照给，现场要看得见车最后一次在线时报的是什么。
    /// </remarks>
    internal const string SessionLostReason = JourneyRuntimeEngine.OnboardSessionLostReason;

    /// <summary>The code a stop held at its AREA machine for the station's admission carries (control-server#198).</summary>
    internal const string AreaEndAdmissionHeldReason = "TASK_TYPE_NOT_ALLOWED_AT_STATION";

    /// <summary>
    /// The attribution a row carries when its unknown safety evidence is explained by this server's own in-flight move order
    /// (control-server#139), so the card can say why the row was not sent to maintenance.
    /// </summary>
    internal const string OwnMovementOrderInFlight = "OWN_MOVEMENT_ORDER_IN_FLIGHT";

    /// <summary>
    /// 写在旅程阻断码上、看板要给中文说明的码（批次7-12，control-server#217）：批次7-10（control-server#215）的释放拒绝码。
    /// 键取常量，码改名时编译期就断在这里；<c>Batch7CargoHoldingDashboardTests</c> 按 <see cref="DemandReleaseReasons.IsRefusalCode"/>
    /// 扫一遍，新加一个拒绝码而忘了说明就红。其余阻断码仍只显示码值，与之前相同。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Descriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DemandReleaseReasons.AfterArrival] =
                "车已到当前下一站，这条需求不再释放改派（旧版本留下的码，下一轮会清掉）",
            [DemandReleaseReasons.CurrentStopWithOtherDemands] =
                "要释放的需求的取货站正是车的当前下一站，而这趟旅程上还有别的需求，这一次不释放",
            [DemandReleaseReasons.AnchorWithOtherDemands] =
                "要释放的是这趟旅程的第一条需求，而旅程上还有别的未完成需求，这一次不释放",
            [DemandReleaseReasons.OrderCancelNotConfirmed] =
                "取消开往取货站的运单还没有得到确认（结果未知不释放），确认之后再判",
            [DemandReleaseReasons.OrderStateUnknown] =
                "开往当前下一站的运单已发出创建、结果还不知道（RIoT 上可能已有一张活的），对账出结论之后再判",
            [DemandReleaseReasons.PickupOrderAppeared] =
                "释放这一刻发现取货停靠上刚出现了运单，这一轮不释放，下一轮再判",
            [DemandReleaseReasons.PickupOrderSucceeded] =
                "开往取货站的运单在 RIoT 上已经成功，车已经到了取货站，不释放",
            [DemandReleaseReasons.FaultSupervisionInEffect] =
                "车有未清除的故障，需求不释放也不取消，车留给故障处理（急停与故障监看跟着这趟旅程走）",
            // control-server#316：在途单在 RIoT 上停住，服务端不发任何命令，要人去 RIoT 或车前处理。
            [JourneyRuntimeEngine.OrderHangReason] =
                "车正在执行的运单在 RIoT 上挂起（HANG）：服务端不暂停、不急停、不改派，这辆车也不接途中追加。请到现场确认原因，"
                + "在 RIoT 里继续（continue，可以重复）；继续后旅程自动往下走。不要在 RIoT 里取消它：取消后服务端不改派，要确认后重建（入口随 #299/#318 提供）",
            [JourneyRuntimeEngine.OrderStateUnrecognizedReason] =
                "车正在执行的运单在 RIoT 上处于未识别的状态（SUSPENDED 8）：服务端按仍在执行处理，不做任何自动动作，请人工到 RIoT 核实",
            [JourneyRuntimeEngine.OrderEndedWithoutArrivalReason] =
                "订单在 RIoT 中被取消或删除，服务端不改派；确认后重建（重建入口随 #299/#318 提供，目前还没有）。"
                + "在那之前旅程停在原处、需求不动，这辆车不接新单和途中追加",
            // control-server#299：故障的人工出口。在途单 FAILED 记下的故障只能由人确认后经 /api/safety/v1/vehicle-fault-recoveries
            // 清除（docs/vehicle-fault-clearance-field-guide.md）；清除时车上可能有货的，旅程转为下面那个码等人。
            [Runtime.Faults.VehicleFaultEvidence.OrderFailed] =
                "车正在执行的运单在 RIoT 上失败（FAILED），服务端已把车判为疑似故障：不派新单，需求不改派。"
                + "请到现场排除原因；若急停已锁住，先按急停人工解除；然后由现场人员经故障清除入口确认（见现场说明），服务端核对后清除故障",
            [Runtime.Faults.VehicleFaultRecoveryService.CargoOnBoardReason] =
                "车辆故障已由人工清除，但车上可能有货：货物绑定保留，需求不改派，旅程停在这里等人处置"
                + "（同车重建入口随 #318 提供，目前还没有）。这辆车不接新单",
        };

    private readonly BlockedJourneyEscalationOptions _escalation;
    private readonly TimeProvider _clock;

    public BlockedJourneysQueryEndpoint()
        : this(BlockedJourneyEscalationOptions.Load(AppContext.BaseDirectory), TimeProvider.System)
    {
    }

    internal BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions escalation, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(escalation);
        ArgumentNullException.ThrowIfNull(clock);
        _escalation = escalation;
        _clock = clock;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "blocked-journeys";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = _clock.GetUtcNow();
        JourneyRuntimeRow[] blocked = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.BlockReasonCode != null && row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken);
        string[] agvIds = [.. blocked.Select(row => row.AgvId).Distinct(StringComparer.Ordinal)];
        Dictionary<string, SessionRecoveryRow> sessions = await dbContext.SessionRecoveries.AsNoTracking()
            .Where(row => agvIds.Contains(row.AgvId))
            .ToDictionaryAsync(row => row.AgvId, StringComparer.Ordinal, cancellationToken);
        string[] gateUpperIds = [.. blocked.Select(row => row.GateUpperId)];
        HashSet<string> departedForGate = new(
            await dbContext.OrderIntents.AsNoTracking()
                .Where(row => gateUpperIds.Contains(row.UpperId))
                .Select(row => row.UpperId)
                .ToArrayAsync(cancellationToken),
            StringComparer.Ordinal);
        HashSet<string> ownOrderInFlight = await OwnMovementOrdersInFlightAsync(dbContext, blocked, cancellationToken);
        JourneyDemandList demands =
            await JourneyDemandList.ReadAsync(dbContext, [.. blocked.Select(row => row.JourneyId)], cancellationToken);

        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset. Longest-held first, unknown starts first of all.
        return new
        {
            shiftLeaderAfterSeconds = (long)_escalation.ShiftLeaderAfter.TotalSeconds,
            maintenanceAdministratorAfterSeconds = (long)_escalation.MaintenanceAdministratorAfter.TotalSeconds,
            journeys = blocked
                .OrderBy(row => row.BlockReasonSince ?? DateTimeOffset.MinValue)
                .ThenBy(row => row.AgvId, StringComparer.Ordinal)
                .ThenBy(row => row.DemandId, StringComparer.Ordinal)
                .Select(row => Fact(
                    row,
                    sessions.GetValueOrDefault(row.AgvId),
                    departedForGate.Contains(row.GateUpperId),
                    ownOrderInFlight.Contains(row.JourneyId),
                    demands.FactsOf(row.JourneyId),
                    now))
                .ToArray()
        };
    }

    private object Fact(
        JourneyRuntimeRow row,
        SessionRecoveryRow? session,
        bool departedForGate,
        bool ownOrderInFlight,
        object[] demands,
        DateTimeOffset now)
    {
        TimeSpan? blockedFor = row.BlockReasonSince is DateTimeOffset since
            ? (now > since ? now - since : TimeSpan.Zero)
            : null;
        bool sessionLost = string.Equals(row.BlockReasonCode, SessionLostReason, StringComparison.Ordinal);
        bool carriesSession = sessionLost ||
                              string.Equals(row.BlockReasonCode, SessionNotReadyReason, StringComparison.Ordinal) ||
                              HeldForAdmissionWithTheSessionDown(row, session);
        bool explained = carriesSession && OwnMovementOrderExplanation.Explains(
            row.BlockReasonCode,
            session?.ReasonCode,
            session?.SafetyReasonCodesJson,
            session?.SafetyUnknownPresent,
            ownOrderInFlight);
        // 失联那一种，会话行上的安全判定是车最后一次在线时的，说不了现在——按「说不清」传，也就是最高档。
        BlockedJourneyEscalationLevel level = _escalation.Classify(
            blockedFor, carriesSession, sessionLost ? null : session?.SafetyUnknownPresent, explained);
        return new
        {
            agvId = row.AgvId,
            demandId = row.DemandId,
            stage = row.Stage.ToString(),
            stationId = AtGateLeg(row.Stage, departedForGate) ? row.GateStationId : row.PickupStationId,
            pickupStationId = row.PickupStationId,
            gateStationId = row.GateStationId,
            blockReasonCode = row.BlockReasonCode,
            blockReasonDescription = row.BlockReasonCode is { } code ? Descriptions.GetValueOrDefault(code) : null,
            blockReasonSince = row.BlockReasonSince,
            blockedSeconds = blockedFor is TimeSpan elapsed ? (long?)elapsed.TotalSeconds : null,
            escalationLevel = level.ToString(),
            unknownExplainedBy = explained ? OwnMovementOrderInFlight : null,
            // 车已经失联时，这三项一个都不给（control-server#234）。会话行停在车最后一次在线时的判定，
            // 而 REQ-0269 禁止把不确定新旧的旧值当现状——车载告警卡片 2026-09-10 就是栽在这里
            // （docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md）。
            //
            // 不给，而不是「给了再标注这是旧的」：看板这一侧的规矩是失联直述，拿不到就说拿不到
            // （车队会话卡片 FleetSessionsQueryEndpoint 对听不到的车也是就绪与原因码两项都不给），
            // 而 DashboardSkeletonTests.NoStaleOrLastUpdatedPresentationExistsAnywhereInTheDashboard
            // 连「陈旧／最后更新」这类词都不许出现在看板源码里。一旦开了「标注它是旧的」这条路，
            // 下一个人就会觉得显示旧值是可以的，而这正是那次缺陷的形状。
            //
            // 分档那一行已经按「说不清」处理，两件事各做各的：分档决定这一行归谁管，这里决定这一格
            // 上写着什么。
            session = carriesSession
                ? new
                {
                    present = session is not null,
                    reasonCode = sessionLost ? null : session?.ReasonCode,
                    safetyReasonCodesJson = sessionLost ? null : session?.SafetyReasonCodesJson,
                    safetyUnknownPresent = sessionLost ? null : session?.SafetyUnknownPresent
                }
                : null,
            // 批次7-12（control-server#217）：一趟旅程一行，行内列出它未移除的全部需求与各自状态，不再只有锚需求（demandId 照旧给锚）。
            journeyId = row.JourneyId,
            demands
        };
    }

    /// <summary>
    /// A loaded stop held at its AREA machine for the station's admission while the vehicle's session is not Ready (or has
    /// no row). The runtime keeps <see cref="AreaEndAdmissionHeldReason"/> on it through the session loss, because the
    /// escalation to manual recovery is counted from that code (control-server#198); the dashboard still judges it the way
    /// it judges <see cref="SessionNotReadyReason"/> -- session facts shown, unknown safety evidence straight to the top --
    /// and leaves the code and its start as they are.
    /// </summary>
    private static bool HeldForAdmissionWithTheSessionDown(JourneyRuntimeRow row, SessionRecoveryRow? session) =>
        row.Stage == JourneyRuntimeStage.AwaitingGateArrival &&
        string.Equals(row.BlockReasonCode, AreaEndAdmissionHeldReason, StringComparison.Ordinal) &&
        session?.Readiness != SessionReadiness.Ready;

    /// <summary>
    /// The journeys (by journey id) for which this server itself has a move order in flight on RIoT, by its own records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dashboard reads the database and never calls RIoT, so "in flight" is what this server has recorded and not yet seen
    /// end. Each condition narrows, none widens: the journey is in one of the two arrival stages, so the runtime has not yet
    /// observed that leg arrive; that leg's intent is <c>CONFIRMED</c> with an order id, so RIoT did accept an order this server
    /// created; the intent is bound to this journey's vehicle; and the vehicle holds no fault fact, because a move order RIoT
    /// reports FAILED is recorded as one (<c>ObserveOrderFailureAsync</c>) and a failed order explains nothing.
    /// </para>
    /// <para>
    /// A journey waiting on departure safety has no gate order yet (it is created at departure authorization), so an unknown
    /// there is not explained and stays at the top.
    /// </para>
    /// </remarks>
    private static async Task<HashSet<string>> OwnMovementOrdersInFlightAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow[] blocked,
        CancellationToken cancellationToken)
    {
        JourneyRuntimeRow[] legs = [.. blocked.Where(row =>
            string.Equals(row.BlockReasonCode, SessionNotReadyReason, StringComparison.Ordinal) &&
            InFlightLegUpperId(row) is not null)];
        if (legs.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        string[] upperIds = [.. legs.Select(leg => InFlightLegUpperId(leg)!)];
        OrderIntentRow[] intents = await dbContext.OrderIntents.AsNoTracking()
            .Where(row => upperIds.Contains(row.UpperId))
            .ToArrayAsync(cancellationToken);
        string[] agvIds = [.. legs.Select(leg => leg.AgvId).Distinct(StringComparer.Ordinal)];
        HashSet<string> faulted = new(
            await dbContext.VehicleFaultStates.AsNoTracking()
                .Where(row => agvIds.Contains(row.AgvId) && row.Level != VehicleFaultLevel.None)
                .Select(row => row.AgvId)
                .ToArrayAsync(cancellationToken),
            StringComparer.Ordinal);

        return new HashSet<string>(
            legs.Where(leg =>
                    !faulted.Contains(leg.AgvId) &&
                    intents.Any(intent =>
                        string.Equals(intent.UpperId, InFlightLegUpperId(leg), StringComparison.Ordinal) &&
                        string.Equals(intent.DemandId, leg.DemandId, StringComparison.Ordinal) &&
                        string.Equals(intent.Status, "CONFIRMED", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(intent.OrderId) &&
                        string.Equals(intent.VehicleKey, leg.VehicleKey, StringComparison.Ordinal)))
                .Select(leg => leg.JourneyId),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The upper id of the move order a journey in an arrival stage is waiting on; null in every other stage.
    /// </summary>
    private static string? InFlightLegUpperId(JourneyRuntimeRow row) => row.Stage switch
    {
        JourneyRuntimeStage.AwaitingPickupArrival => row.PickupUpperId,
        JourneyRuntimeStage.AwaitingGateArrival => row.GateUpperId,
        _ => null
    };

    /// <summary>
    /// 这条旅程停在哪个站：去关卡那一段（已经为关卡建过移动意图）算关卡，之前都算取货站。
    /// </summary>
    /// <remarks>
    /// <see cref="JourneyRuntimeStage.Blocked"/> 不带它是从哪一段进来的，所以按关卡那张移动意图在不在判断——那张意图在出发授权时才建，
    /// 有它就说明车已经离开取货站。
    /// </remarks>
    private static bool AtGateLeg(JourneyRuntimeStage stage, bool departedForGate) => stage switch
    {
        JourneyRuntimeStage.AwaitingGateArrival or JourneyRuntimeStage.AwaitingUnloadResult => true,
        JourneyRuntimeStage.Blocked => departedForGate,
        _ => false
    };
}
