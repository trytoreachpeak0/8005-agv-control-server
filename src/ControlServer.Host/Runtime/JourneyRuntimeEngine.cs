using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.Release;
using ControlServer.Host.Runtime.TaskTypeStations;

namespace ControlServer.Host.Runtime;

public sealed partial class JourneyRuntimeEngine(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    IRiotMapStationCatalog mapStationCatalog,
    MapStationResolver stationResolver,
    IFixedTaskStationResolver fixedStationResolver,
    TaskTypeStationAccess taskTypeStations,
    CatalogBindingHoldConvergence catalogBindingHolds,
    MapRenameHoldConvergence mapRenameHolds,
    MovementDispatchService movementDispatch,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
    ISublotBoxCountReader boxCountReader,
    IPackageCapacityStore packageCapacityStore,
    CatalogAvailabilityAccess catalogAvailability,
    ICatalogAvailabilityStore catalogStore,
    PreCreateGate createGate,
    VehicleRoster roster,
    VehicleDispatchPolicyAccess dispatchPolicy,
    IVehicleMotionFacts motionFacts,
    CheckpointWaitLedger checkpointWaits,
    VehicleFaultCoordinator faults,
    DispatchRoundRunner dispatchRound,
    OnboardDispatchFactsReader onboardFacts,
    IDispatchZoneParameterStore zoneParameters,
    SlotGroupFullnessBoard slotGroupFullness,
    IRiotVehicleSafetyFacts vehicleSafety,
    ForeignOrders.ForeignRunningOrderSupervisor foreignOrders,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<JourneyRuntimeEngine> logger,
    IChargingPolicyResolver chargingPolicy,
    FixedStationSweepWarnings? fixedStationWarnings = null,
    RiotOrderCommandService? orderCommands = null,
    IdleReturn.IdleReturnMaterializationFailures? idleReturnMaterializationFailures = null,
    Charging.StationClearanceExit? clearanceExit = null,
    StagingDepartureWarnings? stagingDepartureWarnings = null,
    InTransitDoorReadings? doorReadings = null)
{
    // control-server#789: the door grace and the door fault's sub-cause outlive the per-round engine (a singleton in the host,
    // which the worker also reads to wake when a grace runs out); an engine built without it keeps its own.
    private readonly InTransitDoorReadings _doorReadings = doorReadings ??
        new InTransitDoorReadings(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));

    // control-server#713 review L2: which journeys have already been warned about, outliving the per-round engine (a singleton
    // in the host); an engine built without it keeps its own.
    private readonly StagingDepartureWarnings _stagingDepartureWarnings = stagingDepartureWarnings ?? new StagingDepartureWarnings();

    // control-server#390 review L3: consecutive materialization failures outlive the per-round engine (a singleton in the
    // host); an engine built without them keeps its own.
    private readonly IdleReturn.IdleReturnMaterializationFailures _idleReturnMaterializationFailures =
        idleReturnMaterializationFailures ?? new IdleReturn.IdleReturnMaterializationFailures();

    // control-server#391: the fixed task station sweep's open warnings outlive the per-round engine (the host registers
    // them as a singleton); an engine built without them keeps its own.
    private readonly FixedStationSweepWarnings _fixedStationWarnings = fixedStationWarnings ?? new FixedStationSweepWarnings();

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?> LogUnloadOrderFallback =
        LoggerMessage.Define<string, string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(2126, nameof(LogUnloadOrderFallback)),
            "Vehicle {AgvId} journey {JourneyId} stop {StopId}: the unload order across demands could not be told by side " +
            "({Reason}; demands {DemandIds}), so it falls back to the order they joined in and unloads {NextDemandId} next. " +
            "Specification section 20 wants front before rear (control-server#303).");
    /// <summary>写 <see cref="AdvanceFailedReason"/> 这一步自己也失败了（control-server#331）。</summary>
    private static readonly Action<ILogger, string, Exception?> LogFailedAdvanceNotNamed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2128, nameof(LogFailedAdvanceNotNamed)),
            "Journey {JourneyId} advanced no further this round and the block reason naming that could not be " +
            "written either; the round's own failure follows.");
    private static readonly Action<ILogger, string, Exception?> LogBoxCountFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2102, nameof(LogBoxCountFailed)),
        "SUBLOT_BOX_COUNT failed closed for demand {DemandId}.");
    private static readonly Action<ILogger, string, string, string, Exception?> LogDepartureAnswerNotValid =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2337, nameof(LogDepartureAnswerNotValid)),
            "Pre-departure safety check {CheckId} of {AgvId} has no answer that permits departing: {Answers}.");
    private static readonly Action<ILogger, string, string, Exception?> LogSublotRejected =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2108, nameof(LogSublotRejected)),
            "The sublot entered for demand {DemandId} was refused: {ReasonCode}.");
    private static readonly Action<ILogger, string, string, string, string, Exception?> LogSlotCommandHeldOffStop =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2318, nameof(LogSlotCommandHeldOffStop)),
            "The {OperationType} command for vehicle {AgvId} was not sent: the vehicle is not proven at its stop. {Expected} {Observed}");
    private static readonly Action<ILogger, string, string, Exception?> LogSlotCommandResendWithheld =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(2319, nameof(LogSlotCommandResendWithheld)),
            "Unanswered slot commands {MessageIds} for vehicle {AgvId} were not sent again: the vehicle is not proven at their stop.");
    private static readonly Action<ILogger, Exception?> LogMapStationCatalogFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2103, nameof(LogMapStationCatalogFailed)),
        "RIoT Map station catalog failed closed; no new journey action was taken.");
    private static readonly Action<ILogger, string, string, Exception?> LogCheckpointWaitExceeded =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2105, nameof(LogCheckpointWaitExceeded)),
            "Vehicle {AgvId} has been holding at a traffic checkpoint past its budget on journey " +
            "{DemandId}; it is not arriving on its own.");
    /// <summary>
    /// The vehicle has gone quiet on a journey waiting for one of its facts (ADR-cross-0027, control-server#234).
    /// Logged on the edge, when the block is first written, rather than every round the silence lasts: a warning
    /// repeated every two seconds is not an escalation, it is noise. The escalation is the dashboard's ladder,
    /// measured from <see cref="JourneyRuntimeRow.BlockReasonSince"/>.
    /// </summary>
    private static readonly Action<ILogger, string, long, string, DateTimeOffset?, Exception?> LogOnboardSessionLost =
        LoggerMessage.Define<string, long, string, DateTimeOffset?>(
            LogLevel.Warning,
            new EventId(2119, nameof(LogOnboardSessionLost)),
            "Onboard session for {AgvId} (generation {SessionGeneration}) has gone silent while journey " +
            "{DemandId} waits on a fact only the vehicle can supply; last inbound at {LastInboundAt}. " +
            "The journey is shown as blocked and nothing else is done to it (REQ-0287).");
    private static readonly Action<ILogger, string, string, string, Exception?> LogOrderFailedSymptom =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2107, nameof(LogOrderFailedSymptom)),
            "RIoT reports order {UpperId} FAILED on vehicle {AgvId}; journey {DemandId} recorded the " +
            "symptom with the fault model and stopped advancing on its own.");
    private static readonly Action<ILogger, string, string, string, int?, string, Exception?> LogInTransitOrderStalled =
        LoggerMessage.Define<string, string, string, int?, string>(
            LogLevel.Warning,
            new EventId(2127, nameof(LogInTransitOrderStalled)),
            "RIoT reports order {UpperId} of journey {DemandId} on vehicle {AgvId} in state {OrderState}; the journey " +
            "names {ReasonCode} and waits for a person. No order command, emergency stop or fault is issued for it.");
    private static readonly Action<ILogger, string, DateTimeOffset, string, Exception?> LogCargoHoldingDeadlineEndedDemand =
        LoggerMessage.Define<string, DateTimeOffset, string>(
            LogLevel.Warning,
            new EventId(2309, nameof(LogCargoHoldingDeadlineEndedDemand)),
            "Vehicle {AgvId}'s cargo holding deadline {Deadline} passed before demand {DemandId} started loading; " +
            "it was released back to the backlog for redispatch and its stops left empty were removed from the plan.");
    private static readonly Action<ILogger, string, DateTimeOffset, string, Exception?> LogStationDeadlineEndedStop =
        LoggerMessage.Define<string, DateTimeOffset, string>(
            LogLevel.Warning,
            new EventId(2108, nameof(LogStationDeadlineEndedStop)),
            "Nobody entered a sublot at vehicle {AgvId}'s pickup before the station departure deadline " +
            "{Deadline}; demand {DemandId} ended as CANCELLED_BY_STATION_TIMEOUT and the vehicle was released.");
    private static readonly Action<ILogger, string, DateTimeOffset, string, int, Exception?> LogStationDeadlineReleasedDemand =
        LoggerMessage.Define<string, DateTimeOffset, string, int>(
            LogLevel.Warning,
            new EventId(2310, nameof(LogStationDeadlineReleasedDemand)),
            "Nobody entered a sublot at vehicle {AgvId}'s pickup before the station departure deadline {Deadline}; demand " +
            "{DemandId} went back to the backlog for redispatch (release {Release} at a station deadline).");
    private static readonly Action<ILogger, string, int, int, string, Exception?> LogStagingLowBatteryReleasedDemand =
        LoggerMessage.Define<string, int, int, string>(
            LogLevel.Warning,
            new EventId(2313, nameof(LogStagingLowBatteryReleasedDemand)),
            "Vehicle {AgvId} waits at the staging station with nothing loaded and its battery at {BatteryPercent}% is below " +
            "the mandatory charge line of {EntryThreshold}%; demand {DemandId} went back to the backlog and the journey ended, " +
            "so charging allocation can send the vehicle to charge.");
    private static readonly Action<ILogger, string, string, string, string, Exception?> LogManualCarryAlarmRaised =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2311, nameof(LogManualCarryAlarmRaised)),
            "Demand {DemandId} ({TransportDemandKey}) on vehicle {AgvId} ended and will not be dispatched again: {AlarmCode}. " +
            "Its cargo is still at the machine or partly on the vehicle; a person must deal with it.");
    private static readonly Action<ILogger, string, string, DateTimeOffset?, DateTimeOffset?, long, Exception?>
        LogLoadedStopDeadlineRefreshed =
            LoggerMessage.Define<string, string, DateTimeOffset?, DateTimeOffset?, long>(
                LogLevel.Information,
                new EventId(2139, nameof(LogLoadedStopDeadlineRefreshed)),
                "Vehicle {AgvId} has loaded everything at pickup {StationId} and still waits there; its worklist carried " +
                "the station departure deadline {SentDeadline}, the server now judges by {Deadline}, so worklist revision " +
                "{Revision} was sent with no items and the server's deadline.");
    private static readonly Action<ILogger, string, string, bool, DateTimeOffset?, Exception?> LogEmptiedStopEntersDepartureWait =
        LoggerMessage.Define<string, string, bool, DateTimeOffset?>(
            LogLevel.Information,
            new EventId(2292, nameof(LogEmptiedStopEntersDepartureWait)),
            "Vehicle {AgvId}'s pickup {StationId} has nothing left to enter (its last outstanding demand was ended before " +
            "a sublot) while the journey goes on; it now waits for departure. Loaded at this stop: {LoadedHere}; station " +
            "departure deadline {Deadline} (none means the wait is off and the vehicle is sent on in this round).");
    private static readonly Action<ILogger, string, string, string, Exception?> LogLoadYieldedToEndedDemand =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2190, nameof(LogLoadYieldedToEndedDemand)),
            "Vehicle {AgvId}'s sublot entry {SubmissionId} for demand {DemandId} lost to a change made while the iteration " +
            "read it (the demand ended or was held for recovery, or a cancellation opened at its stop); no load was " +
            "commanded, and the next iteration judges the entry afresh.");
    private static readonly Action<ILogger, string, string, string, Exception?> LogYieldedToJourneyCommit =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2191, nameof(LogYieldedToJourneyCommit)),
            "Vehicle {AgvId}'s journey {JourneyId} yielded this iteration: journey {ConflictingJourneyIds} was written by " +
            "someone else after this iteration read it. What it had not saved was withdrawn; the next iteration reads it " +
            "afresh (control-server#357).");
    private static readonly Action<ILogger, string, string, Exception?> LogYieldedToUnavailableConnection =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2193, nameof(LogYieldedToUnavailableConnection)),
            "Vehicle {AgvId}'s journey {JourneyId} yielded this iteration: its Onboard connection is not available (not " +
            "connected, another session generation, a write past its timeout, or closed). What it had not saved was withdrawn " +
            "and the other vehicles go on; the next iteration tries it again (control-server#334).");
    private static readonly Action<ILogger, string, string, Exception?> LogAdvanceFailedVehicleIsolated =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2307, nameof(LogAdvanceFailedVehicleIsolated)),
            "Vehicle {AgvId}'s journey {JourneyId} failed to advance this iteration. What it had not committed was withdrawn, " +
            "it takes no dispatch this iteration, and the other vehicles and the dispatch round go on; the first such failure " +
            "of the iteration is rethrown at its end (control-server#487).");
    private static readonly Action<ILogger, string, string, string, DateTimeOffset, Exception?> LogStationTimeoutDoorNotClosed =
        LoggerMessage.Define<string, string, string, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2109, nameof(LogStationTimeoutDoorNotClosed)),
            "Vehicle {AgvId} is still loading demand {DemandId} at station {StationId} past its station " +
            "departure deadline {Deadline} with a slot door not closed; the stop keeps waiting under " +
            "STATION_TIMEOUT_DOOR_NOT_CLOSED.");
    private static readonly Action<ILogger, string, string, string, Exception?> LogDeterminateLoadFailureSettled =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2110, nameof(LogDeterminateLoadFailureSettled)),
            "Vehicle {AgvId} reported a determinate load failure after the station departure deadline; " +
            "demand {DemandId} ended as {TerminalReason} and the vehicle was released.");
    // control-server#551: names which drift the store refused -- stations, executable task types, deployment identity, a
    // version moving backwards, several at once -- with the old and the new value of each (DescribeAdmissionPolicyDrift).
    private static readonly Action<ILogger, long, int, string, Exception?> LogAdmissionPolicyDrift =
        LoggerMessage.Define<long, int, string>(
            LogLevel.Warning,
            new EventId(2116, nameof(LogAdmissionPolicyDrift)),
            "Admission policy version {AdmissionPolicyVersion} cannot be bound for map {MapId}: {DriftCauses}. " +
            "Journeys already under way continue on the bound policy; no further demand is taken on until " +
            "admissionPolicyVersion is raised.");

    private static readonly Action<ILogger, string, string, string, TimeSpan, Exception?> LogAreaEndAdmissionRevokedTimeout =
        LoggerMessage.Define<string, string, string, TimeSpan>(
            LogLevel.Warning,
            new EventId(2117, nameof(LogAreaEndAdmissionRevokedTimeout)),
            "Vehicle {AgvId} has waited at AREA machine station {StationId} with demand {DemandId} on board for longer " +
            "than {Timeout} for the station to admit its task type again; the journey is blocked for manual recovery.");

    private static readonly Action<ILogger, Exception?> LogMapRenameObservationFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2196, nameof(LogMapRenameObservationFailed)),
        "The Map name check against RIoT's Map list failed; it is not a rename, so nothing is held and no baseline moves. " +
        "This round goes on, the next one reads the Map list again, and a Map that is really gone is the catalog freshness " +
        "gate's to block (control-server#186).");

    private static readonly Action<ILogger, int, Exception?> LogCatalogBindingHoldConvergenceFailed =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(2118, nameof(LogCatalogBindingHoldConvergenceFailed)),
            "The catalog change convergence for map {MapId} failed; this round goes on without it and the next " +
            "complete catalog confirmation converges again.");

    /// <summary>
    /// The terminal reason of a demand whose pickup stop ran out its station departure deadline
    /// (ADR-cross-0055).
    /// </summary>
    public const string StationTimeoutCancellationReason = "CANCELLED_BY_STATION_TIMEOUT";

    /// <summary>
    /// 持货期限截断时，车上那张空清单与（旅程因此收尾时）旅程的原因码（control-server#290）。需求本身不终结，按释放改派退回积压
    /// （<see cref="DemandReleaseReasons.Released"/>），这个码只说这一站与这趟旅程为什么结束。
    /// </summary>
    public const string CargoHoldingTruncationReason = "RELEASED_AT_CARGO_HOLDING_DEADLINE";

    /// <summary>
    /// The load stop has run out its station departure deadline with a slot door still not closed
    /// (ADR-cross-0058 decision 4). The stop does not end; the duty moves to someone shutting the door.
    /// </summary>
    public const string StationTimeoutDoorNotClosedReason = "STATION_TIMEOUT_DOOR_NOT_CLOSED";

    /// <summary>
    /// 本停靠有一次装货纠错还没走完：离站等待停在这里，等纠错结清（ADR-cross-0054）。这之前它只以字面量写在判定处，
    /// control-server#331 把它提成常量，因为「推进失败不覆盖它」要从同一处引用它。
    /// </summary>
    public const string LoadCorrectionInProgressReason = "LOAD_CORRECTION_IN_PROGRESS";

    /// <summary>
    /// 车载端对离站安全询问答了「不安全」：车停在站里，等有人在车前把不安全的那一项处理掉。control-server#331 第四轮审查把它
    /// 提成常量，理由同 <see cref="LoadCorrectionInProgressReason"/>。
    /// </summary>
    public const string PreDepartureSafetyNotValidReason = "PRE_DEPARTURE_SAFETY_NOT_VALID";

    /// <summary>
    /// A loaded journey at its AREA machine station waited longer than
    /// <see cref="JourneyRuntimeOptions.AreaEndAdmissionRevokedTimeout"/> for the station to admit its task type again
    /// (control-server#198). The journey is <see cref="JourneyRuntimeStage.Blocked"/>: the goods on the vehicle are an
    /// administrator's to dispose of through a recovery session.
    /// </summary>
    public const string AreaEndAdmissionRevokedTimeoutReason = "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT";

    /// <summary>
    /// 扫码之后给这条需求分仓位，分不出够数的仓位（REQ-0352，control-server#754）：这一侧能分的空仓比它预留的花篮数少——受理保证了数量，
    /// 所以多半是受理之后这一侧有仓位被禁用——或者分配要读的东西读不到（这条需求没有分区归属冻结、车的仓位模型或车载端事实读不到）。
    /// 这次录入按 <c>SLOT_INOPERABLE</c> 拒收，不装货、不开门，旅程停在等录入，等操作员在那一行取消这条需求。
    /// </summary>
    public const string LoadSlotAssignmentShortReason = "LOAD_SLOT_ASSIGNMENT_SHORT";

    /// <summary>
    /// 要下装货或下一条卸货命令时，RIoT 读不出车此刻停在这个停靠上（control-server#783）：当前站不是这一站（含 0，即没有当前站）、
    /// 读数离线、过期、在别的地图上或车在动。命令不发、不开门：装货那一侧这次录入被拒收，车回到站上之后操作员再扫一次；
    /// 卸货那一侧每一轮再核，车回到站上的那一轮照常发。
    /// </summary>
    /// <remarks>
    /// 到站判定（<see cref="CheckArrivalAsync"/>）只在到站那一刻核一次位置。之后补偿放回、修复续行、重连、服务端重启、急停释放都不重进到站阶段，
    /// 而车可能在阻塞或断线期间被人开走、在别处停稳、会话恢复 Ready——10-19 agv03 一事。所以在开门的那一处再核，哪一条入口都绕不过去。
    /// </remarks>
    public const string SlotCommandVehicleNotAtStopReason = "SLOT_COMMAND_VEHICLE_NOT_AT_STOP";

    /// <summary>卸货因车不在站上扣着期间，多久再记一次当前读数（事件 2318，control-server#783 审查 S-1）。</summary>
    private static readonly TimeSpan HeldSlotCommandLogInterval = TimeSpan.FromMinutes(5);

    /// <summary>The code a stop held at its AREA machine for the station's admission carries while it waits.</summary>
    private const string AreaEndAdmissionHeldReason = "TASK_TYPE_NOT_ALLOWED_AT_STATION";

    /// <summary>The journey is not arriving because the vehicle is holding at a traffic checkpoint.</summary>
    public const string CheckpointWaitReason = "VEHICLE_WAITING_AT_CHECKPOINT";

    /// <summary>The checkpoint wait has lasted longer than the configured budget.</summary>
    public const string CheckpointWaitExceededReason = "VEHICLE_CHECKPOINT_WAIT_EXCEEDED";

    /// <summary>
    /// After the leg name (<c>PICKUP_</c>, <c>GATE_</c>): this leg's order has never been sent, and the vehicle does not now show
    /// what creating it needs -- Onboard's departure summary, no fault, RIoT's safety read (control-server#375). A wait on the
    /// vehicle; the next round asks again.
    /// </summary>
    public const string NeverSentLegWaitingVehicleSuffix = "CREATE_WAITING_VEHICLE";

    /// <summary>
    /// RIoT reports this leg's in-flight order HANG (9): it stopped executing it, and only a person can move it on, by
    /// continuing or cancelling it in RIoT (control-server#316; riot-behavior-lab BC-ORDER-015).
    /// </summary>
    public const string OrderHangReason = "ORDER_HANG";

    /// <summary>
    /// RIoT reports this leg's in-flight order in SUSPENDED (8), a state never observed in the lab and of unknown
    /// meaning. Named, and nothing else is done: it is treated as a live order (control-server#316, cs#296).
    /// </summary>
    public const string OrderStateUnrecognizedReason = "ORDER_STATE_UNRECOGNIZED";

    /// <summary>
    /// RIoT reports this leg's in-flight order CANCELLED (2) or DELETED (6) although the vehicle never arrived:
    /// someone ended it outside this server (control-server#316). Named and alarmed; since control-server#318 this is the
    /// transitional code while the order waits to be rebuilt for the same vehicle and demand -- never released, never
    /// redispatched, nobody asked.
    /// </summary>
    public const string OrderEndedWithoutArrivalReason = "ORDER_ENDED_WITHOUT_ARRIVAL";

    /// <summary>
    /// Whether <paramref name="reasonCode"/> says the journey's in-transit order is not moving: one of the three codes an order
    /// RIoT stopped writes (control-server#316), or one of the codes of an ended order of this server's being rebuilt
    /// (control-server#318).
    /// </summary>
    /// <remarks>
    /// A journey carrying one of them is not <see cref="JourneyRuntimeStage.Blocked"/> -- it stays in its arrival stage so
    /// that a continue in RIoT, or the rebuilt order, moves it on without anything else -- but its vehicle is not under way,
    /// so it takes no appended demand (see where <c>underWay</c> is built, and <c>DispatchRoundRunner.ReadEnRoutePlanAsync</c>),
    /// a silent session does not overwrite the code (<see cref="NameSilentOnboardSessionAsync"/>), and the release service
    /// neither releases its demand nor cancels its order (<c>DemandReleaseService</c>).
    /// </remarks>
    public static bool IsStalledOrderReason(string? reasonCode) => reasonCode is
        OrderHangReason or OrderStateUnrecognizedReason or OrderEndedWithoutArrivalReason or
        VehicleFaultRecoveryService.CargoOnBoardReason or VehicleFaultRecoveryService.NothingOnBoardReason or
        OwnOrderRebuildWaitingVehicleReason or OwnOrderRebuildBlockedByCreateGateReason or
        OwnOrderRebuildOrderUnconfirmedReason or OwnOrderRebuildStoppedReason or
        OwnOrderRebuildWaitingCargoEvidenceReason or OwnOrderRebuildCargoNotInPlaceReason or
        OwnOrderRebuildCargoUnprovenReason or
        OwnOrderRebuildVehicleIneligibleReason or OwnOrderRebuildMandatoryChargeReason or OwnOrderNeverAppearedReason;

    /// <summary>
    /// The journey is waiting on a fact only the vehicle can supply, and the vehicle has gone quiet: no legal inbound
    /// from this session generation for <see cref="SessionLiveness.Timeout"/> (ADR-cross-0027, control-server#234).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is display and escalation, nothing else.</b> REQ-0287: a session that has failed blocks the vehicle's
    /// new business and is otherwise watched through RIoT; no timeout of it ever ends an order, releases a lease,
    /// reassigns, or moves the vehicle. So this code changes no stage and issues no order command -- it makes a
    /// journey that was invisible show up on the blocked-journey card, and it climbs the escalation ladder from there.
    /// </para>
    /// <para>
    /// <b>Not <c>ONBOARD_SESSION_NOT_READY</c>, on purpose.</b> That one is written when the session row itself is no
    /// longer Ready, which a dropped or silent link does not do by itself: nothing on the disconnect path writes the
    /// database at all (<c>OnboardTcpServer.HandleClientAsync</c>'s finally only detaches the peer), and
    /// <c>WireToGateStore.RecordConnectionLossAsync</c> still has no product caller. A silent vehicle therefore keeps a
    /// Ready session row, and the readiness branch of <see cref="AdvanceAsync"/> is never reached for it.
    /// </para>
    /// </remarks>
    public const string OnboardSessionLostReason = "ONBOARD_SESSION_LOST";

    /// <summary>
    /// 这一轮推进抛了异常，旅程一步没动（control-server#331）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要一个码。</b>推进抛出的异常由 <c>JourneyRuntimeWorker</c> 记成 2002 然后接着下一轮，旅程行一个字节都不变。
    /// 每一轮都抛同一个异常时，看板上显示的就是最后一次写码时留下的那个值——control-server#331 的现场里是断线那一刻的
    /// <c>ONBOARD_SESSION_NOT_READY</c>，从 16:20:44 起再没更新过，而真正的原因（到站那一段发布每轮都失败）只在日志里。
    /// 看板说的是一件已经不成立的事，等于把人往错的方向引。
    /// </para>
    /// <para>
    /// <b>它只说「推进失败了」，不说失败在哪。</b>异常的类型与栈在 2002 日志里，这里要的是让这辆车在阻塞看板上出现、
    /// 并按 program#55 的档位往上爬。所以它不改阶段、不发命令、不动需求。
    /// </para>
    /// <para>
    /// <b>已经 <c>Blocked</c> 的旅程不覆盖。</b>那条旅程等的是它的码指名的那次人工处置，覆盖掉就抹掉了「在等谁」这个
    /// 唯一的记录——与 <see cref="AdvanceAsync"/> 里就绪闸门那一段同一个理由。
    /// </para>
    /// </remarks>
    public const string AdvanceFailedReason = "JOURNEY_ADVANCE_FAILED";

    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    private readonly TaskTypeStationAccess _taskTypeStations = taskTypeStations;


    /// <summary>
    /// Runs the catalog change convergence without letting its failure end the round (control-server#201, review D of
    /// #162). A busy database or a constraint conflict here used to escape <see cref="ExecuteOnceAsync"/>, so the
    /// admission policy and every journey under way sat out the round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carrying on opens nothing: a demand bound to a renamed or removed station is already refused this round by the
    /// fixed station view, which judges every binding against this very catalog before it looks at holds. The hold
    /// arriving a round late only delays what it adds on top -- staying in force once the station is back.
    /// </para>
    /// <para>
    /// The convergence's transaction rolls back on its own, but what it touched stays tracked on the shared context:
    /// a pending entry the round's next save would write after all (a hold without its change record or its audit),
    /// or an entry it already saved inside the rolled-back transaction, tracked as Unchanged for a row the database no
    /// longer has. So every entry the attempt began tracking is detached, and every pending one too; nothing else in
    /// the round leaves one pending at this point, because every step before this one saved. A shutdown cancellation
    /// still ends the round.
    /// </para>
    /// </remarks>
    private async Task ConvergeCatalogBindingHoldsAsync(
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        HashSet<object> trackedBefore = dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            await catalogBindingHolds.ApplyAsync(currentMap, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (EntityEntry left in dbContext.ChangeTracker.Entries()
                .Where(entry => !trackedBefore.Contains(entry.Entity)
                    || entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToArray())
            {
                left.State = EntityState.Detached;
            }
            LogCatalogBindingHoldConvergenceFailed(logger, currentMap.MapId, error);
        }
    }

    /// <summary>
    /// Runs the foreign running order supervision (control-server#330) without letting its failure end the round, the way
    /// <see cref="ConvergeCatalogBindingHoldsAsync"/> runs the catalog convergence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carrying on opens nothing. The vehicles already held stay held -- the round reads them from the store afterwards, not
    /// from the supervision -- and a foreign order this attempt did not get to record is still caught by the 0/1 gate every
    /// dispatch goes through: RIoT's vehicle safety read reports <c>RIOT_NONFINAL_ORDER_PRESENT</c> for any unfinished order on
    /// the vehicle. What is lost is one round of recognising and cancelling, which the next round redoes from the store: a
    /// decided cancel is re-read and sent, an armed one is never sent again.
    /// </para>
    /// <para>
    /// What the attempt left tracked is detached, as for the convergence: a half-staged row or audit attempt must not ride out
    /// on the round's next save. A shutdown cancellation still ends the round.
    /// </para>
    /// </remarks>
    private async Task SuperviseForeignOrdersAsync(CancellationToken cancellationToken)
    {
        HashSet<object> trackedBefore = dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            await foreignOrders.SuperviseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (EntityEntry left in dbContext.ChangeTracker.Entries()
                .Where(entry => !trackedBefore.Contains(entry.Entity)
                    || entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToArray())
            {
                left.State = EntityState.Detached;
            }
            LogForeignOrderSupervisionFailed(logger, error);
        }
    }

    /// <summary>
    /// Runs the Map level rename check (control-server#186) without letting its failure end the round, the way
    /// <see cref="ConvergeCatalogBindingHoldsAsync"/> runs the catalog convergence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failed Map list read -- a timeout, an error, an answer that is not a list -- is not a rename (the ticket's item 4):
    /// nothing is held, the baseline stays, and holds a rename already raised stay in force, because the fixed station view
    /// reads them from the store. Whether the Map can be used at all is the catalog freshness gate's question, driven by the
    /// station catalog read right after this.
    /// </para>
    /// <para>
    /// First in the round, before the fixed station view reads the holds, so a rename holds its task types in the very
    /// round that first sees it. What the attempt left tracked is detached; a shutdown cancellation still ends the round.
    /// </para>
    /// </remarks>
    private async Task ObserveMapNamesAsync(CancellationToken cancellationToken)
    {
        HashSet<object> trackedBefore = dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            await mapRenameHolds.ObserveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (EntityEntry left in dbContext.ChangeTracker.Entries()
                .Where(entry => !trackedBefore.Contains(entry.Entity)
                    || entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToArray())
            {
                left.State = EntityState.Detached;
            }
            LogMapRenameObservationFailed(logger, error);
        }
    }

    private static readonly Action<ILogger, Exception?> LogForeignOrderSupervisionFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2189, nameof(LogForeignOrderSupervisionFailed)),
        "The supervision of foreign orders running on this server's vehicles failed; this round goes on without it, vehicles " +
        "already held stay held, and the next round takes it up again from what is recorded.");

    public async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        if (!runtimeOptions.Enabled)
        {
            return;
        }

        try
        {
            await ExecuteRoundAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // control-server#273: at the end of every round, however it ended -- the Map catalog could not be read and the
            // round returned early, or an advance threw. When the engine is stuck is exactly when a vehicle standing still
            // needs someone told. After the dispatch round, so the battery reads never delay a dispatch. The watch reads,
            // records and logs, never writes a stage, a reason or an order, and throws nothing but a shutdown cancellation,
            // so it cannot replace the exception a failed round is carrying out of here.
            if (!cancellationToken.IsCancellationRequested)
            {
                await new WaitingJourneyWatch(dbContext, vehicleFacts, chargingPolicy, runtimeOptions, timeProvider, logger)
                    .ObserveAsync(cancellationToken).ConfigureAwait(false);
                await WatchOpenStopsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// control-server#539 review H-1: every vehicle on which this server has an emergency stop open has that stop watched every
    /// round -- settled, re-triggered when a latch comes off while the stop stands (REQ-0248 as revised by CP-0011) -- whether
    /// or not this round saw a symptom on it (<see cref="VehicleFaultCoordinator.WatchOpenStopAsync"/>). At the end of the
    /// round, however it ended, beside the waiting-journey watch and for the same reason: an engine that is stuck is exactly
    /// when a stop must not be forgotten. One vehicle's failed read does not keep the next one from being watched.
    /// </summary>
    private async Task WatchOpenStopsAsync(CancellationToken cancellationToken)
    {
        foreach (FleetVehicle vehicle in roster.Vehicles)
        {
            try
            {
                await faults.WatchOpenStopAsync(new EmergencyStopSubject(vehicle.AgvId, vehicle.VehicleKey), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogOpenStopWatchFailed(logger, vehicle.AgvId, error);
            }
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogOpenStopWatchFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2308, nameof(LogOpenStopWatchFailed)),
            "The open emergency stop on {AgvId} could not be watched this round; it is watched again next round.");

    private async Task ExecuteRoundAsync(CancellationToken cancellationToken)
    {
        // control-server#330: first, before anything reads which vehicles are free -- a foreign order found running on a
        // vehicle of ours this round holds that vehicle this round. Not behind the Map catalog read: cancelling an order that
        // is not ours does not depend on the Map.
        await SuperviseForeignOrdersAsync(cancellationToken).ConfigureAwait(false);
        // control-server#391 (REQ-0204): a public station whose holder has left it on evidence is freed, and then given to a
        // vehicle already heading for it without a reservation, before this round dispatches anything against it -- that
        // vehicle comes before any new task. Not behind the Map catalog read: departure is read off RIoT's vehicle position,
        // and which stations are public off the Map's active binding set.
        FixedStationExclusivitySweep fixedStationSweep = new(dbContext, vehicleFacts, timeProvider, logger, _fixedStationWarnings);
        await fixedStationSweep.ReleaseDepartedAsync(cancellationToken).ConfigureAwait(false);
        TaskTypeStationBindingSetVersion? publicStationBindings = await _taskTypeStations.Bindings
            .ReadActiveAsync(runtimeOptions.MapId, cancellationToken).ConfigureAwait(false);
        await fixedStationSweep.ReserveApproachingAsync(
                (publicStationBindings?.Bindings ?? []).Select(binding => binding.StationRiotId).ToHashSet(),
                cancellationToken)
            .ConfigureAwait(false);
        // control-server#186: a Map renamed under the same mapId holds every task type bound on it, before this round's
        // fixed station view reads the holds. Not behind the station catalog read: the two reads fail independently.
        await ObserveMapNamesAsync(cancellationToken).ConfigureAwait(false);

        RiotMapStationCatalogSnapshot currentMap;
        IFixedTaskStationView fixedStations;
        IReadOnlyList<RiotMapStation> machineStations;
        try
        {
            currentMap = await mapStationCatalog.ReadMapStationsAsync(
                runtimeOptions.MapId, cancellationToken).ConfigureAwait(false);
            // Once per round, so that every candidate is judged against the same rules and bindings. A fixed
            // station missing from the Map is not a catalog failure (control-server#160, REQ-0342): the view
            // refuses the task type bound to it and nothing else, so journeys under way and every other task type
            // go on. Only a resolver that cannot use the Map at all throws StationResolutionException.
            fixedStations = await fixedStationResolver.ReadForRoundAsync(currentMap, cancellationToken)
                .ConfigureAwait(false);
            machineStations = stationResolver.ParseAreaNamedMachineStations(currentMap);
        }
        catch (Exception error) when (
            error is HttpRequestException or InvalidDataException or JsonException or StationResolutionException)
        {
            LogMapStationCatalogFailed(logger, error);
            // REQ-0302/REQ-0308: a failed attempt is a catalog-level state, and explicitly not a
            // confirmation -- it must not extend the freshness window. Whether it blocks depends
            // on how long ago the last real confirmation was, which the availability check decides.
            await catalogAvailability.RecordFailureAsync(
                runtimeOptions.MapId,
                error is StationResolutionException
                    ? MapStationCatalogState.CandidateInvalid
                    : MapStationCatalogState.RefreshFailed,
                error is StationResolutionException resolution
                    ? resolution.ReasonCode
                    : "CATALOG_REFRESH_FAILED",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Read whole and machine stations parsed: this is what a complete confirmation is, and the only thing
        // freshness is measured from. Whether some task type's fixed station is in it does not enter into it
        // (REQ-0302); that is the task type's own admission question.
        await catalogAvailability.RecordConfirmationAsync(currentMap, cancellationToken)
            .ConfigureAwait(false);
        // control-server#162: a bound station renamed or gone holds its own Map + TASK_TYPE, read from the next round on.
        await ConvergeCatalogBindingHoldsAsync(currentMap, cancellationToken).ConfigureAwait(false);

        // The policy is re-derived from the live map every iteration, and map 25 is shared: RIoT's
        // other users add, rename and remove stations on it. Any such edit to an area-named station
        // arrives here as a different set under the version this deployment names, and the store
        // refuses to rebind it. That refusal used to escape this method, so every iteration failed
        // before AdvanceAsync -- a loaded vehicle already bound for the gate was never given its
        // unload until someone raised the version and restarted. ADR-cross-0050 and 0051 confine a
        // policy change to what is not yet committed: the bound policy stays in force for every
        // journey under way, and only taking on a further demand waits for the version, which
        // AdmissionPolicyDriftCriterion enforces for every vehicle this round serves. One operation of
        // a journey under way is not committed yet and is asked against the current admissions: the
        // unload of a journey whose AREA machine is the drop-off (STAGING_TO_WIRE), until it is
        // prepared. A machine that no longer admits it holds the loaded vehicle there, and past
        // JourneyRuntimeOptions.AreaEndAdmissionRevokedTimeout the journey is blocked for manual
        // recovery rather than left waiting unseen (control-server#198).
        string[] liveStationNames = machineStations.Select(station => station.StationName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool admissionPolicyDrifted = false;
        // Each area-named station is paired with every task type this build can execute, whichever end of the
        // route the station is (control-server#163): the admission follows the AREA machine end, which is the
        // pickup for WIRE_TO_GATE and the drop-off for STAGING_TO_WIRE. Adding STAGING_TO_WIRE changed the
        // relations and their hash, so the shipped admissionPolicyVersion moved past 1 with it; adding the four
        // same-direction task types (control-server#545) moved it to 3 (AdmissionPolicyVersionGuardTests).
        string[] seededTaskTypes = await AdmissionSeedTaskTypesAsync(cancellationToken).ConfigureAwait(false);
        StationTaskTypeAdmission[] seededRelations = liveStationNames
            .SelectMany(stationName => seededTaskTypes
                .Select(taskType => new StationTaskTypeAdmission(stationName, taskType)))
            .ToArray();
        try
        {
            await store.ApplyAdmissionPolicyAsync(
                new AdmissionPolicyDefinition(
                    runtimeOptions.AdmissionPolicyVersion,
                    runtimeOptions.AdmissionPolicyDeploymentId,
                    seededRelations,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessIdentityConflictException error)
        {
            admissionPolicyDrifted = true;
            // Wording the log must not cost the round or hide the refusal: a failed read falls back to the store's words.
            string driftCauses;
            try
            {
                driftCauses = await DescribeAdmissionPolicyDriftAsync(
                    liveStationNames, seededTaskTypes, seededRelations.Length, error, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception describeError) when (!cancellationToken.IsCancellationRequested)
            {
                driftCauses = $"the store said: {error.Message} (the cause could not be read from the database: " +
                              $"{describeError.GetType().Name}: {describeError.Message})";
            }
            LogAdmissionPolicyDrift(
                logger,
                runtimeOptions.AdmissionPolicyVersion,
                runtimeOptions.MapId,
                driftCauses,
                error);
        }

        // control-server#390: an idle return commitment the dispatch round made (a purpose claim and a waiting point
        // reservation, batch 8-18) becomes a journey here, before this round reads which journeys it advances. A commitment
        // that cannot become one is released here too, so none can hold its vehicle for good.
        await MaterializeIdleReturnsAsync(cancellationToken).ConfigureAwait(false);

        JourneyRuntimeRow[] active = await dbContext.JourneyRuntimes
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        active = active.OrderBy(row => row.CreatedAt).ToArray();
        // The one-journey invariant is per vehicle, not per server. It was written as a global
        // check while there was one vehicle, and those are the same statement then; with a fleet
        // they are not, and reading it globally would refuse to run the fleet at all.
        string[] doubleBooked = active
            .GroupBy(row => row.AgvId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (doubleBooked.Length > 0)
        {
            throw new BusinessIdentityConflictException(
                $"More than one unresolved journey exists for vehicle: {string.Join(',', doubleBooked)}.");
        }

        // Vehicles are advanced before free ones are served, and a journey created this round is
        // not advanced until the next one. With one vehicle that is exactly the old shape -- either
        // it had a journey and only advanced, or it had none and only discovered.
        HashSet<string> yielded = new(StringComparer.Ordinal);
        ExceptionDispatchInfo? firstFailure = null;
        foreach (JourneyRuntimeRow runtime in active)
        {
            // control-server#357：这台车这一轮的决定都出自上面那一次读。入站在那之后提交了这一行（取消收尾、保持阻断……），
            // 守护让这台车推进期间的每一次保存都先核这一行的版本，第一次保存就失败；这台车这一轮让开，下一轮按新行重判。
            // 只让这一台让开：别的车的推进与这次提交无关，整轮抛掉会让一台车的入站拖住全车队。
            //
            // 这一行已经不在跟踪里（前面某台车让开时它的实例被丢了）就这一轮跳过：不跟踪的行守护核不到，推进照样会存意图、发消息、
            // 调 RIoT，而旅程行自己的改动一样都存不下（独立审查必修 2）。
            if (dbContext.Entry(runtime).State == EntityState.Detached)
            {
                yielded.Add(runtime.AgvId);
                continue;
            }
            TrackedBeforeAdvance before = TrackedBeforeAdvance.Take(dbContext);
            dbContext.GuardedJourneyId = runtime.JourneyId;
            try
            {
                try
                {
                    // control-server#390: an idle return has its own branch; it never enters the transport state machine.
                    if (runtime.IsIdleReturn())
                    {
                        await AdvanceIdleReturnAsync(runtime, currentMap, cancellationToken).ConfigureAwait(false);
                    }
                    // control-server#404: so has a charging journey, up to its arrival at the charger.
                    else if (runtime.IsCharging())
                    {
                        await AdvanceChargingAsync(runtime, currentMap, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await AdvanceAsync(runtime, currentMap, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                    when (!JourneyRowConflict.Is(error) &&
                          (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
                {
                    // control-server#334：这台车的车载端连接此刻不可用（没连上、代次不对、写超时、连接已关）。这是这一台车的事，
                    // 它这一轮让开，别的车照常推进——整轮抛掉会让后面每一台车的推进、急停确认与 REQ-0248 重触发、派车都停在它身上，
                    // 而断线不写库、会话行还是 Ready，下一轮它照样抛，一直到它重连为止。写超时按构造总会留下一条未确认的发件箱行，
                    // 这条路径因此一定会走到。
                    if (OnboardConnectionUnavailableException.IsIn(error))
                    {
                        LogYieldedToUnavailableConnection(logger, runtime.AgvId, runtime.JourneyId, error);
                    }
                    else
                    {
                        // control-server#487：别的失败也只是这一台车的事。整轮抛掉会让排在后面的每一台车的推进、急停确认与 REQ-0248
                        // 重触发、派车都停在它身上，而一台每轮都抛同一个异常的车（control-server#291、#354）会让它们一直停着。
                        // 这台车照 control-server#331 在看板上说出「推进失败」，这一轮不给它派车（它进 yielded）；第一个这样的异常在
                        // 轮末原样抛出，2002 记的仍是它。
                        LogAdvanceFailedVehicleIsolated(logger, runtime.AgvId, runtime.JourneyId, error);
                        firstFailure ??= ExceptionDispatchInfo.Capture(error);
                    }
                    await NameFailedAdvanceAsync(runtime, before, error, cancellationToken).ConfigureAwait(false);
                    yielded.Add(runtime.AgvId);
                    continue;
                }

                await ClearFailedAdvanceAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException conflict) when (JourneyRowConflict.Is(conflict))
            {
                YieldToJourneyCommit(runtime, before, conflict);
                yielded.Add(runtime.AgvId);
            }
            finally
            {
                dbContext.GuardedJourneyId = null;
            }
        }

        // 没有在途旅程的车上还没答的出发请求（control-server#713）：在派车之前答，免得这一轮刚派的旅程把它当成自己的。
        await AnswerDepartureRequestsWithoutJourneyAsync(active, cancellationToken).ConfigureAwait(false);

        await ServeFreeVehiclesAsync(
                currentMap, fixedStations, admissionPolicyDrifted, active, yielded, cancellationToken)
            .ConfigureAwait(false);
        // control-server#487: after every other vehicle has advanced and the dispatch round has run. A failure of the dispatch
        // round itself leaves above instead; the isolated one was already logged with its vehicle (2307).
        firstFailure?.Throw();
    }

    /// <summary>
    /// The second half of a round: once every journey under way has advanced, the vehicles free for work and those that can
    /// take an appended demand are served by the dispatch round.
    /// </summary>
    private async Task ServeFreeVehiclesAsync(
        RiotMapStationCatalogSnapshot currentMap,
        IFixedTaskStationView fixedStations,
        bool admissionPolicyDrifted,
        JourneyRuntimeRow[] active,
        HashSet<string> yielded,
        CancellationToken cancellationToken)
    {
        HashSet<string> busy = active.Select(row => row.AgvId).ToHashSet(StringComparer.Ordinal);
        // control-server#330 (REQ-0164, the 0/1 gate not relaxed): a vehicle a foreign order is running on takes no new
        // dispatch and no appended demand until RIoT reads that order back ended -- whether its cancel is on the way, went out
        // and did not take, or ownership could not be proven and nothing is cancelled. Read after this round's supervision, so
        // an order found this round holds its vehicle this round.
        HashSet<string> heldByForeignOrder = await ForeignOrders.ForeignRunningOrders
            .HeldAgvIdsAsync(dbContext, cancellationToken).ConfigureAwait(false);
        FleetVehicle[] free = roster.Vehicles
            .Where(vehicle => !busy.Contains(vehicle.AgvId) && !heldByForeignOrder.Contains(vehicle.AgvId))
            .ToArray();
        // Blocked 的旅程占着车，却接不了追加：它等的是人介入，在途资格链无条件拒绝它。所以它让车算 busy
        // （不能当空闲车派），但不进 underWay（不值得当可追加的车去问）。这个区分不是优化：underWay 的
        // 含义是「可以考虑给它追加的车」，把一辆接不了追加的车放进去，会让轮次在一个本就没有活可派的局面下
        // 照样把整张候选表判一遍。
        // A journey whose in-flight order stopped in RIoT (HANG, SUSPENDED, ended without arrival) is left in its arrival
        // stage on purpose, so that a continue resumes it untouched, but it is waiting on a person just the same: an appended
        // demand would be bound behind an order that does not move (control-server#316). Its code was written by the
        // advance above, in this same round.
        HashSet<string> blocked = active
            .Where(row => row.Stage == JourneyRuntimeStage.Blocked || IsStalledOrderReason(row.BlockReasonCode))
            .Select(row => row.AgvId)
            .ToHashSet(StringComparer.Ordinal);
        // 这一轮让开的车（control-server#357）：手里那份旅程是旧读，此刻库里的它可能已经收尾或转了阻塞。这一轮不把它当可追加的车去问，
        // 下一轮按新行重判；它仍算 busy，不会被当空闲车派。
        blocked.UnionWith(yielded);
        // An idle return holds its vehicle (REQ-0290: a purpose already started is not taken over by a new one), so it is busy,
        // and it takes no appended demand: it carries none to append to (control-server#390). The same goes for a charging
        // journey (control-server#404): a vehicle committed to charging takes no transport on the way.
        blocked.UnionWith(active.Where(row => row.CarriesNoDemand()).Select(row => row.AgvId));
        FleetVehicle[] underWay = roster.Vehicles
            .Where(vehicle => busy.Contains(vehicle.AgvId) && !blocked.Contains(vehicle.AgvId) &&
                              !heldByForeignOrder.Contains(vehicle.AgvId))
            .ToArray();
        // 空闲车与可追加的在途车都没有，才退（批次7-06，control-server#211）。在这之前判的是「没有空闲车」——
        // 那时在途车走一条一律拒绝的占位路径，问它等于白问，所以提前退出是对的。本票让在途车与空闲车在同一张
        // 候选表上竞争（REQ-0205），「全车队都在途」于是成了一种正常的、有活可派的局面：单车现场里它甚至是
        // 常态——车一接单就不再空闲，此后到卸完货为止的每一条新需求都只能靠追加接。按空闲车判会让这些需求一条
        // 都看不见，而这正是同区追加那条 L2 场景第一次跑出来的样子。
        if (free.Length == 0 && underWay.Length == 0)
        {
            return;
        }

        // The orphan check guards intake, so it runs when this round is about to take work on --
        // which with one vehicle is exactly when it ran before, since the single vehicle being
        // free is the same statement as no journey being active.
        // A demand released for redispatch has no membership in force by design (control-server#215); it waits for
        // this very round to take it on again, so it is not an orphan.
        string[] unresolvedDemandIds = await DemandJourneyLookup.OrphanCandidates(dbContext)
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // A demand is carried by a journey when a membership in force says so (control-server#207), not when a journey
        // row names it: a demand added to a journey has no row of its own. A journey whose demands have all ended is no
        // orphan either -- none of them is unresolved -- and the next round closes it.
        string[] memberDemandIds = await DemandJourneyLookup.Memberships(dbContext)
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] orphaned = unresolvedDemandIds.Except(memberDemandIds, StringComparer.Ordinal).ToArray();
        if (orphaned.Length > 0)
        {
            throw new BusinessIdentityConflictException(
                $"Unresolved accepted demand has no production journey runtime: {string.Join(',', orphaned)}.");
        }

        await dispatchRound.RunAsync(
                currentMap, fixedStations, free, underWay, admissionPolicyDrifted, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The task types the station admission seed carries: those this build can execute, at either fixed end. The seed
    /// is per area-named machine station, and a task type is admitted at its AREA end whether that is the pickup
    /// (fixed end at the destination) or the drop-off (fixed end at the origin); control-server#160 seeded only the
    /// former, which was all it could execute.
    /// </summary>
    /// <remarks>
    /// Read from the rules the Map's active binding set was built on -- the same version the resolver judges the round
    /// by -- not the latest one: a rule version written ahead of its binding set must not change the seed, which the
    /// store would take for a policy drift and stop every acceptance on.
    /// </remarks>
    private async Task<string[]> AdmissionSeedTaskTypesAsync(CancellationToken cancellationToken)
    {
        TaskTypeStationBindingSetVersion? bindingSet = await _taskTypeStations.Bindings
            .ReadActiveAsync(runtimeOptions.MapId, cancellationToken).ConfigureAwait(false);
        TaskTypeStationRuleVersion? rules = bindingSet is null
            ? await _taskTypeStations.Rules.ReadCurrentAsync(cancellationToken).ConfigureAwait(false)
            : await _taskTypeStations.Rules.ReadVersionAsync(bindingSet.RuleVersion, cancellationToken)
                .ConfigureAwait(false);
        return
        [
            .. (rules?.Rules ?? [])
                .Where(rule => ExecutableTaskTypes.Contains(rule.TaskType))
                .Select(rule => rule.TaskType)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// What EventId 2116 says the store refused (control-server#551): each of the four drifts that holds, with its old
    /// and its new value, joined by "; ".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is against the version the store has bound, read by its <c>PolicyVersion</c>. Under the same
    /// version that is the configured one; when the configured version moved backwards it is the higher one bound, and
    /// the log names it. The store replaces every relation row when it binds a new version, so rows of an earlier
    /// version are not there to mix in; the filter keeps the comparison right should that ever stop being so.
    /// </para>
    /// <para>
    /// The stations and the task types are compared as this build seeds them, not as read back from the relations: the
    /// relations are their product, and with either side empty the other would read as empty too (review S1).
    /// </para>
    /// <para>
    /// A refusal none of the four explains -- the relations pair the same stations with the same task types
    /// differently, or the store refused for another reason -- is said to be one, with the store's own words. It is
    /// never reported as a drift of nothing. Above the bound version the store compares neither content nor identity,
    /// so a refusal there is always one of these (review S4).
    /// </para>
    /// </remarks>
    private async Task<string> DescribeAdmissionPolicyDriftAsync(
        string[] liveStationNames,
        string[] seededTaskTypes,
        int seededRelationCount,
        BusinessIdentityConflictException refusal,
        CancellationToken cancellationToken)
    {
        AdmissionPolicyStateRow? bound = await dbContext.AdmissionPolicyState.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == 1, cancellationToken).ConfigureAwait(false);
        if (bound is null || runtimeOptions.AdmissionPolicyVersion > bound.Version)
        {
            return $"cause not distinguished from the database (configured version " +
                   $"{runtimeOptions.AdmissionPolicyVersion} is above the bound version {bound?.Version ?? 0}, where " +
                   $"the store refuses only the relations themselves; the store said: {refusal.Message})";
        }

        long boundVersion = bound.Version;
        StationTaskTypeAdmission[] boundRelations = await dbContext.StationTaskTypeAdmissions.AsNoTracking()
            .Where(row => row.PolicyVersion == boundVersion)
            .Select(row => new StationTaskTypeAdmission(row.StationId, row.TaskType))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        List<string> causes = [];
        string[] boundStations = Distinct(boundRelations.Select(relation => relation.StationId));
        string[] seededStations = Distinct(liveStationNames);
        if (!boundStations.SequenceEqual(seededStations, StringComparer.Ordinal))
        {
            causes.Add(
                $"stations changed under bound version {boundVersion} " +
                $"(added: {List(seededStations.Except(boundStations, StringComparer.Ordinal))}; " +
                $"removed: {List(boundStations.Except(seededStations, StringComparer.Ordinal))})");
        }

        string[] boundTaskTypes = Distinct(boundRelations.Select(relation => relation.TaskType));
        string[] seeded = Distinct(seededTaskTypes);
        if (!boundTaskTypes.SequenceEqual(seeded, StringComparer.Ordinal))
        {
            causes.Add(
                $"executable task types changed under bound version {boundVersion} " +
                $"(bound: {List(boundTaskTypes)}; this build seeds: {List(seeded)}; " +
                $"added: {List(seeded.Except(boundTaskTypes, StringComparer.Ordinal))}; " +
                $"removed: {List(boundTaskTypes.Except(seeded, StringComparer.Ordinal))})");
        }

        if (!string.Equals(bound.DeploymentId, runtimeOptions.AdmissionPolicyDeploymentId, StringComparison.Ordinal))
        {
            causes.Add(
                $"deployment identity changed (bound: {bound.DeploymentId}; " +
                $"configured: {runtimeOptions.AdmissionPolicyDeploymentId})");
        }

        if (runtimeOptions.AdmissionPolicyVersion < boundVersion)
        {
            causes.Add(
                $"version moved backwards (configured: {runtimeOptions.AdmissionPolicyVersion}; " +
                $"highest bound: {boundVersion})");
        }

        return causes.Count > 0
            ? string.Join("; ", causes)
            : $"cause not distinguished from the database (bound version {boundVersion} holds " +
              $"{boundRelations.Length} relations, this build seeds {seededRelationCount}, over the same stations, " +
              $"task types and deployment identity; the store said: {refusal.Message})";

        static string[] Distinct(IEnumerable<string> values) =>
            [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // An empty side of one cause is written out as such; the whole description is never "none" (see the remarks).
        static string List(IEnumerable<string> values) =>
            string.Join(", ", values) is { Length: > 0 } joined ? joined : "none";
    }

    /// <summary>
    /// 这台车这一轮推进抛了异常，它这一轮让开：撤回它这一轮没落库的改动，看板上把 <see cref="AdvanceFailedReason"/> 写到旅程行上
    /// （control-server#331），然后轮次接着推进下一台车。车载端连接不可用（control-server#334）与别的失败（control-server#487）
    /// 走这同一条路，只是日志不同。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>先撤回</b>（<see cref="TrackedBeforeAdvance.RestoreAsync"/>）。失败那一轮留在变更跟踪里的东西是半截的——旅程行可能已经被改成一个
    /// 它没走到的样子，别的车的行也可能被它改过（让站）——落库等于把半截状态当成事实，而后面的车的每一次保存都会顺带把它存下去。
    /// 撤回之后跟踪里留下的每一个条目都与库一致，或者是推进之前就留着的未保存改动。已经发生的外部副作用——保存之后才发的出站消息、
    /// 建了的 RIoT 单——不在撤回之列，下一轮按已存的意图续上，与整轮抛掉之后下一轮的样子相同。
    /// </para>
    /// <para>
    /// <b>不许清空变更跟踪。</b>轮次还要继续，清空会让排在后面的每一台车的旅程行都脱离跟踪，被循环开头那条「不在跟踪里就跳过」挡掉——
    /// 换一种方式照样让一台车拖住全队。所以只重读、只存这一行，存完不再跟踪它。
    /// </para>
    /// <para>
    /// <b>别的车还留着没保存的改动时，这一轮不写码。</b>这里的保存是为这一个字段来的，不该顺带提交别处刻意留到下一步的改动
    /// （与 <see cref="ClearFailedAdvanceAsync"/> 同一条规矩）。不写的代价只是看板晚一轮说出这件事：这台车下一轮照样失败，照样再试。
    /// </para>
    /// <para>
    /// <b>码相同就不动开始时间</b>（<see cref="JourneyRuntimeRow.SetBlockReason"/> 的幂等）：每轮都抛同一个异常时，
    /// 「已挂多久」要从第一次失败算起，否则 program#55 的升级档位每轮归零、永远升不上去。
    /// </para>
    /// <para>
    /// <b>不覆盖任何指名在等谁的码</b>——与失联那一段（<see cref="NameSilentOnboardSessionAsync"/>）是同一个判定
    /// <see cref="CarriesACodeThatNamesAWaitOnAPerson"/>，不另列清单。覆盖它们的代价不止是看板说错：在途单停住的码换掉，
    /// 派单轮次（<c>DispatchRoundRunner</c> 按 <see cref="IsStalledOrderReason"/> 挡途中追加）的闸就开了；
    /// <c>NameStalledOrderAsync</c> 下一轮写回原码时开始时间归零、升级档位清零、告警重发（control-server#331 审查必修 2）。
    /// </para>
    /// <para>
    /// <b><c>ONBOARD_SESSION_NOT_READY</c> 要分真假。</b>会话此刻确实没就绪、这一轮又是传输类失败时（连接不在，发送抛
    /// <c>IOException</c>，见 <see cref="IsTransportFailure"/>），它是真的，失败就是它造成的，这时不覆盖：真车载端挂着本服务端在途单时整段路都是这个码，覆盖掉会让两个码
    /// 每轮来回切——开始时间每轮归零，<c>ControlServerDbContext.ReconcileJourneyWaits</c> 里在路上这个码不算「在等」，
    /// 等待起点与告警也每轮清零重来（审查建议 3）。会话已经回到 <c>Ready</c> 而码还留着，它就是过时的——control-server#331
    /// 的现场正是这样，从断线那一刻留到最后——这时才换成「推进失败」。与传输无关的失败，会话未就绪也照样换成推进失败（第三轮审查
    /// 建议 3）：那种失败不是未就绪造成的，归给它会让看板一路说一件不相干的事。
    /// </para>
    /// <para>
    /// <b>写码失败不许顶替原异常。</b>原异常才是这一轮真正出的事；这里再抛一个（比如数据库也不可用）会让 2002 记下
    /// 一个与病因无关的错误。所以这里自己吞掉并单独记一条 2128。
    /// </para>
    /// <para>
    /// <b>让开的车自己的急停确认会推迟。</b>它这一轮走不到故障模型，它自己的急停确认与 REQ-0248 重触发这一轮不跑。断线时上限靠静默窗口：
    /// 会话行仍是 Ready 而连接已经没了时，这台车每一轮都在补发（<c>ReplayPendingForSessionAsync</c>）那里让开；最后一条合法入站之后约
    /// 6 s（<see cref="SessionLiveness.Timeout"/>），<see cref="NameSilentOnboardSessionAsync"/> 在补发之前接手，故障监看随之恢复——谁要把
    /// 静默判定挪到补发之后、或把静默窗口拉长，这里的推迟就跟着变长。别的失败没有这样的上限：每轮都抛的车自己的监看一直停着，看板上
    /// 它挂着推进失败、按 program#55 升级。修之前是整队都停着，所以这不是回归。
    /// </para>
    /// </remarks>
    private async Task NameFailedAdvanceAsync(
        JourneyRuntimeRow runtime,
        TrackedBeforeAdvance before,
        Exception failure,
        CancellationToken cancellationToken)
    {
        await before.RestoreAsync(dbContext, cancellationToken).ConfigureAwait(false);
        dbContext.Entry(runtime).State = EntityState.Detached;
        if (dbContext.ChangeTracker.Entries()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            return;
        }

        string journeyId = runtime.JourneyId;
        JourneyRuntimeRow? current = null;
        try
        {
            current = await dbContext.JourneyRuntimes
                .SingleOrDefaultAsync(row => row.JourneyId == journeyId, cancellationToken).ConfigureAwait(false);
            if (!await ShouldNameFailedAdvanceAsync(current, failure, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            current!.SetBlockReason(AdvanceFailedReason, now);
            current.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailedAdvanceNotNamed(logger, journeyId, error);
        }
        finally
        {
            if (current is not null)
            {
                dbContext.Entry(current).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// 失败的这一轮要不要在旅程行上写 <see cref="AdvanceFailedReason"/>，理由见 <see cref="NameFailedAdvanceAsync"/> 的 remarks。
    /// </summary>
    private async Task<bool> ShouldNameFailedAdvanceAsync(
        JourneyRuntimeRow? current,
        Exception failure,
        CancellationToken cancellationToken) =>
        current is not null &&
        current.Stage != JourneyRuntimeStage.Completed &&
        !string.Equals(current.BlockReasonCode, AdvanceFailedReason, StringComparison.Ordinal) &&
        !CarriesACodeThatNamesAWaitOnAPerson(current) &&
        !(string.Equals(current.BlockReasonCode, JourneyWaitClassification.SessionNotReadyReason, StringComparison.Ordinal) &&
          IsTransportFailure(failure) &&
          await CurrentReadySessionAsync(current.AgvId, cancellationToken).ConfigureAwait(false) is null);

    /// <summary>
    /// 推进重新走通的那一轮，把上一轮留下的 <see cref="AdvanceFailedReason"/> 清掉：它说的是「上一轮失败了」，
    /// 这一轮走通就不再成立（control-server#331）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只清这一个码。</b>别的码都是 <see cref="AdvanceAsync"/> 自己按当前事实写的，这一轮没有改写它就说明它仍然成立。
    /// </para>
    /// <para>
    /// <b>所以不能改成「每轮开头先清」。</b>那样每轮都要重写一次，开始时间跟着归零，看板上的「已挂多久」永远是 0——
    /// 连抛一百轮与刚抛第一轮长得一模一样。
    /// </para>
    /// <para>
    /// <b>这一轮还留着别的未保存改动时不清。</b>这里的保存是为这一个字段来的，不该顺带把别处刻意留到下一步的改动一起提交；
    /// 留着不清没有代价，下一轮再试一次。
    /// </para>
    /// </remarks>
    private async Task ClearFailedAdvanceAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        if (!string.Equals(runtime.BlockReasonCode, AdvanceFailedReason, StringComparison.Ordinal))
        {
            return;
        }

        if (dbContext.ChangeTracker.Entries()
            .Any(entry => !ReferenceEquals(entry.Entity, runtime) &&
                          entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AdvanceAsync(
        JourneyRuntimeRow runtime,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        // 上一轮推进落库的时刻，这一轮任何写入之前读（control-server#783 审查 S-1：长时间扣着卸货时按它判这一轮该不该再记读数）。
        DateTimeOffset previousAdvanceAt = runtime.UpdatedAt;
        SessionRecoveryRow? session = await CurrentReadySessionAsync(runtime.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            // ADR-cross-0055: the station departure wait is about this session's chance to scan, and a
            // journey behind a closed readiness gate has none -- the runtime advances no stop there, so
            // an entry would not be acted on either. Left running, the clock would end the stop on the
            // first iteration after readiness returned. Voided here and refilled behind the gate, the
            // same way a reconnect's is. A disconnect that leaves the session Ready is caught instead by
            // the liveness check in TryEndStopAtStationDeadlineAsync.
            bool waitVoided = runtime.StationDepartureWaitStartedAt is not null;
            runtime.StationDepartureWaitStartedAt = null;

            // A result that is already durable names the recovery this journey waits on, and it
            // names it from StationOperations alone -- reading it involves no session. Deciding
            // that here, ahead of the readiness gate, is what keeps the gate from closing on
            // exactly the journeys that most need their block named: since 147f02c a refused
            // result moves the session itself to RecoveryRequired, so the vehicle that just failed
            // a load is the one whose session is no longer Ready. L2 run
            // 20260904-recovery-entry-after-announce-001 finished on that deadlock, sitting at
            // AwaitingLoadResult / ONBOARD_SESSION_NOT_READY over a load result the database
            // already held.
            if (await TryBlockOnRecordedRecoveryAsync(runtime, now, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            // A Blocked journey is already waiting on the recovery its block reason names, and the
            // vehicle is normally powered down for exactly that repair -- so losing the session is
            // the ordinary case there, not news. Overwriting the reason threw away the only record
            // of which recovery is outstanding, and nothing rebuilds it: the field left a journey
            // reading "Blocked / ONBOARD_SESSION_NOT_READY", which names neither. Session
            // readiness carries its own row and its own reason code.
            // A stop held at its AREA machine for the station's admission is still held for that while the session is
            // down, and the reason is what control-server#198 counts the wait from: overwritten here, every reconnect
            // started the count again, and a link dropping more often than the threshold kept the loaded vehicle waiting
            // for ever.
            // An in-flight order RIoT has stopped is named ahead of the gate's own code (control-server#316, review high
            // item). This is not a corner: a real onboard reads RIoT's safety interface while its vehicle has this
            // server's order in flight, reports VEHICLE_NOT_READY, and the session sits at DEPARTURE_SAFETY_NOT_READY for
            // the whole leg -- HANG included, since 9 is a non-final state. Left to the gate, ORDER_HANG was never written
            // on a real onboard and 2127 never raised. Only the two arrival stages have an in-flight order to read.
            // A FAILED one goes to the fault model here too (control-server#358): recorded, held and stopped in this round,
            // and a vehicle already stopped keeps its trigger confirmed and re-triggered -- none of which waits on the
            // session, and none of which sends to it.
            if (runtime.Stage != JourneyRuntimeStage.Blocked && !IsHeldForAreaEndAdmission(runtime) &&
                await NameInFlightOrderWithoutThePeerAsync(runtime, currentMap, "ONBOARD_SESSION_NOT_READY", cancellationToken)
                    .ConfigureAwait(false))
            {
                if (waitVoided)
                {
                    runtime.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (runtime.Stage != JourneyRuntimeStage.Blocked && !IsHeldForAreaEndAdmission(runtime))
            {
                runtime.SetBlockReason("ONBOARD_SESSION_NOT_READY", now);
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (waitVoided)
            {
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            // 闸门关着，但派往取货站的那一版计划可能正是因为闸门关着才一直发不出去（control-server#314）。
            await PublishPickupDispatchPlanPastOwnOrderAsync(runtime, now, cancellationToken).ConfigureAwait(false);
            return;
        }
        // 本轮推进读到的停靠与归属。每一个要发出去的 id 都从这里取，取货与关卡两段不再各读各的列。
        //
        // 读在失联判定「之前」（批次7-06 与 control-server#234 在这里相邻）：失联判定里那道
        // control-server#228 的升级要按当前停靠判，而当前停靠只有游标说得出。读停靠是纯查询，不向车发
        // 任何东西，所以把它挪到前面不碰 #234 那句「这一行以下不是发给对端就是在等对端」——发布仍然
        // 一条都在失联判定之后。
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        // 哪些停靠是待送点取货停靠（control-server#709）：本轮下面每一处读期限、持货与仓门告警的地方都按它分。开关关着时什么也不读。
        // 停靠的角色与站点落库后不变，途中追加新开的停靠由它被推进到的那一轮认出，所以每轮认一次就够。
        await RecognizeStagingPickupStopsAsync(stops, cancellationToken).ConfigureAwait(false);
        // The session row says Ready, but does the vehicle still answer? Judged here, before anything is
        // published, because everything below this line either sends to the peer or waits on a fact only the
        // peer can supply (control-server#234).
        if (await NameSilentOnboardSessionAsync(runtime, session, stops, currentMap, now, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }
        // 车还没确认的出发答复（control-server#713）跟着这一轮补发：会话就绪才走到这里，所以不夹在握手中间。
        HashSet<string> replayable = RuntimeMessageIds(runtime, stops);
        replayable.UnionWith(await CurrentStopDepartures.PendingResultIdsAsync(dbContext, runtime.AgvId, cancellationToken)
            .ConfigureAwait(false));
        await WithholdSlotCommandsOffTheirStopAsync(replayable, runtime, stops, session, cancellationToken).ConfigureAwait(false);
        await publisher.ReplayPendingForSessionAsync(
            runtime.AgvId,
            session.SessionGeneration,
            replayable,
            cancellationToken).ConfigureAwait(false);
        // 计划整体替换下发（ADR-cross-0053）：途中追加会在序列中间插进新停靠，而车上手里那张计划是插入之前的。
        // 补发在重放之后：重放先把没确认的旧行发完，新的那一版才不会被它顶回去。
        await RefreshUpcomingStopPlanAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);

        // 装货阶段（批次7-07，control-server#212）：每一轮都从事实重判一次，变了才落库、该发才发。放在重放与计划重发之后、
        // 各阶段之前：持货期限、两侧满没满、分区参数都可能在两轮之间变，而它们与车停在哪个阶段无关。各阶段里还有两处
        // 就地再判——装完进离站等待那一刻、为离站请求移动那一刻——因为那两处的事实在本轮里才刚变。
        bool holdingApplicable = await HoldingApplicableAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        // 车载屏上的「出发」（control-server#713）：在装货阶段重判之前答，接受了的那一次放回、关闭装货阶段与答复同一次提交。
        // 待送点缺电而车上有货：按自动出发处理（CP-0013），同一个做法。两种都让旅程变了，重新加载再往下走（或已收尾）。
        if (await AnswerDepartureRequestsAsync(runtime, session, holdingApplicable, now, cancellationToken).ConfigureAwait(false) ||
            await TryDepartLowBatteryStagingStopWithCargoAsync(runtime, stops, session, holdingApplicable, now, cancellationToken)
                .ConfigureAwait(false))
        {
            if (runtime.Stage == JourneyRuntimeStage.Completed)
            {
                return;
            }
            stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
            // 放回改了计划：答复与业务快照之后紧跟新的一版计划（CV-CURRENT-STOP-DEPARTURE-ACCEPTED 的顺序）。
            await RefreshUpcomingStopPlanAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
        }
        if (runtime.Stage is not (JourneyRuntimeStage.Blocked or JourneyRuntimeStage.Completed))
        {
            await ReconcileLoadingPhaseAsync(
                runtime, stops, session, holdingApplicable, departingFrom: null, now, cancellationToken)
                .ConfigureAwait(false);
            // 持货超时截断（control-server#290）：紧跟判定之后、各阶段之前，这一轮下面读到的停靠与待做项已是截断之后的。
            if (await TruncateAtCargoHoldingDeadlineAsync(runtime, now, cancellationToken).ConfigureAwait(false) is { } truncated)
            {
                if (runtime.Stage == JourneyRuntimeStage.Completed)
                {
                    return;
                }
                // 截在待送点上：按截断之前的游标认，截断可能刚把当前停靠结束掉。
                bool truncatedAtStaging = StandsAtStaging(stops);
                stops = truncated;
                // 待送点上截断改了计划：空清单之后紧跟新的一版计划，再往下才可能发离站核验（向量 CV-STAGING-CARGO-HOLDING-TIMEOUT 的线序，
                // protocol#17 审查转 C11）。待送点到期在这一轮里就发核验（control-server#727），不在这里补，车先收到核验、后收到计划。
                // 只限待送点（#731 审查 M3）：机台取货要等站点期限，计划照旧在下一轮开头发，cs#290 以来的线序一点不动。计划没变时不发（同出发那一处）。
                if (truncatedAtStaging)
                {
                    await RefreshUpcomingStopPlanAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
                }
                // 离站核验已经发出、而它问的下一站刚被截掉（审查 L2）：作废那一张，换一对新身份，下面等核验那一段把新的一张发出去，
                // 问的是截断之后的下一站。做法与扣车那一处相同。
                if (runtime.Stage == JourneyRuntimeStage.AwaitingDepartureSafety)
                {
                    await RetireDepartureCheckForDoorHoldAsync(runtime, stops, now, cancellationToken).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Set when this iteration has already asked a pre-departure check again, so the judgment that
        // follows neither asks a third time nor forgets why the vehicle is still waiting.
        bool reissuedDepartureCheck = false;
        switch (runtime.Stage)
        {
            case JourneyRuntimeStage.AwaitingPickupArrival:
                // 这一站的单终结过、正在按同车同需求重建（control-server#318）：旧单不再读，重建自己走完这一轮。
                // 在那之前：门锁故障仍在效而这张单已在 RIoT 被取消或删除时，故障模型照样每轮监看（control-server#335 审查 P1）。
                await SuperviseDoorFaultOnEndedOrderAsync(runtime, stops.Current, cancellationToken).ConfigureAwait(false);
                if (await AdvanceOwnOrderRebuildAsync(
                        runtime, stops.Current, currentMap, mayCreate: true, reasonOnceRebuilt: null, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return;
                }
                // 与关卡侧对称，取这个停靠自己的单号（批次7-06）：旅程行上的 PickupUpperId 是锚需求那一段的，
                // 第二个取货停靠用它会去确认一段早已走完的移动。
                if (!await EnsureMovementConfirmedAsync(
                        runtime, stops.Current, currentMap, "PICKUP", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                await PublishPickupDispatchPlanOnceAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
                ArrivalCheck pickupArrival = await CheckArrivalAsync(
                    runtime, stops.Current, "TO_PICKUP", session, cancellationToken).ConfigureAwait(false);
                if (!pickupArrival.Trusted)
                {
                    // FAILED goes to the fault model inside NameStalledOrderAsync, first (control-server#358).
                    if (await NameStalledOrderAsync(runtime, pickupArrival.Intent, pickupArrival.Order, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await PublishPickupStateAsync(runtime, stops, session, holdingApplicable, pickupArrival.Vehicle!, cancellationToken)
                    .ConfigureAwait(false);
                // REQ-0204（批次8-20，control-server#391）：到站可信了，这趟旅程在这个公共站点上的预占转为占用，与阶段前移同一次保存。
                await FixedStationExclusivity.StageOccupyOnArrivalAsync(
                        dbContext, runtime.MapId, stops.Current.StationRiotId, runtime.JourneyId, now, cancellationToken)
                    .ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now);
                break;
            case JourneyRuntimeStage.AwaitingSublot:
                // Refills the station departure wait a disconnect voided (ADR-cross-0055). A reconnect
                // does not re-enter this stage, so the arrival's seed cannot be what refills it; and
                // this line runs behind the readiness gate, which is "after the recovery handshake and
                // the projection reconciliation".
                bool waitRefilled = runtime.StationDepartureWaitStartedAt is null;
                runtime.StationDepartureWaitStartedAt ??= now;
                ProtocolInboxRow? sublot = await FindMatchingSublotAsync(runtime, stops, session, cancellationToken)
                    .ConfigureAwait(false);
                // An operator cancelling before any entry (ADR-cross-0046; control-server#83) holds the stop
                // until the vehicle reports: no load starts and the deadline does not end it. This check only
                // saves an iteration: it reads outside any lock, and the cancellation's result is processed
                // inbound, where it can settle -- close -- between the inbox read above and this one. What keeps
                // a load from being commanded for a demand that has lost to a cancellation is the re-check under
                // the write lock just before the command (EnteredDemandStillLoadableAsync, control-server#362);
                // the order of these two reads guarantees nothing. It used to be read as the guarantee.
                //
                // 按「这个停靠上任一条需求」问（批次7-06）：取消要车证明那一排仓位是空的，而那是一次整排的证明，
                // 不是对某一条需求的证明——所以一条需求的取消开着，这个停靠上的每一条都等着。
                if (await OpenCancellationAtCurrentStopAsync(stops, cancellationToken).ConfigureAwait(false))
                {
                    // What the entry read concluded does not stand while the cancellation decides the stop, so this
                    // iteration's unsaved change to the block is undone -- code and start time both, back to what the
                    // row holds in the store. This is only for undoing an unsaved change: every new block is written
                    // through SetBlockReason, and writing the old code back through it here would restart a block
                    // that never ended.
                    EntityEntry<JourneyRuntimeRow> tracked = dbContext.Entry(runtime);
                    tracked.Property(row => row.BlockReasonCode).CurrentValue =
                        tracked.Property(row => row.BlockReasonCode).OriginalValue;
                    tracked.Property(row => row.BlockReasonSince).CurrentValue =
                        tracked.Property(row => row.BlockReasonSince).OriginalValue;
                    if (waitRefilled)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                if (sublot is null)
                {
                    // 本站的待做项已经没了，旅程却还在（车上有货）：扫码前取消结束了本站最后一条待录入的（control-server#668）。
                    // 取消那一路（PickupStopTermination → StopEndWorklist）只终结需求、发空清单，不改阶段；留在这里，唯一的出口是下面的
                    // 到期结束，它按原期限判、期限为空时永不结束——等待配为 0 时车载着货永久停在站上，非零时同站追加的提前结束与纠错窗口
                    // 都不生效。所以这里接上离站等待那一段，与装货途中被终结之后的续跑（AwaitingLoadResult 的 resumingAfterEnding）
                    // 同一个规则：本站装上过货，就从此刻起算，给已装的那几条留出纠错时间（服务端只在离站等待里授权纠错）；一条都没装上，
                    // 起点不动。纠错闸、同站追加的提前结束、持货等单、仓门扣车、车上那一版期限，都由离站等待那一段照常处理。
                    if (stops.OutstandingAtCurrentStop.Count == 0)
                    {
                        bool loadedAtThisStop = stops.CurrentStopDemands
                            .Any(item => item.Membership.Status == JourneyDemandStatuses.Loaded);
                        runtime.StationDepartureWaitStartedAt = loadedAtThisStop
                            ? now
                            : runtime.StationDepartureWaitStartedAt ?? now;
                        SetStage(runtime, JourneyRuntimeStage.AwaitingStationDeparture, now);
                        LogEmptiedStopEntersDepartureWait(
                            logger, runtime.AgvId, stops.Current.StationId, loadedAtThisStop,
                            EffectiveStationDepartureDeadline(runtime, stops), null);
                        goto case JourneyRuntimeStage.AwaitingStationDeparture;
                    }
                    // 待送点缺电（control-server#709，用户定「缺电直接去充电」）：一篮没装就放回需求、旅程收尾，车交给充电分配。
                    if (IsStagingPickup(stops.Current) &&
                        await TryReleaseLowBatteryStagingStopAsync(runtime, stops, session, now, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    if (await TryEndStopAtStationDeadlineAsync(runtime, stops, session, now, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    // 重填的期限送到车上（control-server#339）：清单升一版，录入请求跟着那一版重发。重发不是因为升版让旧的那张作废——
                    // 同一站、同一作业会话、还有待录入时，车不把更高的号当成本站结束（车载端 EndsStopOf）——而是车在会话离开 Ready 时
                    // 已经清掉了手上的录入请求，不重发，操作员就没有可答的请求。升版之前发出的录入仍在本停靠的地址区间里，照常受理。
                    if (await AdvanceWorklistPastAStaleDeadlineAsync(runtime, stops, cancellationToken)
                            .ConfigureAwait(false) is { } reissued)
                    {
                        // 发布把这个上下文里未保存的改动一起保存：重填的起点、升版的次数、旧一版的退役与新的一版同在或同不在。
                        runtime.UpdatedAt = now;
                        await PublishStopWorklistAsync(
                            runtime,
                            reissued,
                            session,
                            StationDepartureDeadlineAt(runtime, stops.Current),
                            cancellationToken).ConfigureAwait(false);
                        await PublishEntryRequestAsync(runtime, reissued, session, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    // 本站有一条被扫码前取消（或别的终结、放回）而本站还有待做项（control-server#723）：号已随终结前进，车上那一版还列着它、
                    // 录入请求还期待它的子批。清单与录入请求按新号一起发，与装完一条回到等录入同一个做法。
                    if (await WorklistBehindAnEndingAtTheStopAsync(runtime, stops, cancellationToken).ConfigureAwait(false))
                    {
                        runtime.UpdatedAt = now;
                        await PublishStopWorklistAsync(
                            runtime,
                            stops,
                            session,
                            StationDepartureDeadlineAt(runtime, stops.Current),
                            cancellationToken).ConfigureAwait(false);
                        await PublishEntryRequestAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    // 上面两条报文是两次保存，崩在两次之间时清单已对上、录入请求还没有：补发录入请求。
                    if (await EntryRequestMissingBehindTheWorklistAsync(runtime, stops, cancellationToken).ConfigureAwait(false))
                    {
                        runtime.UpdatedAt = now;
                        await PublishEntryRequestAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    // The one quiet exit of this stage. A wait refilled above and not saved here would be
                    // refilled again on every later iteration, which is the same as never running out.
                    if (runtime.BlockReasonCode is not null || waitRefilled)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                // The entry answers the request open at this stop. Whether it may load is judged now,
                // after the cancellation has had its chance to decide the stop: an entry that loses to
                // one is neither loaded nor answered. A refusal is answered here, which is the point of
                // BR-013's "give the operator the real reason", and leaves the stop exactly where it was.
                if (await RevalidateEnteredSublotAsync(runtime, stops, session, sublot, now, cancellationToken)
                        .ConfigureAwait(false) is not { } entered)
                {
                    return;
                }
                // 开门之前核车此刻是不是停在这个停靠上（control-server#783）。读 RIoT 放在写事务之外：事务是 BEGIN IMMEDIATE，
                // 不该拿着写锁等一次 HTTP。读数就在这一轮取、这一轮用，不跨轮沿用。不在站就拒收这次录入，与上面那些拒收同一条路，
                // 停靠不动；车回到站上之后操作员再扫一次。
                if (await VehicleNotAtStopAsync(runtime, stops.Current, SlotOperationType.Load, cancellationToken)
                        .ConfigureAwait(false) is { } notAtStop)
                {
                    runtime.SetBlockReason(SlotCommandVehicleNotAtStopReason, now);
                    await RefuseAsync(
                        runtime,
                        stops,
                        session,
                        sublot,
                        entered.Demand.DemandId,
                        entered.Demand.Sublot,
                        notAtStop,
                        notAtStop == ServerReasonCodes.StationMismatch
                            ? $"车此刻不在 {stops.Current.StationId} 站上，不予开仓。请把车开回这一站、停稳之后重新扫码；"
                              + "车确实在站上而仍被拒收，请联系系统维护人员。"
                            : $"读不出车此刻的位置（离线、读数过期或车在移动），不能确认车停在 {stops.Current.StationId} 站上，"
                              + "不予开仓。请确认车停稳在这一站之后重新扫码；持续出现请联系系统维护人员。",
                        now,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                // 下命令之前在写锁里再判一次（control-server#362）。上面读收件箱、查取消、按游标认需求都是锁外读，
                // 取消的结果走入站、不拿 JourneyMutationGate，可以落在这几次读之间：车报 ALL_EMPTY，需求已 Cancelled、取消已
                // Reconciled、归属已 TERMINATED，而这一轮手里的游标还说它待装。照旧读下命令，车会给一条已取消的需求开仓，装上的货
                // 需求记着取消、业务键已抑制，关卡也不给它下卸货命令——货留在车上。所以判断挪到命令落库的同一个写事务里：这个库的
                // BeginTransaction 是 BEGIN IMMEDIATE，下面读到的是此前提交的一切，入站那次写要么在前（这里读到、让开），要么在后
                // （它读到命令已下，照已下命令处理）。
                //
                // 输的一方这一轮不装也不答，下一轮按新读重判：本站已被那次终结结束时，它已一并答了这条录入 STALE
                // （control-server#324），下一轮按「已拒收」跳过；本站还有别的待做项时，下一轮按新游标判它不在派车范围。
                // 两条都是没有竞态时这条录入本来会得到的答复。同站另一条需求的扫码前取消在这期间开了的，下一轮由上面锁外那次
                // 查取消扣住本站，直到车报结果。
                //
                // 这一支自己开写事务，而且必须是它自己的：命令在提交之后才上线，有外层事务就意味着上线早于外层提交，车可能照一条
                // 最后没落库的命令开仓。今天没有这样的调用者，所以直接拒绝，而不是悄悄加入。
                if (dbContext.Database.CurrentTransaction is not null)
                {
                    throw new InvalidOperationException(
                        "A load is commanded only from a write transaction of its own: the command goes out after that " +
                        "transaction commits, which an outer transaction would postpone past the send.");
                }
                await using (IDbContextTransaction transaction = await dbContext.Database
                                 .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await EnteredDemandStillLoadableAsync(runtime, stops, entered, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        // 同上面取消开着的那一支：这一轮对阻塞码的改动不成立，退回库里的值；重填的离站等待照存。
                        EntityEntry<JourneyRuntimeRow> tracked = dbContext.Entry(runtime);
                        tracked.Property(row => row.BlockReasonCode).CurrentValue =
                            tracked.Property(row => row.BlockReasonCode).OriginalValue;
                        tracked.Property(row => row.BlockReasonSince).CurrentValue =
                            tracked.Property(row => row.BlockReasonSince).OriginalValue;
                        if (waitRefilled)
                        {
                            runtime.UpdatedAt = now;
                            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        }
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        LogLoadYieldedToEndedDemand(logger, runtime.AgvId, sublot.MessageId, entered.Demand.DemandId, null);
                        return;
                    }
                    // 具体仓位在这里分（REQ-0352，control-server#754）：受理只预留了数量。分配与装货命令、LOADING 同一个写事务，
                    // 所以要么三者都在、要么都不在——崩在中间，下一轮重新分，不会多出第二组。已经分过的读回原值、不重分：
                    // 重放（PrepareSlotOperationAsync）与恢复都按第一次写下的仓位判冲突。
                    if (await AssignTargetSlotsAsync(runtime, entered, cancellationToken).ConfigureAwait(false)
                        is not { } assigned)
                    {
                        // 这一侧能分的空仓不够：本事务什么都不写（重填的离站等待照存），提交之后在事务外拒收这次录入，与别的拒收
                        // 同一条路（RefuseAsync 自己保存）。
                        if (waitRefilled)
                        {
                            runtime.UpdatedAt = now;
                            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        }
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        runtime.SetBlockReason(LoadSlotAssignmentShortReason, now);
                        await RefuseAsync(
                            runtime,
                            stops,
                            session,
                            sublot,
                            entered.Demand.DemandId,
                            entered.Demand.Sublot,
                            ServerReasonCodes.SlotInoperable,
                            $"子批 {entered.Demand.Sublot} 所需的这一侧可分配的空仓不足 {entered.Membership.ExpectedBasketCount} 个，"
                            + "或读不到这一侧的仓位信息，不予开仓。请取消这条需求并联系系统维护人员。",
                            now,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    entered = entered with { Membership = assigned };
                    await StageLoadAsync(runtime, stops, entered, session, sublot.MessageId, cancellationToken)
                        .ConfigureAwait(false);
                    runtime.ConsumedSublotMessageId = sublot.MessageId;
                    // 「这个停靠此刻在装哪一条」从这里起是落库的状态（批次7-06）。与命令同一个事务：
                    // 命令与状态要么都在，要么都不在，否则重启之后会对着一条没有命令的需求等结果。
                    (await TrackedMembershipAsync(runtime, entered.Demand.DemandId, cancellationToken)
                        .ConfigureAwait(false)).Status = JourneyDemandStatuses.Loading;
                    // The submission is this command's answer. Leaving the command unsettled replayed it
                    // into every later session, where the peer refused it as a business id whose content
                    // had changed and tore the session down.
                    await store.SettleAnsweredCommandAsync(
                        stops.CurrentSublotRequestMessageId(runtime.WorklistRevision), now, cancellationToken).ConfigureAwait(false);
                    SetStage(runtime, JourneyRuntimeStage.AwaitingLoadResult, now);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                // 提交之后才上线：车只照已落库的命令开仓。
                await publisher.SendPersistedAsync(entered.Membership.LoadCommandMessageId, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case JourneyRuntimeStage.AwaitingLoadResult:
                // 等的是「此刻在装的那一条」的 attempt（批次7-06）。批次7-03 查旅程行上锚需求的那一个，
                // 而命令是按被录入那条发出去的——两者分岔的样子就是「发了 A 查 B」，旅程停在这里而且不报错。
                //
                // 没有「正在装的」有两种来历，只有一种是缺陷（批次7-07，control-server#212 查出）。装货落定要保存两次：先把这一条
                // 记成 LOADED、结算命令（下面重新加载游标要读到它），再在离站或下一条的那一次保存里把阶段前移。崩在两次之间，
                // 重启后读到的就是「阶段还是 AwaitingLoadResult、这一站没有 LOADING、刚装的那条已是 LOADED」——此前这里一律抛，
                // 而推进段当时没有逐车隔离，于是每一轮都在这里整轮中止，车队里每辆车都不再推进（cs#487 起逐车隔离，今天只停这一辆）。这一种按落库的状态续上，走与刚落定时
                // 同一段后续；这一站连一条 LOADED 都没有，才是这台服务器自己的不变量被破坏了，照旧抛。
                //
                // 第二种合法来历（control-server#291）：这一站在装的那一条被终结了——确定的装货失败、装货途中取消——而旅程还带着别的需求，
                // 不收尾。终结它的那条路若没有把阶段带走，留下的就是「没有在装、没有装上、只有一条 TERMINATED」。确定的装货失败自己会带走
                // （TrySettleDeterminateLoadFailureAsync），恢复协调器的取消结果不会；这里兜住它们，也兜住已经卡在这个状态的库。
                // 接的是同一段后续：本站还有待装的就回去等录入，没有就离站。
                // 异常处置会话里的装货补偿、故障货物交接与强制机械取出终结的也是这一条（control-server#499）：恢复协调器在终结的同一次
                // 保存里把旅程从 Blocked 放回这个阶段（BlockedJourneyRelease），走的就是这里——本站已有 LOADED 的走上面 #289 那一种。
                JourneyStopDemand? loading = stops.LoadingAtCurrentStop;
                bool resumingAfterCommit = loading is null;
                bool loadedHere = stops.CurrentStopDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Loaded);
                // 待送点上装货中取消的那一条不终结，按释放改派放回（control-server#713）：归属被移除、游标看不见它，按库里那一行认。
                bool releasedWhileLoadingHere = resumingAfterCommit && !loadedHere && IsStagingPickup(stops.Current) &&
                    await dbContext.Set<JourneyDemandRow>().AsNoTracking().AnyAsync(
                        row => row.JourneyId == runtime.JourneyId && row.PickupStopId == stops.Current.StopId &&
                               row.RemovedAt != null && row.Status == JourneyDemandStatuses.Loading,
                        cancellationToken).ConfigureAwait(false);
                bool resumingAfterEnding = resumingAfterCommit && !loadedHere &&
                    (stops.AllAtStop(stops.Current).Any(item => item.Membership.Status == JourneyDemandStatuses.Terminated) ||
                     releasedWhileLoadingHere);
                if (resumingAfterCommit && !loadedHere && !resumingAfterEnding)
                {
                    throw new InvalidDataException(
                        $"Journey {runtime.JourneyId} waits for a load result with no demand loading at its stop.");
                }
                StationOperationRow? load = loading is null
                    ? null
                    : await dbContext.StationOperations.SingleOrDefaultAsync(
                        row => row.SlotOperationAttemptId == loading.Membership.LoadSlotOperationAttemptId,
                        cancellationToken).ConfigureAwait(false);
                if (load?.Status == StationOperationStatus.RecoveryRequired)
                {
                    Block(runtime, "LOAD_RESULT_REQUIRES_RECOVERY", now);
                }
                else if (load?.Status == StationOperationStatus.Failed)
                {
                    await TrySettleDeterminateLoadFailureAsync(
                            runtime, stops, loading!, load, session, now, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                else if (resumingAfterCommit || load?.Status == StationOperationStatus.Committed)
                {
                    if (!resumingAfterCommit)
                    {
                        await store.SettleAnsweredCommandAsync(
                            loading!.Membership.LoadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                        (await TrackedMembershipAsync(runtime, loading.Demand.DemandId, cancellationToken)
                            .ConfigureAwait(false)).Status = JourneyDemandStatuses.Loaded;
                        // 持货期限的起算点：第一个 LoadBatch 安全闭环（ADR-cross-0057，批次7-07）。写在同一次保存里，只写一次——
                        // 新停靠、断联、重启都不碰它（与站点离站等待那一列相反，那一列断联就作废）。取服务端此刻的时钟，
                        // 而不是结果里车报的 observedAt：期限拿服务端的钟去比，起算点混进车上的钟，两边一偏（clock-skew 场景）
                        // 期限就提前或推后同样的量。代价是晚于真实闭环至多一轮轮询。
                        runtime.CargoHoldingStartedAt ??= now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    // 持货期限在这一批执行期间到了（control-server#290）：闭环的这一轮就结束装货阶段并截断，ADR-cross-0057「等该批
                    // 安全闭环后立即结束装货阶段，不再接受新的待装需求」。等到下一轮的轮首再判，这里会先给车发一版只剩未开始那几条的清单
                    // 与录入请求，操作员在几秒里看到一条马上就被取消的需求。轮首那次判定读到的阶段还是「在装」，所以在这里按「这一批已闭环」再判。
                    if (HoldingAppliesAt(holdingApplicable, stops) && HoldingDeadlinePassed(runtime, stops, now))
                    {
                        await ReconcileLoadingPhaseAsync(
                            runtime, stops, session, holdingApplicable, departingFrom: null, now, cancellationToken,
                            loadBatchJustClosed: true).ConfigureAwait(false);
                        if (await TruncateAtCargoHoldingDeadlineAsync(runtime, now, cancellationToken).ConfigureAwait(false) is { } truncatedHere)
                        {
                            if (runtime.Stage == JourneyRuntimeStage.Completed)
                            {
                                return;
                            }
                            // 同轮首那一处：待送点上截断改了计划，先发计划，下面才可能发离站核验（control-server#727）；机台照旧（#731 审查 M3）。
                            if (IsStagingPickup(stops.Current))
                            {
                                await RefreshUpcomingStopPlanAsync(runtime, truncatedHere, session, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    // 这个停靠上还有没装的需求，就回到等录入等下一条（同一站逐条串行，规格第 22 节补记）：重新加载
                    // 游标，因为刚写下的状态决定了清单该列谁、修订号该走到几。
                    stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
                        .ConfigureAwait(false);
                    if (stops.OutstandingAtCurrentStop.Count > 0)
                    {
                        // 这一支刚落定、#289 的续跑（resumingAfterCommit）与本站在装那一条被终结之后的续跑（resumingAfterEnding）共用。
                        //
                        // 一个 LoadBatch 安全闭环，离站等待就从完整时长重新计时（ADR-cross-0055，control-server#643），与最后一批落定
                        // 那一处（下面转入离站等待之前）同一个口径、同一个判别。这之前这里只在续跑时补填空起点，正常落定不碰它，
                        // 于是同站后面每一条可录的时间都比前一条少，闭环之后几十秒本站就被期限结束、还没录的需求被终结。
                        // #289 的续跑同样重置：那一批确实闭环了，只是崩在阶段前移之前；起点取此刻而不是闭环那一轮，代价是期限推后
                        // 宕机的时长，与 control-server#633 取服务端此刻同一个取舍。
                        //
                        // resumingAfterEnding 一条都没装上，不是 LoadBatch 闭环，不开始新的计时；断联作废过的才按「此刻」补填，
                        // 与等录入那一处同一个规则。
                        //
                        // 重置写在发清单之前：下面那一版清单本来就因待做项变了而升号，带的就是新期限，车上与服务端判定用的是同一个值。
                        // 续跑时那一版上一轮已经写盘、带的是上一轮的起点，升号来自紧接着的 AdvanceWorklistPastAStaleDeadlineAsync
                        // （期限与排给车的那一版不同就升一版），所以那一次调用在每次崩溃续跑里都是承重的。
                        runtime.StationDepartureWaitStartedAt = resumingAfterEnding
                            ? runtime.StationDepartureWaitStartedAt ?? now
                            : now;
                        // 续跑重发的是上一轮已经写盘的那一版（control-server#291 独立审查）。车确认过、又换了一代时，候选报文只有信封不同，
                        // 不带 keepAcknowledgedIgnoring 就被重放校验拒；断联作废过离站等待时期限也变了，那就升一版（control-server#339），
                        // 与到站重跑（PublishPickupStateAsync）同一个做法。第一次发这一版时发件箱里还没有它，两样都不起作用。
                        stops = await AdvanceWorklistPastAStaleDeadlineAsync(runtime, stops, cancellationToken).ConfigureAwait(false)
                                ?? stops;
                        await PublishStopWorklistAsync(
                            runtime,
                            stops,
                            session,
                            StationDepartureDeadlineAt(runtime, stops.Current),
                            cancellationToken,
                            keepAcknowledgedIgnoring: NothingButTheEnvelope).ConfigureAwait(false);
                        await PublishEntryRequestAsync(
                                runtime, stops, session, cancellationToken, keepAcknowledgedIgnoring: NothingButTheEnvelope)
                            .ConfigureAwait(false);
                        SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now);
                        break;
                    }
                    // REQ-0237 / ADR-cross-0054: the load is committed and the vehicle is still at the
                    // pickup, which is the only time an ordinary mis-placement may be corrected. Asking
                    // for departure safety in this same iteration, as this server did until 2026-09-13,
                    // left no such time at all. Not saved here: with the wait off the next case departs
                    // at once and saves once, as before.
                    // 本站一条都没装上就结束的（resumingAfterEnding）没有可纠正的放置，离站等待的起点不动——与站点期限结束停靠一致。
                    runtime.StationDepartureWaitStartedAt = resumingAfterEnding
                        ? runtime.StationDepartureWaitStartedAt ?? now
                        : now;
                    SetStage(runtime, JourneyRuntimeStage.AwaitingStationDeparture, now);
                    goto case JourneyRuntimeStage.AwaitingStationDeparture;
                }
                else
                {
                    // No result yet. Refills the station departure wait a disconnect voided (ADR-cross-0055),
                    // the way AwaitingSublot does: this stage is not re-entered on a reconnect either, and
                    // without it a stop loading across a reconnect would have no deadline to raise its
                    // alarm by.
                    bool loadWaitRefilled = runtime.StationDepartureWaitStartedAt is null;
                    runtime.StationDepartureWaitStartedAt ??= now;
                    DateTimeOffset? doorAlarmAt = await DoorAlarmDueAtAsync(runtime, stops, loading!, cancellationToken)
                        .ConfigureAwait(false);
                    bool loadWaitChanged =
                        ReconcileStationTimeoutDoorNotClosed(runtime, stops.Current.StationId, session, doorAlarmAt, now) ||
                        loadWaitRefilled;
                    // 重填的期限同样送到车上（control-server#339），与等录入那一处同一个判据。录入请求不重发：这一站此刻在装，
                    // 没有开着的录入；下一条的录入请求在这一批落定之后随下一版清单发出，号接着这一版往上走。
                    if (await AdvanceWorklistPastAStaleDeadlineAsync(runtime, stops, cancellationToken)
                            .ConfigureAwait(false) is { } reissued)
                    {
                        runtime.UpdatedAt = now;
                        await PublishStopWorklistAsync(
                            runtime,
                            reissued,
                            session,
                            StationDepartureDeadlineAt(runtime, stops.Current),
                            cancellationToken).ConfigureAwait(false);
                    }
                    // 同站另一条在这期间被扫码前取消（control-server#723）：车上那一版还列着它。只发清单，理由同上。
                    else if (await WorklistBehindAnEndingAtTheStopAsync(runtime, stops, cancellationToken).ConfigureAwait(false))
                    {
                        runtime.UpdatedAt = now;
                        await PublishStopWorklistAsync(
                            runtime,
                            stops,
                            session,
                            StationDepartureDeadlineAt(runtime, stops.Current),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (loadWaitChanged)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                break;
            case JourneyRuntimeStage.AwaitingStationDeparture:
                // 装完这一站的那一刻就判装货阶段（批次7-07）：从装货落定掉进这里的是同一轮，轮首那次判定读到的还是
                // 「有一条在装」。离站等待关掉时（期限为零）同一轮就要离站，不在这里判，车会带着 LOADING 离开最后一个装货停靠。
                await ReconcileLoadingPhaseAsync(
                    runtime, stops, session, holdingApplicable, departingFrom: null, now, cancellationToken)
                    .ConfigureAwait(false);
                bool stationWaitOver =
                    await StationDepartureWaitIsOverAsync(runtime, stops, now, cancellationToken).ConfigureAwait(false);
                // 期限此刻定了，再对一次车上的那一个（control-server#633）：最后一批落定重置了起点、纠错闭环把起点往后挪，
                // 两处都不经过任何发清单的路。放在判定之后，是因为纠错那一挪就在上面这次判定里。
                await RefreshDeadlineOfALoadedStopAsync(runtime, stops, session, now, cancellationToken).ConfigureAwait(false);
                if (!stationWaitOver)
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                // 持货等单（REQ-0354）：站点离站等待到期只结束本站作业，不等于车辆离开——车停在最后装货的站点，
                // 不发离站请求，直到装满、持货超时或让站。每一轮回到这里，由轮首那次判定决定还等不等。
                // 操作员已在本站按过出发（control-server#725）不在此列：受理时是 VEHICLE_FULL、被扣车或纠错挡住期间派车轮判出不再满，装货阶段按事实
                // 回到 CARGO_HOLDING_WAIT，这时再扣车，车就停到持货期限，而再按出发只会被答「已在出发」（#732 独立审查 M1）。
                if (runtime.LoadingPhaseState == LoadingPhaseStates.CargoHoldingWait && stops.Current.OperatorDepartedAt is null)
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                // 门未证明扣车（REQ-0364，control-server#385 审查 N2 (c)）：扣车没被维修放行解除之前，这趟旅程不离站——不发离站检查、
                // 不建下一段的单。不能只靠车在门没锁时回 UNSAFE：锁的传感器时好时坏，读到一次锁闭车就会回 SAFE。每一轮回到这里再判。
                if (await store.SlotDoorHeldAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false))
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                runtime.StationDepartureWaitStartedAt = null;
                // 卸货停靠的这两个 id 受理时没有写过——在本票之前它永远是旅程的终点，没有「离开之前」可言。
                // 多停靠计划里它后面还能有停靠，所以第一次要离站时补上并落库。
                await EnsureDepartureCheckIdsAsync(stops.Current, cancellationToken).ConfigureAwait(false);
                // 核验的两个 id 取当前停靠。命令里的 demandId 取「第一条受理的需求」（锚需求），即使它已经在更早的
                // 停靠卸完了：协议只放得下一个，而离站安全本来就是一次整车判断，不是对某一条需求的判断
                // （票面第 10 条）。腿与目标站取「即将出发的那一段」，也就是下一个停靠。
                await publisher.PublishPreDepartureSafetyCheckAsync(
                    DepartureCheckMessageId(stops.Current),
                    runtime.AgvId,
                    session.SessionGeneration,
                    new PreDepartureSafetyCheckCommand(
                        DepartureCheckId(stops.Current),
                        PreDepartureCheckPurposes.Departure,
                        runtime.TransportColumn(runtime.DemandId),
                        NextStopAfterCurrent(stops).MovementLegId,
                        session.SafetyRevision ?? throw new InvalidDataException("Safety revision is required."),
                        NextStopAfterCurrent(stops).StationId),
                    cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingDepartureSafety, now);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                // The answer arrives in tens of milliseconds and is stamped with the peer's own
                // short validity window. Returning here and reading it on the next poll spent
                // that entire window waiting, so the evidence was always expired by the time it
                // was judged and the journey never left this stage. Judge it while it is valid.
                goto case JourneyRuntimeStage.AwaitingDepartureSafety;
            case JourneyRuntimeStage.AwaitingDepartureSafety:
                // 离站检查已经发出之后才扣的车，同样停在这里（control-server#385 审查 N2 (c)）：不判应答、不重发过期的检查、不建单。
                // 扣车之前发出的那张检查同时作废、换一对新身份（审查 N2 追加）：车在扣车期间对它答的 SAFE 说明不了放行之后的车，
                // 即使放行来得快、那条应答还没过期也不能拿来离站。放行之后先发那张新检查，只有它的应答能让车走。
                if (await store.SlotDoorHeldAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false))
                {
                    await RetireDepartureCheckForDoorHoldAsync(runtime, stops, now, cancellationToken).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (await PublishDepartureCheckNotYetSentAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false))
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                (SafetyCheckObservation? safety, string? safetyResultMessageId) = await AwaitSafeDepartureResultAsync(
                    runtime, stops, session, cancellationToken).ConfigureAwait(false);
                now = timeProvider.GetUtcNow();
                if (safety is null)
                {
                    if (!reissuedDepartureCheck &&
                        await ReissueExpiredDepartureCheckAsync(runtime, stops, session, now, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        reissuedDepartureCheck = true;
                        // The new check is answered within its peer's short window, so it is judged
                        // now, the way the first check is judged in the iteration that asks it.
                        goto case JourneyRuntimeStage.AwaitingDepartureSafety;
                    }
                    if (reissuedDepartureCheck && runtime.BlockReasonCode is null)
                    {
                        runtime.SetBlockReason("PREDEPARTURE_CHECK_EXPIRED", now);
                    }
                    if (runtime.BlockReasonCode is not null)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                // REQ-0305: every RIoT move order that does not exist yet is gated again, against
                // the frozen endpoint and the catalog as it stands now. The gate leg is a second
                // order created minutes after the demand was taken, and nothing about the first
                // order's gate says the vehicle can still reach the gate station from where it now
                // is.
                JourneyStopRow departedFrom = stops.Current;
                JourneyStopRow nextStop = NextStopAfterCurrent(stops);
                CreateGateOutcome gateLeg = await GateLegAsync(runtime, nextStop, currentMap, cancellationToken)
                    .ConfigureAwait(false);
                if (!gateLeg.IsAllowed)
                {
                    runtime.SetBlockReason(gateLeg.BlockReason, timeProvider.GetUtcNow());
                    runtime.UpdatedAt = timeProvider.GetUtcNow();
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                // 这一段腿已经存过意图就沿用它的时刻（control-server#357 独立审查必修 1）：意图存下之后推进可能在任何一步停下——
                // 让开、重启——下一轮重放到这里，意图若按新的 now 生成，就与已存的那一份对不上，AuthorizeMovementAsync 每一轮都抛，
                // 这台车之后的车全都不再推进。
                DateTimeOffset? legIntentCreatedAt = await dbContext.OrderIntents.AsNoTracking()
                    .Where(row => row.UpperId == nextStop.UpperId)
                    .Select(row => (DateTimeOffset?)row.CreatedAt)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                OrderIntent gateIntent = JourneyPlanBuilder.LegIntent(runtime, nextStop, legIntentCreatedAt ?? now);
                await new WireToGateStore(dbContext).AuthorizeMovementAsync(
                    gateIntent, safety, now, cancellationToken, runtimeOptions.PeerClockSkewTolerance).ConfigureAwait(false);
                // 暂存的一切在守护下落库。守护不在这里撤（增量复核必修 M1）：这之后建单之前还有一次按单号的 RIoT GET 与对账审计的保存，
                // 那几次保存不碰旅程行、令牌核不到，入站在那段窗口里写的 Blocked 只有守护挡得住。撤守护的地方是派车服务：对账审计之后、
                // 用掉「最多建一次」的计数之前（IMovementIntentStore.ReleaseJourneyGuardBeforeExternalEffect）。之后记录建单的保存不再因守护丢掉；
                // 旅程行自己之后的改动（阶段前移）照旧吃令牌，冲突了下一轮重放，重放沿用已存意图、只对账，不建第二张单。
                // 意图已确认时派车服务直接返回、不放开守护——那时没有外部副作用要发生。
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                MovementDispatchResult dispatch = await movementDispatch.ReconcileOrCreateAsync(
                    nextStop.UpperId, cancellationToken).ConfigureAwait(false);
                // The answer that departed the vehicle, not the first one under the check id (control-server#826).
                runtime.ConsumedSafetyResultMessageId = safetyResultMessageId;
                await store.SettleAnsweredCommandAsync(
                    DepartureCheckMessageId(departedFrom), now, cancellationToken).ConfigureAwait(false);
                // 车开走了，这个停靠就完成了：当前停靠指针从此是落库的状态（批次7-06），下一轮加载游标读到的是下一个。
                (await TrackedStopAsync(departedFrom.StopId, cancellationToken).ConfigureAwait(false)).Status =
                    JourneyStopStatuses.Completed;
                // 它（以及这辆车更早已结束的停靠）遗留的录入请求与拒收退役，同这次保存落库（control-server#740）。
                await StopScopedOutbox.FenceEndedAsync(dbContext, runtime.AgvId, now, cancellationToken).ConfigureAwait(false);
                // 让站（批次7-08，control-server#213）：车开走了，它的下一停靠从此是 nextStop。停在那个站持货等单的别的车，
                // 与这个停靠完成落在同一次保存里被标记；装或卸都算（规格第 5.1 节第 8 条）。
                await StationYield.StageTriggerAsync(
                        dbContext, runtime.VehicleKey, nextStop.StationRiotId, now, cancellationToken)
                    .ConfigureAwait(false);
                SetStage(
                    runtime,
                    nextStop.StopRole == JourneyStopRoles.Pickup
                        ? JourneyRuntimeStage.AwaitingPickupArrival
                        : JourneyRuntimeStage.AwaitingGateArrival,
                    now);
                runtime.SetBlockReason(
                    dispatch.Outcome == MovementDispatchOutcome.Confirmed ? null : dispatch.Outcome.ToString(),
                    now);
                // 「离开」就是这一刻：服务端刚为离站向 RIoT 请求了移动（REQ-0354）。离开的是计划里最后一个装货停靠时，
                // 装货阶段在这里结束，与停靠完成、阶段前移落在同一次保存里——之后的追加由在途链与追加事务各拒一次。
                // 离开待送点去别的停靠，持货起算点清空（control-server#710 审查 M1）：后面机台取货停靠的持货期限从它自己第一批装货闭环算起。
                ClearHoldingStartLeavingStaging(runtime, departedFrom, nextStop);
                await ReconcileLoadingPhaseAsync(
                    runtime, stops, session, holdingApplicable, departingFrom: departedFrom, now, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case JourneyRuntimeStage.AwaitingGateArrival:
                // control-server#228: judged first, every round, before anything below can return. The arrival check
                // below returns early whenever it cannot trust the arrival -- an onboard that stopped heartbeating on a
                // session still Ready does that every round -- and ObserveOrderFailureAsync may write another code; neither
                // is the admission returning, so neither stops or restarts this count. Escalating returns at once, so the
                // order failure check never runs in the same round and cannot write its code over the escalation, and a
                // Blocked journey is not observed for arrival again.
                if (await EscalateAreaEndAdmissionRevokedPastTimeoutAsync(runtime, stops, now, cancellationToken)
                        .ConfigureAwait(false))
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                await SuperviseDoorFaultOnEndedOrderAsync(runtime, stops.Current, cancellationToken).ConfigureAwait(false);
                if (await AdvanceOwnOrderRebuildAsync(
                        runtime, stops.Current, currentMap, mayCreate: true, reasonOnceRebuilt: null, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return;
                }
                if (!await EnsureMovementConfirmedAsync(
                        runtime, stops.Current, currentMap, "GATE", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                ArrivalCheck gateArrival = await CheckArrivalAsync(
                    runtime, stops.Current, "TO_GATE", session, cancellationToken).ConfigureAwait(false);
                if (!gateArrival.Trusted)
                {
                    // FAILED goes to the fault model inside NameStalledOrderAsync, first (control-server#358).
                    if (await NameStalledOrderAsync(runtime, gateArrival.Intent, gateArrival.Order, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
                    return;
                }
                // REQ-0204（批次8-20，control-server#391）：到站可信了就转占用，不等卸货准入——车已经站在那里。暂存，随这次推进的保存落库，
                // 下面准入挂住那条路径的保存也带上它。
                await FixedStationExclusivity.StageOccupyOnArrivalAsync(
                        dbContext, runtime.MapId, stops.Current.StationRiotId, runtime.JourneyId, now, cancellationToken)
                    .ConfigureAwait(false);
                if (!await UnloadAdmittedAsync(runtime, stops, cancellationToken).ConfigureAwait(false))
                {
                    // The first hold stores when the wait began, in this same save as the hold itself, so a crash leaves
                    // both or neither. The block is written from that start too: back from another code -- a failed
                    // order -- the hold names the whole wait, not the part since the last code change.
                    DateTimeOffset revokedSince = runtime.HoldForAreaEndAdmission(now);
                    runtime.SetBlockReason(AreaEndAdmissionHeldReason, revokedSince);
                    runtime.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                await PublishGateStateAndUnloadAsync(runtime, stops, session, holdingApplicable, gateArrival.Vehicle!, cancellationToken)
                    .ConfigureAwait(false);
                // Admitted again: the one place the wait's start is cleared.
                runtime.ReleaseAreaEndAdmissionHold();
                SetStage(runtime, JourneyRuntimeStage.AwaitingUnloadResult, now);
                break;
            case JourneyRuntimeStage.AwaitingUnloadResult:
                // 等的是本停靠此刻该卸的那一条（批次7-06）。卸货不需要一个「正在卸」的状态：卸是服务端自己按顺序发的
                // （先前侧后后侧，control-server#303），一条卸完才发下一条，已经发出去的那一条由游标优先认出来
                // （JourneyStopCursor.NextToUnloadAtCurrentStopAsync）。
                //
                // 卸完一条要保存不止一次（control-server#291）：先把这一条记成 UNLOADED 单独保存（下面重新加载游标要读到它），再在下一条
                // 卸货命令、离站或旅程收尾那一次保存里推进；旅程收尾之前还有一次停靠完成的保存。崩在任意两次之间，重启后阶段都还是这一个，
                // 而此前这里只认「还有一条已下命令、在等结果」这一种——下一条命令没落库的静默停住（U1、U5），本站卸空了的每轮抛（U2、U3）。
                // 与装货侧（control-server#212）同一个修法：按落库的状态认出是哪一种，接着走刚落定时同一段后续。三种都要求这一站至少
                // 有一条已经卸完（control-server#499 起，或在卸货命令之后被交接终结）；连一条都没有，才是这台服务器自己的不变量被破坏了，照旧抛。
                if (stops.OpenStops.Count == 0)
                {
                    // 最后一个停靠已存为完成、旅程收尾那一次保存没落（U3 后半）：收尾。停靠完成只在卸完之后写，所以这里不再判别的。
                    if (!stops.AllDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Unloaded))
                    {
                        throw new InvalidDataException(
                            $"Journey {runtime.JourneyId} waits for an unload result with every stop completed and nothing unloaded.");
                    }
                    await JourneyClosure.StageAsync(dbContext, runtime, reasonCode: null, now, cancellationToken)
                        .ConfigureAwait(false);
                    checkpointWaits.Clear(runtime.VehicleKey);
                    break;
                }
                JourneyStopDemand? unloading = (await stops.NextToUnloadAtCurrentStopAsync(dbContext, cancellationToken)
                    .ConfigureAwait(false)).Next;
                // 「本站已有进展」还有第二种（control-server#499）：本站一条在下了卸货命令之后被终结——卸货结果要恢复、旅程阻塞，异常处置
                // 会话把它交接掉（故障货物交接或强制机械取出），而旅程还带着别的需求。恢复协调器把旅程放回这个阶段
                // （BlockedJourneyRelease），这里接着走与卸完一条时同一段：本站还有没卸的发下一条命令，没有就离站或收尾。
                // 认的是「卸货操作已经落库」而不只是 TERMINATED：在取货停靠就被终结、从没装上的需求，归属行同样挂在这个卸货停靠上，
                // 它不算这一站的进展。与装货侧 control-server#291 的 A 同一个意思，只是卸货停靠上的 TERMINATED 多一种来历。只在
                // 本站还没有 UNLOADED、又有 TERMINATED 时才查库，正常卸货的每一轮不多读。
                bool unloadedHereBefore = stops.AllAtStop(stops.Current)
                    .Any(item => item.Membership.Status == JourneyDemandStatuses.Unloaded) ||
                    await EndedHereAfterItsUnloadWasCommandedAsync(stops, cancellationToken).ConfigureAwait(false);
                if (unloading is null && !unloadedHereBefore)
                {
                    throw new InvalidDataException(
                        $"Journey {runtime.JourneyId} waits for an unload result with nothing left to unload.");
                }
                if (unloading is not null)
                {
                    StationOperationRow? unload = await dbContext.StationOperations.SingleOrDefaultAsync(
                        row => row.SlotOperationAttemptId == unloading.Membership.UnloadSlotOperationAttemptId,
                        cancellationToken).ConfigureAwait(false);
                    if (unload is null && unloadedHereBefore)
                    {
                        // 开门之前核位置（control-server#783）：补偿或交接把旅程放回这里、重启后续跑，都走这一处。不在站就每一轮再核。
                        if (await HoldUnloadOffStopAsync(runtime, stops.Current, previousAdvanceAt, now, cancellationToken)
                                .ConfigureAwait(false))
                        {
                            return;
                        }
                        // 前一条已存为 UNLOADED，这一条的卸货命令没落库（U1、U5）：发这一版清单与这一条的命令，与刚卸完时同一段。
                        // 两条报文的 id 都由停靠进度确定地派生，已经写盘的那一版清单按同一个 id 复用，不会再起一行——同一代、或那一行车还没
                        // 确认时如此；车确认过、又换了一代时，候选报文只有信封不同，靠 keepAcknowledgedIgnoring 沿用已确认那一行，否则被重放
                        // 校验拒（control-server#291 独立审查）。卸货停靠没有离站期限，清单内容不会因断联而变。
                        await PublishStopWorklistAsync(
                            runtime, stops, session, stationDepartureDeadlineAt: null, cancellationToken,
                            keepAcknowledgedIgnoring: NothingButTheEnvelope)
                            .ConfigureAwait(false);
                        await PublishUnloadCommandAsync(runtime, stops, session, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    if (unload?.Status == StationOperationStatus.RecoveryRequired)
                    {
                        Block(runtime, "UNLOAD_RESULT_REQUIRES_RECOVERY", now);
                        break;
                    }
                    AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
                        row => row.DemandId == unloading.Demand.DemandId, cancellationToken).ConfigureAwait(false);
                    if (unload?.Status != StationOperationStatus.Committed ||
                        demand.Status != DemandExecutionStatus.Succeeded)
                    {
                        return;
                    }
                    await store.SettleAnsweredCommandAsync(
                        unloading.Membership.UnloadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                    (await TrackedMembershipAsync(runtime, unloading.Demand.DemandId, cancellationToken)
                        .ConfigureAwait(false)).Status = JourneyDemandStatuses.Unloaded;
                    // The cargo has left the vehicle: once nothing of the journey is on board, a fault's binding of it has done its
                    // work, and left live it would be taken for the next fault's cargo (control-server#376). Saved with the unload.
                    await FaultedCargoBindings.StageReleaseWhenNothingLeftOnBoardAsync(
                            dbContext, runtime.JourneyId, runtime.AgvId, FaultedCargoBindings.UnloadedWithNothingLeftOnBoardReason,
                            now, cancellationToken)
                        .ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                // 走到这里的是刚卸完的那一轮，或重启后本站已经卸空、推进那一次保存没落的那一轮（U2、U3 前半）：两者读到的落库状态相同。
                stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
                    .ConfigureAwait(false);
                if (stops.OutstandingAtCurrentStop.Count > 0)
                {
                    // 本停靠还有没卸的：发这一版清单与下一条卸货命令，留在等结果。开门之前核位置（control-server#783）；不在站就先不发，
                    // 刚卸完那一条已经单独存过，下一轮按上面「前一条已卸、这一条的命令没落库」那一支再核再发。
                    if (await HoldUnloadOffStopAsync(runtime, stops.Current, previousAdvanceAt, now, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    await PublishStopWorklistAsync(
                        runtime, stops, session, stationDepartureDeadlineAt: null, cancellationToken)
                        .ConfigureAwait(false);
                    await PublishUnloadCommandAsync(runtime, stops, session, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                if (stops.OpenStops.Count > 1)
                {
                    // 卸完了这一站，计划里还有下一站：走与取货停靠<b>同一条</b>离站路——发离站安全核验、
                    // 按答复过 REQ-0305 的创建门禁、建下一段腿的订单意图并授权移动、下 RIoT 订单，
                    // 由那条路在车真开走之后才把本停靠标记完成。所以这里既不标记完成也不重载游标：
                    // 离站段要的 stops.Current 正是「正要离开的这个停靠」。
                    //
                    // 先前这里直接 SetStage 到下一站的到站阶段，中间<b>什么都没做</b>。全仓只有
                    // :761-762 建后续腿的订单意图，而那两行在离站安全分支里，卸货停靠走不到——于是
                    // 下一轮按一个库里根本不存在的 UpperId 调 SingleAsync，抛 InvalidOperationException，
                    // 冒到 JourneyRuntimeWorker 的<b>整轮</b> catch（记 Error 级事件 2002 LogIterationFailed），
                    // 于是<b>这一轮整个中止</b>：推进循环里排在后面的车不再推进，派车轮次也不跑。每一轮重复。
                    // （那是当时的后果：推进段当时没有逐车隔离。cs#487 起推进段逐车隔离，同样的异常今天只让这一辆车每轮抛、
                    // 永不前进，别的车照常推进，异常在轮末重抛。）
                    // 判据是 LeavingAnUnloadStopAuthorisesTheLegToTheNextStop：它断订单意图在不在，
                    // 不断「推进没抛」——那个异常在测试这一侧什么都看不到。
                    //
                    // OpenStops 含当前停靠，所以判的是 > 1 不是 > 0。
                    //
                    // 离站等待那一段对卸货停靠不计时（ADR-cross-0055「纯卸货站卸完直接结束」，control-server#637）：
                    // 只过纠错闸与扣车闸，同一轮就发离站核验。
                    SetStage(runtime, JourneyRuntimeStage.AwaitingStationDeparture, now);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    goto case JourneyRuntimeStage.AwaitingStationDeparture;
                }

                (await TrackedStopAsync(stops.Current.StopId, cancellationToken).ConfigureAwait(false)).Status =
                    JourneyStopStatuses.Completed;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
                    .ConfigureAwait(false);

                // 与终结的五条来路同一个出口：写 Completed，并暂存车要收的收尾快照（control-server#323）。
                await JourneyClosure.StageAsync(dbContext, runtime, reasonCode: null, now, cancellationToken)
                    .ConfigureAwait(false);
                checkpointWaits.Clear(runtime.VehicleKey);
                // The vehicle's purpose claim was already released with the unload that ended the journey's last
                // demand (JourneyPurposeClaimRelease). The order occupancy that used to be released here, one round
                // later, was retired in batch 8-16 (control-server#387).
                break;
            case JourneyRuntimeStage.Blocked:
            case JourneyRuntimeStage.Completed:
                return;
            default:
                throw new InvalidDataException($"Journey stage '{runtime.Stage}' is not supported.");
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // 正常卸完的那一轮：收尾快照随上面这次保存落库，保存之后才发（control-server#323）。
        if (runtime.Stage == JourneyRuntimeStage.Completed)
        {
            await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        }
    }



    /// <summary>
    /// One arrival evaluation, with the order reading it was decided from.
    /// </summary>
    /// <remarks>
    /// The observation is carried out rather than discarded because a journey that did not arrive
    /// still has to say why, and the order's own state is the first thing that answers it. Reading
    /// it a second time for that would be a second RIoT call per poll and, worse, a second answer:
    /// the two reads could disagree, and then "not arrived" and "the order failed" would be judged
    /// against different facts.
    /// </remarks>
    /// <param name="Vehicle">
    /// 到站判定读到的车辆观测；订单还没对上时没读，为空。到站快照的 <c>batteryState</c> 按它投影（批次9-05，control-server#403），
    /// 不为此再读一次 RIoT：两次读数可能不一致，而到站可信是按这一次判的。
    /// </param>
    private sealed record ArrivalCheck(
        bool Trusted,
        string Purpose,
        OrderIntentRow Intent,
        RiotOrderObservation Order,
        RiotVehicleObservation? Vehicle);

    /// <summary>
    /// 要下装货或下一条卸货命令的这一刻，RIoT 说车停在 <paramref name="stop"/> 上吗（control-server#783）。停在上面答 null；
    /// 否则答拒收用的原因码：读数可信而站号不对（含 0 与空，RIoT 用 0 表示没有当前站，与 control-server#772 同一口径）答
    /// <see cref="ServerReasonCodes.StationMismatch"/>，读数本身不可信（离线、地图不对、在动、过期）答 <see cref="ServerReasonCodes.VehicleNotReady"/>。
    /// </summary>
    /// <remarks>
    /// 判据就是 <see cref="CheckArrivalAsync"/> 里车辆位置那一部分，不含订单与车载端那几项：装卸阶段车应当停在站上，所以位置读成 0
    /// 就是不在站——这只在过了到站判定之后的装卸阶段用，不碰到站之前。每次调用都重读 RIoT，结论不跨轮沿用。不满足时记一条 2318，
    /// 带期望与读到的值，现场据此判断是车真被挪走了还是读数抖动。
    /// </remarks>
    private async Task<string?> VehicleNotAtStopAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        SlotOperationType operation,
        CancellationToken cancellationToken,
        bool log = true)
    {
        // 读数有自己的预算（审查 S-4）：网关自己的超时是 30 秒，RIoT 卡住时每辆停站的车每轮多等一个网关超时，整轮——急停确认也在内——
        // 都排在后面。预算到点或读失败，都按读不到处理：位置证明不了，不开门。做法同 WaitingJourneyWatch.ReadBatteryAsync。
        RiotVehicleObservation? vehicle;
        using (CancellationTokenSource expiry = new(runtimeOptions.SlotCommandPositionReadBudget, timeProvider))
        using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token))
        {
            try
            {
                vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, linked.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                vehicle = null;
            }
        }
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool trustworthy = vehicle is not null &&
                           vehicle.Connected &&
                           vehicle.CurrentMap == runtime.MapIdentity &&
                           vehicle.Speed == 0 &&
                           vehicle.ObservedAt <= now && now - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge;
        string? reason = !trustworthy
            ? ServerReasonCodes.VehicleNotReady
            : vehicle!.CurrentStationId is > 0 and int station && station == stop.StationRiotId
                ? null
                : ServerReasonCodes.StationMismatch;
        if (reason is not null && log)
        {
            LogSlotCommandHeldOffStop(
                logger,
                operation.ToString(),
                runtime.AgvId,
                $"Expected station {stop.StationRiotId} ({stop.StationId}) on map {runtime.MapIdentity}.",
                vehicle is null
                    ? $"No reading within {runtimeOptions.SlotCommandPositionReadBudget.TotalSeconds:0.###} s; answered {reason}."
                    : $"Observed connected={vehicle.Connected} map={vehicle.CurrentMap} station={vehicle.CurrentStationId} " +
                      $"speed={vehicle.Speed} observedAt={vehicle.ObservedAt:O}; answered {reason}.",
                null);
        }
        return reason;
    }

    /// <summary>
    /// 未答复的装卸命令跨代重发之前核位置（control-server#783）：<paramref name="replayable"/> 里有车载端没答结果、而且是在更早一代会话里
    /// 发出的 <c>SlotOperationCommand</c> 时，按它所属的停靠核车在不在那里，不在就把它从这一轮的重发里拿掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 装卸命令的答复是结果不是 DurableAck，所以在结果到来之前，它每一轮都随 <c>ReplayPendingForSessionAsync</c> 再发一次。同一代里这无害：
    /// 同一条连接按序送达，车收到过第一次，再发只会被按 attempt 去重。危险的是跨代那一次——命令发出后连接断了、车没收到、车被开走、
    /// 以新的一代连上，换代重发就是车收到的第一次，会在站外开门。
    /// </para>
    /// <para>
    /// 所以只在有跨代的未答复命令时读 RIoT：等结果、阻塞的旅程可能一挂几个小时，每轮都读，RIoT 一卡就拖住整轮（WaitingJourneyBatteryWatchTests
    /// 的读挂住、读失败两条就是这样红的）。扣住不发不丢东西：车载端执行过的，握手时自己重放没被确认的结果；没执行过的，车回到站上那一轮照常重发。
    /// 记 Debug 级的 2319 而不是 2318：车离站期间这里每一轮都会走到。
    /// </para>
    /// </remarks>
    private async Task WithholdSlotCommandsOffTheirStopAsync(
        HashSet<string> replayable,
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        Dictionary<string, (string StopId, SlotOperationType Type)> commands = new(StringComparer.Ordinal);
        foreach (JourneyStopDemand demand in stops.AllDemands)
        {
            commands[demand.Membership.LoadCommandMessageId] = (demand.Membership.PickupStopId, SlotOperationType.Load);
            commands[demand.Membership.UnloadCommandMessageId] = (demand.Membership.UnloadStopId, SlotOperationType.Unload);
        }
        string[] candidates = [.. commands.Keys.Where(replayable.Contains)];
        if (candidates.Length == 0)
        {
            return;
        }
        ProtocolOutboxRow[] unanswered = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => candidates.Contains(row.MessageId) && row.MessageType == "SlotOperationCommand" &&
                          row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] fromAnEarlierSession = [.. unanswered
            .Where(row => StoredSessionGeneration(row) < session.SessionGeneration)
            .Select(row => row.MessageId)];
        foreach (IGrouping<string, string> atStop in fromAnEarlierSession.GroupBy(id => commands[id].StopId, StringComparer.Ordinal))
        {
            // 停靠不在游标里（不该有）时也扣住：扣住只是推迟，放出去可能是站外开门。
            JourneyStopRow? stop = stops.Stops.FirstOrDefault(row => row.StopId == atStop.Key);
            if (stop is null ||
                await VehicleNotAtStopAsync(runtime, stop, commands[atStop.First()].Type, cancellationToken, log: false)
                    .ConfigureAwait(false) is not null)
            {
                replayable.ExceptWith(atStop);
                LogSlotCommandResendWithheld(logger, string.Join(',', atStop), runtime.AgvId, null);
            }
        }
    }

    private static long StoredSessionGeneration(ProtocolOutboxRow row)
    {
        using JsonDocument envelope = JsonDocument.Parse(row.PayloadJson);
        return envelope.RootElement.GetProperty("sessionGeneration").GetInt64();
    }

    /// <summary>
    /// 卸货那一侧的位置闸（control-server#783）：车不在 <paramref name="stop"/> 上时写下 <see cref="SlotCommandVehicleNotAtStopReason"/>、
    /// 保存、答 true，调用方这一轮不发命令；车在站上时清掉这个码（别的码不动）、答 false，调用方照常发。卸货没有录入可拒，
    /// 所以是每一轮再核，车回到站上的那一轮自己接着走。
    /// </summary>
    private async Task<bool> HoldUnloadOffStopAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        DateTimeOffset previousAdvanceAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 2318 在第一次挂上这个码的那一轮记，之后每满 HeldSlotCommandLogInterval 再记一次（审查 S-1）：车离站期间这里每一轮都会走到，
        // 每轮都记是刷屏；只记第一次，现场又看不出车此刻读成哪一站——推回站上的车 RIoT 常报 0，要在 RIoT 上重新定位（10-18 实测）。
        // 「满一格」按挂码以来的时长算：上一轮推进时还在前一格、这一轮进了下一格就记。不另存状态，上一轮的时刻是旅程行的 UpdatedAt。
        bool alreadyHeld = string.Equals(runtime.BlockReasonCode, SlotCommandVehicleNotAtStopReason, StringComparison.Ordinal);
        bool logDue = !alreadyHeld ||
                      (runtime.BlockReasonSince is { } since &&
                       (now - since).Ticks / HeldSlotCommandLogInterval.Ticks >
                       (previousAdvanceAt - since).Ticks / HeldSlotCommandLogInterval.Ticks);
        if (await VehicleNotAtStopAsync(runtime, stop, SlotOperationType.Unload, cancellationToken, log: logDue)
                .ConfigureAwait(false) is null)
        {
            if (alreadyHeld)
            {
                runtime.SetBlockReason(null, now);
            }
            return false;
        }
        runtime.SetBlockReason(SlotCommandVehicleNotAtStopReason, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <param name="stop">车正驶向的那个停靠——这一次到站判定的全部依据（批次7-06，control-server#211）。</param>
    /// <remarks>
    /// <para>
    /// <b>按 <c>MovementLegId</c> 取意图，因为那是主键。</b>这之前按 <c>(DemandId, Purpose)</c> 取，而那一对
    /// 只在「一趟旅程两个停靠」时碰巧唯一：<see cref="JourneyPlanBuilder.LegIntent"/> 给每一段后续腿都建
    /// <c>Purpose = "TO_GATE"</c>，所以第三个停靠一出现就有两行同键，<c>SingleAsync</c> 每一轮都抛。
    /// 碰巧唯一的键，加一个停靠就不唯一；主键加多少个停靠都唯一。
    /// </para>
    /// <para>
    /// 目标站同理取自停靠而不是旅程行。旅程行上的 <c>PickupStationRiotId</c> 与 <c>GateStationRiotId</c> 只描述
    /// 锚需求那两个站，车开到第二个取货站之后它们指的还是第一个——到站判定因此永远不成立，而表现出来不是报错，
    /// 是操作员在第二个站扫了码、服务端收下了、什么都不发生。
    /// </para>
    /// </remarks>
    private async Task<ArrivalCheck> CheckArrivalAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string purpose,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        OrderIntentRow intent = await dbContext.OrderIntents.SingleAsync(
            row => row.MovementLegId == stop.MovementLegId,
            cancellationToken).ConfigureAwait(false);
        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken)
            .ConfigureAwait(false);
        int targetStation = stop.StationRiotId;
        bool exactOrder = order.Kind == RiotOrderObservationKind.Terminal &&
                          order.OrderState == RiotOrderState.Success &&
                          !string.IsNullOrWhiteSpace(order.OrderId) &&
                          order.OrderId == intent.OrderId &&
                          order.VehicleKey == runtime.VehicleKey &&
                          order.MapId == runtime.MapId &&
                          order.DestinationStationId == targetStation;
        if (!exactOrder)
        {
            return new ArrivalCheck(false, purpose, intent, order, null);
        }
        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken)
            .ConfigureAwait(false);
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(runtime.AgvId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool trusted = vehicle.Connected && vehicle.Enabled &&
               vehicle.ProcState == "IDLE" &&
               vehicle.CurrentMap == runtime.MapIdentity &&
               vehicle.CurrentStationId == targetStation &&
               vehicle.Speed == 0 &&
               vehicle.LockStatus == 0 &&
               string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
               vehicle.ObservedAt <= now && now - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge &&
               onboard is not null && onboard.SessionGeneration == session.SessionGeneration &&
               onboard.VehicleStopped && onboard.AllTargetSlotsLocked && onboard.AllUnlockOutputsReset &&
               !onboard.UnknownPresent;
        return new ArrivalCheck(trusted, purpose, intent, order, vehicle);
    }

    /// <summary>
    /// REQ-0232's one symptom this loop can establish for itself: RIoT reports the move order this
    /// journey has in flight as FAILED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this symptom and not the other four.</b> REQ-0232 names five - offline, comms lost,
    /// navigation failed, one order FAILED, elapsed time - and the fault model treats all five
    /// alike once they arrive. What separates this one is that the loop can establish it without
    /// guessing: a terminal FAILED on an order this server created is RIoT's own statement about
    /// this project's own task, and it arrives with the two things the model needs before it can
    /// protect anything - a vehicle with an order in flight, so <c>OrderHold</c> has a target, and
    /// an order <c>ResumeAsync</c> can later be asked about, so the fault has a clearing path.
    /// A vehicle merely unreachable has neither, which is why ticket 09 declined to route
    /// <c>VEHICLE_OFFLINE</c> here: it would record a fault nothing could clear.
    /// </para>
    /// <para>
    /// <b>The stage is deliberately not moved to Blocked.</b> The fault fact is what blocks new
    /// dispatch, and it does so from the moment it is recorded. Leaving the journey in its arrival
    /// stage keeps this loop evaluating, which is the only thing that advances REQ-0247's stop
    /// proof - a window of samples needs the loop to keep sampling - and keeps the escalation live
    /// if the vehicle turns out to be moving after all. What changes is the reason the journey
    /// names for standing still, in the same shape a checkpoint wait names its own.
    /// </para>
    /// <para>
    /// Cargo is claimed only on the gate leg. On the way to the pickup the vehicle is empty and
    /// that is known, which is REQ-0238's one case for not binding; past a committed load it is
    /// carrying this demand's product and the binding has to hold.
    /// </para>
    /// <para>
    /// <b>Called on either side of the readiness gate</b> (control-server#358): through
    /// <see cref="NameStalledOrderAsync"/>, which the arrival branches and the gate both use, and from the rebuild for a
    /// rebuilt order that FAILED before it was confirmed (control-server#318, review S2). Nothing here needs the onboard
    /// session or sends to it -- the facts and the commands are RIoT's -- and the fault model's evaluation is the only
    /// thing that advances the stop proof, the escalation, the trigger's confirmation and REQ-0248's re-trigger, so it
    /// must not wait for a session that a real onboard does not hold Ready for the whole of a leg with this server's
    /// order in flight. While the engine holds <c>JourneyMutationGate</c> this makes the same RIoT calls behind the gate as
    /// in front of it -- an emergency-state read, a motion sample, and when due an <c>OrderHold</c> and a trigger with their
    /// read-backs -- each bounded by <c>RIoT:timeoutSeconds</c> (30 s by default, no retry; <c>RiotSdkRegistration</c>).
    /// Behind the gate they are new time spent holding it (control-server#334's concern, not a peer send).
    /// </para>
    /// </remarks>
    private async Task<bool> ObserveOrderFailureAsync(
        JourneyRuntimeRow runtime,
        OrderIntentRow intent,
        RiotOrderObservation order,
        CancellationToken cancellationToken)
    {
        if (order.Kind != RiotOrderObservationKind.Terminal ||
            order.OrderState != RiotOrderState.Failed ||
            intent.OrderId is not string orderId)
        {
            return false;
        }

        await faults.ObserveAsync(
            new EmergencyStopSubject(runtime.AgvId, runtime.VehicleKey),
            VehicleFaultEvidence.OrderFailed,
            await InFlightFaultContextAsync(runtime, intent, orderId, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        LogOrderFailedSymptom(logger, intent.UpperId, runtime.AgvId, runtime.DemandId ?? runtime.JourneyId, null);
        checkpointWaits.Clear(runtime.VehicleKey);
        if (!string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.OrderFailed, StringComparison.Ordinal))
        {
            runtime.SetBlockReason(VehicleFaultEvidence.OrderFailed, timeProvider.GetUtcNow());
            runtime.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Hands a FAILED in-flight order to the fault model (control-server#358), names one RIoT has stopped without it being
    /// FAILED or arrived (control-server#316), and says whether this round stops here. Returns false, having cleared any code
    /// of its own, when the order is neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Until this ticket all three were silent.</b> The gateway reads 9 as Active and 2, 6 and 8 as Terminal; the arrival
    /// branches accept Terminal SUCCESS as an arrival and Terminal FAILED as a fault, and handed everything else to
    /// <see cref="NameCheckpointWaitAsync"/>, which clears the code when the vehicle is not at a checkpoint. The journey
    /// stood in its arrival stage with no code at all, so the dashboard did not list it.
    /// </para>
    /// <para>
    /// <b>HANG is named and nothing else is done</b> (the user's decision of 2026-09-22 on #299, option H-a). Not a hold,
    /// not an emergency stop, not a fault fact: handed to <c>VehicleFaultCoordinator</c> as it stands, a HANG between
    /// stations escalates to an emergency stop, after which RIoT refuses the continue (100021 under a latch) and
    /// REQ-0356's release refuses too, because HANG counts as an unfinished order -- a loop with no way out on this server.
    /// During a HANG RIoT is not driving the order; what can still move the vehicle is its own onboard controls, which
    /// need a person at it. A continue in RIoT puts the order back to 3 and the next round clears the code here. A
    /// continue that leaves the order in HANG, which BC-ORDER-018 has seen, keeps the code and its start time.
    /// </para>
    /// <para>
    /// <b>SUSPENDED (8) is named as unrecognised</b> and treated as a live order: the lab has never seen it and the SDK
    /// marks it removed. Whether the gateway should read it as terminal is cs#296's question.
    /// </para>
    /// <para>
    /// <b>CANCELLED and DELETED mean someone ended the order outside this server</b>, which the user said on 2026-09-22 is
    /// almost always a mistake, to be answered by rebuilding the order rather than by redispatching the demand. The code is
    /// written and alarmed as before, and since control-server#318 the ending is recorded in the same save to be rebuilt for
    /// the same vehicle and the same demand once the delay is over -- no person's confirmation, by the user's later decision
    /// the same day, because usually nobody is watching the system (see <c>JourneyRuntimeEngine.OwnOrderRebuild.cs</c>). The
    /// delay is the time for whoever cancelled it, who may be standing beside the vehicle, to stop it. An order this server
    /// cancelled itself is named and not recorded. The release service neither releases nor cancels while the code stands.
    /// </para>
    /// <para>
    /// <b>FAILED reaches the fault model here, first, by construction</b> (control-server#358): this method hands the order
    /// to <see cref="ObserveOrderFailureAsync"/> before anything else and returns true when it was FAILED, so a FAILED order
    /// keeps its own code and never reaches the switch below, where it would read as an order that moved on. It used to say
    /// "ordered after <c>ObserveOrderFailureAsync</c>", which each arrival branch arranged by calling that first; the
    /// readiness gate called only this method, so behind it FAILED went nowhere -- no fault, no hold, no emergency stop, and
    /// for a vehicle already stopped no trigger confirmation and no REQ-0248 re-trigger -- and the gate wrote its own code
    /// over <c>VEHICLE_ORDER_FAILED</c>. Every caller that judges an in-flight order goes through here, and none calls the
    /// fault observation on its own.
    /// </para>
    /// </remarks>
    private async Task<bool> NameStalledOrderAsync(
        JourneyRuntimeRow runtime,
        OrderIntentRow intent,
        RiotOrderObservation order,
        CancellationToken cancellationToken,
        string? reasonOnceMovedOn = null)
    {
        if (await ObserveOrderFailureAsync(runtime, intent, order, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // control-server#335: an order still in flight whose vehicle's doors are not proven locked goes to the fault model here
        // too, on both sides of the readiness gate for the reason FAILED does.
        if (await ObserveDoorsInTransitAsync(runtime, intent, order, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        string upperId = intent.UpperId;
        string? reason = (order.Kind, order.OrderState) switch
        {
            (RiotOrderObservationKind.Active, RiotOrderState.Hang) => OrderHangReason,
            (RiotOrderObservationKind.Terminal, RiotOrderState.Suspended) => OrderStateUnrecognizedReason,
            (RiotOrderObservationKind.Terminal, RiotOrderState.Cancelled or RiotOrderState.Deleted) =>
                OrderEndedWithoutArrivalReason,
            _ => null,
        };
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (reason is null)
        {
            // An order that could not be read has not moved on. The gateway returns Unknown rather than throwing for a failed
            // or indeterminate read, and treating that as a continue cleared the code for a round, restarted its clock,
            // raised the warning again and let the vehicle back into underWay for that round (independent review, item 1).
            // The round stops here with the code as it is: nothing below can judge an order it cannot see either.
            if (order.Kind is not (RiotOrderObservationKind.Active or RiotOrderObservationKind.Terminal) &&
                KeepsItsCodeWhileTheOrderIsUnread(runtime.BlockReasonCode))
            {
                return true;
            }

            // The order moved on -- a continue in RIoT, most often. The code is this method's to clear: SetStage only
            // clears on a stage change, and a HANG that comes and goes inside one stage never reaches one. Behind the
            // readiness gate the code goes straight to the gate's own, in the same save: cleared to null first, the row
            // would read "no block" for the moment between two saves, which is long enough for the dashboard to drop it
            // (incremental review).
            if (IsStalledOrderReason(runtime.BlockReasonCode))
            {
                runtime.SetBlockReason(reasonOnceMovedOn, now);
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        checkpointWaits.Clear(runtime.VehicleKey);
        string code = reason;
        if (reason == OrderEndedWithoutArrivalReason)
        {
            // control-server#318: the ending is recorded to be rebuilt, in the save that names it.
            code = await RecordOrderEndedInRiotAsync(runtime, upperId, order, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(runtime.BlockReasonCode, code, StringComparison.Ordinal))
        {
            LogInTransitOrderStalled(
                logger, upperId, runtime.DemandId ?? runtime.JourneyId, runtime.AgvId, order.OrderState, reason, null);
            runtime.SetBlockReason(code, now);
            runtime.UpdatedAt = now;
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// A code a round that could not read the in-flight order leaves standing: the stalled-order family, and
    /// <see cref="VehicleFaultEvidence.OrderFailed"/> (control-server#358). A read that does not answer says nothing about
    /// an order that was FAILED -- FAILED is terminal -- and left to the readiness gate the code would read "waiting for the
    /// vehicle" for that round, then flip back on the next, restarting its start time both ways.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="IsStalledOrderReason"/> on purpose: that one also decides en-route appends and the release
    /// service's refusals, where a fault fact already does the blocking and this code is not meant to join them.
    /// </remarks>
    private static bool KeepsItsCodeWhileTheOrderIsUnread(string? reasonCode) =>
        IsStalledOrderReason(reasonCode) ||
        string.Equals(reasonCode, VehicleFaultEvidence.OrderFailed, StringComparison.Ordinal);

    /// <summary>
    /// <see cref="NameStalledOrderAsync"/> for a journey whose round cannot go on to the arrival branches because the peer
    /// cannot be sent to: behind a closed readiness gate (<paramref name="peerReason"/> <c>ONBOARD_SESSION_NOT_READY</c>),
    /// or with a Ready session that has gone silent (<see cref="OnboardSessionLostReason"/>, control-server#358). Reads the
    /// in-flight order of the current stop and says whether a code of its own now stands, which the caller then leaves in
    /// place of <paramref name="peerReason"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same rules as in the arrival branches, because it is the same method: a FAILED order goes to the fault model -- the
    /// fault, the hold, the escalation, and for a vehicle already stopped the trigger's confirmation and REQ-0248's
    /// re-trigger (control-server#358) -- a stalled order is named, an order that has definitely moved on clears the code
    /// (and the caller writes <paramref name="peerReason"/> after it), and an order that cannot be read keeps whatever code
    /// stands. Nothing is sent to the peer, which is why both callers stop where they do: the reads and the commands are
    /// RIoT's, and the plan the gate may still publish after this (control-server#314) is withheld once a fault is held.
    /// </para>
    /// <para>
    /// <b>Both callers, because neither condition is rare.</b> A real onboard is not ready for the whole of a leg that
    /// carries this server's order, and a vehicle that stops talking -- an onboard PC that hangs, a lost link -- is exactly
    /// when its order is likeliest to fail with the vehicle still moving. The fault model's evaluation is the only thing that
    /// stops such a vehicle, and it needs nothing from the peer.
    /// </para>
    /// <para>
    /// Only the two arrival stages, and only a confirmed intent: anywhere else there is no in-flight move order this
    /// server is waiting on, and nothing to read. A read that throws counts as unreadable, so a RIoT hiccup cannot take the
    /// caller's write away from a journey that had no code of its own to begin with.
    /// </para>
    /// </remarks>
    private async Task<bool> NameInFlightOrderWithoutThePeerAsync(
        JourneyRuntimeRow runtime,
        RiotMapStationCatalogSnapshot currentMap,
        string peerReason,
        CancellationToken cancellationToken)
    {
        if (!WaitsOnAnArrivalTheVehicleReports(runtime.Stage))
        {
            return false;
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        // A rebuild under way is kept and named here too (control-server#318): a real onboard is not ready for most of a
        // leg. It creates nothing here -- a new order waits for a Ready, answering session that vouches for the vehicle
        // (review M2) -- but an order already sent is reconciled, which only reads (incremental review B2), and a rebuilt order
        // that FAILED goes on being fed to the fault model; once a new order is confirmed the journey carries
        // peerReason again, and the next round names a HANG like any other.
        //
        // A door fault on an order someone cancelled or deleted in RIoT is supervised before that, as in the stage body
        // (control-server#335 incremental review, P1g): a real onboard is not ready for the whole of a leg, so this -- not the
        // stage body -- is the path such a vehicle takes every round, and the rebuild below returns without ever reaching the
        // fault model. Unsupervised, a latched vehicle was never released and the rebuild waited on the fault for good.
        await SuperviseDoorFaultOnEndedOrderAsync(runtime, stops.Current, cancellationToken).ConfigureAwait(false);
        if (await OwnOrderRebuilds.ForStopAsync(dbContext, stops.Current, cancellationToken).ConfigureAwait(false) is not null)
        {
            return await AdvanceOwnOrderRebuildAsync(
                    runtime, stops.Current, currentMap, mayCreate: false, reasonOnceRebuilt: peerReason, cancellationToken)
                .ConfigureAwait(false);
        }
        OrderIntentRow? intent = await dbContext.OrderIntents.SingleOrDefaultAsync(
            row => row.MovementLegId == stops.Current.MovementLegId, cancellationToken).ConfigureAwait(false);
        if (intent is null)
        {
            return false;
        }

        if (intent is not { Status: "CONFIRMED", OrderId: not null })
        {
            return await NameUnconfirmedOrderWithoutThePeerAsync(runtime, intent, currentMap, peerReason, cancellationToken)
                .ConfigureAwait(false);
        }

        RiotOrderObservation order;
        try
        {
            order = await vehicleFacts.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return KeepsItsCodeWhileTheOrderIsUnread(runtime.BlockReasonCode);
        }

        return await NameStalledOrderAsync(runtime, intent, order, cancellationToken, reasonOnceMovedOn: peerReason)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="NameInFlightOrderWithoutThePeerAsync"/> for an ordinary leg whose create has not been confirmed
    /// (control-server#367): reconciles it when a create has been sent, and judges it like a confirmed order once RIoT reports it
    /// terminal. True when a code of its own now stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why behind the gate at all.</b> A lost create answer leaves the intent unconfirmed while the vehicle drives the order,
    /// and a real onboard reports its session not ready for exactly that reason -- RIoT shows it an unfinished order of this
    /// server's -- for the whole of the leg. Until this ticket nothing behind the gate reconciled such an intent, so its order
    /// could FAIL with the vehicle moving and be neither recorded nor stopped until the session came back.
    /// </para>
    /// <para>
    /// <b>It never creates.</b> Only an intent whose create has already been sent (<c>CreateAttemptCount &gt; 0</c>) is
    /// reconciled, and every branch of <c>MovementDispatchService.ReconcileOrCreateAsync</c> that creates requires a pending intent
    /// with no attempt. An intent never sent has no order to read and is left to the gate, as before: a new order waits for a
    /// Ready session in front of the gate. This is the same line the rebuild draws behind the gate (control-server#318, review
    /// B2).
    /// </para>
    /// <para>
    /// A reconciliation that confirms the order goes on to the confirmed path, so a HANG on the way is named like any other. One
    /// that finds it terminal goes to <see cref="NameOrderEndedBeforeConfirmationAsync"/>, as in front of the gate: FAILED to the
    /// fault model, CANCELLED and DELETED recorded to be rebuilt, SUSPENDED named for a person. A rebuild recorded here creates
    /// nothing while the session is not ready (<c>mayCreate: false</c> behind the gate).
    /// </para>
    /// </remarks>
    private async Task<bool> NameUnconfirmedOrderWithoutThePeerAsync(
        JourneyRuntimeRow runtime,
        OrderIntentRow intent,
        RiotMapStationCatalogSnapshot currentMap,
        string peerReason,
        CancellationToken cancellationToken)
    {
        if (intent.CreateAttemptCount == 0)
        {
            return false;
        }

        if (intent.Status != "TERMINAL_RECONCILIATION_REQUIRED")
        {
            MovementDispatchResult result;
            try
            {
                result = await movementDispatch.ReconcileOrCreateAsync(intent.UpperId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                          !cancellationToken.IsCancellationRequested)
            {
                // control-server#673 review S2: an unread round breaks the run of "no such order".
                dispatchRound.Charging.Board.SeenOrUnread(intent.UpperId);
                return false;
            }

            if (result.Outcome == MovementDispatchOutcome.Confirmed)
            {
                // Read afresh rather than by going round again: what decides the next step is the row as reconciliation left it.
                OrderIntentRow confirmed = await dbContext.OrderIntents.AsNoTracking()
                    .SingleAsync(row => row.UpperId == intent.UpperId, cancellationToken).ConfigureAwait(false);
                RiotOrderObservation order;
                try
                {
                    order = await vehicleFacts.ReconcileByUpperIdAsync(confirmed.UpperId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                              !cancellationToken.IsCancellationRequested)
                {
                    return KeepsItsCodeWhileTheOrderIsUnread(runtime.BlockReasonCode);
                }

                return await NameStalledOrderAsync(runtime, confirmed, order, cancellationToken, reasonOnceMovedOn: peerReason)
                    .ConfigureAwait(false);
            }

            if (result.Outcome != MovementDispatchOutcome.TerminalReconciliationRequired)
            {
                // control-server#673: behind the gate too -- the record is kept and named here, and the rebuild creates nothing
                // until the session is ready, as for a cancellation read here.
                return result.Outcome == MovementDispatchOutcome.ResultUnknown &&
                       await RecordOwnOrderNeverAppearedAsync(runtime, intent.UpperId, endedRebuild: null, cancellationToken)
                           .ConfigureAwait(false);
            }
        }

        return await NameOrderEndedBeforeConfirmationAsync(runtime, intent.UpperId, peerReason, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Names the vehicle having gone quiet, and says whether this round should stop here
    /// (control-server#234). Returns true when the journey is left where it is for this round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this runs before the advance rather than inside the waiting branches.</b> The first thing the
    /// advance does after the readiness gate is replay this session's unacknowledged outbound messages, and
    /// <c>OnboardPeer.SendAsync</c> throws <c>IOException</c> when the addressee has no attached connection.
    /// So from the moment the connection is gone, every round of every in-flight journey threw out of
    /// <c>ReplayPendingForSessionAsync</c> and was logged as "iteration failed closed" — the waiting branches
    /// below were never reached, and no code could be written from them. Measured on the first run of
    /// <c>onboard-silent-liveness-loss</c>: one such exception every second, and the journey never blocked.
    /// </para>
    /// <para>
    /// <b>Stopping the round costs almost nothing, and the reason is a shared threshold — not the contents of
    /// the branches below.</b> It is tempting to say "everything below needs the peer anyway", and that is
    /// false: <see cref="ObserveOrderFailureAsync"/>, <see cref="EnsureMovementConfirmedAsync"/> and
    /// <see cref="NameCheckpointWaitAsync"/> all judge RIoT facts, which a silent peer does not stop arriving.
    /// The real reason is that this side and the session layer measure the same six seconds
    /// (<see cref="SessionLiveness.Timeout"/>) from the same event — the last legal inbound of this generation.
    /// So by the time this fires, the session layer has already closed the connection, and the round it stops
    /// is one that would have thrown out of <c>ReplayPendingForSessionAsync</c> a few lines below
    /// (<c>OnboardPeer.SendAsync</c> throws for an addressee with no attached connection). That is what every
    /// round did before this ticket, once a second, for as long as the silence lasted.
    /// </para>
    /// <para>
    /// <b>Two load-bearing constraints follow, and they are this method's to carry.</b>
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>This side's window must never be longer than the session layer's.</b> Both read
    /// <see cref="SessionLiveness.Timeout"/> today, so they cannot drift apart by accident;
    /// <c>OnboardSilentLivenessLossTests.TheEngineAndTheSessionLayerMeasureTheSameWindow</c> fails if they do.
    /// Make this one longer and the branches below run against a peer that is already gone — the throwing round
    /// comes back, with a block code on top of it.
    /// </description></item>
    /// <item><description>
    /// <b>The session layer's own close must not be removed.</b> It is what makes "silent" and "disconnected"
    /// the same state by the time this runs. Take it away and a vehicle that is silent with its socket still
    /// open reaches the branches below every round.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>The one window where this does decide something, and it is bounded.</b> The two clocks do not start
    /// at exactly the same instant: the inbox stamps <c>ReceivedAt</c> when a message starts being processed,
    /// while the session layer refreshes after it finishes, so the session layer expires a few milliseconds
    /// later. A runtime round landing inside that gap — about one part in a few hundred, against a two-second
    /// poll — stops a round the replay would still have survived. The in-flight order is judged before it stops
    /// either way (below), so nothing a RIoT read would show is lost to it.
    /// </para>
    /// <para>
    /// <b>The silence itself is display and escalation only (REQ-0287).</b> Because of the silence the stage is
    /// not moved, the demand is not ended, the lease is not released, nothing is reassigned, and no order command
    /// is issued — <c>OrderHold</c> least of all, which ADR-cross-0026 asks for and REQ-0287 forbids; the user
    /// deferred that conflict to batch 9 on 2026-09-20.
    /// </para>
    /// <para>
    /// <b>The order is still judged, from RIoT (control-server#358).</b> REQ-0287 says a lost session "only blocks
    /// the vehicle's new business and goes on observing it through RIoT", and that observation is what
    /// <see cref="NameInFlightOrderWithoutThePeerAsync"/> does before this stops the round. When RIoT reports the order
    /// FAILED, the hold and the emergency stop that follow are REQ-0232/0234/0246's answer to the FAILED -- the same
    /// answer a round with an answering peer gives -- not an action taken because of the silence. Until this ticket the
    /// silence stopped that observation too, so a vehicle whose order failed while it was not heard from was neither
    /// recorded nor stopped until it spoke again.
    /// </para>
    /// <para>
    /// <b>Three codes this must not overwrite, and a family of three more.</b> Two are the ones the readiness gate above also leaves
    /// alone: a <see cref="JourneyRuntimeStage.Blocked"/> journey's code names the recovery it is waiting on
    /// and nothing rebuilds it, and a stop held at its AREA machine carries the code control-server#198 counts
    /// its escalation from. A silent session is judged for them too — control-server#228's escalation is on
    /// its own clock and runs whether or not the vehicle answers — but their codes stay as they are.
    /// </para>
    /// <para>
    /// The third is <see cref="VehicleFaultEvidence.OrderFailed"/>, and it is this ticket's own addition.
    /// Escalation does not care — both codes sit at the top tier — but <b>what a person is told does</b>. That
    /// code means RIoT reported this journey's move order FAILED, which is REQ-0232's symptom and has already
    /// been recorded as a vehicle fault; an emergency stop triggered by an in-flight order reported FAILED has
    /// no automatic release path (<c>docs/emergency-stop-field-fallback.md</c>), so it is precisely the thing
    /// the person walking up to the vehicle has to know. Writing "the vehicle stopped talking" over it would
    /// replace the reason they need with a symptom of it, and reset <see cref="JourneyRuntimeRow.BlockReasonSince"/>
    /// while doing so. A failed order and a silent session are usually the same event seen from two sides.
    /// </para>
    /// <para>
    /// The three codes <see cref="NameStalledOrderAsync"/> writes join it for the same reason (control-server#316): a power
    /// cycle mid-order is one of the ways into HANG (BC-ORDER-015 P), so a hanging order and a silent session are often one
    /// event too, and the person has to be told the order is waiting for a continue or a cancel in RIoT. An order that
    /// stalls or fails during the silence is named here as well, since control-server#358: it used to wait until the
    /// vehicle was heard from again.
    /// </para>
    /// <para>
    /// The two checkpoint codes are deliberately <b>not</b> on this list, which keeps the existing convention:
    /// <see cref="NameCheckpointWaitAsync"/> already overwrites and clears them freely as the wait comes and
    /// goes, so they are the runtime's running commentary rather than a record of a decision. Nothing here
    /// changes that.
    /// </para>
    /// </remarks>
    private async Task<bool> NameSilentOnboardSessionAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        JourneyStopCursor stops,
        RiotMapStationCatalogSnapshot currentMap,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!WaitsOnAnArrivalTheVehicleReports(runtime.Stage))
        {
            return false;
        }
        bool heard = await SessionLiveness.HeardFromAsync(
            dbContext, runtime.AgvId, session.SessionGeneration, now, cancellationToken).ConfigureAwait(false);
        if (heard)
        {
            // Back on air on this generation: the code is this method's to clear, because SetStage only clears
            // on a stage change and a silence that comes and goes inside one stage never reaches one.
            if (string.Equals(runtime.BlockReasonCode, OnboardSessionLostReason, StringComparison.Ordinal))
            {
                runtime.SetBlockReason(null, now);
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        // control-server#228's escalation is measured from AreaEndAdmissionRevokedSince, which has nothing to
        // do with whether the vehicle is answering. So it is judged first and, when it fires, it decides the
        // round: a loaded stop whose station stopped admitting it goes to Blocked under its own code even
        // though the vehicle is also silent.
        if (await EscalateAreaEndAdmissionRevokedPastTimeoutAsync(runtime, stops, now, cancellationToken)
                .ConfigureAwait(false))
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        // The in-flight order is judged before the round stops (control-server#358): a FAILED one goes to the fault model --
        // recorded, held and stopped, and a vehicle already stopped keeps its trigger confirmed and re-triggered -- and a
        // stalled one is named, all from RIoT and nothing sent to the silent peer. Until this ticket a silence stopped all of
        // that until the vehicle was heard from again, which is exactly when a failing vehicle may still be moving.
        if (await NameInFlightOrderWithoutThePeerAsync(runtime, currentMap, OnboardSessionLostReason, cancellationToken)
                .ConfigureAwait(false))
        {
            return true;
        }

        // Stage != Blocked is redundant while WaitsOnAnArrivalTheVehicleReports admits only the two arrival
        // stages, and it stays for the same reason the readiness gate above carries it: it states which codes
        // this write must not touch, so widening the stage set later cannot quietly start overwriting a
        // Blocked journey's recovery code. Redundant today, load-bearing the day someone widens it.
        if (!CarriesACodeThatNamesAWaitOnAPerson(runtime))
        {
            // No lower bound: the log says when the vehicle was last heard from, however long ago that was.
            DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
                runtime.AgvId, session.SessionGeneration, notBefore: null, cancellationToken).ConfigureAwait(false);
            LogOnboardSessionLost(
                logger, runtime.AgvId, session.SessionGeneration, runtime.DemandId ?? runtime.JourneyId, lastInboundAt, null);
            runtime.SetBlockReason(OnboardSessionLostReason, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// The two stages where the journey's only way forward is the vehicle reporting that it has arrived, and
    /// where a silent vehicle therefore leaves the row carrying no block code at all (control-server#234).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately these two and not every stage.</b> The stops at a station have their own liveness
    /// measure already — <c>TryEndStopAtStationDeadlineAsync</c> and <c>TrySettleDeterminateLoadFailureAsync</c>
    /// refuse to act on evidence older than <see cref="JourneyRuntimeOptions.MaximumEvidenceAge"/>, and a door
    /// left open past the deadline raises <see cref="StationTimeoutDoorNotClosedReason"/>. Judging silence in
    /// front of those would take the decision away from them: run wider once, this returned early for every
    /// station stop whose test advanced the clock without heartbeating, and fourteen existing tests went red.
    /// They were right to. A guard that stops a stage from reaching its own judgment is not the same change as
    /// naming a wait nobody was naming.
    /// </para>
    /// <para>
    /// The arrival stages have no such measure: the arrival is simply not trusted, round after round, and the
    /// row ends up with no code. That is the gap this ticket exists to close, and it is the whole of it. A
    /// station stop whose vehicle has gone quiet is a separate question — it is visible through its own
    /// deadline and alarm — and widening to it belongs in its own ticket, with its own evidence.
    /// </para>
    /// </remarks>
    private static bool WaitsOnAnArrivalTheVehicleReports(JourneyRuntimeStage stage) =>
        stage is JourneyRuntimeStage.AwaitingPickupArrival or JourneyRuntimeStage.AwaitingGateArrival;

    /// <summary>
    /// Says why a journey that has not arrived is not arriving, when RIoT's answer is that the
    /// vehicle is holding at a traffic checkpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is B2's <c>MT_WAIT_FOR_CHECKPOINT</c> handling, and it deliberately stops short of
    /// the motion model.</b> The state stays
    /// <see cref="VehicleMotionReading.Unknown"/> — a vehicle waiting for its turn at a checkpoint
    /// has not been shown to have stopped rolling, and adding the state to the gateway's
    /// not-moving list would weaken REQ-0247's stop proof to buy a nicer word here. What this adds
    /// is the other half of "recognised": the reason the journey is standing still is named
    /// instead of being left blank, which with one vehicle almost never happened and with several
    /// is the ordinary case.
    /// </para>
    /// <para>
    /// <b>The disposition is bounded waiting, not escalation.</b> Below the budget the wait is
    /// traffic working as designed and the journey simply says so. Past it, the reason changes and
    /// the wait is logged for a person, because a vehicle that never gets its turn will not fix
    /// itself. It is deliberately not routed into the two-level fault model: that path proves a
    /// stop before it decides, an unknown reading satisfies its escalation condition by
    /// construction, and every long checkpoint wait would therefore end in an emergency stop.
    /// </para>
    /// <para>
    /// A vehicle that is not waiting has its wait forgotten, so the next wait is measured from its
    /// own beginning, and any block reason this wrote is cleared rather than left to describe a
    /// situation that has passed.
    /// </para>
    /// </remarks>
    private async Task NameCheckpointWaitAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        VehicleMotionSample sample = await motionFacts
            .SampleMotionAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        bool waiting = string.Equals(
            sample.MovementState, RiotMovementStates.WaitForCheckpoint, StringComparison.Ordinal);
        if (!waiting)
        {
            checkpointWaits.Clear(runtime.VehicleKey);
            if (IsCheckpointReason(runtime.BlockReasonCode))
            {
                runtime.SetBlockReason(null, timeProvider.GetUtcNow());
                runtime.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        TimeSpan waited = checkpointWaits.Observe(runtime.VehicleKey, sample.ObservedAt);
        bool exceeded = waited > runtimeOptions.CheckpointWaitBudget;
        string reason = exceeded ? CheckpointWaitExceededReason : CheckpointWaitReason;
        if (exceeded)
        {
            LogCheckpointWaitExceeded(logger, runtime.AgvId, runtime.DemandId ?? runtime.JourneyId, null);
        }
        if (string.Equals(runtime.BlockReasonCode, reason, StringComparison.Ordinal))
        {
            return;
        }

        runtime.SetBlockReason(reason, timeProvider.GetUtcNow());
        runtime.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsCheckpointReason(string? reason) =>
        string.Equals(reason, CheckpointWaitReason, StringComparison.Ordinal) ||
        string.Equals(reason, CheckpointWaitExceededReason, StringComparison.Ordinal);

    private async Task<bool> EnsureMovementConfirmedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        RiotMapStationCatalogSnapshot currentMap,
        string legName,
        CancellationToken cancellationToken)
    {
        string upperId = stop.UpperId;
        OrderIntentRow intent = await dbContext.OrderIntents.SingleAsync(
            row => row.UpperId == upperId,
            cancellationToken).ConfigureAwait(false);
        if (intent.Status == "CONFIRMED" && intent.OrderId is not null)
        {
            return true;
        }
        // control-server#375: an intent never sent may be created by the reconciliation below, rounds or minutes after the
        // departure check (or the dispatch admission) that let the leg go -- a read that timed out is enough to put that
        // distance there -- and the doors, the vehicle's condition, an emergency stop, the catalog or an operator hold may have
        // changed since. So it is created only once two of the checks a rebuild's HeldBeforeCreateAsync makes pass again
        // (control-server#366 M1): the vehicle's condition, then REQ-0305's create gate for this stop, which the departure path
        // asks too. Neither holding writes anything but the journey's code, and the next round asks again. "Never sent" is the
        // store's own definition, so an intent RESULT_UNKNOWN for a reason a person has to look at -- a read that found an order
        // not matching it -- is not held here and keeps its {leg}_ResultUnknown. An order already sent is only reconciled, which
        // moves nothing, and is not held here either.
        if (await new WireToGateStore(dbContext).IsNeverSentAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            if ((await VehicleConditionReasonsAsync(runtime, cancellationToken).ConfigureAwait(false)).Length > 0)
            {
                runtime.SetBlockReason($"{legName}_{NeverSentLegWaitingVehicleSuffix}", timeProvider.GetUtcNow());
                runtime.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            CreateGateOutcome gate = await GateLegAsync(
                    runtime, stop, currentMap, cancellationToken, toTheStopItself: stop.StopRole == JourneyStopRoles.Pickup)
                .ConfigureAwait(false);
            if (!gate.IsAllowed)
            {
                runtime.SetBlockReason(gate.BlockReason, timeProvider.GetUtcNow());
                runtime.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        MovementDispatchResult result;
        try
        {
            result = await movementDispatch.ReconcileOrCreateAsync(upperId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // control-server#673 review S2: a round that could not read RIoT breaks "no such order without a break" as much as an
            // unread answer inside the shared judgement does. The exception goes on as before.
            dispatchRound.Charging.Board.SeenOrUnread(upperId);
            throw;
        }

        if (result.Outcome == MovementDispatchOutcome.Confirmed)
        {
            runtime.SetBlockReason(null, timeProvider.GetUtcNow());
            runtime.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        // control-server#367: an order that ended before the create was ever confirmed is judged like a confirmed one.
        if (result.Outcome == MovementDispatchOutcome.TerminalReconciliationRequired &&
            await NameOrderEndedBeforeConfirmationAsync(runtime, upperId, reasonOnceMovedOn: null, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }
        // control-server#673: a create sent with its result unknown that RIoT never shows is recorded to be rebuilt, once
        // control-server#611's evidence is complete; until then the leg keeps {leg}_ResultUnknown, as before.
        if (result.Outcome == MovementDispatchOutcome.ResultUnknown &&
            await RecordOwnOrderNeverAppearedAsync(runtime, upperId, endedRebuild: null, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        runtime.SetBlockReason($"{legName}_{result.Outcome}", timeProvider.GetUtcNow());
        runtime.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Judges an ordinary leg's order that RIoT reported terminal before its create was ever confirmed, the way an order that was
    /// confirmed is judged, and says whether a code of its own now stands (control-server#367). False when the intent under
    /// <paramref name="upperId"/> is not terminal-reconciled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is needed.</b> A create whose answer was lost leaves the intent unconfirmed; the next reconciliation finds the
    /// order terminal and marks the intent <c>TERMINAL_RECONCILIATION_REQUIRED</c> with RIoT's orderId, whatever the terminal state
    /// was, and from then on <c>ReconcileOrCreateAsync</c> answers that without reading RIoT. The arrival branches stopped at the
    /// confirmation and wrote <c>{leg}_TerminalReconciliationRequired</c>, a code nothing ever cleared: a FAILED never reached the
    /// fault model -- no fault, no hold, no emergency stop, with the vehicle possibly still moving -- and a cancellation never
    /// reached REQ-0360's rebuild, so the journey could only be moved on by editing the database.
    /// </para>
    /// <para>
    /// <b>So it is handed to <see cref="NameStalledOrderAsync"/>, the same method a confirmed order goes through</b>, and ends the
    /// same way: FAILED goes to the fault model (the fault is then cleared through the controlled entry and the order rebuilt by
    /// REQ-0362 -- the reading the rebuild already gave its own orders, control-server#318 review S2, not a new rule); CANCELLED
    /// and DELETED are recorded to be rebuilt by REQ-0360 under REQ-0361's guards, unless this server cancelled the order itself;
    /// SUSPENDED (8, never observed in the lab) is named <see cref="OrderStateUnrecognizedReason"/> for a person. The rebuild then
    /// takes the stop over from the next round, ahead of the confirmation. Cargo proof before rebuilding a cancelled leg with cargo
    /// on board (REQ-0360 as revised by CP-0007) is control-server#366's, on top of this.
    /// </para>
    /// <para>
    /// <b>Every round, not once</b>, because the fault model's evaluation is what advances the stop proof, the escalation, the
    /// trigger's confirmation and REQ-0248's re-trigger. The order is read afresh each time; the intent stays terminal, so the
    /// confirmation keeps coming back here until a person clears the fault and the rebuild takes the stop over. A read that does
    /// not answer keeps whatever code of this family stands (<see cref="KeepsItsCodeWhileTheOrderIsUnread"/>), so an unreadable
    /// round does not write the confirmation's code over <see cref="VehicleFaultEvidence.OrderFailed"/> and restart its start time.
    /// </para>
    /// <para>
    /// Called on either side of the readiness gate: from <see cref="EnsureMovementConfirmedAsync"/> and from
    /// <see cref="NameUnconfirmedOrderWithoutThePeerAsync"/>. It reads RIoT and commands it through the fault model; it never sends
    /// to the onboard peer, and it never creates an order.
    /// </para>
    /// </remarks>
    private async Task<bool> NameOrderEndedBeforeConfirmationAsync(
        JourneyRuntimeRow runtime,
        string upperId,
        string? reasonOnceMovedOn,
        CancellationToken cancellationToken)
    {
        OrderIntentRow intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (intent is not { Status: "TERMINAL_RECONCILIATION_REQUIRED", OrderId: not null })
        {
            return false;
        }

        RiotOrderObservation order;
        try
        {
            order = await vehicleFacts.ReconcileByUpperIdAsync(upperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return KeepsItsCodeWhileTheOrderIsUnread(runtime.BlockReasonCode);
        }

        return await NameStalledOrderAsync(runtime, intent, order, cancellationToken, reasonOnceMovedOn).ConfigureAwait(false);
    }

    /// <summary>
    /// 车到了当前停靠：把这个停靠的车辆业务状态、清单、计划与录入请求发出去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 四条消息的 id、作业会话与站点全部取自停靠行；清单项与录入请求的期待子批取这个停靠上还没终结的需求
    /// （单需求时就是今天那一条）。方法名里的 Pickup 留着，因为今天只有取货停靠会走到这里——问录入是取货才有的事。
    /// </para>
    /// <para>
    /// <b>这一段可能从头重跑（control-server#331）。</b>它做完的标志是调用方随后那次阶段前移；断线打断在中间时阶段不动，
    /// 重连后整段再来一遍，而前半段车早已确认。四张因此都带 <c>keepAcknowledgedIgnoring</c>：车确认过的那一版，
    /// 除信封外一字不差就沿用它，不再入队。任何别的不同照旧交给重放校验去拒——那说明同一 messageId 下内容真的变了，
    /// 看板会显示 <see cref="AdvanceFailedReason"/>。
    /// </para>
    /// <para>
    /// <b>合法地变了的两样不走沿用，走新的一版</b>（control-server#339）。清单的期限会因 ADR-cross-0055 的断联重填而变：清单升一版
    /// （<see cref="AdvanceWorklistPastAStaleDeadlineAsync"/>），新号新 id。持货等单的旅程在断线窗口里装货阶段会变：那一变由
    /// <see cref="ReconcileLoadingPhaseAsync"/> 按新号发成一张装货阶段快照，这里就不再发到站那一张（<see cref="ArrivalBusinessStateSupersededAsync"/>）。
    /// cs#331 曾让清单沿用时忽略期限——那样车上留着旧期限，现场看到的不是服务端判定用的那一个，所以这一项不再忽略。
    /// </para>
    /// <para>
    /// <b>到站计划与录入请求也带，因为断线不止一次</b>（第三轮审查必修 1）。只断一次时它们是没确认的行，由每轮开头的补发按新的一代
    /// 改写，用不上沿用；断第二次时，上一代补发的计划可能已经被确认，下一代重跑时就撞上「已确认即拒」，与第一次断线时车辆业务状态
    /// 那一张是同一个形状。没确认的行不受影响：沿用只看已确认的行。
    /// </para>
    /// </remarks>
    private async Task PublishPickupStateAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        bool holdingApplicable,
        RiotVehicleObservation arrivedVehicle,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        if (!await ArrivalBusinessStateSupersededAsync(runtime, stop, cancellationToken).ConfigureAwait(false))
        {
            await FenceLoadingPhaseSnapshotSentOnTheWayAsync(runtime, stop, cancellationToken).ConfigureAwait(false);
            await ProjectBatteryStateForArrivalAsync(runtime, stop, arrivedVehicle, cancellationToken).ConfigureAwait(false);
            await publisher.PublishVehicleBusinessStateAsync(
                stop.VehicleBusinessMessageId,
                runtime.AgvId,
                session.SessionGeneration,
                TransportBusinessState(
                    StopRevision(runtime.VehicleBusinessRevision, stop),
                    CurrentLoadingPhase(runtime, holdingApplicable, stops),
                    PublishedBatteryState(runtime)),
                cancellationToken,
                keepAcknowledgedIgnoring: NothingButTheEnvelope).ConfigureAwait(false);
        }
        // ADR-cross-0055: the station departure wait starts at the arrival. Seeded ahead of the
        // worklist, whose save carries it, because the worklist is where the vehicle is told the
        // deadline -- a first snapshot sent before the seed would tell it there is none.
        runtime.StationDepartureWaitStartedAt ??= timeProvider.GetUtcNow();
        // 这一段重跑、而断联之后期限重填过时，前一次排给车的那一版清单带的是旧期限：升一版，清单与录入请求按新号发（control-server#339）。
        // 升过之后清单是新的 id，沿用无从谈起；没升时期限一致，清单与另三张一样只许信封不同。
        stops = await AdvanceWorklistPastAStaleDeadlineAsync(runtime, stops, cancellationToken).ConfigureAwait(false)
                ?? stops;
        await PublishStopWorklistAsync(
            runtime,
            stops,
            session,
            StationDepartureDeadlineAt(runtime, stop),
            cancellationToken,
            keepAcknowledgedIgnoring: NothingButTheEnvelope).ConfigureAwait(false);
        await RetireSupersededSnapshotAsync(PickupDispatchPlanMessageId(runtime), cancellationToken)
            .ConfigureAwait(false);
        // 车在路上收到的那张重发版（途中追加整体重发，号按「还没到站」算）同样被到站这一版取代（批次7-07 审查）。
        await RetireSupersededSnapshotAsync(ReSentPlanMessageId(runtime, stop, arrivedAtStop: false), cancellationToken)
            .ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            stop.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(
                runtime, stops.Stops, stop, arrivedAtCurrent: true,
                PlanRevisionAt(runtime.PlanRevision, stop, arrivedAtStop: true), IsStagingPickupLeg),
            cancellationToken,
            keepAcknowledgedIgnoring: NothingButTheEnvelope).ConfigureAwait(false);
        await PublishEntryRequestAsync(
            runtime, stops, session, cancellationToken, keepAcknowledgedIgnoring: NothingButTheEnvelope)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 当前停靠此刻这一版的清单（批次7-06，control-server#211）。到站时发第一版，此后每做完一条需求再发一版。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>清单与录入请求载的是同一个集合</b>——「本停靠还没做完的需求」——修订号取 <see cref="WorklistRevisionAt"/>。
    /// 集合少一条，号就加一，旧的那一版随 <c>expiresOnRevisionChange</c> 在车上作废。两条报文分成两个方法只是为了
    /// 保住线序（到站那一次，计划夹在它们中间），它们仍在同一次保存里落库。
    /// </para>
    /// <para>
    /// <b>旧的那一版在发件箱里也要退役。</b>重放按未确认的行补发，旧清单一旦被补发，车载端会把它当成修订号回退
    /// （<c>SNAPSHOT_REVISION_REGRESSION</c>）而断会话；旧录入请求被补发，则是一条内容已变的业务 id。
    /// </para>
    /// <para>
    /// <b>退役只打标记，与新的一版同一次保存</b>（<see cref="FenceSupersededSnapshotAsync"/>；入队那一步保存）。退役若自己先保存一次，
    /// 这个上下文里此前没保存的改动会被一起带进库——重填升版时就是升版的次数——而新的一版还没入队：崩在两次保存之间，
    /// 下一轮按新的号找不到排给车的那一版，再也认不出车上的期限是旧的（control-server#339，PR #353 审查 M1）。
    /// </para>
    /// <para>
    /// 一条待做的需求都没有时这里什么也不发：本停靠做完了，调用方接着往下一个停靠推，下一个停靠到站时发它自己那一版。
    /// 这是时机上的选择，不是协议不许——<b>这里曾写着空清单「违反 schema」，那是错的</b>（control-server#323）：<c>minItems: 1</c>
    /// 只在录入请求的 <c>expectedSublots</c> 上，<c>CurrentStopWorklistSnapshot.items</c> 没有下限，空清单是合法报文。
    /// 旅程收尾时的空清单就由 <see cref="JourneyClosure"/> 发；本站被期限、取消或补偿结束而旅程继续的那种（B 形态）由
    /// <see cref="StopEndWorklist"/> 发（control-server#324）。正常装完最后一条、车直接离站的那一刻仍然不发；离站等待开着、车还停在
    /// 取货停靠上而期限挪过时，由 <see cref="RefreshDeadlineOfALoadedStopAsync"/> 发一张带期限的空清单（control-server#633）。
    /// 本站一条被终结或放回、本站还有待做项时，下一轮由 <see cref="WorklistBehindAnEndingAtTheStopAsync"/> 认出来、在这里发（control-server#723）。
    /// </para>
    /// </remarks>
    private async Task PublishStopWorklistAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset? stationDepartureDeadlineAt,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null)
    {
        JourneyStopRow stop = stops.Current;
        IReadOnlyList<JourneyStopDemand> outstanding = stops.OutstandingAtCurrentStop;
        if (outstanding.Count == 0)
        {
            return;
        }

        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stop);
        long baseRevision = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        if (revision > baseRevision)
        {
            // 上一版：同一个停靠，偏移少一。
            await FenceSupersededSnapshotAsync(
                stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision - 1), cancellationToken)
                .ConfigureAwait(false);
        }

        await publisher.PublishCurrentStopWorklistAsync(
            stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision),
            runtime.AgvId,
            session.SessionGeneration,
            Worklist(stop, outstanding, revision, stationDepartureDeadlineAt),
            cancellationToken,
            keepAcknowledgedIgnoring).ConfigureAwait(false);
    }

    /// <summary>
    /// 到站那一段重跑时，四张报文沿用车已确认那一版的条件：只有信封的代次与发送时间可以不同（control-server#331）。
    /// 清单的期限重填过时它已经是新的一版、新的 id，不在沿用之列（control-server#339）。
    /// </summary>
    private static readonly IReadOnlySet<string> NothingButTheEnvelope = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>取货停靠此刻这一版的录入请求，期待子批就是清单那一版列的那些。</summary>
    /// <remarks>
    /// 上一张的退役在这里仍自己保存一次，与清单那一侧不同：崩在它与这一张入队之间，留下的是「这一版清单已排、录入请求没有」，
    /// 等录入那一段按 <see cref="EntryRequestMissingBehindTheWorklistAsync"/> 补发，不会停住。
    /// </remarks>
    private async Task PublishEntryRequestAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null)
    {
        JourneyStopRow stop = stops.Current;
        IReadOnlyList<JourneyStopDemand> outstanding = stops.OutstandingAtCurrentStop;
        if (outstanding.Count == 0)
        {
            return;
        }

        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stop);
        long baseRevision = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        if (revision > baseRevision)
        {
            await RetireSupersededSnapshotAsync(
                stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, revision - 1), cancellationToken)
                .ConfigureAwait(false);
        }

        await publisher.PublishSublotEntryRequestAsync(
            stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, revision),
            runtime.AgvId,
            session.SessionGeneration,
            new SublotEntryRequest(
                stop.OperationSessionId,
                stop.StationId,
                revision,
                [.. outstanding.Select(item => item.Demand.Sublot)]),
            cancellationToken,
            keepAcknowledgedIgnoring).ConfigureAwait(false);
    }

    /// <summary>
    /// 本停靠此刻这一版清单已经排给了车，而它带的离站期限不是服务端此刻判定用的那一个：清单升一版，返回升版之后的游标；
    /// 期限一致（或这一版还没排给车）返回空（control-server#339）。只升号，不发——发哪几条由调用方按所在阶段定。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么会不一致。</b>断联那一轮作废离站等待，恢复之后从此刻重填（ADR-cross-0055），而车手上那一版清单是断联之前发的。
    /// 车载端（<c>w2g/fp-v2-impl</c>）不作废也不重新计满期限，永远照最新一版清单上的期限显示；只把服务端这一侧重填，
    /// 现场看到的就不是服务端判定用的那一个——车上已显示「已到期」，服务端却刚重新计满。
    /// </para>
    /// <para>
    /// <b>为什么是升一版，而不是同号重发。</b>车按消息类型与修订号采纳清单，同号不同内容按 <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c>
    /// 拒收；发件箱这一侧，同一 messageId 下内容变了由重放校验拒绝。两道护栏都不放宽：内容变了就是新的一版，新的号、新的 id
    /// （<see cref="JourneyStopRow.WorklistRefills"/>，<see cref="JourneyStopCursor.WorklistRevisionAt"/>）。
    /// </para>
    /// <para>
    /// <b>判据是「排给车的那一版与服务端此刻的期限是否一致」，不是「这一轮重填了」。</b>后者只在重填那一轮成立一次：那一轮若从别的出口
    /// 保存了重填（例如取消开着），之后每一轮都读不到它，车上的期限就永远是旧的。按一致与否判，哪一轮都能对上。
    /// 期限开关关着时两边都是空，永远一致，不升号——修订号序列与之前逐字相同。
    /// </para>
    /// </remarks>
    private async Task<JourneyStopCursor?> AdvanceWorklistPastAStaleDeadlineAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        if (stops.OutstandingAtCurrentStop.Count == 0)
        {
            return null;
        }

        string messageId = stops.WorklistMessageIdAt(
            runtime.WorklistRevision, stop, stops.WorklistRevisionAt(runtime.WorklistRevision, stop));
        string? queued = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageId == messageId)
            .Select(row => row.PayloadJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (queued is null)
        {
            return null;
        }

        // 这一行是本服务端自己写的清单，字段按构造一定在；缺了就当期限为空，与「期限开关关着」同样处理——不会因此升版。
        DateTimeOffset? sent = null;
        using (JsonDocument document = JsonDocument.Parse(queued))
        {
            if (document.RootElement.TryGetProperty("payload", out JsonElement payload) &&
                payload.TryGetProperty("stationDepartureDeadlineAt", out JsonElement deadline) &&
                deadline.ValueKind != JsonValueKind.Null)
            {
                sent = deadline.GetDateTimeOffset();
            }
        }
        if (sent == StationDepartureDeadlineAt(runtime, stops.Current))
        {
            return null;
        }

        // 只在这个上下文里改，不保存：旧的那一版由 PublishStopWorklistAsync 退役，升版的次数、退役与新的一版在入队那一次保存里
        // 一起落库。崩在那之前就什么都没落库，下一轮照样认出车上的期限是旧的、照样升版（PR #353 审查 M1）。
        JourneyStopRow tracked = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == stop.StopId, cancellationToken).ConfigureAwait(false);
        tracked.WorklistRefills += 1;
        return await JourneyStopCursor.LoadIncludingUnsavedChangesAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 取货停靠的需求都装完了、车还停在这里等离站时，车上那一版清单带的期限不是服务端此刻判定用的那一个：升一版，发一张空清单把期限
    /// 送到车上（control-server#633）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么会不一致。</b>最后一批落定时离站等待的起点重置为此刻（ADR-cross-0054 的纠正时间），纠错闭环时起点挪到闭环那一刻
    /// （<see cref="StationDepartureWaitIsOverAsync"/>）。两处都在本站已无待做项之后，<see cref="PublishStopWorklistAsync"/> 在那时什么也不发，
    /// 车上留着的是装最后一条之前那一版，期限是旧的。车载端永远照最新一版清单上的期限显示（<see cref="AdvanceWorklistPastAStaleDeadlineAsync"/>
    /// 的同一个理由），于是装得慢的一站车上已显示「已到期」，服务端的纠正时间却还开着。
    /// </para>
    /// <para>
    /// <b>为什么是空清单、为什么是 COMPLETED。</b>清单载的是本站还没做完的需求，此刻一条都没有；v3 schema 规定没有项时
    /// <c>stopEndedReason</c> 必填，本站的待做项是装完的，取 <c>COMPLETED</c>。作业会话照写本站那一个、期限照写服务端那一个：
    /// 本站作业会话仍按离站期限结束（REQ-0355），结束本站不等于车辆离开。号数与 <see cref="StopEndWorklist"/> 同一个做法——用本停靠
    /// 此刻的「下一号」并让本停靠多占一版，后面停靠的首号跟着顺延。
    /// </para>
    /// <para>
    /// <b>判据与 <see cref="AdvanceWorklistPastAStaleDeadlineAsync"/> 同一个：排给车的那一版与服务端此刻的期限是否一致。</b>期限开关关着时
    /// 两边都是空，永远一致，一版也不多发。车上那一版的作业会话为空——本站被期限、取消或补偿结束（<see cref="StopEndWorklist"/>）——时，
    /// 那张空清单说的是本站为什么结束，不能拿 COMPLETED 盖掉它：补发的这一版照留它的原因与空会话，只补期限，而且只在期限还没到时补
    /// （control-server#668：扫码前取消、装货途中被终结之后车仍在离站等待，车上原本一直是「无倒计时」）。卸货停靠没有离站期限，不在此列。
    /// </para>
    /// </remarks>
    private async Task RefreshDeadlineOfALoadedStopAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (stops.OpenStops.Count == 0)
        {
            return;
        }
        JourneyStopRow stop = stops.Current;
        // 本站已由操作员按出发结束（control-server#725）：车马上走，不再为期限补一版清单。车载端把「已受理」绑在这一站的计划腿上，
        // 腿离开 ARRIVED 之前再来一版清单只会让屏上那张清单在离站前多翻一次（hmi#352 审查转来）。
        if (stop.StopRole != JourneyStopRoles.Pickup || stops.OutstandingAtCurrentStop.Count > 0 || stop.OperatorDepartedAt is not null)
        {
            return;
        }

        long first = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        long formula = stops.WorklistRevisionAt(runtime.WorklistRevision, stop);
        if (formula <= first)
        {
            return;
        }
        // 车上的是本停靠此刻「下一号」的前一号：装完最后一条之前那一版、或本方法上一次发的那张空清单。
        string onVehicle = stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, formula - 1);
        string? queued = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageId == onVehicle)
            .Select(row => row.PayloadJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (queued is null)
        {
            return;
        }

        string? sentSession = null;
        string? sentReason = null;
        DateTimeOffset? sent = null;
        using (JsonDocument document = JsonDocument.Parse(queued))
        {
            if (document.RootElement.TryGetProperty("payload", out JsonElement payload))
            {
                if (payload.TryGetProperty("operationSessionId", out JsonElement session0) &&
                    session0.ValueKind == JsonValueKind.String)
                {
                    sentSession = session0.GetString();
                }
                if (payload.TryGetProperty("stopEndedReason", out JsonElement reason0) &&
                    reason0.ValueKind == JsonValueKind.String)
                {
                    sentReason = reason0.GetString();
                }
                if (payload.TryGetProperty("stationDepartureDeadlineAt", out JsonElement deadline0) &&
                    deadline0.ValueKind != JsonValueKind.Null)
                {
                    sent = deadline0.GetDateTimeOffset();
                }
            }
        }
        // 与离站判定读同一个时刻：同站追加提前结束时，车上倒计时就是车真正要走的那一刻（control-server#646）。
        DateTimeOffset? deadline = EffectiveStationDepartureDeadline(runtime, stops);
        if (sent == deadline)
        {
            return;
        }
        // 车上那一版是本站被期限、取消或补偿结束时的空清单（StopEndWorklist，作业会话为空）：它说的是本站为什么结束，原因与空会话照留，
        // 只补上期限（control-server#668）。扫码前取消、装货途中被终结之后车仍在离站等待里，不补，车上一直是「无倒计时」，车却要等到期限才走。
        // 车上那一版本来就没有期限、而此刻的期限也已过时不补：被期限结束的那一种车当轮就走，补一个已到期的时刻只多一版、什么也不告诉操作员。
        // 车上已经有期限（上面补过一版）而期限被提前到过去时照补——同站追加让离站提前到已过的时刻，车当轮就走，屏上不能还剩着倒计时
        // （PR #674 审查 M1）；与会话不为空那一支同一个结果，屏上显示「已到期，等待本站结束」。
        if (sentSession is null && sent is null && (deadline is not { } ahead || now >= ahead))
        {
            return;
        }

        // 与 StopEndWorklist 同一道保险：算式万一算低了，也不发一个车上已有的号。
        long revision = Math.Max(
            formula,
            (await JourneyClosure.HighestSentRevisionAsync(
                    dbContext, runtime.AgvId, "CurrentStopWorklistSnapshot", cancellationToken)
                .ConfigureAwait(false) ?? 0) + 1);
        // 本停靠此前发给车的每一版都退役，补发不能把车上的期限拨回去。只打标记，随下面入队那一次保存落库。
        for (long earlier = first; earlier < formula; earlier++)
        {
            await FenceSupersededSnapshotAsync(
                    stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, earlier), cancellationToken)
                .ConfigureAwait(false);
        }
        JourneyStopRow tracked = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == stop.StopId, cancellationToken).ConfigureAwait(false);
        tracked.WorklistRefills += revision - formula + 1;
        runtime.UpdatedAt = now;

        await publisher.PublishCurrentStopWorklistAsync(
            stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision),
            runtime.AgvId,
            session.SessionGeneration,
            sentSession is null
                ? new CurrentStopWorklistProjection(
                    stop.StationId, revision, null, deadline, [], sentReason ?? StopEndedReasons.ForEnding(null))
                : new CurrentStopWorklistProjection(
                    stop.StationId, revision, stop.OperationSessionId, deadline, [], StopEndedReasons.ForEnding(null)),
            cancellationToken).ConfigureAwait(false);
        LogLoadedStopDeadlineRefreshed(logger, runtime.AgvId, stop.StationId, sent, deadline, revision, null);
    }

    /// <summary>
    /// 本停靠此刻这一版清单已经排给了车，同一版的录入请求却不在发件箱里（control-server#339）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 等录入时重填升版，清单与录入请求是两条报文、两次保存。崩在两次之间，清单那一侧已经对上了服务端的期限，
    /// <see cref="AdvanceWorklistPastAStaleDeadlineAsync"/> 认不出任何事；而车在会话离开 <c>Ready</c> 时已经清掉了手上的录入请求
    /// （车载端 <c>WireToGateBusinessService.OnSessionStateChanged</c>），不补发，这一站操作员就没有可答的请求。
    /// </para>
    /// <para>
    /// 正常推进里不会出现这种样子：进入等录入的两处（到站、上一条装完还有下一条）都是清单与录入请求一起发，等录入期间号只因重填而变，
    /// 而重填同样两条一起发。装货中重填只发清单，但那时阶段不是等录入，下一次回到等录入之前号必然随装完的那一条再升一次。
    /// </para>
    /// </remarks>
    private async Task<bool> EntryRequestMissingBehindTheWorklistAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        if (stops.OutstandingAtCurrentStop.Count == 0)
        {
            return false;
        }

        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stop);
        string worklistId = stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision);
        string entryId = stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, revision);
        List<string> queued = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageId == worklistId || row.MessageId == entryId)
            .Select(row => row.MessageId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return queued.Contains(worklistId) && !queued.Contains(entryId);
    }

    /// <summary>
    /// 本停靠此刻这一版清单还没排给车，而本站已经发过更早的一版、也还有待做项：本站有一条在这期间被终结或放回了（control-server#723）。
    /// 返回真时，本站较早各版清单与录入请求已在这个上下文里退役（只打标记），调用方按此刻的号发清单（等录入时连同录入请求），随那一次保存落库。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么会落后。</b>号按 <see cref="JourneyStopCursor.WorklistRevisionAt"/> 算，被终结（以及放回时补的版数）也算本站做完一条，号随终结
    /// 那一次保存就前进了。但终结那一路（<c>OnboardRecoveryCoordinator</c> → <see cref="PickupStopTermination"/>）只在本站因此结束时
    /// 由 <see cref="StopEndWorklist"/> 发一张空清单；本站还有别的待做项时，谁也不发。引擎这边，等录入只在期限变了时重发清单，在装时也一样。
    /// 车上于是一直留着旧的那一版：屏上仍列着已取消的需求，录入请求仍期待它的子批（10-15 生产 agv02，操作员重按取消被判
    /// <c>BUSINESS_ID_CONTENT_CONFLICT</c>）。07:12:54 那次按期限放回之所以刷新了，是因为它结束了整个停靠、车「到达」同站的下一个停靠，
    /// 到站那一段发的是下一个停靠的首版——不是放回本身发的。
    /// </para>
    /// <para>
    /// <b>判据是「此刻那一版在不在发件箱里」</b>，与 <see cref="AdvanceWorklistPastAStaleDeadlineAsync"/> 同一个思路：不认来路，取消、管理员终结、
    /// 放回都一样；崩在终结之后、补发之前，下一轮照样认得出。正常推进里这一版总在：到站、装完一条、期限重填都是在升号的同一次保存里把它排上，
    /// 本站第一版（<paramref name="stops"/> 首号）由到站发，不归这里。
    /// </para>
    /// <para>
    /// <b>较早各版全部退役，不只上一号。</b>两次终结落在同一轮之间时号跳过一号，车上的是再早一版；只退上一号，车上那一版留着未确认，
    /// 重连补发时排在新的一版之后送到，车判修订号回退而断会话（<see cref="StopEndWorklist"/> 同一个理由）。
    /// </para>
    /// </remarks>
    private async Task<bool> WorklistBehindAnEndingAtTheStopAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        if (stops.OutstandingAtCurrentStop.Count == 0)
        {
            return false;
        }

        long first = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stop);
        string worklistId = stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision);
        if (await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == worklistId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        for (long earlier = first; earlier < revision; earlier++)
        {
            await FenceSupersededSnapshotAsync(
                stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, earlier), cancellationToken).ConfigureAwait(false);
            await FenceSupersededSnapshotAsync(
                stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, earlier), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// CV-DEMAND-ACCEPT-TO-PICKUP's first snapshot: the plan as it stands once the TO_PICKUP order
    /// exists and before the vehicle is at the pickup -- the pickup leg ACTIVE, the drop-off leg
    /// PLANNED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Published once, then left to the replay.</b> Every iteration of this stage passes through
    /// here, and publishing sends whatever is unacknowledged, so calling the publisher each time
    /// would put the same line on the wire once per poll. The replay at the top of
    /// <see cref="AdvanceAsync"/> already re-sends an unacknowledged row, which is what a lost
    /// acknowledgement needs.
    /// </para>
    /// <para>
    /// <b>Not before the order is confirmed.</b> The vector's checkpoints put the TO_PICKUP intent
    /// ahead of the RIoT call and the projection revision ahead of the send; a plan naming a leg
    /// nothing has dispatched would project a journey that is not committed.
    /// </para>
    /// <para>
    /// Until 2026-09-13 the server sent no plan before the arrival. Neither G2 noticed: this side's
    /// test compared the outbox as a sorted set of types, and the onboard side's is written against
    /// a fake server that sends the vector's order.
    /// </para>
    /// </remarks>
    private async Task PublishPickupDispatchPlanOnceAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        string messageId = PickupDispatchPlanMessageId(runtime);
        if (await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // 未就绪那条路径（control-server#314）不经过本轮推进开头的认待送点，所以这里自己认一次；开关关着时什么也不读。
        await RecognizeStagingPickupStopsAsync(stops, cancellationToken).ConfigureAwait(false);
        // 车还在路上，所以当前停靠这条腿是 ACTIVE 而不是 ARRIVED。
        await publisher.PublishUpcomingStopPlanAsync(
            messageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(
                runtime, stops.Stops, stops.Current, arrivedAtCurrent: false,
                PlanRevisionAt(runtime.PlanRevision, stops.Current, arrivedAtStop: false), IsStagingPickupLeg),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 会话只因本服务端自己的在途单而未就绪时，仍把派往取货站的那一版计划送到车上（control-server#314）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么闸门外还要发这一版。</b>取货那一段的 RIoT 单在受理那一轮就建成、确认，这一版计划却要等下一轮推进才发。
    /// 车上一挂着本服务端的未结束单，车辆安全接口就回 <c>motionState=Unknown</c>（control-server#138，按设计保留），
    /// 车载端报 <c>VEHICLE_NOT_READY</c>，会话落到 <c>DEPARTURE_SAFETY_NOT_READY</c>——而那张单不结束它就回不到 <c>Ready</c>。
    /// 所以车载端只要在两轮之间读一次安全接口，这一版计划就要等到站才能发：车在路上时车载端不知道这趟要去哪，
    /// G3 里则是死锁（装置等计划确认才让车走）。批次 7 出口的 <c>g3-task-type-admission-fail-closed</c> 撞上的就是这个窗口，约 200 毫秒。
    /// </para>
    /// <para>
    /// <b>放行的只有这一版，其余照旧挡在闸门后面。</b>它是一张「车要去哪」的投影，不推进任何停靠、不开锁、不建单；
    /// 车载端对行程快照不看自己的就绪状态，照收照确认（8005-agv-onboard-hmi <c>WireToGateSessionClient</c> 的接收循环
    /// 在判就绪之前就把三种行程快照交给 <c>ApplyJourneySnapshotAsync</c>）。
    /// </para>
    /// <para>
    /// <b>条件一条都不能少，每一条都按失败关闭。</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// 会话的原因码是 <c>DEPARTURE_SAFETY_NOT_READY</c>，安全原因只有车本身（<see cref="Dashboard.OwnMovementOrderExplanation"/>，
    /// 与阻塞看板 control-server#139 用同一个判据）。这个原因码在 <c>GetRecoveryReason</c> 里排在能力快照、安全快照、恢复报告、
    /// 强制恢复代次、待对账事实<b>之后</b>，所以它说明这五样都已具备：握手已经完成、对账已经结清（control-server#259 的「握手完成前
    /// 不下发」由此承担一半，另一半是 <c>OnboardPeer</c> 只路由握手完成的连接）。
    /// </description></item>
    /// <item><description>
    /// <b>但它说明不了「只剩出发安全这一个原因」。</b>同一个函数把出发安全排在作业待恢复（<c>OPERATION_RECOVERY_REQUIRED</c>）与
    /// 强制恢复待硬件记录<b>之前</b>，这两者与出发不安全同时成立时，原因码只写出发安全。挡它们的不是原因码，是这里再问一次
    /// <see cref="WireToGateStore.OperationNeedsRecoveryAsync"/> 与 <see cref="WireToGateStore.ForcedRecoveryAwaitsHardwareRecordAsync"/>——
    /// 与 <c>DecideReadinessAsync</c> 用的是同一个查询。<see cref="TryBlockOnRecordedRecoveryAsync"/> 帮不上：它只在等装货、
    /// 卸货结果的两个阶段把旅程挪去 <c>Blocked</c>，这里的阶段是 <c>AwaitingPickupArrival</c>；而强制恢复可以在车在路上、
    /// 会话正是 <c>RECOVERY_REQUIRED</c> 时由管理员发起。
    /// </description></item>
    /// <item><description>
    /// 当前停靠那张单是本服务端建的、已确认、落在这辆车上，而车没有挂着故障——否则「未知」可能另有来源。
    /// 与 <see cref="PublishPickupDispatchPlanOnceAsync"/> 的「不在单确认之前」是同一条线，这里只读库、不问 RIoT。
    /// </description></item>
    /// <item><description>
    /// 这辆车没有被外来订单挡着（control-server#330）：RIoT 的车辆安全读取对车上任何未终结的单都报
    /// <c>RIOT_NONFINAL_ORDER_PRESENT</c>，外来单同样造成这个「未知」，不能归给自己的单。
    /// </description></item>
    /// <item><description>
    /// 这一代会话听得到（<see cref="SessionLiveness"/>）。听不到的车，发送会在 <c>OnboardPeer</c> 抛异常，
    /// 每轮一次——control-server#234 刚消掉的那种刷屏。
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>恰好一次。</b>发件箱行与就绪那条路是同一行：id 是 <see cref="PickupDispatchPlanMessageId"/>（锚需求或改派旅程的
    /// <c>StableGuid</c>），<see cref="PublishPickupDispatchPlanOnceAsync"/> 见到已有这一行就不再写。会话回到 <c>Ready</c> 之后，
    /// 顶上的补发只补没确认的行——车确认过就一次也不再发。
    /// </para>
    /// <para>
    /// <b>重连。</b>这里也先补发这一行（只这一行），和就绪那条路的顺序一样。车断了又连上、仍因自己的单未就绪时，
    /// 补发把它的代次改写成新的一代（<see cref="OnboardJourneyPublisher.ReplayPendingForSessionAsync"/>）；新一代还在握手时，
    /// 原因码不是 <c>DEPARTURE_SAFETY_NOT_READY</c>，这里一行都不发。会话行的代次若落后于连接的代次，
    /// <c>OnboardPeer</c> 拒收，这一行留在发件箱等下一轮改写——与 control-server#309 的代次比对是同一条路。
    /// </para>
    /// </remarks>
    private async Task PublishPickupDispatchPlanPastOwnOrderAsync(
        JourneyRuntimeRow runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtime.Stage != JourneyRuntimeStage.AwaitingPickupArrival)
        {
            return;
        }
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        bool ownOrderInFlight = await OwnMovementOrderInFlightAsync(runtime, stops.Current.UpperId, cancellationToken)
            .ConfigureAwait(false);
        // control-server#330：同一辆车上还挂着一张外来订单时，「未知」可能是它造成的，放行不适用。
        bool foreignOrderHoldsVehicle = (await ForeignOrders.ForeignRunningOrders
                .HeldAgvIdsAsync(dbContext, cancellationToken).ConfigureAwait(false))
            .Contains(runtime.AgvId);
        if (!Dashboard.OwnMovementOrderExplanation.Explains(
                runtime.BlockReasonCode,
                session.ReasonCode,
                session.SafetyReasonCodesJson,
                session.SafetyUnknownPresent,
                ownOrderInFlight,
                foreignOrderHoldsVehicle))
        {
            return;
        }
        // 原因码把这两样藏在出发安全后面（见上面的注释），所以单独问。
        if (await store.OperationNeedsRecoveryAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false) ||
            await store.ForcedRecoveryAwaitsHardwareRecordAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        if (!await SessionLiveness.HeardFromAsync(
                dbContext, runtime.AgvId, session.SessionGeneration, now, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await publisher.ReplayPendingForSessionAsync(
            runtime.AgvId,
            session.SessionGeneration,
            new HashSet<string>(StringComparer.Ordinal) { PickupDispatchPlanMessageId(runtime) },
            cancellationToken).ConfigureAwait(false);
        await PublishPickupDispatchPlanOnceAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 这一段移动的 RIoT 单是本服务端建的、已确认、落在这辆车上，而车没有挂着故障（control-server#314）。
    /// </summary>
    /// <remarks>
    /// 与阻塞看板判「自己的在途单」同一组条件（<c>BlockedJourneysQueryEndpoint.OwnMovementOrdersInFlightAsync</c>），
    /// 但按当前停靠的单号问，而不是旅程行上锚需求那一段的：第二个取货停靠用锚需求的单号，会拿一段早已走完的移动当证据。
    /// </remarks>
    private async Task<bool> OwnMovementOrderInFlightAsync(
        JourneyRuntimeRow runtime,
        string upperId,
        CancellationToken cancellationToken)
    {
        bool confirmed = await dbContext.OrderIntents.AsNoTracking()
            .AnyAsync(
                row => row.UpperId == upperId &&
                       row.Status == "CONFIRMED" &&
                       row.OrderId != null && row.OrderId != "" &&
                       row.VehicleKey == runtime.VehicleKey,
                cancellationToken)
            .ConfigureAwait(false);
        return confirmed &&
               !await dbContext.VehicleFaultStates.AsNoTracking()
                   .AnyAsync(
                       row => row.AgvId == runtime.AgvId && row.Level != VehicleFaultLevel.None,
                       cancellationToken)
                   .ConfigureAwait(false);
    }

    /// <summary>
    /// 车上那张计划与此刻的停靠序列不一致时，发一张新的（ADR-cross-0053，批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判据是内容，不是某个计数器。</b>途中追加在序列中间插进两个停靠，而当前停靠的序位没变——按序位算出来的
    /// 计划修订号于是一字未动，靠它看不出该重发。所以这里把「现在应该是什么样」与「发件箱里最后发出去的那一张」
    /// 逐条比：腿的身份、序位、站点、状态，任一条不同就换一版。
    /// </para>
    /// <para>
    /// <b>到站那一次不走这里。</b>到站本来就要发一张新的计划，序位一到就换号；这里管的是两次到站<b>之间</b>计划被
    /// 改写的那种情形，今天只有途中追加一种。没有可比的那一张（这个停靠还没发过计划）时什么也不做。
    /// </para>
    /// </remarks>
    private async Task RefreshUpcomingStopPlanAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        if (runtime.Stage is JourneyRuntimeStage.Completed or JourneyRuntimeStage.Blocked)
        {
            return;
        }

        // 只有被追加过的旅程才需要整体重发，而追加必然带来第二条需求。单需求旅程——今天现场跑的全部——
        // 因此一步都不进这一段：既省掉每个 tick 读一次发件箱，也让这次改动在单需求那条路上完全不执行。
        // 数的是「曾经有过」而不是「此刻挂着」（批次7-10，control-server#215）：释放改派移走一条需求、删掉它的停靠之后，
        // 此刻只剩一条，而计划恰恰刚被改过。前提「归属只标移除、不删行」由 JourneyStopCursor.EverCarriedMoreThanOneDemand 承担。
        if (!stops.EverCarriedMoreThanOneDemand)
        {
            return;
        }

        JourneyStopRow stop = stops.Current;
        bool arrived = runtime.Stage is not
            (JourneyRuntimeStage.AwaitingPickupArrival or JourneyRuntimeStage.AwaitingGateArrival);
        if (await LastSentPlanAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false) is not { } last)
        {
            return;
        }

        UpcomingStopPlanProjection current = JourneyPlanBuilder.Plan(
            runtime, stops.Stops, stop, arrived, last.Revision, IsStagingPickupLeg);
        if (PlanOnTheWireMatches(last.PayloadJson, current))
        {
            return;
        }

        long revision = last.Revision + 1;
        // 重发把计划流的基准一并抬高，好让后面每一次到站按序位算出来的号仍然落在这一号之上
        // （批次7-06，control-server#211）。
        //
        // <b>两条算式必须互相钳制，否则它们各走各的。</b>到站发的是「基准 + 序位」，重发发的是「上一号 + 1」：
        // 基准不动的话，一次追加就让下一次到站算出与这一号相同的号——而内容不同——两次追加算出的号还会比它低。
        // 车载端按消息类型记修订号，号同内容不同是 SNAPSHOT_REVISION_CONTENT_CONFLICT，号更低是
        // SNAPSHOT_REVISION_REGRESSION，两种都当场拆会话。
        //
        // 抬高的量正好让 PlanRevisionAt(新基准, 当前停靠, arrived) 等于这一号：此后的序位差原样成立，
        // 而不是在算式里塞一个「已经重发过几次」的偏移量。
        runtime.PlanRevision = revision - (arrived ? stop.Sequence : stop.Sequence - 1);
        await RetireSupersededSnapshotAsync(last.MessageId, cancellationToken).ConfigureAwait(false);
        // 基准抬过之后 PlanRevisionAt(基准, 停靠, arrived) 恰好等于 revision，所以这里与补发集合、到站退役取的是同一个 id。
        await publisher.PublishUpcomingStopPlanAsync(
            ReSentPlanMessageId(runtime, stop, arrived),
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(runtime, stops.Stops, stop, arrived, revision, IsStagingPickupLeg),
            cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这辆车最后收到的那一版计划：发件箱里最新的一条 <c>UpcomingStopPlanSnapshot</c>。</summary>
    /// <remarks>
    /// <para>
    /// <b>从发件箱读，而不是把消息 id 推算出来。</b>一张计划的第一版由三个地方发出，各有各的 id 来源——
    /// 派往取货站那一版按锚需求算（<c>PickupDispatchPlanMessageId</c>），到站那两版按停靠行上的
    /// <c>PlanMessageId</c>。推算要把这三种来源在这里再写一遍，而写漏一种的表现是「最后一版找不到，于是不重发」：
    /// 静默地什么都不做，正是最难发现的那种错。实际写漏过一次，同区追加那条 L2 场景跑出来才看见。
    /// </para>
    /// <para>
    /// 按载荷里的 <c>agvId</c> 筛，因为发件箱行本身没有这一列。整表读回来在内存里筛看着粗，但这个方法只在
    /// 被追加过的旅程上调用（调用处第一道判断），而那在今天的现场是零。
    /// </para>
    /// </remarks>
    private async Task<(long Revision, string MessageId, string PayloadJson)?> LastSentPlanAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        (long Revision, string MessageId, string PayloadJson)? last = null;
        DateTimeOffset latest = DateTimeOffset.MinValue;
        foreach (ProtocolOutboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement envelope = document.RootElement;
            if (RequiredString(envelope, "agvId") != agvId)
            {
                continue;
            }

            if (row.CreatedAt < latest ||
                (row.CreatedAt == latest && last is { } seen &&
                 string.CompareOrdinal(row.MessageId, seen.MessageId) <= 0))
            {
                continue;
            }

            latest = row.CreatedAt;
            last = (
                envelope.GetProperty("payload").GetProperty("planRevision").GetInt64(),
                row.MessageId,
                row.PayloadJson);
        }

        return last;
    }

    /// <summary>车上那一版的腿，与现在应该发的那一份，是不是同一串。</summary>
    /// <remarks>
    /// 比的是线上那一份（<see cref="OnboardJourneyPublisher.WireLegs"/>：排好序、<c>sequence</c> 从 1 重新编号），不是投影原样——
    /// 停靠序号有空档时两者不同，按投影比会把车上那一版判成「变了」而白白重发一次（control-server#664）。
    /// </remarks>
    internal static bool PlanOnTheWireMatches(string payloadJson, UpcomingStopPlanProjection current)
    {
        IReadOnlyList<UpcomingMovementLeg> wire = OnboardJourneyPublisher.WireLegs(current);
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        JsonElement legs = document.RootElement.GetProperty("payload").GetProperty("legs");
        if (legs.GetArrayLength() != wire.Count)
        {
            return false;
        }

        int index = 0;
        foreach (JsonElement leg in legs.EnumerateArray())
        {
            UpcomingMovementLeg expected = wire[index++];
            if (RequiredString(leg, "movementLegId") != expected.MovementLegId ||
                leg.GetProperty("sequence").GetInt32() != expected.Sequence ||
                RequiredString(leg, "stationId") != expected.StationId ||
                RequiredString(leg, "state") != expected.State)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Retires a snapshot that the next revision of its stream supersedes, if it is still waiting
    /// for an acknowledgement.
    /// </summary>
    /// <remarks>
    /// The replay sends unacknowledged rows oldest first, and the vehicle refuses a snapshot below the
    /// revision it has adopted as SNAPSHOT_REVISION_REGRESSION, tearing the session down. The plan
    /// sent before the arrival is the one row that can fall behind like that without the journey
    /// having needed a message from the vehicle in between, so the pickup retires it before
    /// publishing the plan that replaces it. A snapshot states what is current, so nothing the
    /// vehicle needs is lost: it adopts the higher revision directly.
    /// </remarks>
    private async Task RetireSupersededSnapshotAsync(string messageId, CancellationToken cancellationToken)
    {
        ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(
                item => item.MessageId == messageId && item.AcknowledgedAt == null && item.FencedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.FencedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 对录入所答复的那条需求下装货命令：只落库，调用方提交之后再发（control-server#362）。attempt id、命令 id 与目标仓位取
    /// <b>它自己</b>在本旅程的归属行，作业会话与站点取当前停靠。
    /// </summary>
    /// <remarks>
    /// <paramref name="entered"/> 是操作员扫出来的那一条，不是锚需求（批次7-06，control-server#211）。批次7-03 取锚需求，
    /// 是因为那时录入范围被一句守卫退回了锚需求；守卫删掉之后再取锚需求，就是「操作员扫第二条、车上收到第一条的仓位」。
    /// </remarks>
    private async Task StageLoadAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JourneyStopDemand entered,
        SessionRecoveryRow session,
        string correlationId,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        int[] slots = JsonSerializer.Deserialize<int[]>(entered.Membership.TargetSlotsJson) ?? [];
        string hash = BusinessHash(entered.Demand.DemandId, entered.Demand.Sublot, "LOAD", slots);
        // The load carries the admission only where the AREA machine is the pickup (WIRE_TO_GATE); STAGING_TO_WIRE's
        // is carried by the unload at the machine (I6 overturned, scope specification 21.2 item 2).
        bool admission = await store.AreaEndOperationAsync(
                entered.Demand.DemandId, entered.Demand.WorkType, cancellationToken)
            .ConfigureAwait(false) == SlotOperationType.Load;
        await publisher.StageSlotOperationCommandAsync(
            entered.Membership.LoadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                correlationId,
                entered.Demand.DemandId,
                entered.Demand.Sublot,
                stop.OperationSessionId,
                entered.Membership.LoadSlotOperationAttemptId,
                SlotOperationType.Load,
                slots,
                session.ForcedRecoveryGeneration,
                hash),
            cancellationToken,
            admission ? stop.StationId : null,
            admission ? entered.Demand.WorkType : null).ConfigureAwait(false);
    }

    /// <summary>
    /// 车到了卸货停靠：发这个停靠的三条快照，再对本停靠该卸的第一条需求下卸货命令。
    /// </summary>
    /// <remarks>
    /// 清单与卸货命令都随本停靠的进度走（批次7-06，control-server#211）：清单列的是还没卸的，命令发给
    /// <see cref="JourneyStopCursor.NextToUnloadAtCurrentStopAsync"/>。单需求下这两者都只有那一条，与之前逐字相同。
    /// </remarks>
    private async Task PublishGateStateAndUnloadAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        bool holdingApplicable,
        RiotVehicleObservation arrivedVehicle,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        await FenceLoadingPhaseSnapshotSentOnTheWayAsync(runtime, stop, cancellationToken).ConfigureAwait(false);
        await ProjectBatteryStateForArrivalAsync(runtime, stop, arrivedVehicle, cancellationToken).ConfigureAwait(false);
        await publisher.PublishVehicleBusinessStateAsync(
            stop.VehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            TransportBusinessState(
                StopRevision(runtime.VehicleBusinessRevision, stop),
                CurrentLoadingPhase(runtime, holdingApplicable, stops),
                PublishedBatteryState(runtime)),
            cancellationToken).ConfigureAwait(false);
        // The drop-off stop has no departure wait: ADR-cross-0055's wait is the pickup's.
        await PublishStopWorklistAsync(
            runtime, stops, session, stationDepartureDeadlineAt: null, cancellationToken).ConfigureAwait(false);
        // 与取货到站同一件事：车在路上收到的重发版被到站这一版取代，先退役，免得补发把车上的计划拨回去（批次7-07 审查）。
        await RetireSupersededSnapshotAsync(ReSentPlanMessageId(runtime, stop, arrivedAtStop: false), cancellationToken)
            .ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            stop.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(
                runtime, stops.Stops, stop, arrivedAtCurrent: true,
                PlanRevisionAt(runtime.PlanRevision, stop, arrivedAtStop: true), IsStagingPickupLeg),
            cancellationToken).ConfigureAwait(false);
        await PublishUnloadCommandAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 对本停靠该卸的那一条需求下卸货命令。一条卸完再下一条——同一站的几条需求逐条串行，一次只开一排仓门
    /// （规格第 22 节补记；一次只开一个仓门的现场口径见 8005-agv-program#111）。
    /// </summary>
    /// <remarks>
    /// 跨需求的先后是先前侧后后侧、同侧按加入先后（control-server#303，规格第 20 节），排序在
    /// <see cref="JourneyStopCursor.NextToUnloadAtCurrentStopAsync"/>。装货没有对应的排序：装哪一条、先装哪一条由操作员扫码的
    /// 顺序决定，服务端不改它。
    /// </remarks>
    private async Task PublishUnloadCommandAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        UnloadChoice choice = await stops.NextToUnloadAtCurrentStopAsync(dbContext, cancellationToken).ConfigureAwait(false);
        if (choice.Next is not { } next)
        {
            return;
        }
        if (choice.UnorderedReason is { } unordered)
        {
            // 只在这里记：这是真要选下一条卸谁的那一刻，一个停靠至多「需求条数减一」次（已发出那条优先，不走到这里）。
            LogUnloadOrderFallback(
                logger, runtime.AgvId, runtime.JourneyId, stop.StopId, unordered,
                string.Join(',', choice.UnorderedDemandIds), next.Demand.DemandId, null);
        }

        int[] slots = JsonSerializer.Deserialize<int[]>(next.Membership.TargetSlotsJson) ?? [];
        // The unload carries the admission where the AREA machine is the drop-off (STAGING_TO_WIRE).
        bool admission = await store.AreaEndOperationAsync(
                next.Demand.DemandId, next.Demand.WorkType, cancellationToken)
            .ConfigureAwait(false) == SlotOperationType.Unload;
        await publisher.PublishSlotOperationCommandAsync(
            next.Membership.UnloadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                null,
                next.Demand.DemandId,
                next.Demand.Sublot,
                stop.OperationSessionId,
                next.Membership.UnloadSlotOperationAttemptId,
                SlotOperationType.Unload,
                slots,
                session.ForcedRecoveryGeneration,
                BusinessHash(next.Demand.DemandId, next.Demand.Sublot, "UNLOAD", slots)),
            cancellationToken,
            admission ? stop.StationId : null,
            admission ? next.Demand.WorkType : null).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the unload may be commanded as far as station admission goes. Only a journey whose AREA machine is the
    /// drop-off (STAGING_TO_WIRE) is asked, and only until its unload is prepared: from then on the decision frozen with
    /// the unload is what stands (ADR-cross-0050/0051), so a restart that re-enters the arrival with the unload already
    /// prepared goes on to replay it rather than stopping here on the current policy.
    /// </summary>
    private async Task<bool> UnloadAdmittedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        // 问的是本停靠此刻该卸的那一条（批次7-06）：准入是「这个站允许这条需求的任务类型吗」，而一站几条需求的
        // 任务类型未必相同。没有该卸的就没有可问的，放行——调用方接着往下判。
        if ((await stops.NextToUnloadAtCurrentStopAsync(dbContext, cancellationToken).ConfigureAwait(false)).Next
            is not { } next)
        {
            return true;
        }

        string workType = next.Demand.WorkType;
        if (await store.AreaEndOperationAsync(next.Demand.DemandId, workType, cancellationToken).ConfigureAwait(false)
                != SlotOperationType.Unload ||
            await UnloadPreparedAsync(stops, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        return await store.IsTaskTypeAllowedAtAreaEndAsync(next.Demand.DemandId, workType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The submission answering the entry request this stop has open, or null when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A submission answers one entry request, and its address says which.</b> The vehicle, the
    /// session generation, the operation session, the station and the worklist revision are the address.
    /// A submission whose address is not this stop's cannot be an answer to this request, so it is
    /// skipped rather than judged. Judging the whole inbox is what this method used to do, and it raised
    /// <c>SUBLOT_SUBMISSION_MISMATCH</c> within one poll of every arrival with nobody having scanned
    /// anything: the row that raised it was a finished journey's, another vehicle's, or this journey's
    /// own earlier round. Nothing here writes a block reason.
    /// </para>
    /// <para>
    /// <b>The read is narrowed in the store as well as here.</b> The operation session is written into
    /// the submission's own JSON, so a substring is a filter the database can apply and the inbox's
    /// history is not read back and parsed on every poll. It is a pre-filter only: the parsed comparison
    /// below is what decides, and a pre-filter that decided anything would be a bug rather than a
    /// shortcut.
    /// </para>
    /// <para>
    /// <b>A submission already refused is not read again.</b> The stored <c>SublotRejected</c> is the
    /// record that this entry was judged (<see cref="LoadCancellationBeforeSublot.RefusedSubmissionIdsAsync"/>);
    /// without skipping it every poll would re-run the remote reads and re-send the same refusal. Only
    /// the rows that answer this stop are asked about, so that lookup is bounded by this stop's entries
    /// rather than by every refusal the server has ever written.
    /// </para>
    /// <para>
    /// <b>Nothing here decides about the load.</b> Which demand the sublot belongs to, whether the station
    /// admits that demand's work type, and whether BR-013 still holds are judged by
    /// <see cref="RevalidateEnteredSublotAsync"/>, which the caller reaches only after it has read the
    /// open cancellation — so an entry that loses to a cancellation is neither loaded nor answered.
    /// </para>
    /// </remarks>
    private async Task<ProtocolInboxRow?> FindMatchingSublotAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        // 预筛用当前停靠的作业会话（control-server#208）。下面 AnswersTheStop 读的仍是旅程行上的同源副本：
        // 那个判定被 LoadCancellationBeforeSublot 与取消那条入站链共用，本票不碰那条链，所以它随多停靠一起搬（批次7-06）。
        string operationSessionId = stops.Current.OperationSessionId;
        StopEntryAddress address = stops.EntryAddressOfCurrentStop(runtime.WorklistRevision);
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotSubmitted" && row.RequestJson.Contains(operationSessionId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<ProtocolInboxRow> answers = [];
        foreach (ProtocolInboxRow row in rows.OrderBy(row => row.ReceivedAt))
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            // The same address the cancellation before a sublot reads, plus the generation: the runtime
            // acts only on an answer of the session it is serving, while the cancellation refuses on an
            // entry of any generation (control-server#116 review).
            bool answersThisStop = root.GetProperty("sessionGeneration").GetInt64() == session.SessionGeneration &&
                                   LoadCancellationBeforeSublot.AnswersTheStop(root, address);
            if (answersThisStop)
            {
                answers.Add(row);
            }
        }

        // Asked only about the submissions that answer this stop: the refusal lookup is then bounded by
        // this stop's entries, not by every refusal the server has ever written.
        HashSet<string> refused = await LoadCancellationBeforeSublot
            .RefusedSubmissionIdsAsync(dbContext, [.. answers.Select(row => row.MessageId)], cancellationToken)
            .ConfigureAwait(false);
        // 已经被一条装货命令答复过的录入不再读第二遍（批次7-06，control-server#211）。一个停靠上可以连着录入几条需求，
        // 所以「消费过的」是一个集合；旅程行上那个单数的 ConsumedSublotMessageId 只记得住最后一条，第二次录入会
        // 重新读到第一条、把已经装完的那条需求再判一次，结果是一条拒收而不是下一条装货命令。
        //
        // 证据取发件箱里那条装货命令的 correlationId，与 RefusedSubmissionIdsAsync 同构：那是「这条录入被答复过」
        // 唯一的持久记录，重启之后仍在，而内存里的任何标记都不在。
        HashSet<string> consumed = await ConsumedSubmissionIdsAsync(
            [.. answers.Select(row => row.MessageId)], cancellationToken).ConfigureAwait(false);
        return answers.Find(row => !refused.Contains(row.MessageId) && !consumed.Contains(row.MessageId));
    }

    /// <summary>这些录入里，哪些已经被一条装货命令答复过。</summary>
    private async Task<HashSet<string>> ConsumedSubmissionIdsAsync(
        IReadOnlyCollection<string> submissionIds,
        CancellationToken cancellationToken)
    {
        HashSet<string> consumed = new(StringComparer.Ordinal);
        foreach (string submissionId in submissionIds)
        {
            // 子串只是数据库能用的预筛，判定仍由下面的解析做——同 RefusedSubmissionIdsAsync 的理由。
            string[] stored = await dbContext.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "SlotOperationCommand" && row.PayloadJson.Contains(submissionId))
                .Select(row => row.PayloadJson)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (string payload in stored)
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("correlationId", out JsonElement correlationId) &&
                    string.Equals(correlationId.GetString(), submissionId, StringComparison.Ordinal))
                {
                    consumed.Add(submissionId);
                    break;
                }
            }
        }

        return consumed;
    }

    /// <summary>
    /// Judges the submission that answers this stop's open entry request, answering it when it may not
    /// load. Returns the demand the entry resolved to, or null when nothing may be loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The demand is resolved here, from the sublot, inside the dispatch scope.</b> Protocol 2.0.0 took
    /// the demand off <c>SublotSubmitted</c>: which demand a sublot belongs to is the server's to work
    /// out, and the scope it works it out in is the demands this journey was dispatched for — not the
    /// catalogue, which holds demands this vehicle was never given. FR-001 AC-3 scopes entry to the whole
    /// stop, so the scope is the set of demands still outstanding there, and <b>the demand it resolves to is
    /// the one that gets loaded</b> (control-server#211): every step below — the basket count it is compared
    /// against, the slots the command opens, the attempt its result is looked up by — is that demand's own.
    /// </para>
    /// <para>
    /// <b>BR-013 section 2 makes the recomputed count authoritative after the entry.</b> The count was
    /// computed once at acceptance against the capacity table and the MES box count as they stood then;
    /// both move while the vehicle drives, and the slots were reserved against the number frozen on the
    /// journey row. A count that cannot be established, or that comes out different, stops the load: no
    /// slot is allocated and nothing is unlocked.
    /// </para>
    /// <para>
    /// <b>The one refusal that is not a message.</b> A station whose task-type admission does not allow
    /// the demand's work type is a fact about the station, not about the entry: the journey blocks under
    /// <c>TASK_TYPE_NOT_ALLOWED_AT_STATION</c> and nothing is sent, which is the carve-out
    /// <see cref="LoadCancellationBeforeSublot"/> still carries on the cancellation side.
    /// </para>
    /// <para>
    /// A refusal is saved with the journey left where it was. The stop stays the operator's: the entry is
    /// not recorded as consumed, so a rescan is judged afresh, the station deadline of
    /// <c>control-server#79</c> still runs, and a cancellation before any sublot is still available.
    /// </para>
    /// </remarks>
    private async Task<JourneyStopDemand?> RevalidateEnteredSublotAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        ProtocolInboxRow submission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(submission.RequestJson);
        string enteredSublot = RequiredString(document.RootElement.GetProperty("payload"), "sublot");
        // 派车范围：这个停靠上还没装完的需求（control-server#208 放宽成集合，control-server#211 让它真的生效）。
        // 取「还没装完」而不是「还没终结」，是因为刚装完的那一条不该再被扫一次——它已经不在清单上了。
        JourneyStopDemand? member = stops.OutstandingAtCurrentStop.SingleOrDefault(
            item => string.Equals(item.Demand.Sublot, enteredSublot, StringComparison.Ordinal));
        if (member is null)
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demandId: null,
                enteredSublot,
                ServerReasonCodes.SublotNotInDispatchScope,
                $"子批 {enteredSublot} 不属于本次派车范围，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        AcceptedDemandRow demand = member.Demand;

        // At the AREA machine station, not necessarily this one: STAGING_TO_WIRE loads at a staging station and is
        // admitted at the machine it unloads at (control-server#163). Asked here too, before anything is loaded for a
        // machine that would refuse it.
        if (!await store.IsTaskTypeAllowedAtAreaEndAsync(demand.DemandId, demand.WorkType, cancellationToken)
                .ConfigureAwait(false))
        {
            runtime.SetBlockReason(AreaEndAdmissionHeldReason, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        LiveMesFieldSet? fields = JsonSerializer.Deserialize<LiveMesFieldSet>(
            demand.LiveMesFieldsJson, SerializerOptions);
        if (string.IsNullOrWhiteSpace(fields?.Package))
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.PackageCapacityUnresolved,
                $"子批 {enteredSublot} 没有 PACKAGE 型号，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        int? capacity = await packageCapacityStore.ResolveAndTrackAsync(fields.Package, now, cancellationToken)
            .ConfigureAwait(false);
        if (!AuthoritativeBasketCount.PackageCapacityIsUsable(capacity))
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.PackageCapacityUnresolved,
                $"PACKAGE {fields.Package} 没有已批准的花篮容量对照，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        int? maxBoxCount;
        try
        {
            maxBoxCount = await boxCountReader.ReadMaxBoxCountAsync(demand.Sublot, cancellationToken)
                .ConfigureAwait(false);
        }
        // A MesIngest that times out is unreadable like any other (control-server#334, MesIngestReads).
        catch (Exception error) when (MesIngestReads.IsFailedRead(error, cancellationToken))
        {
            LogBoxCountFailed(logger, demand.DemandId, error);
            maxBoxCount = null;
        }

        if (!AuthoritativeBasketCount.BoxCountIsUsable(maxBoxCount))
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.SublotBoxCountUnavailable,
                $"查不到子批 {enteredSublot} 的箱数，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Both inputs were checked usable just above, so a count is computable.
        int recomputed = AuthoritativeBasketCount.Compute(maxBoxCount, capacity)
            ?? throw new InvalidOperationException("Both inputs are usable, so a count is computable.");
        // 比的是「被录入那一条」自己冻结的花篮数（批次7-06）：仓位是按它预留的，而一站几条需求各有各的数。
        // 批次7-03 比的是旅程行上的锚需求那一份，单需求下两者恒等。
        if (recomputed != member.Membership.ExpectedBasketCount)
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.ExpectedBasketCountMismatch,
                $"子批 {enteredSublot} 的花篮数量由 {member.Membership.ExpectedBasketCount} 变为 {recomputed}，"
                + "与本次派车预留的仓位不符，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        return member;
    }

    /// <summary>
    /// Answers one entry with a <c>SublotRejected</c> carrying the reason, and leaves the journey waiting
    /// for the next one.
    /// </summary>
    /// <remarks>
    /// The refusal is keyed on the submission it answers, which is the only correlation the operator's
    /// screen can attach the reason to, and its own id is derived from that submission rather than
    /// counted, so two refusals of two submissions are two messages instead of one rewritten identity.
    /// The submission is deliberately not recorded as consumed: the stop is still the operator's to
    /// rescan, to cancel, or to run out at its deadline.
    /// </remarks>
    private async Task RefuseAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        ProtocolInboxRow submission,
        string? demandId,
        string enteredSublot,
        string reasonCode,
        string displayMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 这条录入已经被答复过了，就不再答第二次（control-server#324，PR #361 增量复核）。本站结束的那一次改动会为没人答的录入
        // 暂存 WORKLIST_REVISION_STALE，用的正是下面这个派生 id；引擎这一轮在锁外读收件箱时它还不在，读完之后扫码前取消的结果落定、
        // 结束了本站，引擎再按旧游标判出「不在范围」，同一个 id 写一份内容不同的拒收，发件箱的重放校验就抛内容冲突，这一轮对所有车
        // fail-closed。只认这一种情形——同 id 的拒收已在——其余内容冲突照旧抛。
        string rejectionId = JourneyPlanBuilder.StableGuid(submission.MessageId, "sublot-rejected");
        if (await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == rejectionId && row.MessageType == "SublotRejected", cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }
        // 作业会话与修订号取「当前停靠此刻这一版」（批次7-06）：拒收告诉操作员「你扫的那一版清单是第几号」，
        // 而清单每装完一条就升一版，旅程行上那个受理时的值只在第一版上对得上。
        await publisher.PublishSublotRejectedAsync(
            rejectionId,
            submission.MessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SublotRejection(
                demandId,
                stops.Current.OperationSessionId,
                new WireProblem(reasonCode, "payload.sublot", displayMessage),
                stops.WorklistRevisionAt(runtime.WorklistRevision, stops.Current),
                enteredSublot),
            cancellationToken).ConfigureAwait(false);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSublotRejected(logger, demandId ?? runtime.TransportColumn(runtime.DemandId), reasonCode, null);
    }

    /// <summary>
    /// Waits briefly for the peer's answer to the pre-departure safety check, then judges it.
    /// </summary>
    /// <remarks>
    /// The peer stamps its answer with a validity window of its own choosing, and the protocol puts
    /// no floor under that window. Its window is shorter than one poll interval, so reading the
    /// answer on the next iteration always found it expired and the journey stopped at this stage
    /// with PRE_DEPARTURE_SAFETY_NOT_VALID. Waiting here consumes the evidence while it is still
    /// valid rather than asking the peer for a longer window, which would only make the evidence
    /// staler. An answer that is present but not safe stops the wait immediately -- this shortens
    /// the gap between asking and judging, and relaxes nothing.
    /// </remarks>
    private async Task<(SafetyCheckObservation? Safety, string? ResultMessageId)> AwaitSafeDepartureResultAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        // Bounded by attempts rather than by the clock: a test clock does not advance on its own,
        // and a wait that never ends is worse than a stage that does not advance.
        TimeSpan step = TimeSpan.FromMilliseconds(50);
        int attempts = Math.Max(1, (int)(runtimeOptions.DepartureSafetyResultWait / step));
        for (int attempt = 0; ; attempt++)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            (SafetyCheckObservation? safety, bool invalid, string? resultMessageId) = await FindSafeDepartureResultAsync(
                runtime,
                stops, session, now, cancellationToken).ConfigureAwait(false);
            if (safety is not null || invalid || attempt >= attempts)
            {
                // Written once, as the wait ends. Clearing the code before every attempt and writing it
                // back, as this did before control-server#80, would restart BlockReasonSince on every
                // poll of a journey that stays PRE_DEPARTURE_SAFETY_NOT_VALID.
                runtime.SetBlockReason(invalid ? PreDepartureSafetyNotValidReason : null, now);
                return (safety, resultMessageId);
            }
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Judges the peer's answers to the current stop's departure check: the newest answer that still describes the
    /// vehicle decides (control-server#826).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Outdated answers are passed over, not judged.</b> An answer from another session generation, about another
    /// safety state version, past its own <c>validUntil</c>, older than the evidence age, or stamped further ahead of
    /// this server's clock than <see cref="JourneyRuntimeOptions.PeerClockSkewTolerance"/> says nothing about the
    /// vehicle now. This read used to let the oldest answer under the check id decide, which was harmless while the
    /// peer answered a check once; once it answers a re-asked check again (onboard-hmi#386), a first outdated answer
    /// kept every later valid one from being read and the vehicle stood at the stop (agv02, 2026-10-21, 22 minutes).
    /// Nothing in the history of the oldest-first order says it guarded anything else.
    /// </para>
    /// <para>
    /// <b>Of the rest, the newest decides, in both directions.</b> "The first valid one" would let an older SAFE
    /// depart a vehicle whose newer answer to the same check says a door came open. Newest is by the server's own
    /// receive time, then by the peer's <c>observedAt</c>.
    /// </para>
    /// <para>
    /// <b>The tolerance is for the peer's clock, and only for <c>observedAt</c>.</b> The vehicle's clock drifts about
    /// 0.12 s a day between syncs; at zero tolerance an answer judged on receipt was "in the future" and invalid.
    /// <c>ReceivedAt</c> is this server's own clock and keeps its zero tolerance (control-server#254): the two are
    /// different clocks, not two settings of one rule. <c>now - observedAt</c> is negative inside the tolerance and so
    /// passes the evidence age, as it should: the answer is as fresh as it gets.
    /// </para>
    /// <para>
    /// With no answer that still describes the vehicle but some that do not, the result is still "invalid", as it was,
    /// so the journey keeps PRE_DEPARTURE_SAFETY_NOT_VALID until the check is asked again. Every invalid judgment is
    /// logged with what each answer failed on.
    /// </para>
    /// </remarks>
    private async Task<(SafetyCheckObservation? Safety, bool Invalid, string? ResultMessageId)> FindSafeDepartureResultAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "PreDepartureSafetyCheckResult")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderBy(row => row.ReceivedAt).ToArray();
        string checkId = DepartureCheckId(stops.Current);
        List<string> judged = [];
        DepartureAnswer? newest = null;
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            JsonElement payload = root.GetProperty("payload");
            if (RequiredString(payload, "preDepartureSafetyCheckId") != checkId)
                continue;
            DateTimeOffset observedAt = payload.GetProperty("observedAt").GetDateTimeOffset();
            DateTimeOffset validUntil = payload.GetProperty("validUntil").GetDateTimeOffset();
            string? outdatedBy =
                root.GetProperty("sessionGeneration").GetInt64() != session.SessionGeneration ? "session-generation"
                : payload.GetProperty("safetyStateVersion").GetInt64() != session.SafetyRevision ? "safety-state-version"
                : observedAt > now + runtimeOptions.PeerClockSkewTolerance ? "observedAt-ahead"
                : validUntil < now ? "validUntil-passed"
                : now - observedAt > runtimeOptions.MaximumEvidenceAge ? "older-than-evidence-age"
                : null;
            if (outdatedBy is not null)
            {
                judged.Add($"{row.MessageId} {outdatedBy} (observedAt {observedAt:O}, validUntil {validUntil:O})");
                continue;
            }
            // Rows are in receive order, so a later row replaces the one before; on the same receive time, the later
            // observation does.
            if (newest is null || row.ReceivedAt > newest.ReceivedAt || observedAt >= newest.ObservedAt)
            {
                newest = new DepartureAnswer(
                    row.MessageId, row.ReceivedAt, observedAt, validUntil, DepartureAnswerFaults(runtime, stops, root, payload));
            }
        }

        if (newest is null)
        {
            if (judged.Count == 0)
            {
                return (null, false, null);
            }
            LogDepartureAnswerNotValid(logger, checkId, runtime.AgvId, string.Join("; ", judged), null);
            return (null, true, null);
        }
        if (newest.Faults.Length > 0)
        {
            judged.Add($"{newest.MessageId} {string.Join(",", newest.Faults)} (observedAt {newest.ObservedAt:O})");
            LogDepartureAnswerNotValid(logger, checkId, runtime.AgvId, string.Join("; ", judged), null);
            return (null, true, null);
        }
        if (session.SafetyRevision is not long safetyRevision)
        {
            return (null, false, null);
        }
        return (new SafetyCheckObservation(
            checkId,
            safetyRevision,
            true,
            newest.ObservedAt,
            newest.ValidUntil), false, newest.MessageId);
    }

    /// <summary>What a current answer to the departure check says against departing; empty when it permits it.</summary>
    private static string[] DepartureAnswerFaults(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JsonElement root,
        JsonElement payload)
    {
        List<string> faults = [];
        // v3 (control-server#382, review S1): only an answer to a departure check is a departure permit. A SAFE answer
        // under this check id that says it answered a HOLD_RELEASE or NON_BUSINESS_MOVE check answered another question.
        if (!(payload.TryGetProperty("checkPurpose", out JsonElement checkPurpose) &&
              checkPurpose.ValueKind == JsonValueKind.String &&
              checkPurpose.GetString() == PreDepartureCheckPurposes.Departure))
        {
            faults.Add("not-a-departure-check");
        }
        // The protocol requires a correlationId here but never says what it correlates to, and
        // its own valid example points it at neither the request's messageId nor the check id.
        // The peer correlates by the check id; we demanded the request's messageId, so a
        // perfectly good SAFE answer was refused and no departure could ever be authorized.
        // Both readings are accepted. This loses no identity: the check id already had to match
        // above, and it is unique to this journey's leg, so the correlationId was only ever a
        // second name for a fact already proven.
        string correlationId = RequiredString(root, "correlationId");
        if (correlationId != DepartureCheckMessageId(stops.Current) && correlationId != DepartureCheckId(stops.Current))
        {
            faults.Add("correlation-id");
        }
        if (RequiredString(root, "agvId") != runtime.AgvId)
        {
            faults.Add("agv-id");
        }
        JsonElement safety = payload.GetProperty("safety");
        string[] notSafe =
        [
            .. RequiredString(payload, "outcome") == "SAFE" ? Array.Empty<string>() : ["outcome"],
            .. safety.GetProperty("departureSafe").GetBoolean() ? Array.Empty<string>() : ["departureSafe"],
            .. safety.GetProperty("vehicleStopped").GetBoolean() ? Array.Empty<string>() : ["vehicleStopped"],
            .. safety.GetProperty("allTargetSlotsLocked").GetBoolean() ? Array.Empty<string>() : ["allTargetSlotsLocked"],
            .. safety.GetProperty("allUnlockOutputsReset").GetBoolean() ? Array.Empty<string>() : ["allUnlockOutputsReset"],
            .. safety.GetProperty("unknownPresent").GetBoolean() ? ["unknownPresent"] : Array.Empty<string>(),
        ];
        if (notSafe.Length > 0)
        {
            faults.Add($"not-safe[{string.Join(",", notSafe)}]");
        }
        return [.. faults];
    }

    private sealed record DepartureAnswer(
        string MessageId,
        DateTimeOffset ReceivedAt,
        DateTimeOffset ObservedAt,
        DateTimeOffset ValidUntil,
        string[] Faults);

    /// <summary>One vehicle's Onboard-side facts; see <see cref="OnboardDispatchFactsReader.ReadOnboardFactsAsync"/>.</summary>
    private Task<OnboardDispatchFacts?> ReadOnboardFactsAsync(string agvId, CancellationToken cancellationToken) =>
        onboardFacts.ReadOnboardFactsAsync(agvId, cancellationToken);

    /// <summary>
    /// The vehicle's Ready session row, untracked; see <see cref="OnboardDispatchFactsReader.CurrentReadySessionAsync"/>.
    /// </summary>
    private Task<SessionRecoveryRow?> CurrentReadySessionAsync(string agvId, CancellationToken cancellationToken) =>
        onboardFacts.CurrentReadySessionAsync(agvId, cancellationToken);

    /// <summary>
    /// When this session generation was last heard from; see
    /// <see cref="OnboardDispatchFactsReader.LatestInboundAtForSessionAsync"/>.
    /// </summary>
    private Task<DateTimeOffset?> LatestInboundAtForSessionAsync(
        string agvId,
        long generation,
        DateTimeOffset? notBefore,
        CancellationToken cancellationToken) =>
        onboardFacts.LatestInboundAtForSessionAsync(agvId, generation, notBefore, cancellationToken);

    /// <summary>
    /// Re-runs the pre-create gate for the gate-bound move order, against the endpoint this demand
    /// froze rather than a freshly resolved one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// REQ-0305 asks for exactly three things before a move order that does not exist yet:
    /// the catalog must be usable, the frozen <c>mapId + stationId</c> must still be in it, and
    /// RouteCost must pass. REQ-0345 adds a fourth, asked before RIoT is: the demand's frozen
    /// <c>Map + TASK_TYPE</c> must not be held. When any of them fails the new action is blocked and the reason
    /// recorded precisely — never resolved to another station, another Map, or a similar name.
    /// </para>
    /// <para>
    /// The route graph is not consulted here, so this leg has one evidence source rather than two.
    /// That is not a weaker gate, it is a narrower question: the graph's contribution at selection
    /// time was to compare candidate stations against each other, and there is nothing to compare
    /// here — the destination was fixed when the demand was taken.
    /// </para>
    /// </remarks>
    /// <param name="toTheStopItself">
    /// Gate the order to <paramref name="targetStop"/>'s own station rather than to the demand's frozen drop-off: a rebuilt
    /// pickup order (control-server#318) goes to the pickup, and the frozen row names only the drop-off.
    /// </param>
    private async Task<CreateGateOutcome> GateLegAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow targetStop,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken,
        bool toTheStopItself = false)
    {
        CatalogAvailability availability = await catalogAvailability
            .ReadAsync(runtime.MapId, cancellationToken).ConfigureAwait(false);
        if (!availability.IsUsable)
        {
            return new CreateGateOutcome(
                CreateGateVerdict.BlockedCatalogNotFresh, availability.BlockReason, null);
        }

        // REQ-0344's last sentence and REQ-0345: a move order that does not exist yet waits while the demand's frozen
        // Map + TASK_TYPE is held, whatever raised the hold. An order already created is not touched -- this is only
        // ever asked before the gate leg is created. A journey accepted before control-server#160 froze no versions;
        // it is judged as the WIRE_TO_GATE on the runtime's Map it was, the way the dropoff below falls back.
        DemandTaskTypeStationFreeze? frozenVersions = await _taskTypeStations.Freezes
            .ReadAsync(runtime.TransportColumn(runtime.DemandId), cancellationToken).ConfigureAwait(false);
        string taskType = frozenVersions is null
            ? TransportTaskTypes.WireToGate
            : await dbContext.AcceptedDemands
                .Where(row => row.DemandId == runtime.DemandId)
                .Select(row => row.WorkType)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
        TaskTypeHolds holds = await _taskTypeStations
            .ReadHoldsAsync(frozenVersions?.MapId ?? runtime.MapId, cancellationToken).ConfigureAwait(false);
        if (holds.Holds(taskType))
        {
            return new CreateGateOutcome(CreateGateVerdict.BlockedTaskTypeHeld, DispatchReasonCodes.TaskTypeHeld, null);
        }

        IReadOnlyList<FrozenStationFact> frozen = await catalogStore
            .ReadFrozenStationsAsync(runtime.TransportColumn(runtime.DemandId), cancellationToken).ConfigureAwait(false);
        FrozenStationFact? dropoff = toTheStopItself
            ? null
            : frozen.FirstOrDefault(station => station.Role == FrozenStationRole.Dropoff);

        // A journey created before this gate existed has no frozen row. Falling back to the
        // runtime's own gate station keeps that journey moving under the same check rather than
        // blocking it on a record it never had a chance to write.
        int mapId = dropoff?.MapId ?? runtime.MapId;
        // 兜底取「这一段腿要去的那个停靠」（批次7-06），不是旅程行上写死的关卡站：多停靠计划里后者只是其中一个。
        int stationId = dropoff?.StationId ?? targetStop.StationRiotId;
        string transportDemandKey = await dbContext.AcceptedDemands
            .Where(row => row.DemandId == runtime.DemandId)
            .Select(row => row.TransportDemandKey)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        return await createGate.EvaluateAsync(
            new CreateGateRequest(
                runtime.TransportColumn(runtime.DemandId),
                transportDemandKey,
                runtime.AgvId,
                runtime.VehicleKey,
                mapId,
                stationId,
                GraphTraversalCostMm: null,
                TargetStationInCurrentCatalog: currentMap.MapId == mapId &&
                    currentMap.Stations.Any(station => station.StationId == stationId)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The worklist for one stop. The revision is explicit because the gate stop is a different
    /// worklist from the pickup stop -- different station, different role -- and the peer keys a
    /// snapshot's identity on its type and revision. Publishing both at the same revision made the
    /// peer reject the second as a revision whose content had changed, which was right of it: a
    /// revision that does not advance is a promise that the content did not. The connection died on
    /// that rejection, and the unload command queued behind it was never reached, so the gate stage
    /// could not start. The sibling projections at this stop already advance the same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 站点、作业会话与项的角色全部由停靠行给出，项本身是这个停靠上还没终结的需求（批次7-03，control-server#208）。
    /// </para>
    /// <para>
    /// 协议顶层只有一个 <c>operationSessionId</c>（规格第 22 节补记），项至多 8 条。今天一个停靠一条需求，
    /// 所以这里恒为一项，与原来写死的那一项逐字相同。
    /// </para>
    /// </remarks>
    private static CurrentStopWorklistProjection Worklist(
        JourneyStopRow stop,
        IReadOnlyList<JourneyStopDemand> demands,
        long revision,
        DateTimeOffset? stationDepartureDeadlineAt) => new(
            stop.StationId,
            revision,
            stop.OperationSessionId,
            stationDepartureDeadlineAt,
            [.. demands.Select(item => new CurrentStopWorklistItem(
                item.Demand.DemandId,
                item.Demand.TransportDemandKey,
                item.Demand.Sublot,
                item.Demand.WorkType,
                // 协议这一栏说的是「在这个停靠上对这条需求做什么」：取货停靠装货，卸货停靠卸货。
                stop.StopRole == JourneyStopRoles.Pickup ? "PICKUP" : "DROPOFF",
                item.Membership.ExpectedBasketCount))],
            StopEndedReason: null);

    // The activePurpose of a transport journey. An idle return (batch 8-19, control-server#390) sends IDLE_RETURN from its own
    // branch (JourneyRuntimeEngine.IdleReturn.cs, JourneyPlanBuilder.IdleReturnBusinessState), and a charging journey (batch
    // 9-06, control-server#404) sends CHARGING from its own (JourneyRuntimeEngine.Charging.cs,
    // JourneyPlanBuilder.ChargingBusinessState). CLEARING_MAINTENANCE is deferred.
    private const string TransportPurpose = VehicleActivePurposes.Transport;

    // 8005-agv-program#94's semantic table: a vehicle carrying a demand is not charging -- it holds no charger reservation, no
    // charging order and no charging cycle, because a vehicle gets a charging cycle only while it has no other purpose
    // (ChargingAllocator). "Not in a charging cycle" is a fact this server knows, not a guess; UNKNOWN would report a missing
    // feature as a lost observation. A charging journey sends its cycle's own state (ALLOCATED, EN_ROUTE; the rest in batch
    // 9-07). manualChargingHold is false for the same reason: a hold is placed on a vehicle with no purpose (control-server#404),
    // or -- since control-server#410 -- on one entering CLEARING_MAINTENANCE after a field-confirmed unable-to-charge, and either
    // way is lifted before the vehicle can take a transport purpose. The clearing snapshots project false too; once the clearance
    // ends and the purpose is released, charging allocation sends true.
    private const string NotInAChargingCycle = "NOT_CHARGING";

    /// <summary>
    /// 运输旅程的车辆业务状态。<paramref name="batteryState"/> 是这趟旅程记下的那一版投影（<see cref="PublishedBatteryState"/>），
    /// 不是此刻现读的电量（批次9-05，control-server#403）。
    /// </summary>
    private static VehicleBusinessProjection TransportBusinessState(
        long revision, LoadingPhaseProjection loadingPhase, string batteryState) =>
        new(revision, "READY", TransportPurpose, false, batteryState, NotInAChargingCycle, loadingPhase, []);

    /// <summary>
    /// 这趟旅程的车辆业务状态快照里的 <c>batteryState</c>：旅程行上记下的最近一次投影（批次9-05，control-server#403）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>随修订号冻结，重发时原样读回，从不在重发时现读电量</b>（票面第 8 条选的是「记在旅程上」）。同一阶段的快照按确定的消息 id 重发，
    /// 载荷变了就会被当成语义冲突拒收（<c>OnboardJourneyPublisher</c>），旅程会在重连之间来回打转。
    /// </para>
    /// <para>
    /// 值只在两处变：派车受理时按判它的那份事实投影，写进旅程行（<c>JourneyExecutionPlan.PublishedBatteryState</c>）；每个停靠的到站快照
    /// <b>第一次</b>排给车之前，按到站判定读到的那次观测与这趟旅程冻结的策略版本重投影（<see cref="ProjectBatteryStateForArrivalAsync"/>）。
    /// 两处都与新的快照同一次保存。之后这个停靠上的装货阶段快照、这张到站快照的重跑与收尾快照读的都是这一个值。
    /// 电量变了，下一张带它的快照是下一个停靠的到站快照：新的修订号、新的消息 id（由停靠派生，规则与今天相同）。
    /// </para>
    /// <para>
    /// 为空的是本票上线前派出的旅程：它们排给车的快照里是写死的 <c>SUFFICIENT</c>，又没有冻结的策略版本，照旧发它。
    /// </para>
    /// </remarks>
    internal static string PublishedBatteryState(JourneyRuntimeRow runtime) =>
        runtime.PublishedBatteryState ?? BatteryStates.BeforePolicyProjection;

    /// <summary>
    /// 这个停靠的到站快照第一次排给车之前，按到站那次观测与这趟旅程冻结的策略版本重投影 <c>batteryState</c>（批次9-05，control-server#403）。
    /// 已经排过（到站那一段断线后重跑）就不动：车上那一张的值必须原样再发。
    /// </summary>
    /// <remarks>
    /// 搬运途中越过强制充电线，这里投出 <c>MANDATORY_CHARGE</c>，旅程照常推进：服务端不据此拒装、不取消、不改派（<c>REQ-0281</c>）；
    /// 车载端也不再据此拒收录入（onboard-hmi#220）。
    /// </remarks>
    private async Task ProjectBatteryStateForArrivalAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        RiotVehicleObservation arrivedVehicle,
        CancellationToken cancellationToken)
    {
        if (runtime.ChargingPolicyVersion is not long version ||
            await ArrivalBusinessStateQueuedAsync(stop, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        DispatchBatteryPolicy policy = DispatchBatteryPolicy.From(
            await chargingPolicy.ReadFrozenAsync(version, cancellationToken).ConfigureAwait(false));
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool fresh = arrivedVehicle.ObservedAt <= now && now - arrivedVehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge;
        runtime.PublishedBatteryState = BatteryEligibility.Project(arrivedVehicle, policy, fresh);
    }

    /// <summary>
    /// 车辆业务状态快照里的 <c>loadingPhase</c>：从旅程行上的三列读（批次7-07，control-server#212）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 批次 7 之前它由阶段推出来——一张「阶段 → 值」的表，只产出 <c>LOADING</c> 与 <c>CLOSED</c>／<c>PLANNED_LOADING_COMPLETE</c>。
    /// 那张表现在只在测试里当判据用（<c>Batch7CargoHoldingTests.PreBatch7LoadingPhase</c>，用在
    /// <c>WithoutHoldingEverySnapshotIsTheOneTheStageDerivedMappingGave</c>）：不持货等单的旅程，从列读出来的必须与它逐条相同。
    /// 形状那一半（只会是批次 5 的两种）由 <c>JourneyRuntimeWorkerSlotGroupAndRestartTests.AJourneyThatDoesNotHoldIsOnlyEverSentTheTwoBatchFiveLoadingPhases</c> 守。
    /// </para>
    /// <para>
    /// <b>发快照的时点。</b>到站两处照旧（取货到站、卸货到站），再加 <see cref="ReconcileLoadingPhaseAsync"/> 里进出
    /// <c>CARGO_HOLDING_WAIT</c>、<c>VEHICLE_FULL</c>、<c>CLOSED</c> 的那几次。program#94 留了「离开取货站时要不要再发一次」
    /// 的口子，这台服务器对<b>不持货等单的旅程</b>仍然不发：多一张会把合成对端与 G3 断言的修订号序列整体推后一号，而那一张
    /// 说的「装货结束了」车在卸货站到站那一张上照样看得到。持货等单的旅程要发，因为车载端要显示倒计时与结束原因
    /// （规格第 3.3 节第 12 项）——那几张只出现在分区允许途中追加的旅程上，参数批准之前一张都不会有。
    /// </para>
    /// </remarks>
    // 车停在（或开往）待送点取货停靠时持货等单总是适用（HoldingAppliesAt，control-server#727）：车上有货时给出期限，一篮没有时起算点为空、期限也为空
    // （CP-0014；control-server#709 时那里一律为空）。按当前停靠判（control-server#710 审查 M1）。
    private LoadingPhaseProjection CurrentLoadingPhase(JourneyRuntimeRow runtime, bool holdingApplicable, JourneyStopCursor stops) =>
        LoadingPhaseMachine.Project(
            runtime.LoadingPhaseState,
            runtime.LoadingClosedReason,
            runtime.CargoHoldingStartedAt,
            runtimeOptions.CargoHoldingTimeout,
            HoldingAppliesAt(holdingApplicable, stops));

    /// <summary>
    /// 这一刻持货等单适不适用：车能服务的分区里有允许途中追加的（<see cref="HoldingApplicableAsync"/>），或者车停在（或开往）待送点取货停靠上
    /// （control-server#727）。
    /// </summary>
    /// <remarks>
    /// 待送点那一半是 REQ-0354 原文「车辆在待送点取货停靠持货等单，不论其能够服务的 DispatchZone 是否允许途中追加」：CP-0014 起那里有持货期限，
    /// 分区全禁追加时也得有，否则那时车装完就停在待送点上没有尽头（期限、装满都不判）。开关关着时 <see cref="StandsAtStaging"/> 恒假，这里就是原值。
    /// </remarks>
    private bool HoldingAppliesAt(bool holdingApplicable, JourneyStopCursor stops) =>
        holdingApplicable || StandsAtStaging(stops);

    /// <summary>
    /// 这辆车适不适用持货等单：它能服务的分区里，至少有一个允许途中追加（REQ-0354「车辆能够服务的 DispatchZone 均禁止途中
    /// 追加时车辆不持货等单」，REQ-0198）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「允许」与途中追加规划器是同一个判法（<see cref="EnRouteAppendPlanner.MaxAllowedIncrease"/>）：零与未配置都是禁止。
    /// 两处判法一旦走岔，就会出现车在等一条永远追加不进来的单。
    /// </para>
    /// <para>
    /// <b>不经过派车轮。</b>车能服务哪些分区来自配置，每区参数来自库，两样推进段自己读得到。经过派车轮的话，MES 读不到的那一轮
    /// 派车轮不跑，这里就说不出适不适用——而不适用持货等单的旅程（参数批准之前的全部旅程）今天离站根本不看 MES，
    /// 不能因为这一票变成看。
    /// </para>
    /// </remarks>
    private async Task<bool> HoldingApplicableAsync(string agvId, CancellationToken cancellationToken)
    {
        string[] servedZones = [.. dispatchPolicy.FromConfiguration().ZoneVehicles
            .Where(zone => zone.Value.Contains(agvId))
            .Select(zone => zone.Key)];
        if (servedZones.Length == 0)
        {
            return false;
        }

        DispatchZoneParameterTableVersion? parameters = await zoneParameters.ReadCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        return servedZones.Any(zone => EnRouteAppendPlanner.MaxAllowedIncrease(parameters, zone) is not null);
    }

    /// <summary>
    /// 按此刻的事实重判一次装货阶段，变了就落库，该告诉车的就发一张车辆业务状态快照（批次7-07，control-server#212）。
    /// </summary>
    /// <param name="departingFrom">
    /// 本轮刚为离开这个停靠向 RIoT 请求了移动；为空表示没有在离站。游标是请求移动之前加载的，那一刻这个停靠在游标里
    /// 还开着，所以「离开的是不是最后一个装货停靠」要把它扣掉来数。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>判定本身在 <see cref="LoadingPhaseMachine"/></b>，这里只收集事实、落库与发快照。
    /// </para>
    /// <para>
    /// <b>状态与快照在同一次保存里。</b>先改旅程行、再调发布——发布把发件箱行与这个上下文里全部未保存的改动一起保存，然后才
    /// 发出去。所以要么状态与快照都在，要么都不在：崩在保存之后、发出之前，下一轮重放按发件箱补发；崩在保存之前，下一轮从同一组
    /// 事实推出同一个结论，再做一遍。反过来先存状态、后发快照，崩在中间就是「库里已经 WAIT、车上永远收不到」：下一轮读到的状态
    /// 已经是 WAIT，没有变化，不会再发。
    /// </para>
    /// </remarks>
    private async Task ReconcileLoadingPhaseAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        bool holdingApplicable,
        JourneyStopRow? departingFrom,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool loadBatchJustClosed = false)
    {
        // 待送点的持货起算点先挪到位（control-server#727），下面的到期判定与发给车的期限读的都是挪过之后的那一个。离站那一次不挪：车正离开。
        bool stagingStartMoved = departingFrom is null &&
            await MoveStagingHoldingStartAsync(runtime, stops, now, cancellationToken).ConfigureAwait(false);
        holdingApplicable = HoldingAppliesAt(holdingApplicable, stops);
        bool pendingLoads = stops.AllDemands.Any(item =>
            item.Demand.Status != DemandExecutionStatus.Cancelled &&
            item.Membership.Status is JourneyDemandStatuses.PendingLoad or JourneyDemandStatuses.Loading);
        bool lastLoadingStopDeparted = !stops.OpenStops.Any(stop =>
            stop.StopRole == JourneyStopRoles.Pickup &&
            !string.Equals(stop.StopId, departingFrom?.StopId, StringComparison.Ordinal));
        bool closed = runtime.LoadingPhaseState == LoadingPhaseStates.Closed;

        bool? vehicleFull = null;
        if (holdingApplicable && !closed && !lastLoadingStopDeparted)
        {
            // 待送点取货停靠按「全满或等着的需求全部装不下」判（control-server#710），别的停靠照旧按两侧都满判。
            JourneyStopRow standing = departingFrom ?? stops.Current;
            vehicleFull = IsStagingPickup(standing)
                ? await JudgeStagingDepartureAsync(
                        runtime,
                        stops,
                        standing,
                        arrived: departingFrom is not null || runtime.Stage != JourneyRuntimeStage.AwaitingPickupArrival,
                        departing: departingFrom is not null,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await JudgeVehicleFullAsync(runtime, cancellationToken).ConfigureAwait(false);
        }

        LoadingPhaseMachine.Facts facts = new(
            runtime.LoadingPhaseState,
            runtime.LoadingClosedReason,
            holdingApplicable,
            pendingLoads,
            lastLoadingStopDeparted,
            vehicleFull,
            HoldingDeadlinePassed: HoldingDeadlinePassed(runtime, stops, now),
            LoadBatchInProgress: runtime.Stage == JourneyRuntimeStage.AwaitingLoadResult,
            DepartureUnderWay: departingFrom is null && runtime.Stage == JourneyRuntimeStage.AwaitingDepartureSafety,
            YieldTriggered: YieldTriggeredBeforeHoldingDeadline(runtime, stops));
        // 「有一批在执行」按落库的阶段判，看板认同一句（Batch7CargoHoldingDashboardTests.TheLoadBatchInProgressPremiseIsTheEnginesOwn 逐字核对上面那一行）。
        // 唯一的例外只活在一轮之内：装货刚落定、阶段还没前移，调用方说这一批已经闭环（control-server#290）。这一轮里装货阶段随即关闭并落库，
        // 看板读到的永远是关闭之后的样子，所以它那句前提照旧成立。
        if (loadBatchJustClosed)
        {
            facts = facts with { LoadBatchInProgress = false };
        }
        LoadingPhaseMachine.Decision decision = LoadingPhaseMachine.Decide(facts);

        // 让站的补判（批次7-08，control-server#213）。触发本来由承诺方在它的事务里写（受理、追加、离站三处，StationYield），
        // 但那只标记承诺<b>那一刻</b>已在等单的车。这辆车在别的车被承诺之后才进入等单——承诺时它还在装——就没有人标记它，
        // 而那辆车照样要来：不补这一处，它会一直停在别的车要来的站上。只在「这一次判出来是等单」时问，
        // 所以不持货等单的旅程（参数批准之前的全部旅程）一次也不多查。
        if (departingFrom is null &&
            !decision.IsClosed &&
            !facts.HoldingDeadlinePassed &&
            runtime.YieldTriggeredAt is null &&
            StationYield.IsHolding(decision.State) &&
            StationYield.StandsAtCurrentStop(runtime.Stage) &&
            stops.Current.StopRole == JourneyStopRoles.Pickup &&
            await StationYield.VehicleHeadingToAsync(
                    dbContext, runtime.VehicleKey, stops.Current.StationRiotId, cancellationToken)
                .ConfigureAwait(false) is { } headingHere)
        {
            runtime.YieldTriggeredAt = now;
            runtime.YieldTriggeredByVehicleKey = headingHere;
            runtime.UpdatedAt = now;
            decision = LoadingPhaseMachine.Decide(facts with { YieldTriggered = true });
        }

        string fromState = runtime.LoadingPhaseState ?? LoadingPhaseStates.Loading;
        string? fromReason = runtime.LoadingClosedReason;
        bool changed = fromState != decision.State ||
                       !string.Equals(fromReason, decision.ClosedReason, StringComparison.Ordinal);
        // 待送点的期限挪了而状态没变（比如车已在装货中又来一条追加）：照样发一张，车上的期限与库里的同一次落库（向量
        // CV-STAGING-CARGO-HOLDING-DEADLINE-MOVED 的 cargo-holding-deadline-durable-before-snapshot）。关了的装货阶段不再发：关闭之后不接追加。
        bool announces = LoadingPhaseMachine.Announces(fromState, fromReason, decision, holdingApplicable) ||
                         stagingStartMoved && !decision.IsClosed;
        if (changed)
        {
            runtime.LoadingPhaseState = decision.State;
            runtime.LoadingClosedReason = decision.ClosedReason;
            runtime.UpdatedAt = now;
        }

        if (announces)
        {
            // 车此刻停在哪个停靠：离站时是正要离开的那一个，否则是游标的当前停靠。
            JourneyStopRow stop = departingFrom ?? stops.Current;
            // 阶段还停在「到站」时，车也可能已经被告知到站了：到站那一段发布被断线打断在中间，阶段不前移，而到站那一张车辆业务状态
            // 早已排给车、多半已经确认（control-server#339）。这时按「还在路上」算号，算出来的正是车手上那一张的号，内容却不同——
            // 车按 SNAPSHOT_REVISION_CONTENT_CONFLICT 拒收，而补发每轮都会再送一次。所以「到没到」看车被告知了什么，不只看阶段。
            bool arrived = departingFrom is not null ||
                           runtime.Stage is not (JourneyRuntimeStage.AwaitingPickupArrival or JourneyRuntimeStage.AwaitingGateArrival) ||
                           await ArrivalBusinessStateQueuedAsync(stop, cancellationToken).ConfigureAwait(false) is not null;
            await PublishLoadingPhaseAsync(runtime, stops, stop, arrived, session, holdingApplicable, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (dbContext.Entry(runtime).State == EntityState.Modified)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 两侧是不是都满了（REQ-0354、ADR-cross-0059）；说不出来为空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一侧满有两种：这一侧已无空仓，或者上一轮派车有一条候选只因本车货物占着这一侧而装不下。前一种这里读账本自己判，与派车用的
    /// 是同一个账本（<see cref="JourneyAwareSlotLedger"/>，会话基线减去本车已预留、已装的货）；后一种从派车轮的读口取
    /// （<see cref="SlotGroupFullnessBoard"/>）。读口这一轮没有这辆车的结论时——重启之后第一轮之前、这辆车上一轮没跑完——
    /// 沿用上一次落库的判定（<c>FullSlotPositionsJson</c>），而不是当成「没有这样的候选」：后者会让一次重启给车发两张来回翻的快照。
    /// </para>
    /// <para>
    /// 车的仓位模型未解析、或读不到车载端事实时说不出来，返回空，由 <see cref="LoadingPhaseMachine"/> 按「不改判」处理。
    /// </para>
    /// </remarks>
    private async Task<bool?> JudgeVehicleFullAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        VehicleSlotPositions? positions = await new VehicleSlotPositionReader(dbContext)
            .ReadAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (positions is null || onboard is null || positions.PhysicalSlotCountByGroup.Count == 0)
        {
            return null;
        }

        IReadOnlySet<string> ownCargoBlocked = slotGroupFullness.OwnCargoBlockedGroupsOf(runtime.AgvId)
            ?? PersistedFullSlotPositions(runtime);
        JourneyAwareSlotLedger ledger = new(dbContext);
        List<string> full = [];
        foreach (string group in positions.PhysicalSlotCountByGroup.Keys.Order(StringComparer.Ordinal))
        {
            int free = await ledger
                .ReadAvailableSlotCountAsync(runtime.AgvId, onboard, positions, group, cancellationToken)
                .ConfigureAwait(false);
            if (free == 0 || ownCargoBlocked.Contains(group))
            {
                full.Add(group);
            }
        }

        string fullJson = JsonSerializer.Serialize(full);
        if (!string.Equals(runtime.FullSlotPositionsJson, fullJson, StringComparison.Ordinal))
        {
            runtime.FullSlotPositionsJson = fullJson;
        }

        return full.Count == positions.PhysicalSlotCountByGroup.Count;
    }

    /// <summary>
    /// 待送点取货停靠上车该不该自己走（control-server#710，<see cref="StagingDepartureRule"/>）：该走答真，交给 <see cref="LoadingPhaseMachine"/>
    /// 当作「满」，判出 <c>VEHICLE_FULL</c>；说不出来为空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>每一轮重判，不再整趟锁住</b>（CP-0018，control-server#800；取代 CP-0012 第五节②的锁）：判过该走之后情形不再成立就答假，
    /// <see cref="LoadingPhaseMachine"/> 撤回成持货等单。只在两处保持：离站安全核验已发出（状态机原有的保持点），以及这里的两处——为离站请求移动的那一轮
    /// （<paramref name="departing"/>），与本停靠已受理出发（<see cref="JourneyStopRow.OperatorDepartedAt"/>，cs#780 独立审查 M1：受理时不改关闭原因，
    /// 不保持就会在核验发出之前被撤回，操作员拿到了受理、没装的已放回，车却等到持货期限）。
    /// </para>
    /// <para>
    /// 「每一侧都满」<b>只按已装货物</b>：会话基线里这一侧的仓，去掉已安全闭环装上车的需求占的仓（<see cref="SlotRoomAtStaging"/>），一个不剩才算满。
    /// 派车的账本（<see cref="JourneyAwareSlotLedger"/>，已装加已预留）不读：判满比派车严，判满成立时派车也必然没有空仓（CP-0018 修订项一末句）。
    /// 「等着的需求」读派车轮上一轮对这辆车的全部裁决（<see cref="SlotGroupFullnessBoard.CandidateVerdictsOf"/>），只因预留装不下的
    /// （<see cref="StagingDepartureRule.OnlyOwnReservations"/>）与只因禁用仓装不下的（<see cref="StagingDepartureRule.OnlyDisabledSlots"/>，CP-0024）不计入。<c>FullSlotPositionsJson</c> 写哪几侧满（已装占满，或有候选被已装货物挡住）。
    /// </para>
    /// <para>
    /// <b>车到站之后才判</b>（<paramref name="arrived"/>，审查 L5）：开往待送点的那一段（<c>AwaitingPickupArrival</c>）答说不出来。
    /// </para>
    /// </remarks>
    private async Task<bool?> JudgeStagingDepartureAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JourneyStopRow standing,
        bool arrived,
        bool departing,
        CancellationToken cancellationToken)
    {
        if (runtime.LoadingPhaseState == LoadingPhaseStates.VehicleFull && (departing || standing.OperatorDepartedAt is not null))
        {
            return true;
        }
        // 车还在开往待送点的路上不判（control-server#710 审查 L5）：路上那几轮等着的需求与到站时未必一样。说不出来，不改判。
        if (!arrived)
        {
            return null;
        }
        IReadOnlyList<DispatchCandidateVerdict>? verdicts = slotGroupFullness.CandidateVerdictsOf(runtime.AgvId);
        if (verdicts is null)
        {
            return null;
        }

        VehicleSlotPositions? positions = await new VehicleSlotPositionReader(dbContext)
            .ReadAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        bool? everyGroupFull = null;
        IReadOnlyDictionary<string, StagingDepartureRule.SlotRoom>? room = null;
        if (positions is not null && onboard is not null && positions.PhysicalSlotCountByGroup.Count > 0)
        {
            room = SlotRoomAtStaging(stops, onboard, positions);
            IReadOnlySet<string> blockedByLoadedCargo = StagingDepartureRule.GroupsBlockedByLoadedCargo(verdicts, room);
            string[] full = [.. room
                .Where(group => group.Value.FreeOfLoadedCargo == 0 || blockedByLoadedCargo.Contains(group.Key))
                .Select(group => group.Key)];
            string fullJson = JsonSerializer.Serialize(full);
            if (!string.Equals(runtime.FullSlotPositionsJson, fullJson, StringComparison.Ordinal))
            {
                runtime.FullSlotPositionsJson = fullJson;
            }
            everyGroupFull = room.Values.All(group => group.FreeOfLoadedCargo == 0);
        }

        return StagingDepartureRule.Decide(new StagingDepartureRule.Facts(
            everyGroupFull,
            StagingDepartureRule.WaitingAt(verdicts, standing.StationRiotId, room)));
    }

    /// <summary>
    /// 每一侧的仓位余量（control-server#800）。只扣已装货物还空着的（CP-0018 修订项一）：会话基线里这一侧的仓（禁用的不在里面，CP-0024 的「可用仓位」），
    /// 去掉这趟旅程上已安全闭环装上车（归属 <c>LOADED</c>）的需求占的仓；已预留、已分仓而还没装的不扣，正在装的那一批也不扣——它还没有安全闭环。
    /// 能用的：会话基线加上这一侧已装货物占着的仓（装货途中重连，新会话的基线只收那时为空的仓，cs#800 独立审查 M1）。按侧名排序。
    /// </summary>
    private static SortedDictionary<string, StagingDepartureRule.SlotRoom> SlotRoomAtStaging(
        JourneyStopCursor stops,
        OnboardDispatchFacts onboard,
        VehicleSlotPositions positions)
    {
        HashSet<int> loaded = [.. stops.Demands
            .Where(item => item.Membership.Status == JourneyDemandStatuses.Loaded)
            .SelectMany(item => JsonSerializer.Deserialize<int[]>(item.Membership.TargetSlotsJson) ?? [])];
        SortedDictionary<string, StagingDepartureRule.SlotRoom> room = new(StringComparer.Ordinal);
        foreach (string group in positions.PhysicalSlotCountByGroup.Keys)
        {
            IReadOnlyList<int> baseline = SessionBaselineSlotLedger.BaselineSlots(onboard, positions, group);
            int loadedHere = loaded.Count(slot =>
                positions.SlotPositionByPhysicalSlot.TryGetValue(slot, out string? position) &&
                string.Equals(position, group, StringComparison.Ordinal));
            room[group] = new StagingDepartureRule.SlotRoom(
                Usable: baseline.Count(slot => !loaded.Contains(slot)) + loadedHere,
                FreeOfLoadedCargo: baseline.Count(slot => !loaded.Contains(slot)));
        }
        return room;
    }

    /// <summary>
    /// 让站已触发，而且早于持货期限（批次7-08，control-server#213）。晚于期限的触发不算：期限先到，结束原因是持货超时。
    /// 这种触发只会出现在期限已过、而这辆车还没来得及判的时候——断联期间推进段不判装货阶段。
    /// </summary>
    private bool YieldTriggeredBeforeHoldingDeadline(JourneyRuntimeRow runtime, JourneyStopCursor stops) =>
        runtime.YieldTriggeredAt is { } triggeredAt &&
        (runtime.CargoHoldingStartedAt is not { } startedAt ||
         StandsAtStaging(stops) && !CargoOnBoard(stops) ||
         triggeredAt < startedAt + runtimeOptions.CargoHoldingTimeout);

    /// <summary>
    /// 持货期限已过：第一个 <c>LoadBatch</c> 闭环那一刻起算，服务端此刻的钟（ADR-cross-0057）。闭环之前恒为假。
    /// </summary>
    /// <remarks>
    /// 车停在待送点取货停靠时（control-server#727，CP-0014）：起算点是使车上有货的那一批闭环、之后以本站为取货站的追加挪过的那一个
    /// （<see cref="MoveStagingHoldingStartAsync"/>），车上一篮没有时恒为假——货在两轮之间被撤销到零篮，起算点要到下一轮开头才撤掉，
    /// 这一轮不能拿着旧起算点判到期。control-server#709 时这里在待送点恒为假。按当前停靠判，不按整趟旅程（control-server#710 审查 M1）。
    /// </remarks>
    private bool HoldingDeadlinePassed(JourneyRuntimeRow runtime, JourneyStopCursor stops, DateTimeOffset now) =>
        (!StandsAtStaging(stops) || CargoOnBoard(stops)) &&
        runtime.CargoHoldingStartedAt is { } startedAt && now >= startedAt + runtimeOptions.CargoHoldingTimeout;

    /// <summary>旅程上有已装上的货（<c>LOADED</c>）：待送点持货期限的前提（control-server#727）。</summary>
    private static bool CargoOnBoard(JourneyStopCursor stops) =>
        stops.AllDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Loaded);

    /// <summary>
    /// 车停在待送点取货停靠上时，按 <see cref="StagingCargoHoldingStart"/> 把持货起算点挪到位（control-server#727）：车上没货就撤掉，有以本站为取货站的
    /// 新追加就挪到最晚那一条的追加时刻。只暂存，与随后的装货阶段快照同一次保存（调用方）。挪了返回真。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>追加从归属行的 <c>AddedAt</c> 读，不在追加那一侧写</b>。追加在派车轮的事务里落库（基础设施层），那里不认得待送点；从这里读，
    /// 晚至多一轮轮询，与装货闭环那一处取「此刻」而不取车报时刻同一个取舍。被移除的归属也读：追加之后、引擎读到之前就被放回的那一条，
    /// 追加照样是事实；放回本身不是输入，所以挪不动期限（CP-0014「Demand 的释放、取消或作业清单的其它变化不重新起算」）。
    /// </para>
    /// <para>
    /// 「以本站为取货站」按取货停靠的站认：同站新开的停靠与并进的停靠（REQ-0196、control-server#714）都算。装货阶段已关闭时不动：关了不接追加，
    /// 期限按 program#94 保留原值。
    /// </para>
    /// </remarks>
    private async Task<bool> MoveStagingHoldingStartAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!StandsAtStaging(stops) ||
            runtime.LoadingPhaseState == LoadingPhaseStates.Closed ||
            runtime.CargoHoldingStartedAt is null)
        {
            return false;
        }
        string stationId = stops.Current.StationId;
        string[] pickupStopsHere = [.. (await dbContext.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.Pickup)
                .Select(row => new { row.StopId, row.StationId })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => string.Equals(row.StationId, stationId, StringComparison.Ordinal))
            .Select(row => row.StopId)];
        DateTimeOffset[] appendedHere = [.. (await dbContext.Set<JourneyDemandRow>().AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId && pickupStopsHere.Contains(row.PickupStopId))
                .Select(row => row.AddedAt)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))];
        DateTimeOffset? next = StagingCargoHoldingStart.Next(runtime.CargoHoldingStartedAt, CargoOnBoard(stops), appendedHere);
        if (next == runtime.CargoHoldingStartedAt)
        {
            return false;
        }
        runtime.CargoHoldingStartedAt = next;
        runtime.UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// 装货阶段已因持货超时关闭，而这趟旅程还带着没开始装的需求：逐条从这趟旅程上释放、退回积压等改派（ADR-cross-0057 实施决定（三），
    /// 用户 2026-10-14 定「截掉，放回待派队列」，control-server#290）。释放了就返回之后重新加载的游标；没有要截的、或这一轮不能截时返回空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么是释放而不是终结。</b>ADR 原文是以 <c>CANCELLED_BY_STOP_COMPLETE</c> 终结。那个码按业务键永久抑制
    /// （<see cref="PickupStopTermination.KeySuppressingReasonCodes"/>），受理行又不删，同一条 MES 需求从此 v2 再也不派：货留在机台、
    /// 没人来搬，等于静默丢单。释放改派（REQ-0328 那条路，<see cref="DemandReleaseService.StageReleasedMembershipAsync"/>）只把归属标移除、
    /// 积压行清掉受理标记：需求仍是 <c>Accepted</c>、不写抑制、等待年龄不重置，下一轮派车按「已释放待改派」重新受理
    /// （<c>DemandJourneyLookup.ReleasedForRedispatch</c>），不能追加回这一趟（<c>EnRouteAppendCriterion</c>），只能给别的车或新旅程。
    /// </para>
    /// <para>
    /// <b>「没开始装」= 归属还是 <c>PENDING_LOAD</c>、需求还是 <c>Accepted</c></b>，不分在车此刻所在的停靠还是后面的取货停靠：期限结束的是
    /// 整个装货阶段，不是某一站。已经下了装货命令的（<c>LOADING</c>）不在内——装货阶段本来就等那一批闭环才关（<see cref="LoadingPhaseMachine"/>），
    /// 已经装上的照常送到。锚需求不释放：旅程行上的离站核验、计划与收尾都按它取需求号，它的归属一移除这些全取不到。按 REQ-0196
    /// （途中追加不并入当前停靠），锚需求总在第一个取货停靠、而那一站从受理起就是当前停靠，所以「锚需求没开始装而持货期限已到」构造上不出现；
    /// 保留这条例外是纵深防御（cs#290 再审 L2）。
    /// </para>
    /// <para>
    /// <b>释放服务的拒绝规则（<see cref="DemandReleaseRules.Decide"/>）在这里逐条判过</b>：
    /// 「当前下一站、车已到站」不释放，理由是清单号：释放让这个停靠的归属少了，按版数算的号与范围都会往回缩。持货期限是硬期限，这一格不能拒，
    /// 改为放回几条就给这个停靠补几版（<see cref="JourneyStopRow.WorklistRefills"/>），再发那张空清单（<see cref="StopEndWorklist.StageAfterReleaseAsync"/>）。
    /// 空清单自己取「算式」与「已发最高号 + 1」中大的那个，所以不补也不会撞号；补版数守的是另一件事：退役本站较早各版按本停靠的版数范围数，
    /// 不补就漏掉车已收到的那一版（审查返工第 2 轮，C11 转 cs#703 实测；第 1 轮变异里删掉它没红，是因为那时没有「本站一条都没做完」的用例）。「当前下一站、车在路上、旅程还有别的需求」
    /// 不释放，理由是取消那张单之后没人把车派往下一站；这里不取消那张单——当前下一站在路上不可删（REQ-0197），车照样开到那里、到了没有作业、
    /// 按离站等待离开，所以这一格不适用。「锚需求」见上一段。取货停靠不是当前下一站的，按构造还没有 RIoT 订单（追加的停靠到车离开上一站才建单）。
    /// </para>
    /// <para>
    /// <b>按状态判，不按「这一次刚关」判。</b>关闭与截断是两次保存（关闭随装货阶段快照一起存），崩在中间，下一轮读到的状态已是关闭、没有变化；
    /// 按「刚关」判的实现从此再也不截。按状态判，每一轮都重来一遍，截完之后就没有可截的，它自然停下。
    /// </para>
    /// <para>
    /// <b>在写锁里重读再截</b>，与站点期限那一处同一个理由：扫码前取消在别的连接上被授权，锁外读到的停靠可能已经过时。当前停靠上有扫码前取消
    /// 开着时这一轮整个不截，等车报结果，<b>不论阶段</b>：同站另一条正在装（<c>AwaitingLoadResult</c>）时也能对待装的那条发起扫码前取消
    /// （<c>OnboardRecoveryCoordinator</c> 只要求它是 <c>PENDING_LOAD</c>），而截断最常发生的正是那一批闭环的那一轮。带着开着的取消把它
    /// 释放掉，车报来的取消结果按需求找旅程找不到（它已不在任何旅程上）就抛，连接断开、重连重放、反复断线（审查 H1）；
    /// 它若已被改派，那条取消还会扣住新旅程。取消要车证明整排仓位是空的，所以同站其余的也一起等（与站点期限同一条规矩）。等录入那一段在这之后读到的录入，下命令前在写锁里还要再判一次（<see cref="EnteredDemandStillLoadableAsync"/>，
    /// 它只认生效的归属），被释放的那条在那里让开；本站已结束时，那条录入由 <see cref="StopEndWorklist"/> 一并答过时。
    /// </para>
    /// <para>
    /// 会话未就绪时推进段不跑，这里也不跑：真车载端带着本服务端的单行驶时整段未就绪，截断发生在到站、会话回到就绪的那一轮。那一站作为当前下一站
    /// 在路上不可删（REQ-0197），车照样开到那里，没有作业，按离站等待离开。
    /// </para>
    /// </remarks>
    private async Task<JourneyStopCursor?> TruncateAtCargoHoldingDeadlineAsync(
        JourneyRuntimeRow runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtime.LoadingPhaseState != LoadingPhaseStates.Closed ||
            runtime.LoadingClosedReason != LoadingClosedReasons.CargoHoldingTimeout ||
            runtime.Stage is JourneyRuntimeStage.Blocked or JourneyRuntimeStage.Completed)
        {
            return null;
        }
        if (!NotYetStarted(runtime, await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false)).Any())
        {
            return null;
        }

        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .Select(row => (JourneyRuntimeStage?)row.Stage)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stageNow is null or JourneyRuntimeStage.Blocked or JourneyRuntimeStage.Completed ||
            await StageCargoHoldingTruncationAsync(runtime, CargoHoldingTruncationReason, now, cancellationToken)
                .ConfigureAwait(false) is not { } leaving)
        {
            return null;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset deadline = runtime.CargoHoldingStartedAt!.Value + runtimeOptions.CargoHoldingTimeout;
        foreach (string demandId in leaving)
        {
            LogCargoHoldingDeadlineEndedDemand(logger, runtime.AgvId, deadline, demandId, null);
        }
        if (runtime.Stage == JourneyRuntimeStage.Completed)
        {
            await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
            return await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        }
        // 车所在的这一站因此空了：那张空清单随上面那次保存落库，提交之后发（control-server#324）。没有暂存就什么也不发。
        await StopEndWorklist.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        return await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 持货截断的那一次改动（只暂存，调用方在自己的写事务里保存）：这趟旅程上没开始装的需求放回待派、版数守恒、计划修订，本站因此空了就暂存一张以
    /// <paramref name="stopEndReason"/> 结束的空清单，只剩没开始装的就收尾旅程。返回放回的需求；没有可截的、或本站开着扫码前取消时什么也不做，返回空。
    /// </summary>
    /// <remarks>
    /// 持货期限到期（<see cref="TruncateAtCargoHoldingDeadlineAsync"/>）与机台取货装货阶段还开着时的操作员出发（control-server#725，
    /// <c>StageMachineDepartureAsync</c>）共用这一份：出发「等同持货期限此刻到期」（CP-0014），两条路只差空清单与收尾说的结束原因。
    /// 出发要把放回与答复同一次提交（向量的 <c>departure-decision-and-releases-durable-before-result</c>），所以这里不保存、不提交、不发送。
    /// </remarks>
    private async Task<string[]?> StageCargoHoldingTruncationAsync(
        JourneyRuntimeRow runtime,
        string stopEndReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 锁里重新加载：默认那一份读库，看得见锁外那次读之后别的连接提交的终结与取消。
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        JourneyStopDemand[] unstarted = [.. NotYetStarted(runtime, stops)];
        // 取货停靠不是当前停靠、却已经有订单意图：离站已授权、开往它的单已保存甚至已在 RIoT 建了，而阶段还没前移（保存冲突或重启，
        // 窗口很窄）。这时截掉它，下一轮会按截断之后的下一站重新核验、再建一张单，同一辆车两张单（cs#290 再审 L3）。与释放服务的
        // PickupOrderExistsAsync 同一个判据：这一站不截，它上面的需求留在原趟，车到了照常处理。当前停靠有订单是常态（车正开往它），
        // 截它不删停靠、不建新单，所以只看后面的停靠。
        string currentStopIdNow = stops.Current.StopId;
        List<JourneyStopDemand> withoutOrder = [];
        foreach (JourneyStopDemand item in unstarted)
        {
            JourneyStopRow pickup = stops.Stops.Single(row => row.StopId == item.Membership.PickupStopId);
            if (!string.Equals(pickup.StopId, currentStopIdNow, StringComparison.Ordinal) &&
                await dbContext.OrderIntents.AsNoTracking()
                    .AnyAsync(row => row.UpperId == pickup.UpperId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            withoutOrder.Add(item);
        }
        unstarted = [.. withoutOrder];
        if (unstarted.Length == 0 ||
            await OpenCancellationAtCurrentStopAsync(stops, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // 释放之后本停靠此刻那一版录入请求按算式就算不回来了，先取好（同终结那一处）。
        string? sublotRequestMessageId = stops.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision);
        string currentStopId = stops.Current.StopId;
        string[] leaving = [.. unstarted.Select(item => item.Demand.DemandId)];
        int releasedFromCurrentStop = unstarted.Count(item =>
            string.Equals(item.Membership.PickupStopId, currentStopId, StringComparison.Ordinal));
        bool last = !stops.Demands.Any(item => !leaving.Contains(item.Demand.DemandId, StringComparer.Ordinal));
        foreach (string demandId in leaving)
        {
            JourneyDemandRow membership = await dbContext.Set<JourneyDemandRow>()
                .SingleAsync(
                    row => row.JourneyId == runtime.JourneyId && row.DemandId == demandId && row.RemovedAt == null,
                    cancellationToken)
                .ConfigureAwait(false);
            await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, now, cancellationToken).ConfigureAwait(false);
        }
        if (releasedFromCurrentStop > 0)
        {
            // 版数守恒：这个停靠的归属少了几条，就补上几版。空清单取号时自己会取「已发最高号 + 1」，但退役本站较早那几版清单按的是
            // 本停靠的版数范围（首号到算式那一号）：不补，本站一条都还没做完时算式不动，车已收到的那一版落在范围外、不退役，重连后被补发、
            // 号比空清单低，车判回退断会话（TruncatingTheStopTheVehicleStandsAtRetiresEveryEarlierWorklistAndAReconnectReplaysOnlyTheEmptyOne
            // 去掉这一段时红在这里）。收尾那一支同样按这个范围算迟到录入的地址，所以两支都补。
            (await TrackedStopAsync(currentStopId, cancellationToken).ConfigureAwait(false)).WorklistRefills +=
                releasedFromCurrentStop;
        }
        if (last)
        {
            // 没开始装的之外一条都没有了（cs#647 同站先卸后装之后才可能）：与释放服务释放最后一条同一个做法，计划收尾、旅程关闭、三套占用一起放。
            await JourneyPlanRevisionStage.StageAsync(
                    dbContext, runtime.JourneyId, leaving, currentStopMayGo: true, routing: null, cancellationToken)
                .ConfigureAwait(false);
            await new PickupStopTermination(dbContext).StageJourneyClosureAsync(
                runtime, sublotRequestMessageId, stopEndReason, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await JourneyPlanRevisionStage.StageAsync(
                    dbContext, runtime.JourneyId, leaving, currentStopMayGo: false, routing: null, cancellationToken)
                .ConfigureAwait(false);
            await StopEndWorklist.StageAfterReleaseAsync(
                dbContext, runtime, releasedFromCurrentStop > 0, sublotRequestMessageId, stopEndReason, now,
                cancellationToken).ConfigureAwait(false);
        }
        return leaving;
    }

    /// <summary>没开始装、可以释放的需求：归属待装、需求仍受理中，锚需求除外（见上）。</summary>
    private static IEnumerable<JourneyStopDemand> NotYetStarted(JourneyRuntimeRow runtime, JourneyStopCursor stops) =>
        stops.AllDemands.Where(item =>
            item.Demand.Status == DemandExecutionStatus.Accepted &&
            item.Membership.Status == JourneyDemandStatuses.PendingLoad &&
            !string.Equals(item.Demand.DemandId, runtime.DemandId, StringComparison.Ordinal));

    private static HashSet<string> PersistedFullSlotPositions(JourneyRuntimeRow runtime) =>
        runtime.FullSlotPositionsJson is { } json
            ? (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 装货阶段变了发给车的那一张车辆业务状态快照（批次7-07，control-server#212）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>修订号是「上一号 + 1」，并把基准一起抬一号</b>，与途中追加整体重发计划（<see cref="RefreshUpcomingStopPlanAsync"/>）
    /// 同一个做法、同一个理由：到站那一张发的是「基准 + 序位 - 1」，两次到站之间没有空号。车停在一个停靠上时上一号是这个停靠
    /// 到站那一张（或本停靠更早的一张装货阶段快照）；车在路上时上一号是上一个停靠的，比当前停靠的算式少一。基准抬一号之后，
    /// 两种情形下「当前停靠的算式」都恰好等于（或比它大一）这一号，下一次到站自然落在它之上。车载端按消息类型记修订号，
    /// 号同内容不同与号回退都会当场拆会话。
    /// </para>
    /// <para>
    /// <b>messageId 由停靠与修订号派生</b>（<c>StableGuid</c>），不取一个 attempt 或状态名：同一个停靠上 WAIT 与 FULL 可以
    /// 来回出现几次，用状态名派生，第二次 WAIT 会撞上第一次那一行。
    /// </para>
    /// <para>
    /// <b>本停靠更早那一张还没确认的，先退役。</b>理由与清单升版那一处相同：重放按未确认的行从旧到新补发，旧的一张补发到一辆
    /// 已经采纳了新号的车上，就是 <c>SNAPSHOT_REVISION_REGRESSION</c>。退役只打标记、不单独保存，与状态、快照同一次保存。
    /// </para>
    /// </remarks>
    private async Task PublishLoadingPhaseAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JourneyStopRow stop,
        bool arrived,
        SessionRecoveryRow session,
        bool holdingApplicable,
        CancellationToken cancellationToken)
    {
        runtime.VehicleBusinessRevision += 1;
        long revision = StopRevision(runtime.VehicleBusinessRevision, stop) - (arrived ? 0 : 1);
        if (arrived)
        {
            await FenceSupersededSnapshotAsync(stop.VehicleBusinessMessageId, cancellationToken).ConfigureAwait(false);
        }
        await FenceSupersededSnapshotAsync(LoadingPhaseMessageId(stop, revision - 1), cancellationToken)
            .ConfigureAwait(false);
        await publisher.PublishVehicleBusinessStateAsync(
            LoadingPhaseMessageId(stop, revision),
            runtime.AgvId,
            session.SessionGeneration,
            TransportBusinessState(revision, CurrentLoadingPhase(runtime, holdingApplicable, stops), PublishedBatteryState(runtime)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 到站那一张车辆业务状态快照之前，把车在路上时发的那一张装货阶段快照退役（若还没确认）。它的号正好比到站那一张小一。
    /// </summary>
    private Task FenceLoadingPhaseSnapshotSentOnTheWayAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken) =>
        FenceSupersededSnapshotAsync(
            LoadingPhaseMessageId(stop, StopRevision(runtime.VehicleBusinessRevision, stop) - 1), cancellationToken);

    /// <summary>
    /// 这个停靠到站那一张车辆业务状态已经排给了车时，返回它的修订号；还没有排过返回空（control-server#339）。
    /// </summary>
    private async Task<long?> ArrivalBusinessStateQueuedAsync(JourneyStopRow stop, CancellationToken cancellationToken)
    {
        string? queued = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageId == stop.VehicleBusinessMessageId)
            .Select(row => row.PayloadJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (queued is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(queued);
        return document.RootElement.GetProperty("payload").GetProperty("vehicleBusinessStateRevision").GetInt64();
    }

    /// <summary>
    /// 到站那一张车辆业务状态已经被取代：它排给车之后，这个停靠又按更高的号发过装货阶段快照（control-server#339）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只在到站那一段重跑时成立：断线打断了那一段、阶段没前移，重连后装货阶段在第一轮开头就变了（持货等单的旅程进出等单、装满、
    /// 结束）。<see cref="ReconcileLoadingPhaseAsync"/> 认出车已被告知到站，把新的状态按到站之后的号发成一张装货阶段快照，基准随之抬一号。
    /// 重跑再以到站那一张的 id 发，按新的基准算出来的号与 <c>loadingPhase</c> 都与车已确认的那一张不同——同一 id 下内容变了，
    /// 重放校验每轮都拒。车需要知道的已经在更新的那一张里，所以不再发它。
    /// </para>
    /// <para>
    /// 没有被取代时（号仍等于按当前基准算的到站号），照旧发，由沿用那一条认出车已确认的那一版。在路上就变了的装货阶段不走到这里：
    /// 那时到站那一张还没排过，到站时照常按抬过的基准发（批次7-07）。
    /// </para>
    /// </remarks>
    private async Task<bool> ArrivalBusinessStateSupersededAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken) =>
        await ArrivalBusinessStateQueuedAsync(stop, cancellationToken).ConfigureAwait(false) is { } queued &&
        queued < StopRevision(runtime.VehicleBusinessRevision, stop);

    /// <summary>一张装货阶段快照的 messageId：停靠加修订号。</summary>
    private static string LoadingPhaseMessageId(JourneyStopRow stop, long revision) =>
        JourneyPlanBuilder.StableGuid($"{stop.StopId}|{revision}", "loading-phase");

    /// <summary>
    /// 同 <see cref="RetireSupersededSnapshotAsync"/>，但不单独保存：由紧接着的那次发布一起保存，退役与新的一张要么都在、要么都不在。
    /// </summary>
    private async Task FenceSupersededSnapshotAsync(string messageId, CancellationToken cancellationToken)
    {
        ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(
                item => item.MessageId == messageId && item.AcknowledgedAt == null && item.FencedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            row.FencedAt = timeProvider.GetUtcNow();
        }
    }

    private static string BusinessHash(string demandId, string sublot, string operation, IEnumerable<int> slots) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{demandId}|{sublot}|{operation}|{string.Join(',', slots)}"))).ToLowerInvariant();

    // Derived rather than stored: the row predates this snapshot, and a deterministic id from the
    // demand is what the stored ones are anyway (WireToGateStore.ToRuntimeRow), without a migration.

    /// <summary>
    /// 车辆业务状态这条流在这个停靠上发的是第几号：每个停靠各发一张，所以是基准加序位。
    /// </summary>
    /// <remarks>
    /// 在批次7-03 之前这个偏移写死在两个发布方法里——取货那段发基准，关卡那段发基准 +1。写死的 +1 就是「这趟只有两个
    /// 停靠」这个假设本身。单需求两停靠下这里算出来的还是基准与基准 +1。
    /// <b>清单不走这条算式</b>（批次7-06）：一个停靠上的清单会发不止一版，见 <see cref="WorklistRevisionAt"/>。
    /// </remarks>
    private static long StopRevision(long journeyBase, JourneyStopRow stop) =>
        journeyBase + stop.Sequence - 1;

    /// <summary>
    /// 计划流在这一刻发的是第几号。它比车辆业务状态那条多一张：派车时先发一张「车还在路上」的计划
    /// （CV-DEMAND-ACCEPT-TO-PICKUP），之后每到一个停靠再发一张。所以号数就是「到站几次」——派车时零次，
    /// 取货到站一次，卸货到站两次。
    /// </summary>
    private static long PlanRevisionAt(long journeyBase, JourneyStopRow stop, bool arrivedAtStop) =>
        journeyBase + (arrivedAtStop ? stop.Sequence : stop.Sequence - 1);

    /// <summary>
    /// 一个停靠的离站核验身份：取货停靠从停靠行上取，卸货停靠按停靠 id 派生（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这两个方法原本的注释说「卸货停靠是旅程的终点，没有『离开之前』可言」，而本票推翻的正是那个前提。</b>
    /// 多停靠计划里卸货停靠后面还可以有停靠，车卸完这一站要继续走——那一次离站和取货停靠的离站没有任何区别：
    /// 离站安全问的是「这辆车<b>现在</b>能不能安全开走」，与它在这一站是装了还是卸了无关，而车上可能还装着
    /// 后面几站要卸的货，那正是更需要问的情形。（车载端那一侧实读过：<c>HandlePreDepartureSafetyCheckAsync</c>
    /// 只比对安全状态版本、然后读 IO 模块当前快照作答，不看旅程阶段也不看上一站做了什么。）
    /// </para>
    /// <para>
    /// <b>卸货停靠派生而不是落库，是为了不动迁移。</b>停靠行由 <c>SingleDemandJourneyShape.Stops</c> 写下，
    /// 而它必须与批次7-01 迁移里的 SQL 回填<b>逐列一致</b>（<c>Batch7JourneyAcceptanceTests</c> 的
    /// <c>ANewlyAcceptedJourneyCannotBeToldApartFromTheSameJourneyBackFilledByTheMigration</c> 盯着这件事）。
    /// 在那里加两列就要改一个已经合入的迁移，而那比加一个新迁移更糟；派生则让<b>已经落库的卸货停靠行也直接可用</b>，
    /// 升级边界上不留一格需要回填的数据。派生用 <c>StableGuid(stopId, …)</c>，同一个停靠每次算出同一个值，
    /// 发出与结算读到的是同一个 id。
    /// </para>
    /// <para>
    /// <b>取货停靠仍然抛，护栏一字未动</b>：它的这两个 id 是受理时写下的，取不到就是那一行坏了。
    /// 只有卸货停靠这一支是新加的，而它「没有」不是缺陷，是那一列从设计上就没为它写过。
    /// </para>
    /// </remarks>
    private static string DepartureCheckId(JourneyStopRow stop) =>
        stop.DepartureSafetyCheckId
        ?? throw new InvalidDataException($"Stop '{stop.StopId}' has no pre-departure safety check id.");

    private static string DepartureCheckMessageId(JourneyStopRow stop) =>
        stop.DepartureSafetyCheckMessageId
        ?? throw new InvalidDataException($"Stop '{stop.StopId}' has no pre-departure safety check message id.");

    /// <summary>
    /// 卸货停靠第一次要离站时，把它的离站核验 id 补上并落库（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <b>写回行，而不是每次派生</b>：停靠行是这两个 id 的唯一真相，读它的地方不止推进段一处
    /// （测试驱动、结算、重连补发都读行），派生会让「行上的值」与「实际发出去的值」分家。写回之后
    /// 上面两个取值器一字未动，取货停靠缺 id 照样响亮地抛。
    /// </remarks>
    private async Task EnsureDepartureCheckIdsAsync(JourneyStopRow stop, CancellationToken cancellationToken)
    {
        if (stop.DepartureSafetyCheckId is not null && stop.DepartureSafetyCheckMessageId is not null)
        {
            return;
        }

        JourneyStopRow tracked = await TrackedStopAsync(stop.StopId, cancellationToken).ConfigureAwait(false);
        tracked.DepartureSafetyCheckId ??= JourneyPlanBuilder.StableGuid(stop.StopId, "unload-departure-safety-check");
        tracked.DepartureSafetyCheckMessageId ??=
            JourneyPlanBuilder.StableGuid(stop.StopId, "unload-departure-safety-request");
        stop.DepartureSafetyCheckId = tracked.DepartureSafetyCheckId;
        stop.DepartureSafetyCheckMessageId = tracked.DepartureSafetyCheckMessageId;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 途中追加整体重发（<see cref="RefreshUpcomingStopPlanAsync"/>）那一版计划的 messageId：停靠加修订号，修订号是
    /// <see cref="PlanRevisionAt"/> 在当前基准上算出的那一号。重发、补发集合（<see cref="RuntimeMessageIds"/>）与到站退役
    /// 三处都从这里取，不会各算各的。
    /// </summary>
    /// <remarks>
    /// <b>只管重发那一版。</b>派往取货站那一版用 <see cref="PickupDispatchPlanMessageId"/>，到站那一版用停靠行上的
    /// <c>PlanMessageId</c>，都不是这个算式。id 不同，到站那一版就顶不掉路上收到、还没确认的这一版：补发会把它的旧号
    /// 补到一辆已经采纳了新号的车上（<c>SNAPSHOT_REVISION_REGRESSION</c>），所以到站时要显式退役它（批次7-07 审查）。
    /// </remarks>
    private static string ReSentPlanMessageId(JourneyRuntimeRow runtime, JourneyStopRow stop, bool arrivedAtStop) =>
        JourneyPlanBuilder.StableGuid(
            $"{stop.StopId}|{PlanRevisionAt(runtime.PlanRevision, stop, arrivedAtStop)}", "plan");

    /// <remarks>
    /// 按需求这一次受理的身份派生（批次7-10，control-server#215）。首次受理的旅程 <c>JourneyId</c> 就是
    /// <c>ForAnchorDemand(DemandId)</c>，照旧按 <c>DemandId</c> 算，一个字节不变；改派出来的旅程
    /// <c>JourneyId</c> 带代次、必与第一趟不同（<c>JourneyIdentity.DerivationKey</c>），按它算。只按
    /// <c>DemandId</c> 算，改派那一趟的 id 与第一趟相同，发件箱里已有那一行，派车计划就一次都不发。
    /// </remarks>
    private static string PickupDispatchPlanMessageId(JourneyRuntimeRow runtime) =>
        JourneyPlanBuilder.StableGuid(
            runtime.JourneyId == JourneyIdentity.ForAnchorDemand(runtime.TransportColumn(runtime.DemandId)) ? runtime.TransportColumn(runtime.DemandId) : runtime.JourneyId,
            "pickup-dispatch-plan");

    /// <summary>
    /// 车离开当前停靠之后要去的那一个：序位上紧接着的、还开着的停靠（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// 取不到就是在最后一个停靠上问了「下一段腿去哪」——那一刻旅程该结束而不是再开一张订单，所以响亮地停下。
    /// </remarks>
    private static JourneyStopRow NextStopAfterCurrent(JourneyStopCursor stops) =>
        stops.OpenStops.FirstOrDefault(stop => stop.Sequence > stops.Current.Sequence)
        ?? throw new InvalidDataException(
            $"Stop {stops.Current.StopId} is the last one; there is no next leg to depart on.");

    /// <summary>
    /// 这个停靠上有没有一条还没结的扫码前取消。
    /// </summary>
    /// <remarks>
    /// 按<b>停靠</b>问而不是按需求（批次7-06）：取消要车证明那一排仓位是空的，那是一次整排的证明，所以一条需求的取消
    /// 开着，这个停靠上的每一条都等着——既不开仓，也不让站点期限结束这个停靠。
    /// </remarks>
    private async Task<bool> OpenCancellationAtCurrentStopAsync(
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        foreach (JourneyStopDemand item in stops.CurrentStopDemands)
        {
            if (await LoadCancellationBeforeSublot
                    .HasOpenCancellationAsync(dbContext, item.Demand.DemandId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 在写锁里重读：被录入的那条需求此刻还能不能装（control-server#362）。旅程仍在这个停靠等录入、需求仍是 Accepted、
    /// 它的归属仍是游标读到的那个状态、这个停靠上没有开着的扫码前取消——四条都成立才下命令。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 调用方在 BEGIN IMMEDIATE 里调用，所以这里全部读库、不读被跟踪的实体：被跟踪的那一份是这一轮开头读的，正是要复核的旧读。
    /// </para>
    /// <para>
    /// 入站能插进来、这四条要挡的写法有三种：
    /// </para>
    /// <list type="bullet">
    /// <item>乙自己的终结（<see cref="PickupStopTermination"/>，扫码前取消的 ALL_EMPTY 走它）：需求 Cancelled、归属 TERMINATED，阶段不动。</item>
    /// <item>乙的取消结果证明不了空（<c>OnboardRecoveryCoordinator.KeepDemandAndJourneyBlockedAsync</c>）：需求 RecoveryRequired、旅程 Blocked，
    /// 归属不动。</item>
    /// <item>同站另一条需求的扫码前取消在锁外查过之后被授权（它只用那条需求自己的录入去挡）：乙的需求、归属、阶段<b>都不动</b>，
    /// 只有第四条挡得住——取消要车证明整排仓位是空的，这时给乙开仓就是在证明进行中往里装。</item>
    /// </list>
    /// <para>
    /// 各条的护栏（evidence/cs362 反向验证）：第四条单删就红（R5）。需求一条单独就挡得住前两种；归属、阶段两条只在组合层面有护栏——
    /// 单删不红，与需求一起删才红（R23、R24）。它们留着，是为了不把正确性押在「终结与转阻塞总是连带写需求」上。
    /// </para>
    /// <para>
    /// <b>control-server#357 之后</b>（evidence/cs357/green/04）：写了旅程行的入站写（转阻塞必写 Blocked）在这之前就被旅程行的版本冲突挡住，
    /// 这台车这一轮让开、走不到这里，所以 R24 不再红。<b>阶段</b>一条因此没有护栏了：阶段一变就是旅程行被写，版本必然跟着变，要让它单独
    /// 起作用只能绕开保存钩子，而那正是 <c>JourneyRowWriteArchitectureTests</c> 禁止的——它是有意保留、没有护栏的纵深防御。
    /// <b>需求</b>一条补了只写需求表的那一格（<c>ADemandHeldForRecoveryWithoutItsJourneyBeingWrittenIsNotLoaded</c>），单删它就红。
    /// R5、R23 照旧。
    /// </para>
    /// </remarks>
    private async Task<bool> EnteredDemandStillLoadableAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JourneyStopDemand entered,
        CancellationToken cancellationToken)
    {
        JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .Select(row => (JourneyRuntimeStage?)row.Stage)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stageNow != JourneyRuntimeStage.AwaitingSublot)
        {
            return false;
        }
        DemandExecutionStatus? demandNow = await dbContext.AcceptedDemands.AsNoTracking()
            .Where(row => row.DemandId == entered.Demand.DemandId)
            .Select(row => (DemandExecutionStatus?)row.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (demandNow != DemandExecutionStatus.Accepted)
        {
            return false;
        }
        string? membershipNow = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId && row.DemandId == entered.Demand.DemandId && row.RemovedAt == null)
            .Select(row => row.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(membershipNow, entered.Membership.Status, StringComparison.Ordinal))
        {
            return false;
        }
        return !await OpenCancellationAtCurrentStopAsync(stops, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 给被录入的那条需求定具体仓位（REQ-0352，control-server#754）：已经分过的读回原值；还没分的按它那一侧此刻编号最小的空仓
    /// 分 <c>ExpectedBasketCount</c> 个，写进被跟踪的归属行，由调用方的那次保存落盘。能分的不够时返回空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 能分的是这一侧会话基线里的空仓去掉已分给本车别的需求的（<see cref="JourneyAwareSlotLedger.ReadAssignableSlotsAsync"/>），
    /// 禁用的仓不在基线里。别的需求还没分到的只是数量，受理已保证这一侧够所有预留，所以先扫的拿最小的号，后扫的拿剩下里最小的。
    /// </para>
    /// <para>
    /// 侧取这条需求受理时冻结的那一版分区归属（分组版本仍在受理时冻结）。读不到冻结、或车的仓位模型与车载端事实读不到，
    /// 都按「能分的不够」处理：猜一侧开错门，比让操作员取消重派更糟。
    /// </para>
    /// <para>
    /// 调用方在装货的写事务里调它（BEGIN IMMEDIATE）：同一辆车两次分配不会交错，读到的已分配集合就是此刻的。
    /// </para>
    /// </remarks>
    private async Task<JourneyDemandRow?> AssignTargetSlotsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopDemand entered,
        CancellationToken cancellationToken)
    {
        JourneyDemandRow membership = await TrackedMembershipAsync(runtime, entered.Demand.DemandId, cancellationToken)
            .ConfigureAwait(false);
        if ((JsonSerializer.Deserialize<int[]>(membership.TargetSlotsJson) ?? []).Length > 0)
        {
            return membership;
        }

        IReadOnlyDictionary<string, string> sides = await new DemandAreaAssignmentFreezeStore(dbContext)
            .ReadFrozenSlotPositionsAsync([entered.Demand.DemandId], cancellationToken).ConfigureAwait(false);
        VehicleSlotPositions? positions = await new VehicleSlotPositionReader(dbContext)
            .ReadAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (!sides.TryGetValue(entered.Demand.DemandId, out string? side) || positions is null || onboard is null)
        {
            return null;
        }

        IReadOnlyList<int> assignable = await new JourneyAwareSlotLedger(dbContext)
            .ReadAssignableSlotsAsync(runtime.AgvId, entered.Demand.DemandId, onboard, positions, side, cancellationToken)
            .ConfigureAwait(false);
        if (assignable.Count < membership.ExpectedBasketCount)
        {
            return null;
        }

        membership.TargetSlotsJson = JsonSerializer.Serialize(assignable.Take(membership.ExpectedBasketCount).ToArray());
        return membership;
    }

    /// <summary>被跟踪的那一份归属行，用来写状态；游标读出来的是 <c>AsNoTracking</c> 的。</summary>
    private Task<JourneyDemandRow> TrackedMembershipAsync(
        JourneyRuntimeRow runtime,
        string demandId,
        CancellationToken cancellationToken) =>
        dbContext.Set<JourneyDemandRow>()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.DemandId == demandId, cancellationToken);

    /// <summary>被跟踪的那一份停靠行，用来写状态。</summary>
    private Task<JourneyStopRow> TrackedStopAsync(string stopId, CancellationToken cancellationToken) =>
        dbContext.Set<JourneyStopRow>().SingleAsync(row => row.StopId == stopId, cancellationToken);

    /// <summary>
    /// 这趟旅程有权补发的消息，按停靠与归属枚举出来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在批次7-03 之前这里是写死的 11 个 id。单需求两个停靠下枚举出来的仍是那 11 个：每个停靠贡献车辆业务状态、清单、
    /// 计划三条，取货停靠另有录入请求与离站核验两条，每条需求贡献装、卸两条命令，再加上派车时那张现算的计划。
    /// </para>
    /// <para>
    /// <b>这个集合少一个就是一条报文再也不补发</b>，车载端会一直等一个不会来的东西；多一个或换了 id，车载端会当成
    /// 没见过的新消息。<c>Batch7StopDrivenAdvanceWireParityTests</c> 的重连那条用例钉的就是它。
    /// </para>
    /// <para>
    /// 离站核验过期重发会换一对新 id，写回停靠行（见 <see cref="ReissueExpiredDepartureCheckAsync"/>），所以这里读停靠行
    /// 读到的就是当前那一对。归属取 <see cref="JourneyStopCursor.AllDemands"/> 而不是未终结的那一份：一条需求终结
    /// 不会让它的装卸命令变成别人的消息。
    /// </para>
    /// </remarks>
    private static HashSet<string> RuntimeMessageIds(JourneyRuntimeRow runtime, JourneyStopCursor stops)
    {
        HashSet<string> ids = [PickupDispatchPlanMessageId(runtime)];
        foreach (JourneyStopRow stop in stops.Stops)
        {
            ids.Add(stop.VehicleBusinessMessageId);
            ids.Add(stop.PlanMessageId);
            // 清单与录入请求在一个停靠上可能发不止一版（批次7-06）：挂几条需求就有几版，每一版一个 id。全部枚举出来
            // ——少一个就是一条报文再也不补发，而这个集合的每一项在发件箱里不一定有行，多出来的项不会让任何东西发出去。
            // 计划在一个停靠上也可能发不止一版（途中追加改写了序列）。重发版的 id 由停靠与修订号派生，而重发每次都把
            // 基准抬到「按当前停靠的算式恰好等于这一版的号」（RefreshUpcomingStopPlanAsync），所以这个停靠上<b>最新</b>那一版
            // 的号就是下面两个算式之一——车在这一站上，或者还在来这一站的路上。更早的重发版不在这个集合里，不补发，也<b>不该</b>
            // 补发：补发一张比车上那张旧的计划就是 SNAPSHOT_REVISION_REGRESSION。更早的那几版有两道挡：途中追加重发时退役上一版
            // （RefreshUpcomingStopPlanAsync），车到站发到站那一版时退役路上收到的那张（取货与卸货两处到站都退役，批次7-07 审查）。
            // 「未到站」算式那一个 id 在车到站之后仍在这个集合里，挡住它的是退役，不是「不在集合里」。
            //
            // 批次7-07（control-server#212）之前这里枚举的是修订号 1 到 18，写的人把它当成「一趟旅程里的第几版」，而它是
            // <b>按车</b>单调的绝对号：一辆车跑到第四、五趟，重发版的号就超过 18，不在这个集合里，断线之后再也不补发。
            ids.Add(ReSentPlanMessageId(runtime, stop, arrivedAtStop: true));
            ids.Add(ReSentPlanMessageId(runtime, stop, arrivedAtStop: false));
            // 装货阶段快照同一个道理（批次7-07）：最新那一张的号是这个停靠的业务状态算式，或者比它小一（车还在路上）。
            ids.Add(LoadingPhaseMessageId(stop, StopRevision(runtime.VehicleBusinessRevision, stop)));
            ids.Add(LoadingPhaseMessageId(stop, StopRevision(runtime.VehicleBusinessRevision, stop) - 1));

            long baseRevision = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
            for (long offset = 0; offset < stops.WorklistVersionsOf(stop); offset++)
            {
                ids.Add(stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, baseRevision + offset));
                if (stop.SublotRequestMessageId is not null)
                {
                    ids.Add(stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, baseRevision + offset));
                }
            }
            if (stop.DepartureSafetyCheckMessageId is { } departureCheck)
            {
                ids.Add(departureCheck);
            }
        }
        foreach (JourneyStopDemand demand in stops.AllDemands)
        {
            ids.Add(demand.Membership.LoadCommandMessageId);
            ids.Add(demand.Membership.UnloadCommandMessageId);
        }

        return ids;
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Protocol field '{propertyName}' is required.");

    private static void SetStage(JourneyRuntimeRow runtime, JourneyRuntimeStage stage, DateTimeOffset now)
    {
        runtime.Stage = stage;
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
    }

    /// <summary>
    /// The two block transitions that need nothing from the peer: a station operation the server
    /// has already judged <see cref="StationOperationStatus.RecoveryRequired"/> blocks the journey
    /// under the reason code naming which recovery is outstanding. Every other transition in
    /// <c>AdvanceAsync</c> publishes to the vehicle or reads its revisions, so those stay behind
    /// the readiness gate.
    /// </summary>
    private async Task<bool> TryBlockOnRecordedRecoveryAsync(
        JourneyRuntimeRow runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // attempt 取「此刻在做的那一条」的（批次7-06）：装的那条由录入决定，卸的那条由游标按侧与已发出的操作决定。
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        JourneyStopDemand? unloading = runtime.Stage == JourneyRuntimeStage.AwaitingUnloadResult
            ? (await stops.NextToUnloadAtCurrentStopAsync(dbContext, cancellationToken).ConfigureAwait(false)).Next
            : null;
        (string? attemptId, string reason) = runtime.Stage switch
        {
            JourneyRuntimeStage.AwaitingLoadResult =>
                (stops.LoadingAtCurrentStop?.Membership.LoadSlotOperationAttemptId, "LOAD_RESULT_REQUIRES_RECOVERY"),
            JourneyRuntimeStage.AwaitingUnloadResult =>
                (unloading?.Membership.UnloadSlotOperationAttemptId, "UNLOAD_RESULT_REQUIRES_RECOVERY"),
            _ => (null, string.Empty)
        };
        if (attemptId is null)
        {
            return false;
        }

        StationOperationRow? operation = await dbContext.StationOperations.SingleOrDefaultAsync(
            row => row.SlotOperationAttemptId == attemptId,
            cancellationToken).ConfigureAwait(false);
        if (operation?.Status != StationOperationStatus.RecoveryRequired)
        {
            return false;
        }

        Block(runtime, reason, now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 这一轮的失败是不是传输类的：连接不在或断在半路（<see cref="IOException"/> 一族，含 <c>OnboardPeer</c> 在没有已恢复的对端时抛的
    /// 那一种，以及 <see cref="System.Net.Sockets.SocketException"/>），沿内层异常一路找（control-server#331 第三轮审查建议 3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只有这一类失败可以说「是会话未就绪造成的」。别的异常与会话无关，会话恰好也未就绪时仍要写推进失败——真车挂着本服务端在途单时
    /// 整段路都未就绪，把与会话无关、每轮都抛的异常也归给未就绪，看板一路说的就是一件不相干的事。
    /// </para>
    /// <para>
    /// <see cref="ObjectDisposedException"/> 也算（第四轮审查建议 1）：连接被释放时 <c>OnboardPeer.DisposeAsync</c> 释放发送闸门，
    /// 与之赛跑的发送在等闸门时抛的就是它，那也是「连接不在了」。先例是 <see cref="JourneyClosure"/> 补发时的
    /// <c>error is IOException or ObjectDisposedException</c>。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 这台车这一轮让开（control-server#357）：失败的那次保存什么都没写；这台车推进期间暂存、还没保存的改动撤回到推进之前，
    /// 推进期间才开始跟踪的条目不再跟踪，这台车的旅程行不再跟踪——这一轮后面再读到它（等人监看）读的是库里的新行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只撤这台车的，而且只撤没保存的</b>（独立审查必修 2）。车 A 推进时会改车 B 的行（让站，<c>StationYield.StageTriggerAsync</c>
    /// 改的就是 <c>active</c> 里 B 那个实例）；先前这里把所有挂着改动的条目一律解除跟踪，A 一让开，B 的实例就脱离跟踪，轮到 B 时守护核不到它。
    /// 现在推进之前拍一份快照：快照里的条目若还挂着未保存的改动，就恢复成快照时的值与状态；已经保存成功的保持原样，与库一致。
    /// </para>
    /// <para>
    /// <b>已经发生的外部副作用不在撤回之列。</b>出站消息在一次保存之后才发；RIoT 建单之前，按单号的 GET 与对账审计都还在守护下，
    /// 派车服务在用掉「最多建一次」的计数之前撤掉守护，记录建单的保存不再因守护而丢——只有旅程行自己的改动还吃令牌，失败了下一轮重放，
    /// 重放沿用已存意图（独立审查必修 1、增量复核必修 M1）。
    /// </para>
    /// </remarks>
    private void YieldToJourneyCommit(
        JourneyRuntimeRow runtime,
        TrackedBeforeAdvance before,
        DbUpdateConcurrencyException conflict)
    {
        before.Restore(dbContext);
        dbContext.Entry(runtime).State = EntityState.Detached;
        string conflicting = string.Join(
            ",",
            conflict.Entries.Select(entry => entry.Entity).OfType<JourneyRuntimeRow>()
                .Select(row => row.JourneyId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        LogYieldedToJourneyCommit(logger, runtime.AgvId, runtime.JourneyId, conflicting, null);
    }

    /// <summary>一台车推进之前被跟踪的每一个条目：它的状态、此刻的值与原始值（原始值是库里的样子，保存时的并发条件按它写）。</summary>
    internal sealed class TrackedBeforeAdvance
    {
        private readonly List<(EntityEntry Entry, EntityState State, PropertyValues Values, PropertyValues Originals)> entries;

        private TrackedBeforeAdvance(
            List<(EntityEntry Entry, EntityState State, PropertyValues Values, PropertyValues Originals)> entries) =>
            this.entries = entries;

        public static TrackedBeforeAdvance Take(ControlServerDbContext context) =>
            new([
                .. context.ChangeTracker.Entries()
                    .Select(entry => (entry, entry.State, entry.CurrentValues.Clone(), entry.OriginalValues.Clone()))
            ]);

        public void Restore(ControlServerDbContext context)
        {
            HashSet<object> known = entries.Select(item => item.Entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (EntityEntry entry in context.ChangeTracker.Entries().ToArray())
            {
                if (!known.Contains(entry.Entity))
                {
                    entry.State = EntityState.Detached;
                }
            }
            foreach ((EntityEntry entry, EntityState state, PropertyValues values, _) in entries)
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    entry.CurrentValues.SetValues(values);
                    entry.State = state;
                }
            }
        }

        /// <summary>
        /// <see cref="Restore"/> for an advance that threw something other than a journey row conflict (control-server#487),
        /// where the throw can come from anywhere, between a save and its transaction's commit included.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why <see cref="Restore"/> is not enough here.</b> A save inside a transaction that then rolls back leaves what it
        /// saved tracked as <see cref="EntityState.Unchanged"/>, with values the database does not have. <see cref="Restore"/>
        /// only goes back over entries still carrying a change, so it would leave those alone, and the vehicle whose row it is
        /// would advance this round on values that never happened. The advance writes transactions of its own (a load, a sublot
        /// entry), so this is reachable; <see cref="Restore"/> serves the journey row conflict, which a failed save raises
        /// before anything of it lands.
        /// </para>
        /// <para>
        /// So: an entry the advance began tracking is let go whatever its state -- an Unchanged one may be a row the rollback
        /// took back out of the database. Every entry that was Unchanged before the advance is read again from the database,
        /// whether or not anything about it looks changed: the database is the only thing that knows whether a save of it
        /// committed, and comparing values would miss exactly the entry that was saved and then rolled back. One the database
        /// no longer has comes out Detached. An entry that already carried a change before the advance -- an earlier step's
        /// change left for a later save on purpose -- is not read again, which would discard that change. While it still
        /// carries that change in the same state against the same original values, no save has accepted it, and it goes back
        /// to how it was: values, original values and state. Once a save of the failing advance took it along (it is
        /// Unchanged, Detached after a delete, or saved and then changed again), the tracker cannot say
        /// whether that save committed or rolled back, so <see cref="SettleSavedChangeAsync"/> asks the database: rolled back,
        /// it goes back to its change; committed, it stays as the database has it, so the change is not saved a second time.
        /// </para>
        /// <para>
        /// <b>A row read again is the database's newest.</b> The vehicles after the failing one then advance on rows read
        /// mid-round rather than at its start, so an inbound commit that landed between the two is no longer caught as
        /// control-server#357's "changed since it was read" yield: their advance simply starts from it. That is what the yield
        /// exists to reach -- the next round re-reads and decides on the new row -- one round early, and a commit landing after
        /// the reread is still caught by the guard (<see cref="ControlServerDbContext.GuardedJourneyId"/>) as before.
        /// </para>
        /// <para>
        /// An entry that cannot be read again is let go rather than left as it is: its vehicle then sits out this round on the
        /// loop's "no longer tracked" check, which is the cost of not knowing, not a wrong value.
        /// </para>
        /// </remarks>
        public async Task RestoreAsync(ControlServerDbContext context, CancellationToken cancellationToken)
        {
            HashSet<object> known = entries.Select(item => item.Entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (EntityEntry entry in context.ChangeTracker.Entries().ToArray())
            {
                if (!known.Contains(entry.Entity))
                {
                    entry.State = EntityState.Detached;
                }
            }
            foreach ((EntityEntry entry, EntityState state, PropertyValues values, PropertyValues originals) in entries)
            {
                if (state is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    if (entry.State == state && SameValues(entry.OriginalValues, originals))
                    {
                        // Carrying the same change against the same original values: no save has accepted it since (a save
                        // would have moved its originals, or its state), so it goes back to how it was. Carrying a change is
                        // not enough by itself: one the failing advance saved, committed and then changed again is Modified
                        // too, with originals the database has, and going back to the old ones would have its next save
                        // collide (a journey row's Version, an inserted row's key). Both are compared because an added
                        // entry's originals are its current values. Anything else is settled against the database.
                        Revert(context, entry, state, values, originals);
                    }
                    else
                    {
                        await SettleSavedChangeAsync(context, entry, state, values, originals, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    continue;
                }
                if (state != EntityState.Unchanged || entry.State == EntityState.Detached)
                {
                    continue;
                }
                try
                {
                    await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        /// <summary>
        /// An entry that carried a change before the advance and no longer carries that same change against the same original
        /// values (Unchanged, Detached after a delete, or changed again after a save): a save took its change along. Whether
        /// that save committed or rolled back the tracker cannot tell, so the database is asked.
        /// </summary>
        /// <remarks>
        /// Committed: the entry is left as the database has it -- a later save must neither write the change a second time
        /// (a Modified row against a version it already raised, an Added row inserted twice) nor resurrect a row already
        /// deleted. Rolled back: it goes back to its change, original values included, for the step it was left for to save
        /// against the version the database really has. A modified row the database still holds with the values it had
        /// before is taken as rolled back; any other answer (the saved values, or someone's later write) is taken as the
        /// database's word, since the change can no longer be saved as it was staged. An answer that cannot be had lets the
        /// entry go, as a failed read again does.
        /// </remarks>
        private static async Task SettleSavedChangeAsync(
            ControlServerDbContext context,
            EntityEntry entry,
            EntityState state,
            PropertyValues values,
            PropertyValues originals,
            CancellationToken cancellationToken)
        {
            PropertyValues? inDatabase;
            try
            {
                inDatabase = await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                entry.State = EntityState.Detached;
                return;
            }

            bool committed = state switch
            {
                EntityState.Added => inDatabase is not null,
                EntityState.Deleted => inDatabase is null,
                _ => inDatabase is null || !SameValues(inDatabase, originals),
            };
            if (!committed)
            {
                Revert(context, entry, state, values, originals);
            }
            else if (inDatabase is null)
            {
                entry.State = EntityState.Detached;
            }
            else
            {
                entry.OriginalValues.SetValues(inDatabase);
                entry.CurrentValues.SetValues(inDatabase);
                entry.State = EntityState.Unchanged;
            }
        }

        /// <summary>
        /// Puts an entry back to its state, values and original values from before the advance. The originals matter: after a
        /// save they are what was saved, and a journey row's own save would otherwise write its concurrency check against a
        /// Version that never landed. An entry a rolled back delete left Detached is tracked again first; one whose key another
        /// instance has taken since stays let go.
        /// </summary>
        private static void Revert(
            ControlServerDbContext context,
            EntityEntry entry,
            EntityState state,
            PropertyValues values,
            PropertyValues originals)
        {
            EntityEntry target = entry;
            if (target.State == EntityState.Detached)
            {
                target = context.Entry(entry.Entity);
                try
                {
                    target.State = EntityState.Unchanged;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
            target.OriginalValues.SetValues(originals);
            target.CurrentValues.SetValues(values);
            target.State = state;
        }

        private static bool SameValues(PropertyValues left, PropertyValues right) =>
            left.Properties.All(property => System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(
                left[property], right[property]));
    }

    private static bool IsTransportFailure(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 旅程的码指名了它在等谁、从何时起在等，别的写入不许改掉它（control-server#331 审查必修 2 把这份清单从
    /// <see cref="NameSilentOnboardSessionAsync"/> 的条件里抽出来，推进失败写码与失联写码共用）。
    /// </summary>
    /// <remarks>
    /// 各项的理由写在 <see cref="NameSilentOnboardSessionAsync"/> 的 remarks 里：<c>Blocked</c> 的码指名在等哪一次人工处置；
    /// AREA 站等准入的码是 control-server#198 升级的起点；<see cref="VehicleFaultEvidence.OrderFailed"/> 与三个在途单停住的码
    /// 告诉走到车前的人该做什么；<see cref="OnboardSessionLostReason"/> 有自己的升级时钟。检查点等待的两个码有意不在里面，
    /// 那是运行中随等随清的旁白。加一项就是加一处「不许覆盖」，两个调用方同时生效。
    /// <see cref="VehicleFaultEvidence.DoorNotProvenLocked"/> 是 control-server#335 加的，理由与订单 FAILED 相同：车因门锁被按住、急停，
    /// 走到车前的人要知道的是门，不是「车不说话了」——失联本身就是门锁那四种情形之一，两者常是同一件事的两面。
    /// <para>
    /// <see cref="StationTimeoutDoorNotClosedReason"/> 与 <see cref="LoadCorrectionInProgressReason"/> 是第三轮审查补进来的：
    /// 一个在等人去关门（ADR-cross-0058 决策 4），一个在等纠错走完，都是 program#55 按开始时间升级的等人码。被覆盖之后，写它的那一段
    /// 下一轮按「码不同」重写，开始时间归零、升级档位清零。加进来也改变了失联那一段对它们的做法——失联不再盖掉它们——这是对的，
    /// 理由与上面几项相同；而实际上失联写码只在两个到站阶段起作用，这两个码只在停站阶段写入，阶段一变就被清掉，产品代码里碰不到一起。
    /// </para>
    /// <para>
    /// <see cref="PreDepartureSafetyNotValidReason"/> 是第四轮审查补进来的：车载端答了「不安全」，车停在站里等人去车前处理。它的写入处
    /// （<c>AwaitSafeDepartureResultAsync</c>）按 control-server#80 只在等待结束时写一次，就是为了不让开始时间每轮重启；推进失败把它
    /// 换成推进失败，下一轮走通时它按「码不同」重写，开始时间照样归零，program#55 的升级档位清零。与上面两个停站码一样，
    /// 失联写码在产品代码里碰不到它（它只在 <c>AwaitingDepartureSafety</c> 写入）。
    /// </para>
    /// <para>
    /// 有意不在里面的：检查点等待的两个码（见上）；<c>PREDEPARTURE_CHECK_EXPIRED</c>，那是服务端重新询问之后、等新答复的短暂状态，
    /// 等的是车载端不是人。<see cref="CheckpointWaitExceededReason"/> 也不在里面：检查点等待本身随等随清，超时码被推进失败换掉的代价
    /// 小，记在 control-server#331 的 PR 剩余风险里。
    /// </para>
    /// <para>
    /// <see cref="LoadSlotAssignmentShortReason"/> 是 control-server#754 加的：扫码后这一侧分不出够数的仓位，录入已被拒收，车停在站上等操作员
    /// 取消这条需求。它只在拒收那一刻写一次，之后没有录入就不再写；被推进失败换掉之后，下一轮走通时 <c>ClearFailedAdvanceAsync</c> 把码清空，
    /// 仓位仍然不够而看板上没了原因。
    /// </para>
    /// <para>
    /// <see cref="SlotCommandVehicleNotAtStopReason"/> 是 control-server#783 加的：车不在这个停靠上，装卸命令扣着不发，要有人把车开回来。
    /// 装货那一侧只在拒收那一刻写，与上一个码同理；卸货那一侧每轮再核，被换掉之后下一轮按「码不同」写回，开始时间归零。
    /// </para>
    /// </remarks>
    private static bool CarriesACodeThatNamesAWaitOnAPerson(JourneyRuntimeRow runtime) =>
        runtime.Stage == JourneyRuntimeStage.Blocked ||
        IsHeldForAreaEndAdmission(runtime) ||
        string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.OrderFailed, StringComparison.Ordinal) ||
        string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.DoorNotProvenLocked, StringComparison.Ordinal) ||
        string.Equals(runtime.BlockReasonCode, HeldOrderResumedWithoutContinueReason, StringComparison.Ordinal) ||
        IsStalledOrderReason(runtime.BlockReasonCode) ||
        string.Equals(runtime.BlockReasonCode, OnboardSessionLostReason, StringComparison.Ordinal) ||
        string.Equals(runtime.BlockReasonCode, StationTimeoutDoorNotClosedReason, StringComparison.Ordinal) ||
        string.Equals(runtime.BlockReasonCode, LoadCorrectionInProgressReason, StringComparison.Ordinal) ||
        string.Equals(runtime.BlockReasonCode, PreDepartureSafetyNotValidReason, StringComparison.Ordinal) ||
        // control-server#754: the entry was refused for want of slots on the demand's side; the stop waits for a cancellation.
        string.Equals(runtime.BlockReasonCode, LoadSlotAssignmentShortReason, StringComparison.Ordinal) ||
        // control-server#783: a slot command held because the vehicle is not proven at its stop; someone has to bring it back.
        string.Equals(runtime.BlockReasonCode, SlotCommandVehicleNotAtStopReason, StringComparison.Ordinal) ||
        // control-server#390: an idle return held because its order may still exist or its vehicle may still move.
        string.Equals(runtime.BlockReasonCode, IdleReturn.IdleReturnExecutionReasons.WaitingPointLostOrderInFlight, StringComparison.Ordinal) ||
        string.Equals(runtime.BlockReasonCode, IdleReturn.IdleReturnExecutionReasons.OrderEndedStopNotProven, StringComparison.Ordinal) ||
        // control-server#447: its start is what the manual settlement's bound is counted from; a failed advance must not reset it.
        string.Equals(runtime.BlockReasonCode, IdleReturn.IdleReturnExecutionReasons.ArrivalNotProven, StringComparison.Ordinal) ||
        // control-server#404: a charging journey whose cycle is gone waits for a person to look.
        string.Equals(runtime.BlockReasonCode, Charging.ChargingExecutionReasons.CycleMissing, StringComparison.Ordinal);

    private static bool IsHeldForAreaEndAdmission(JourneyRuntimeRow runtime) =>
        runtime.Stage == JourneyRuntimeStage.AwaitingGateArrival &&
        string.Equals(runtime.BlockReasonCode, AreaEndAdmissionHeldReason, StringComparison.Ordinal);

    /// <summary>
    /// 当前（卸货）停靠上有一条需求在它的卸货操作落库之后被终结（control-server#499）。本站没有 TERMINATED 的不查库。
    /// </summary>
    private async Task<bool> EndedHereAfterItsUnloadWasCommandedAsync(
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        string[] endedHere =
        [
            .. stops.AllAtStop(stops.Current)
                .Where(item => item.Membership.Status == JourneyDemandStatuses.Terminated)
                .Select(item => item.Membership.UnloadSlotOperationAttemptId)
        ];
        return endedHere.Length > 0 &&
               await dbContext.StationOperations.AsNoTracking()
                   .AnyAsync(
                       row => endedHere.Contains(row.SlotOperationAttemptId) &&
                              row.OperationType == SlotOperationType.Unload,
                       cancellationToken)
                   .ConfigureAwait(false);
    }

    private static void Block(JourneyRuntimeRow runtime, string reason, DateTimeOffset now)
    {
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(reason, now);
        runtime.UpdatedAt = now;
    }

    /// <summary>
    /// Hands a stop held at its AREA machine to a person once the wait for the station's admission has run past
    /// <see cref="JourneyRuntimeOptions.AreaEndAdmissionRevokedTimeout"/>, counted from
    /// <see cref="JourneyRuntimeRow.AreaEndAdmissionRevokedSince"/> (control-server#228). Returns whether it did; the caller
    /// saves.
    /// </summary>
    /// <remarks>
    /// control-server#198, decided by the user on 2026-09-19: the vehicle is loaded and nobody is told while it waits, so
    /// past the threshold the journey is Blocked under <see cref="AreaEndAdmissionRevokedTimeoutReason"/>. Only the journey
    /// changes: the order that brought the vehicle here is not touched, and no new one is created. Whatever code the
    /// journey carries this round -- the hold, a failed order, a checkpoint wait, none -- the block is the escalated hold,
    /// and it starts where the wait started, which is what the dashboard's escalation ladder is measured from. The start
    /// stays on the row: it is cleared only by the admission returning.
    /// <para>
    /// A prepared unload is the admission having returned: it is frozen with the unload, and the stage save that follows
    /// is what clears the start. A restart between the two leaves the start on a row still at AwaitingGateArrival, and
    /// escalating it would hand the journey to a person with an unload command already out; the arrival path below goes
    /// on under the frozen admission instead, and clears it.
    /// </para>
    /// </remarks>
    private async Task<bool> EscalateAreaEndAdmissionRevokedPastTimeoutAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtime.AreaEndAdmissionRevokedSince is not DateTimeOffset revokedSince ||
            now - revokedSince < runtimeOptions.AreaEndAdmissionRevokedTimeout ||
            await UnloadPreparedAsync(stops, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(AreaEndAdmissionHeldReason, revokedSince);
        runtime.EscalateBlockReason(AreaEndAdmissionRevokedTimeoutReason);
        runtime.UpdatedAt = now;
        LogAreaEndAdmissionRevokedTimeout(
            logger,
            runtime.AgvId,
            stops.Current.StationId,
            runtime.DemandId ?? runtime.JourneyId,
            runtimeOptions.AreaEndAdmissionRevokedTimeout,
            null);
        return true;
    }

    /// <summary>
    /// Whether this journey's unload at the AREA machine has already been prepared: the attempt has a
    /// <c>StationOperations</c> row, which is written with the admission frozen on it (ADR-cross-0050/0051).
    /// </summary>
    private async Task<bool> UnloadPreparedAsync(JourneyStopCursor stops, CancellationToken cancellationToken) =>
        (await stops.NextToUnloadAtCurrentStopAsync(dbContext, cancellationToken).ConfigureAwait(false)).Next is { } next &&
        await dbContext.StationOperations.AsNoTracking()
            .AnyAsync(row => row.SlotOperationAttemptId == next.Membership.UnloadSlotOperationAttemptId, cancellationToken)
            .ConfigureAwait(false);


    /// <summary>
    /// 这趟旅程开的、关于还在车上的需求的纠错（见 <see cref="StationDepartureWaitIsOverAsync"/> 的注释：本车开的、不早于需求加入本旅程）。
    /// </summary>
    private async Task<RecoveryWorkflowRow[]> CorrectionsOnBoardAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DateTimeOffset> joinedAt = stops.AllDemands
            .Where(item => item.Membership.Status is JourneyDemandStatuses.Loading or JourneyDemandStatuses.Loaded)
            .ToDictionary(item => item.Demand.DemandId, item => item.Membership.AddedAt, StringComparer.Ordinal);
        string[] demandIds = [.. joinedAt.Keys];
        return [.. (await dbContext.RecoveryWorkflows.AsNoTracking()
                .Where(row => row.DemandId != null && demandIds.Contains(row.DemandId) &&
                              row.WorkflowType == "LOAD_CORRECTION" && row.AgvId == runtime.AgvId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => row.CreatedAt >= joinedAt[row.DemandId!])];
    }

    private static bool IsOpenCorrection(RecoveryWorkflowRow row) =>
        row.State is not (RecoveryWorkflowState.Reconciled or RecoveryWorkflowState.HistoricalOnly);

    /// <summary>这趟旅程有一条纠错开着：离站等待被它挡住，出发请求也被它拒（control-server#713）。</summary>
    private async Task<bool> OpenCorrectionOnBoardAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        CancellationToken cancellationToken) =>
        (await CorrectionsOnBoardAsync(runtime, stops, cancellationToken).ConfigureAwait(false)).Any(IsOpenCorrection);

    /// <summary>
    /// Whether the station departure wait (ADR-cross-0055) has run out for this journey.
    /// </summary>
    /// <remarks>
    /// An open load correction holds the vehicle whatever the clock says, and one that has closed
    /// since the wait began starts the wait over from its full length (ADR-cross-0054) -- measured
    /// from when this server recorded it closed, not from the peer's clock. Updates the runtime's
    /// wait start and block reason; the caller saves.
    /// <para>
    /// 看的是这趟旅程的<b>每一条</b>需求的纠错，不只锚需求（批次7-08，control-server#213 实读发现）。纠错的授权按归属找旅程
    /// （<c>OnboardRecoveryCoordinator.AuthorizeLoadCorrectionAsync</c> 经 <c>DemandJourneyLookup.JourneyOf</c>），追加进来的需求
    /// 也能开纠错；这里原先只按旅程行上的 <c>DemandId</c> 查，那一条纠错开着，车照样离站——让站之后尤其如此，而票面第 3 条要求
    /// 「进行中的纠错」挡住离站。判据是 <c>Batch7StationYieldTests.AnOpenCorrectionOnAnAppendedDemandHoldsTheDeparture</c>。
    /// </para>
    /// <para>
    /// <b>只看还在车上的需求</b>（归属 <c>LOADING</c>、<c>LOADED</c>）。纠错的授权不看这条需求还在不在车上（那个口子归 cs#287），
    /// 车停在后面某一站时能对一条早已卸掉的需求开出纠错；它要车载端对一排已经空了的仓位执行完才关，未必关得掉，算进来车就一直
    /// 停在这里、没人解得开。已移除的归属本来就不在 <see cref="JourneyStopCursor.AllDemands"/> 里。判据是
    /// <c>AnOpenCorrectionOnADemandAlreadyUnloadedDoesNotHoldTheDeparture</c>。
    /// </para>
    /// <para>
    /// <b>而且只看这一趟旅程开的</b>（审查必修 3）。纠错行上没有旅程号，同一个需求号在 7-10 改派之后会先后属于两趟旅程，前一辆车上没结的
    /// 纠错不该挡新车。所以按两样认：本车开的，而且不早于这条需求加入本旅程（归属的 <c>AddedAt</c>）。两样都是服务端自己的钟。
    /// 时刻比较在内存里做：SQLite 不能在库里比较 <c>DateTimeOffset</c>。判据是 <c>AnOpenCorrectionLeftByAnotherJourneyDoesNotHoldTheDeparture</c>。
    /// </para>
    /// </remarks>
    private async Task<bool> StationDepartureWaitIsOverAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RecoveryWorkflowRow[] corrections = await CorrectionsOnBoardAsync(runtime, stops, cancellationToken).ConfigureAwait(false);
        if (corrections.Any(IsOpenCorrection))
        {
            if (runtime.BlockReasonCode != LoadCorrectionInProgressReason)
            {
                runtime.SetBlockReason(LoadCorrectionInProgressReason, now);
                runtime.UpdatedAt = now;
            }
            return false;
        }

        // 机台取货停靠上操作员按了出发、服务端已受理（control-server#725，CP-0014 REQ-0354）：本站作业会话已经结束，不等站点离站期限的剩余部分，
        // 不另留纠错宽限。上面那道纠错闸照样在前（受理时纠错没开着；受理之后才开的，等它收敛），下面的门未证明扣车照样扣。
        if (stops.Current.OperatorDepartedAt is not null)
        {
            if (runtime.BlockReasonCode is not null)
            {
                runtime.SetBlockReason(null, now);
                runtime.UpdatedAt = now;
            }
            return true;
        }

        // ADR-cross-0055 / FR-031 AC-8: the wait belongs only to a stop that can go on loading. An unload stop
        // that is done leaves at once (control-server#637): its worklist carries no deadline, so a wait here would
        // hold the station for the full timeout behind a screen that shows none. A station that unloads and loads is
        // two stops of one role each; when the planner lays them out unload first, the wait is seeded when the pickup
        // stop is reached. (It can also lay them out load first; that order is control-server#647.) The correction gate
        // above and the door hold after this still apply; only the clock is skipped, and no start is seeded.
        if (stops.Current.StopRole == JourneyStopRoles.Unload)
        {
            if (runtime.BlockReasonCode is not null)
            {
                runtime.SetBlockReason(null, now);
                runtime.UpdatedAt = now;
            }
            return true;
        }

        // 同一个理由，取货停靠上的一种（control-server#290 审查 M1）：装货阶段已经关了、这个停靠上一条生效的归属都没有——持货期限把它上面
        // 没开始装的全释放走了，车多半是到站那一轮才截的。这里不会再有人录入、也没有装上的货要纠错，离站等待只会让一辆已经过了持货硬期限
        // 的车在一台空机台前多停一整段。被终结（而不是释放）的需求归属还在，那种停靠走下面的离站等待（control-server#668）——但本站的归属全被
        // 终结、一条没装上时，期限就是等待起点，车同样当轮就走（control-server#742，CP-0015，见 EffectiveStationDepartureDeadline）。#668 要的
        // 「按正常离站等待处理」是为了给本站已装的需求留纠错窗口；本站一条 Loaded 都没有，就没有要纠正的放置，那个理由不成立。本站有一条 Loaded
        // 时照旧等原期限。这里不另写一支，是为了让车上的期限与这里的判定是同一个值；纠错闸照样在前，门未证明扣车与持货等单照样在后。
        if (stops.Current.StopRole == JourneyStopRoles.Pickup &&
            runtime.LoadingPhaseState == LoadingPhaseStates.Closed &&
            stops.AllAtStop(stops.Current).Count == 0)
        {
            if (runtime.BlockReasonCode is not null)
            {
                runtime.SetBlockReason(null, now);
                runtime.UpdatedAt = now;
            }
            return true;
        }

        DateTimeOffset startedAt = runtime.StationDepartureWaitStartedAt ?? now;
        foreach (RecoveryWorkflowRow closed in corrections.Where(row => row.State == RecoveryWorkflowState.Reconciled))
        {
            if (closed.UpdatedAt > startedAt) startedAt = closed.UpdatedAt;
        }
        if (runtime.StationDepartureWaitStartedAt != startedAt || runtime.BlockReasonCode is not null)
        {
            runtime.StationDepartureWaitStartedAt = startedAt;
            runtime.SetBlockReason(null, now);
            runtime.UpdatedAt = now;
        }

        // 同站追加提前结束（control-server#646）也在这个期限里：判定与下发读的是同一个时刻，见 EffectiveStationDepartureDeadline。
        // No deadline here means the wait is off: the vehicle leaves in the iteration its load commits.
        //
        // 待送点取货停靠例外（control-server#709，program#184 R3）：那里没有期限不是「等待关着」，是「等待没有尽头」——装完不走，
        // 只有车满或装不下（control-server#710）、客户点出发与缺电（control-server#713）、持货期限到期（control-server#727）结束它；
        // 同站追加的原地到站仍按宽限结束。
        // 所以那里期限为空答「没完」，而且与分区途中追加参数配没配无关（参数清空时也不退回装完即走）。
        //
        // 车满或等着的需求全部装不下（control-server#710）由装货阶段的 VEHICLE_FULL 表达，判出来的这一轮就结束等待、发离站核验，
        // 不留纠错宽限（用户定，CP-0012 第五节②）。上面那道纠错闸照样在前：已经开着的纠错挡车，闭环之后这里再答，满足就立即走。
        if (IsStagingPickup(stops.Current))
        {
            // 客户点了出发、或车缺电带货先走（control-server#713）、或持货期限到了（control-server#727，CP-0014）：装货阶段以这三个原因关闭，
            // 同样立即结束等待——待送点没有站点离站期限可等，到期之后不另留纠错宽限（CP-0014 第五节第 4 处），已经开着的纠错照样挡在上面那道闸。
            return runtime.LoadingPhaseState == LoadingPhaseStates.VehicleFull ||
                   runtime.LoadingPhaseState == LoadingPhaseStates.Closed && StagingDepartureUnderWay(runtime) ||
                   EffectiveStationDepartureDeadline(runtime, stops) is { } early && now >= early;
        }
        return EffectiveStationDepartureDeadline(runtime, stops) is not { } deadline || now >= deadline;
    }

    /// <summary>
    /// When this journey's station departure wait (ADR-cross-0055) runs out, or null when there is no
    /// such wait to express: the timeout is off, or the wait has not started.
    /// </summary>
    /// <remarks>
    /// The single source of the deadline. The runtime judges the pickup timeout and the departure from
    /// it, and the worklist sends it to the vehicle as <c>stationDepartureDeadlineAt</c>
    /// (control-server#84), so the two can never disagree about when the stop ends. A null is not a
    /// deadline of "now": publishing one the runtime never acts on would be a countdown that expires
    /// into silence.
    /// </remarks>
    public static DateTimeOffset? StationDepartureDeadline(
        JourneyRuntimeRow runtime,
        TimeSpan stationDepartureWaitTimeout) =>
        stationDepartureWaitTimeout <= TimeSpan.Zero || runtime.StationDepartureWaitStartedAt is not { } startedAt
            ? null
            : startedAt + stationDepartureWaitTimeout;

    /// <summary>
    /// <see cref="StationDepartureDeadline"/> of <paramref name="stop"/>: none at a staging pickup (control-server#709), where
    /// nothing but the vehicle being full, the customer's departure or a flat battery ends the wait (program#184 R3).
    /// </summary>
    /// <remarks>
    /// Every reader in this engine that means the stop the vehicle stands at asks this, so the worklist, the deadline that
    /// ends an unscanned stop and the door alarm cannot disagree about whether there is a deadline.
    /// </remarks>
    private DateTimeOffset? StationDepartureDeadlineAt(JourneyRuntimeRow runtime, JourneyStopRow stop) =>
        IsStagingPickup(stop) ? null : StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout);

    /// <summary>
    /// The staging pickups this engine has recognised (control-server#709), by stop id. Filled for each journey as its
    /// iteration loads its stops; always empty while the staging rules are off, so no reader below changes then.
    /// </summary>
    private readonly HashSet<string> stagingPickupStops = new(StringComparer.Ordinal);

    /// <summary>Recognises this journey's staging pickups (<see cref="StagingPickupStops"/>); does nothing, and reads nothing, while the rules are off.</summary>
    private async Task RecognizeStagingPickupStopsAsync(JourneyStopCursor stops, CancellationToken cancellationToken)
    {
        if (!runtimeOptions.StagingDepartureRules.Enabled)
        {
            return;
        }
        stagingPickupStops.UnionWith(
            await StagingPickupStops.RecognizeAsync(dbContext, stops, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 待送点上装货阶段以「车该走了」的原因关闭：客户出发、缺电带货（control-server#713），或持货期限到了（control-server#727）。离站等待、
    /// 缺电带货与出发请求三处读同一句，免得一处认了到期、另一处还当车在等而改判关闭原因。
    /// </summary>
    private static bool StagingDepartureUnderWay(JourneyRuntimeRow runtime) =>
        runtime.LoadingClosedReason is
            LoadingClosedReasons.OperatorDeparted or LoadingClosedReasons.ChargingRequired or LoadingClosedReasons.CargoHoldingTimeout;

    /// <summary>Whether <paramref name="stop"/> is a staging pickup under the staging rules (control-server#709).</summary>
    private bool IsStagingPickup(JourneyStopRow stop) =>
        runtimeOptions.StagingDepartureRules.Enabled && stagingPickupStops.Contains(stop.StopId);

    /// <summary>
    /// What a plan leg reads to say <c>publicStationFunction = WIRE_STAGING</c> (control-server#713); null while the staging
    /// rules are off, so the plan stream is byte-for-byte what it was (the WirePin baselines say so).
    /// </summary>
    private Func<JourneyStopRow, bool>? IsStagingPickupLeg =>
        runtimeOptions.StagingDepartureRules.Enabled ? IsStagingPickup : null;

    /// <summary>
    /// Whether the stop this journey stands at, or is heading for, is a staging pickup: there cargo holding always applies,
    /// and its clock runs only while cargo is on board and restarts with each demand appended for the station
    /// (control-server#727, CP-0014; until then it did not run there at all, control-server#709).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Judged for the current stop, not for the journey (control-server#710 review M1). cs#709 judged it for the journey, which
    /// was safe while a vehicle left the staging station only with both sides full. Since #710 it also leaves with one side
    /// free, and a demand appended for a machine pickup after it then waits at that pickup under the machine rules: holding
    /// with room left, until the holding deadline. Judged for the journey, that deadline never came, and nothing but a station
    /// yield ever sent the vehicle on.
    /// </para>
    /// <para>
    /// The clock that runs there is the machine pickup's own: leaving the staging station clears the start
    /// (<see cref="ClearHoldingStartLeavingStaging"/>), so the next load batch to close starts it again. Without that the
    /// deadline would count from the staging station's clock -- possibly long past, since appends there restart it -- and the
    /// machine pickup's demand would be truncated before the vehicle reached it.
    /// </para>
    /// <para>
    /// The low-battery exit at the staging station (<see cref="TryReleaseLowBatteryStagingStopAsync"/>) still asks about
    /// the whole journey -- whether anything is loaded anywhere on it (cs#709 review L1). That is a different question.
    /// </para>
    /// </remarks>
    private bool StandsAtStaging(JourneyStopCursor stops) => stops.OpenStops.Any() && IsStagingPickup(stops.Current);

    /// <summary>
    /// Leaving a staging pickup for a stop that is not one clears the cargo holding start (control-server#710 review M1, see
    /// <see cref="StandsAtStaging"/>). Staged in the departure's own save. Does nothing while the staging rules are off.
    /// </summary>
    private void ClearHoldingStartLeavingStaging(JourneyRuntimeRow runtime, JourneyStopRow departedFrom, JourneyStopRow nextStop)
    {
        if (IsStagingPickup(departedFrom) && !IsStagingPickup(nextStop))
        {
            runtime.CargoHoldingStartedAt = null;
        }
    }

    /// <summary>
    /// The moment this journey's current stop really ends its departure wait: <see cref="StationDepartureDeadline"/>, or,
    /// when a same-station append ends the wait early (control-server#646), the start plus
    /// <see cref="JourneyRuntimeOptions.SameStationAppendCorrectionGrace"/> if that comes first; or the start itself when every
    /// demand at a machine pickup was ended with nothing loaded there (control-server#742, CP-0015).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同站追加的新需求排成了当前停靠之后的新停靠（REQ-0196：当前停靠不可改），车物理上不动，新需求要等车「离站再到站」才进清单。
    /// 不提前结束，操作员就得空等原停靠的倒计时走完（生产最长约 5 分钟）才能录它；用户 2026-10-12 定：当前取货停靠已装完、紧接着的下一个
    /// 开着的停靠是同一站的取货停靠时，不等原倒计时，走原地的离站核验与「到站」，新停靠到站时从完整时长重新计时。
    /// </para>
    /// <para>
    /// <b>但先停够一段纠错宽限</b>（用户 2026-10-12 定，PR #660 审查 S1）。服务端只在 AwaitingStationDeparture 授权纠错，车载端只对
    /// 「最近一次完成的装货」开纠错入口；新停靠一装完，这一站已装的那几条就再也纠正不了（REQ-0237）。没有宽限时窗口可以是零：追加早于落定，
    /// 落定那一轮直接走到离站核验，这个阶段一次都不落库。宽限从离站等待的起点算——最后一批落定，纠错闭环时挪到闭环那一刻——所以宽限内开的
    /// 纠错照样挡车（<see cref="StationDepartureWaitIsOverAsync"/> 的纠错闸），闭环之后再给一段宽限。ADR-cross-0055 的「闭环后从完整时长重新
    /// 计时」管的是期限，那一项照旧重算；这里对它与对原期限一样，只是取较早的那个：本停靠已无可装的东西，完整时长留给新停靠。
    /// </para>
    /// <para>
    /// <b>判定与下发读这同一个值</b>（PR #660 增量审查 S1）：离站等待那一段的判定、以及送到车上的期限（<see cref="RefreshDeadlineOfALoadedStopAsync"/>），
    /// 车载屏上的倒计时就是车真正要走的那一刻。其余读 <see cref="StationDepartureDeadline"/> 的地方——到站发清单、等录入时发清单与到期结束本站、
    /// 等装货结果、断联重填之后的升版、仓门未闭告警、装货结果的回执——都在当前停靠还有待做项时，那时提前结束的条件不成立，两个值恒等，所以不改。
    /// </para>
    /// <para>
    /// 等录入阶段待做项归零（扫码前取消了本站最后一条待录入的）曾是这句话的例外：取消那一路不改阶段，旅程留在 AwaitingSublot，到期结束本站
    /// 按原期限判（PR #660 第三轮审查 S1）。control-server#668 起，等录入阶段一读到本站待做项为零就转入离站等待，不再走到那里。
    /// </para>
    /// <para>
    /// 只截短不延长：宽限比期限长，或等待关着（期限为空）时，就是 <see cref="StationDepartureDeadline"/>。只认下一个是同站取货：只有那时才有一条
    /// 等着录的新需求，提前才有收益。同站卸货（先装后卸）在这里提前，等于替 control-server#647 决定那种排法的取货停靠等不等，而且纠错窗口关掉
    /// 之后不会重开（卸货停靠卸完当轮就走）。
    /// </para>
    /// </remarks>
    private DateTimeOffset? EffectiveStationDepartureDeadline(JourneyRuntimeRow runtime, JourneyStopCursor stops)
    {
        DateTimeOffset? deadline = StationDepartureDeadlineAt(runtime, stops.Current);
        // 待送点没有期限（control-server#709），但同站追加的原地到站照旧：本停靠已装完、下一个是同站取货时，宽限一过就走到新停靠，
        // 否则新挂上来的需求在待送点上永远扫不了。所以待送点上期限为空不在这里返回，往下算宽限。
        if (deadline is null && !IsStagingPickup(stops.Current) ||
            runtime.StationDepartureWaitStartedAt is not { } startedAt ||
            stops.Current.StopRole != JourneyStopRoles.Pickup ||
            stops.OutstandingAtCurrentStop.Count > 0)
        {
            return deadline;
        }
        if (stops.OpenStops.FirstOrDefault(stop => stop.Sequence > stops.Current.Sequence) is
            { StopRole: JourneyStopRoles.Pickup } next &&
            string.Equals(next.StationId, stops.Current.StationId, StringComparison.Ordinal))
        {
            DateTimeOffset early = startedAt + runtimeOptions.SameStationAppendCorrectionGrace;
            return deadline is { } full && full < early ? full : early;
        }
        // 机台取货停靠上本站的归属全被终结、一条没装上（control-server#742，CP-0015 方案 A）：取消让这一站空了而旅程继续，期限就是等待起点，
        // 车当轮就走，不再白等一整段（现场 agv03 145、164 站各等了约 5 分钟）。没有已装的需求就没有要留的纠错窗口，所以不加宽限；本站有一条
        // Loaded 就不在此列，照旧等原期限（control-server#668 那道护栏）。放在同站宽限之后：下一站同站时照旧走那条路（control-server#646）。
        // 写在这里而不是只写在离站判定里，是为了让送到车上的期限跟着变（本方法注释的「判定与下发读这同一个值」）。
        if (deadline is not null && !IsStagingPickup(stops.Current) && EveryDemandAtCurrentStopEnded(stops))
        {
            return startedAt;
        }
        return deadline;
    }

    /// <summary>
    /// 当前停靠挂着归属，而且每一条都已终结：没有待做、没有在装、也没有装上的（control-server#742）。一条归属都没有的停靠不算——那是
    /// 持货期限把它全释放走的形状，由 <see cref="StationDepartureWaitIsOverAsync"/> 里 control-server#290 那一支管。
    /// </summary>
    private static bool EveryDemandAtCurrentStopEnded(JourneyStopCursor stops)
    {
        IReadOnlyList<JourneyStopDemand> atStop = stops.AllAtStop(stops.Current);
        return atStop.Count > 0 && atStop.All(item => item.Membership.Status == JourneyDemandStatuses.Terminated);
    }

    /// <summary>
    /// Ends the pickup stop once its station departure deadline has passed with no sublot entered
    /// (ADR-cross-0055; ADR-cross-0058 decision 7 redone for the one-demand journey). Called only from
    /// <see cref="JourneyRuntimeStage.AwaitingSublot"/> in an iteration that found no matching entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until batch 5 that stage had no deadline and no exit: an unscanned stop was asked again on
    /// every poll and held the pickup until someone edited the database. The runtime may end it alone
    /// because no slot operation has been commanded in this stage -- a load command moves the journey
    /// to AwaitingLoadResult in the iteration that sends it -- so there is no physical state that only
    /// the peer could settle.
    /// </para>
    /// <para>
    /// <b>Exclusive with starting the load.</b> Both are decided by this one iteration for this one
    /// journey: an entry already durable when the iteration reads the inbox starts the load and this
    /// is never reached; once this has committed the journey is Completed and a later entry is never
    /// read. Whichever is persisted first wins (ADR-cross-0055).
    /// </para>
    /// <para>
    /// The demand stays out of dispatch because its DemandId is already accepted
    /// (AlreadyAcceptedCriterion). Suppressing the business key permanently, so that a new DemandId
    /// for the same sublot is not dispatched either, is REQ-0155/0156/0211 in batch 7.
    /// </para>
    /// <para>
    /// <b>A null deadline returns false on purpose, and is not a stop with no exit.</b> It means the wait is off
    /// (<see cref="JourneyRuntimeOptions.StationDepartureWaitTimeout"/> is zero): the deadline is what lets the runtime end
    /// a stop nobody scanned, and with it off nothing but the operator ends one -- an entry, or a cancellation before the
    /// sublot (ADR-cross-0046). Both are governed exits. What had none was a stop whose last outstanding demand that
    /// cancellation ended while the journey went on: it stayed in AwaitingSublot with nothing to scan or cancel, and this
    /// was its only way out (control-server#668). The caller now moves such a stop to the departure wait before it gets
    /// here, so this method only ever sees a stop that still has work outstanding.
    /// </para>
    /// </remarks>
    private async Task<bool> TryEndStopAtStationDeadlineAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 待送点取货停靠没有期限（control-server#709），这里永远不结束它：不放回、不计 control-server#703 的次数、不挂人工搬运告警。
        if (StationDepartureDeadlineAt(runtime, stops.Current) is not { } deadline ||
            now < deadline)
        {
            return false;
        }
        // ADR-cross-0058 decision 4: ending the stop lets the vehicle be sent on, and it must not move
        // with a door open (ADR-cross-0011/0012). So the stop keeps waiting past its deadline until the
        // door is shut, then ends on that iteration's evidence. The alarm is raised only in
        // AwaitingLoadResult (ReconcileStationTimeoutDoorNotClosed): here no slot has been commanded, so an
        // open door is nothing our command explains and the session leaves Ready first
        // (ADR-cross-0058 Verification, decision 4).
        if (SlotDoorsNotProvenClosed(session))
        {
            return false;
        }
        // ADR-cross-0055 voids the countdown for a disconnect, and FR-031 AC-9 forbids cancelling or
        // departing while the vehicle is off air. A dropped link does not move the session off Ready by
        // itself -- nothing calls RecordConnectionLossAsync -- so readiness alone would let the stop end
        // against evidence the vehicle sent before it vanished, cancelling a demand nobody could have
        // scanned. Liveness is the measure the dispatch facts already use: the last inbound of this
        // generation, within MaximumEvidenceAge. Coming back on the same generation is not a reconnect,
        // so a deadline that passed meanwhile still stands; a real reconnect voids it at the handshake.
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            runtime.AgvId, session.SessionGeneration, now - runtimeOptions.MaximumEvidenceAge, cancellationToken)
            .ConfigureAwait(false);
        if (lastInboundAt is null || now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return false;
        }

        // Decided again under the write lock (control-server#83). The operator's cancellation before a sublot
        // is authorized on another connection's scope, and everything this iteration read about the stop was
        // read without a lock: a cancellation authorized -- or authorized and already settled -- since then
        // would otherwise lose its stop to this one, and its result would find a demand ended for a reason
        // that is not the operator's. BeginTransaction on this store is BEGIN IMMEDIATE, so the reads below
        // see every write committed before it, and the authorization, running inside the inbox's own write
        // transaction, sees this one once it commits.
        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        // By journey, not by demand (control-server#215): a demand released for redispatch has one journey row per
        // dispatch, and this asks about this journey.
        JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .Select(row => (JourneyRuntimeStage?)row.Stage)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        // 取消要按<b>这个停靠</b>问，不是按锚需求（批次7-06，control-server#211）：一条需求的取消开着时，
        // 这个停靠上的每一条都等着它——期限不能在那期间把停靠结束掉。先前这里只查锚需求，于是同站另一条
        // 需求的扫码前取消正等着车证明空仓时，期限照样能结束这个停靠。
        if (stageNow != JourneyRuntimeStage.AwaitingSublot ||
            await OpenCancellationAtCurrentStopAsync(stops, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // 终结的是<b>这个停靠上还没做完的那些</b>，不是旅程行点名的那条（批次7-06，control-server#211）。
        //
        // 先前这里走的是「终结旅程行点名的那条需求」那个重载，终结对象恒为锚需求。一站多需求之后那是错的，
        // 而且错得静默：在第二个取货停靠上超时，被终结的是<b>早已在第一站装上车的锚需求</b>——它的归属行被写成
        // Terminated，JourneyStopCursor.IsDoneAt 从此对它恒为 true，<b>车上已经装着的那批货从计划里消失、
        // 永远不会被卸</b>，而真正超时的那条原封不动继续挂在清单上。
        //
        // 整个停靠上的待做项一起终结：期限是这个停靠的（「这一站的活没在期限内做完」），结束它就是让车走，
        // 留下任何一条待做项都会让下一轮回到同一个停靠、同一个期限。
        //
        // 逐条终结时「是不是最后一条」要看得见前几条刚暂存的终结（control-server#327）：只读库时每一条都判「另一条还开着」，
        // 谁也不收尾，旅程停在离站等待，下一轮在一个没有下一段的停靠上抛。那个判断在 DemandJourneyLookup.IsLastOpenDemandAsync。
        //
        // control-server#703 起「终结」只是兜底：能放回待派的放回（StageStationDeadlineEndingAsync），放不回、或已放回到上限的才终结并告警。
        string sublotRequestMessageId = stops.CurrentSublotRequestMessageId(runtime.WorklistRevision);
        StationDeadlineEnding ending = await StageStationDeadlineEndingAsync(
            runtime, stops, sublotRequestMessageId, now, cancellationToken).ConfigureAwait(false);
        // 本站结束了，车上却还有更早装上的货（批次7-07，control-server#212）：交给离站那一段，由它决定持货等单还是离站。
        // ADR-cross-0055 与 REQ-0354 的同一句话：站点离站等待期限到期只结束本站作业，不等于车辆离开。
        //
        // 在这之前这里只写终结、阶段不动：旅程只有在「最后一条开着的需求」被终结时才收尾，所以一趟多需求旅程在
        // 第二个取货停靠上超时，就停在 AwaitingSublot 上一个待做项都没有的停靠里——每一轮再判一次期限、终结一个
        // 空集合，车带着第一站的货永远不走。离站等待的起点不动：期限正是因为它已经过了才到这里，离站那一段读到的
        // 也是「已经过了」。
        if (runtime.Stage != JourneyRuntimeStage.Completed)
        {
            SetStage(runtime, JourneyRuntimeStage.AwaitingStationDeparture, now);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (runtime.Stage == JourneyRuntimeStage.Completed)
        {
            await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 旅程继续而这一站结束了：那张空清单随上面那次保存落库，提交之后发（control-server#324）。
            await StopEndWorklist.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        }
        checkpointWaits.Clear(runtime.VehicleKey);
        foreach ((string demandId, int release) in ending.Released)
        {
            LogStationDeadlineReleasedDemand(logger, runtime.AgvId, deadline, demandId, release, null);
        }
        foreach ((string demandId, string transportDemandKey, string alarmCode) in ending.Alarmed)
        {
            LogStationDeadlineEndedStop(logger, runtime.AgvId, deadline, demandId, null);
            LogManualCarryAlarmRaised(logger, demandId, transportDemandKey, runtime.AgvId, alarmCode, null);
        }
        return true;
    }

    /// <summary>
    /// 待送点取货停靠上缺电、一篮没装：本趟的需求全部放回待派，旅程收尾，车成为空闲车，交给现有的充电分配（control-server#709，
    /// program#184 用户决定「缺电直接去充电」）。做了就返回真。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要有这一条。</b>待送点没有期限之后，没人扫码的车会一直挂着这趟旅程；而挂着旅程的车不进充电分配（REQ-0290，
    /// <c>ChargingAllocator</c> 的 <c>CHARGING_VEHICLE_HAS_PURPOSE</c>）。不放它走，它会在待送点上一直等到没电。
    /// </para>
    /// <para>
    /// <b>只管一篮没装。</b>这趟旅程上只要有一条已装上或正在装，就什么也不做：结束旅程会让车上的货没有任何需求认领，那一支是
    /// control-server#711（带货充电）的。所以「一篮没装」按整趟旅程判，不按本停靠：同站追加之后前一个停靠装上的货也是车上的货。
    /// </para>
    /// <para>
    /// <b>放回与 control-server#713 的「出发、一篮没装」同一个做法</b>：释放改派（<see cref="DemandReleaseService.StageReleasedMembershipAsync"/>，
    /// 默认原因 <see cref="DemandJourneyLookup.ReleasedForRedispatchReason"/>），需求仍是 <c>Accepted</c>、不写抑制、等待年龄不重置、
    /// 不计站点超时放回的次数（control-server#703 只数 <see cref="DemandJourneyLookup.ReleasedAtStationDeadlineReason"/>），也不挂告警。
    /// 收尾与站点期限放回最后一条那一支相同（<see cref="StageStationDeadlineEndingAsync"/>）：车就停在这一站，不修订计划；三套占用一起放，
    /// 公共站点的占用也在其中，下一轮充电分配看到的是一辆空闲车。车上收尾快照的 <c>stopEndedReason</c> 是 <c>CHARGING_REQUIRED</c>
    /// （协议 4.0.0 候选，control-server#713；v3 时是 <c>DEMAND_RELEASED</c>）。
    /// </para>
    /// <para>
    /// <b>电量按这趟旅程冻结的那一版充电策略判</b>（REQ-0282，<see cref="JourneyRuntimeRow.ChargingPolicyVersion"/>），与充电周期复查
    /// 同一个读法；读不到电量、读数过期、没有冻结版本都不动——不知道就不放。线用 <see cref="Dispatch.Criteria.BatteryEligibility.IsMandatoryCharge"/>，
    /// 派车、空闲返回、充电分配问的都是它。
    /// </para>
    /// <para>
    /// <b>与站点期限结束一站同样的几道闸</b>（<see cref="TryEndStopAtStationDeadlineAsync"/>）：仓门没证明关着不放（放了车就会被派走）；车不在线不放；
    /// 写锁里重读阶段、本停靠有扫码前取消开着就不放，等车报结果。
    /// </para>
    /// </remarks>
    private async Task<bool> TryReleaseLowBatteryStagingStopAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (stops.AllDemands.Any(item =>
                item.Membership.Status is JourneyDemandStatuses.Loading or JourneyDemandStatuses.Loaded) ||
            stops.Demands.Any(item =>
                item.Demand.Status != DemandExecutionStatus.Accepted ||
                item.Membership.Status != JourneyDemandStatuses.PendingLoad) ||
            stops.Demands.Count == 0)
        {
            return false;
        }

        // 读法（读完再取时钟、过期不动、按冻结的那一版策略）在 MandatoryChargeBatteryAsync，与车上有货那一支（control-server#713）共用。
        if (await MandatoryChargeBatteryAsync(runtime, cancellationToken).ConfigureAwait(false) is not { } low)
        {
            return false;
        }
        int battery = low.Percent;
        if (SlotDoorsNotProvenClosed(session))
        {
            return false;
        }
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            runtime.AgvId, session.SessionGeneration, now - runtimeOptions.MaximumEvidenceAge, cancellationToken)
            .ConfigureAwait(false);
        if (lastInboundAt is null || now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return false;
        }

        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .Select(row => (JourneyRuntimeStage?)row.Stage)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stageNow != JourneyRuntimeStage.AwaitingSublot ||
            await OpenCancellationAtCurrentStopAsync(stops, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        // 锁里重新加载：锁外那次读之后，别的连接可能刚终结或取消了其中一条。
        JourneyStopCursor locked = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        if (locked.AllDemands.Any(item =>
                item.Membership.Status is JourneyDemandStatuses.Loading or JourneyDemandStatuses.Loaded) ||
            locked.Demands.Count == 0 ||
            locked.Demands.Any(item =>
                item.Demand.Status != DemandExecutionStatus.Accepted ||
                item.Membership.Status != JourneyDemandStatuses.PendingLoad))
        {
            return false;
        }

        // 收尾之后本停靠此刻那一版录入请求按算式就算不回来了，先取好（同站点期限那一处）。
        string sublotRequestMessageId = locked.CurrentSublotRequestMessageId(runtime.WorklistRevision);
        string currentStopId = locked.Current.StopId;
        string[] leaving = [.. locked.Demands.Select(item => item.Demand.DemandId)];
        int releasedFromCurrentStop = locked.Demands.Count(item =>
            string.Equals(item.Membership.PickupStopId, currentStopId, StringComparison.Ordinal));
        foreach (string demandId in leaving)
        {
            JourneyDemandRow membership = await dbContext.Set<JourneyDemandRow>()
                .SingleAsync(
                    row => row.JourneyId == runtime.JourneyId && row.DemandId == demandId && row.RemovedAt == null,
                    cancellationToken)
                .ConfigureAwait(false);
            await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, now, cancellationToken)
                .ConfigureAwait(false);
        }
        // 版数守恒，与站点期限放回同一个理由：这个停靠的归属少了几条，就补上几版，退役本站较早各版清单才数得全。
        (await TrackedStopAsync(currentStopId, cancellationToken).ConfigureAwait(false)).WorklistRefills +=
            releasedFromCurrentStop;
        // 收尾原因说 CHARGING_REQUIRED（协议 4.0.0 候选 wire-notes「Staging pickup stops」第 4 条，control-server#713）：v3 时这里说的是
        // DEMAND_RELEASED，v4 规定待送点因缺电结束、放回需求时空清单不说它。需求本身仍按释放改派放回（归属与积压行的原因不变）。
        await new PickupStopTermination(dbContext).StageJourneyClosureAsync(
            runtime, sublotRequestMessageId, CurrentStopDepartures.ChargingRequiredReason, now, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        checkpointWaits.Clear(runtime.VehicleKey);
        foreach (string demandId in leaving)
        {
            LogStagingLowBatteryReleasedDemand(
                logger, runtime.AgvId, battery, low.EntryThreshold, demandId, null);
        }
        return true;
    }

    /// <summary>站点期限结束一站时，每条待做项的去向：放回待派的（第几次），与终结并告警的（告警码）。</summary>
    private sealed record StationDeadlineEnding(
        IReadOnlyList<(string DemandId, int Release)> Released,
        IReadOnlyList<(string DemandId, string TransportDemandKey, string AlarmCode)> Alarmed);

    /// <summary>
    /// 站点期限结束当前停靠：本站每条待做项能放回待派的放回，放不回的终结并挂人工搬运告警（control-server#703）。暂存，不保存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>v5 起不放回</b>（REQ-0365，CP-0016，control-server#751）：放回上限出厂为 0，第一次站点期限到点就走下面「到了上限」那一支——
    /// 终结、按业务键抑制、挂人工搬运告警，看板站点超时抑制清单列出它。这样仍不是 control-server#703 要防的静默丢单：告警与终结同一次保存，
    /// MES 关单前一直挂着。放回那一支只在上限配成非 0 时走，那样的配置不符合 REQ-0365，并行实例的部署检查拒绝；代码路径留着，有用例覆盖。
    /// 下面两段是 control-server#703 当时的理由，保留作来历。
    /// </para>
    /// <para>
    /// <b>为什么放回。</b>先前一律以 <c>CANCELLED_BY_STATION_TIMEOUT</c> 终结。那个码按业务键永久抑制
    /// （<see cref="PickupStopTermination.KeySuppressingReasonCodes"/>），受理行又不删，v2 对 MesIngest 只读不回写——操作员只是没在时限内扫码，
    /// 这批料就被 v2 永久丢掉，货留在机台上没有任何告警。放回走的是释放改派那一步
    /// （<see cref="DemandReleaseService.StageReleasedMembershipAsync"/>）：归属移除、积压行清掉受理标记，需求仍是 <c>Accepted</c>、
    /// 不写抑制、等待年龄不重置，下一轮派车按「已释放待改派」以新的一代重新受理，不追加回这一趟。
    /// </para>
    /// <para>
    /// <b>放回的次数有上限</b>（<see cref="JourneyRuntimeOptions.StationTimeoutRedispatchLimit"/>）：一台没人管的机台，车会被反复派去、反复超时。
    /// 次数数的是这条需求移除原因为 <see cref="DemandJourneyLookup.ReleasedAtStationDeadlineReason"/> 的归属行，在库里，重启不丢；持货截断与
    /// 人工释放写的是另一个原因，不算。到了上限就终结（照旧抑制：人去搬之前 v2 不能再派这条），并在同一次保存里挂告警
    /// （<see cref="ManualCarryAlarms"/>）。
    /// </para>
    /// <para>
    /// <b>锚需求、旅程上还有别的需求时放不回</b>：旅程行上的离站核验、计划与收尾都按锚需求取需求号，释放规则也拒这一格
    /// （<c>DemandReleaseReasons.AnchorWithOtherDemands</c>）。只有同站追加才走得到，终结并告警。
    /// </para>
    /// <para>
    /// <b>车已到站的那一格</b>与持货截断（control-server#290）同一个做法：旅程继续时发那张空清单（<see cref="StopEndWorklist.StageAfterReleaseAsync"/>），
    /// 它的号取「算式」与「已发最高号 + 1」的大者，不会发出车已收到过的号；但退役这一站较早几版清单按本停靠的版数范围算，所以放回几条仍给这个
    /// 停靠补几版（<see cref="JourneyStopRow.WorklistRefills"/>），否则较早那一版不退役、重连后被补发。
    /// 放回之后旅程上一条都不剩时按终结收尾的做法关闭旅程、三套占用一起放，原因码就是放回的那个；不修订计划（见那一支的注释）。
    /// 车上 <c>stopEndedReason</c> 是 <c>DEMAND_RELEASED</c>（<see cref="StopEndedReasons"/>）：这单会重新派车，不是丢了。
    /// </para>
    /// <para>
    /// 终结的先暂存：它判「是不是最后一条」时，放回的那几条在库里还开着，所以它不收尾；收尾归后面放回那一段，它看得见两边。
    /// </para>
    /// </remarks>
    private async Task<StationDeadlineEnding> StageStationDeadlineEndingAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        string sublotRequestMessageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        JourneyStopDemand[] outstanding = [.. stops.OutstandingAtCurrentStop];
        HashSet<string> outstandingIds = outstanding.Select(item => item.Demand.DemandId).ToHashSet(StringComparer.Ordinal);
        bool othersRemain = stops.Demands.Any(item => !outstandingIds.Contains(item.Demand.DemandId));

        List<(JourneyStopDemand Item, int Release)> releasing = [];
        List<(JourneyStopDemand Item, string AlarmCode, int Releases)> alarmed = [];
        foreach (JourneyStopDemand item in outstanding)
        {
            string demandId = item.Demand.DemandId;
            int releases = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
                .CountAsync(
                    row => row.DemandId == demandId && row.RemovalReason == DemandJourneyLookup.ReleasedAtStationDeadlineReason,
                    cancellationToken)
                .ConfigureAwait(false);
            if (releases >= runtimeOptions.StationTimeoutRedispatchLimit)
            {
                alarmed.Add((item, ManualCarryAlarms.StationTimeoutRedispatchLimitReached, releases));
            }
            else if (string.Equals(demandId, runtime.DemandId, StringComparison.Ordinal) && othersRemain)
            {
                alarmed.Add((item, ManualCarryAlarms.StationTimeoutAnchorNotReleasable, releases));
            }
            else
            {
                releasing.Add((item, releases + 1));
            }
        }

        foreach ((JourneyStopDemand item, string alarmCode, int releases) in alarmed)
        {
            await new PickupStopTermination(dbContext).StageAsync(
                runtime, sublotRequestMessageId, item.Demand.DemandId, StationTimeoutCancellationReason, now, cancellationToken)
                .ConfigureAwait(false);
            await ManualCarryAlarms.StageAsync(
                dbContext,
                item.Demand.DemandId,
                item.Demand.TransportDemandKey,
                alarmCode,
                new
                {
                    journeyId = runtime.JourneyId,
                    agvId = runtime.AgvId,
                    stationId = stops.Current.StationId,
                    stationTimeoutReleases = releases,
                    redispatchLimit = runtimeOptions.StationTimeoutRedispatchLimit
                },
                now,
                cancellationToken).ConfigureAwait(false);
        }

        if (releasing.Count > 0)
        {
            string[] leaving = [.. releasing.Select(entry => entry.Item.Demand.DemandId)];
            bool last = stops.Demands.All(item => outstandingIds.Contains(item.Demand.DemandId));
            foreach (string demandId in leaving)
            {
                JourneyDemandRow membership = await dbContext.Set<JourneyDemandRow>()
                    .SingleAsync(
                        row => row.JourneyId == runtime.JourneyId && row.DemandId == demandId && row.RemovedAt == null,
                        cancellationToken)
                    .ConfigureAwait(false);
                await DemandReleaseService.StageReleasedMembershipAsync(
                    dbContext, membership, now, cancellationToken, DemandJourneyLookup.ReleasedAtStationDeadlineReason)
                    .ConfigureAwait(false);
            }
            // 版数守恒：这个停靠的归属少了几条，就补上几版。StopEndWorklist 取号时自己会取「已发最高号 + 1」，但退役这一站较早那几版清单
            // 按的是本停靠的版数范围：不补，范围缩掉放回的那几条，较早那一版不退役，重连后被补发、号比空清单低，车判回退断会话
            // （StopEndedJourneyContinuesTests 的三条用例在去掉这一行时红在这里）。收尾那一支同样按这个范围算迟到录入的地址。
            (await TrackedStopAsync(stops.Current.StopId, cancellationToken).ConfigureAwait(false)).WorklistRefills +=
                leaving.Length;
            // 这一站同时有终结并告警的（放回到上限、或放不回的锚需求）时，车上那张空清单或收尾快照说「本站因超时结束」：这一站上确有一条
            // 不会再来，说「任务释放」会让人以为都会回来（审查 L2）。只有放回的才说 DEMAND_RELEASED。
            string endedReason = alarmed.Count > 0
                ? StationTimeoutCancellationReason
                : DemandJourneyLookup.ReleasedAtStationDeadlineReason;
            if (last)
            {
                // 与终结收尾同一个做法，不修订计划：车就停在这一站，停靠不删——删了它，收尾之后「这一站结束时答过时的扫码」就找不到这一站，
                // 那几条应答留在发件箱里不发（与释放服务不同：那里车还没到站，删掉的是一个车没去过的停靠）。
                await new PickupStopTermination(dbContext).StageJourneyClosureAsync(
                    runtime, sublotRequestMessageId, endedReason, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await JourneyPlanRevisionStage.StageAsync(
                        dbContext, runtime.JourneyId, leaving, currentStopMayGo: false, routing: null, cancellationToken)
                    .ConfigureAwait(false);
                await StopEndWorklist.StageAfterReleaseAsync(
                    dbContext, runtime, releasedFromCurrentStop: true, sublotRequestMessageId,
                    endedReason, now, cancellationToken).ConfigureAwait(false);
            }
        }

        return new StationDeadlineEnding(
            [.. releasing.Select(entry => (entry.Item.Demand.DemandId, entry.Release))],
            [.. alarmed.Select(entry => (entry.Item.Demand.DemandId, entry.Item.Demand.TransportDemandKey, entry.AlarmCode))]);
    }

    /// <summary>
    /// Raises <see cref="StationTimeoutDoorNotClosedReason"/> while a load stop is past its station departure
    /// deadline with no result and a slot door not proven closed, and withdraws it once that stops being
    /// true (ADR-cross-0058 decision 4). Returns whether the block reason changed; the caller saves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stage stays <see cref="JourneyRuntimeStage.AwaitingLoadResult"/> and the journey is not Blocked:
    /// nothing is wrong with the operation, and ending the stop would let the vehicle be sent away with a
    /// door open (ADR-cross-0011/0012). The door is someone's to shut, so the alarm moves the duty to a
    /// person rather than ending anything (8005-agv-program#55). Once the door is shut the alarm is
    /// withdrawn, and the result that then arrives settles the stop as the vehicle reports it.
    /// </para>
    /// <para>
    /// The door evidence is <see cref="SlotDoorsNotProvenClosed"/>, the reading that holds the AwaitingSublot
    /// deadline back, and never RIoT's stop proof. Written on the edge only, since this runs every poll; the
    /// withdrawal clears only this code, so a reason written by anything else survives. Its start time is
    /// the block reason's own (control-server#80).
    /// </para>
    /// <para>
    /// <paramref name="alarmAt"/> is when the alarm becomes due: the station departure deadline at a machine pickup, and at
    /// a staging pickup, which has none, the load command plus
    /// <see cref="StagingDepartureRulesOptions.LoadNotSettledAlarmAfter"/> (<see cref="DoorAlarmDueAtAsync"/>,
    /// control-server#709). Null means no alarm is due.
    /// </para>
    /// </remarks>
    private bool ReconcileStationTimeoutDoorNotClosed(
        JourneyRuntimeRow runtime,
        string stationId,
        SessionRecoveryRow session,
        DateTimeOffset? alarmAt,
        DateTimeOffset now)
    {
        if (alarmAt is { } deadline &&
            now >= deadline &&
            SlotDoorsNotProvenClosed(session))
        {
            if (runtime.BlockReasonCode == StationTimeoutDoorNotClosedReason)
            {
                return false;
            }
            runtime.SetBlockReason(StationTimeoutDoorNotClosedReason, now);
            LogStationTimeoutDoorNotClosed(
                logger, runtime.AgvId, runtime.DemandId ?? runtime.JourneyId, stationId, deadline, null);
            return true;
        }
        if (runtime.BlockReasonCode != StationTimeoutDoorNotClosedReason)
        {
            return false;
        }
        runtime.SetBlockReason(null, now);
        return true;
    }

    /// <summary>
    /// When the door-not-closed alarm of the load in progress at the current stop becomes due
    /// (<see cref="ReconcileStationTimeoutDoorNotClosed"/>).
    /// </summary>
    /// <remarks>
    /// At a machine pickup that is the station departure deadline, as it always was. A staging pickup has no deadline
    /// (control-server#709), and without another clock an open door there would never be reported: the alarm is due
    /// <see cref="StagingDepartureRulesOptions.LoadNotSettledAlarmAfter"/> after the load command was queued. The command's
    /// own outbox row says when, so the clock survives a restart and a reconnect. It only tells a person, as before; it ends
    /// nothing.
    /// </remarks>
    private async Task<DateTimeOffset?> DoorAlarmDueAtAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JourneyStopDemand loading,
        CancellationToken cancellationToken)
    {
        if (!IsStagingPickup(stops.Current))
        {
            return StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout);
        }
        string? commandId = loading.Membership.LoadCommandMessageId;
        DateTimeOffset? queuedAt = commandId is null
            ? null
            : await dbContext.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageId == commandId)
                .Select(row => (DateTimeOffset?)row.CreatedAt)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return queuedAt + runtimeOptions.StagingDepartureRules.LoadNotSettledAlarmAfter;
    }

    /// <summary>
    /// Ends the demand of a load the store judged <see cref="StationOperationStatus.Failed"/>: a determinate
    /// failure received after the stop's station departure deadline (ADR-cross-0058 decision 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Defensive path: the v2 onboard never produces it; the protocol allows another onboard version to
    /// send it.</b> After its deadline the v2 onboard reopens a closed, empty slot rather than failing it
    /// (8005-agv-program#55), so this runs only for a peer that does report <c>FAILED</c> with
    /// <c>OPERATOR_TIMEOUT</c>. L1 and the synthetic peer are its whole proof.
    /// </para>
    /// <para>
    /// The server may close the stop alone because such a failure only follows the deadline, so the
    /// condition for closing it already holds when it arrives; one that arrived earlier never became
    /// Failed (<see cref="DeterminateLoadFailure"/>). The demand ends under the terminal reason its failure
    /// reason names -- <c>OPERATOR_TIMEOUT</c> ends it <c>CANCELLED_BY_STATION_TIMEOUT</c> -- the unanswered
    /// load command is settled, and the vehicle is released through the same tail as the sublot deadline
    /// (<see cref="PickupStopTermination"/>). The operation stays Failed, as the record of the load.
    /// </para>
    /// <para>
    /// <b>Re-checked under the write lock.</b> The inbound processor handles every vehicle message inside a
    /// write transaction, the cancellation of an in-flight load included, and a Failed load whose demand is
    /// still Accepted is one that cancellation authorizes. So the decision is taken again inside one here:
    /// the stage, the operation, the demand and the absence of an open load cancellation are read afresh,
    /// and whichever writer commits first decides the demand. The one that loses changes nothing, rather
    /// than rewriting a terminal reason already recorded.
    /// </para>
    /// <para>
    /// Liveness is the station deadline's measure (FR-031 AC-9): nothing is ended against evidence from a
    /// vehicle that has since gone off air.
    /// </para>
    /// </remarks>
    private async Task<bool> TrySettleDeterminateLoadFailureAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JourneyStopDemand loading,
        StationOperationRow load,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            runtime.AgvId, session.SessionGeneration, now - runtimeOptions.MaximumEvidenceAge, cancellationToken)
            .ConfigureAwait(false);
        if (lastInboundAt is null || now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return false;
        }
        // The store makes a load Failed only for a result whose reason has a terminal state, and records
        // that result in the same transaction; a null here would be a store defect, left standing rather
        // than guessed at.
        if (await DeterminateLoadFailureTerminalReasonAsync(loading, cancellationToken).ConfigureAwait(false)
            is not { } terminalReason)
        {
            return false;
        }

        await using (IDbContextTransaction transaction = await dbContext.Database
                         .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await dbContext.Entry(runtime).ReloadAsync(cancellationToken).ConfigureAwait(false);
            await dbContext.Entry(load).ReloadAsync(cancellationToken).ConfigureAwait(false);
            AcceptedDemandRow demand = await dbContext.AcceptedDemands
                .SingleAsync(row => row.DemandId == loading.Demand.DemandId, cancellationToken).ConfigureAwait(false);
            await dbContext.Entry(demand).ReloadAsync(cancellationToken).ConfigureAwait(false);
            // The one definition the sublot deadline and the cancellation's authorization use too. A cancellation
            // that did not reconcile is not "open", but it has already put the demand in RecoveryRequired and
            // the journey in Blocked, which the checks below refuse on their own.
            bool cancellationOpen = await LoadCancellationBeforeSublot
                .HasOpenCancellationAsync(dbContext, loading.Demand.DemandId, cancellationToken)
                .ConfigureAwait(false);
            if (runtime.Stage != JourneyRuntimeStage.AwaitingLoadResult ||
                load.Status != StationOperationStatus.Failed ||
                demand.Status != DemandExecutionStatus.Accepted ||
                cancellationOpen)
            {
                return false;
            }

            // The failure is the load command's answer. Left unsettled it would be replayed into every later
            // session, where the peer refuses it as a business id whose content changed.
            ProtocolOutboxRow? loadCommand = await dbContext.ProtocolOutbox
                .SingleOrDefaultAsync(
                    row => row.MessageId == loading.Membership.LoadCommandMessageId, cancellationToken)
                .ConfigureAwait(false);
            if (loadCommand is not null)
            {
                loadCommand.AcknowledgedAt ??= now;
            }
            // 终结的是「装失败的那一条」（批次7-06）。旅程还带着别的需求时，第二步（关闭旅程）不跑，下一轮回到
            // 当前停靠继续推进。
            await new PickupStopTermination(dbContext).StageAsync(
                runtime, stops.CurrentSublotRequestMessageId(runtime.WorklistRevision), loading.Demand.DemandId, terminalReason, now,
                cancellationToken).ConfigureAwait(false);
            // 不放回待派（control-server#703）：确定的失败里可以有 COMPLETED 的仓位，这条需求的货可能一部分已在车上，改派会让别的车去搬
            // 同一批货；归属也已是 LOADING、装货命令已结算，释放只收没开始装的。所以终结照旧，同一次保存挂人工告警，让人去看货在哪。
            await ManualCarryAlarms.StageAsync(
                dbContext,
                loading.Demand.DemandId,
                loading.Demand.TransportDemandKey,
                ManualCarryAlarms.DeterminateLoadFailureNeedsManual,
                new
                {
                    journeyId = runtime.JourneyId,
                    agvId = runtime.AgvId,
                    stationId = stops.Current.StationId,
                    slotOperationAttemptId = loading.Membership.LoadSlotOperationAttemptId,
                    terminalReason
                },
                now,
                cancellationToken).ConfigureAwait(false);
            // 旅程还带着别的需求、没有收尾时，阶段要离开等装货结果（control-server#291 的 S2）：这一条已经终结，这一站再没有在装的，
            // 留在这里下一轮每轮抛 "no demand loading at its stop"，车带着更早装上的货永远不走。与站点期限结束停靠
            // （TryEndStopAtStationDeadlineAsync）同一个出口：本站没有待装的就交给离站那一段，由它决定持货等单还是离站；
            // 还有待装的就回到等录入——期限已过，那里的期限出口会照它自己的条件（门、在线、取消）结束本站。与终结同一次保存。
            //
            // 回等录入时这里不发新一版清单与录入请求：终结让本站的号前进了，下一轮等录入那一段先走期限出口（确定的失败只在本站离站期限之后
            // 出现——店里只把期限之后到的确定失败记成 Failed，DeterminateLoadFailure；期限之前的进恢复，
            // JourneyRuntimeWorkerLoadDeadlineTests.AFailureReportedBeforeTheDeadlineIsNotSettledAsDeterminate），期限出口结束本站；
            // 期限出口这一轮没结束它（门没证明关着、车不在线、取消开着）时，由 WorklistBehindAnEndingAtTheStopAsync 认出号已前进、
            // 补发新一版清单与录入请求（control-server#723）。所以这里不必与续跑对称地自己发。
            if (runtime.Stage != JourneyRuntimeStage.Completed)
            {
                bool othersToLoadHere = stops.OutstandingAtCurrentStop
                    .Any(item => item.Demand.DemandId != loading.Demand.DemandId);
                SetStage(
                    runtime,
                    othersToLoadHere ? JourneyRuntimeStage.AwaitingSublot : JourneyRuntimeStage.AwaitingStationDeparture,
                    now);
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (runtime.Stage == JourneyRuntimeStage.Completed)
        {
            await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 旅程继续而这一站结束了：那张空清单随上面那次保存落库，提交之后发（control-server#324）。
            await StopEndWorklist.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        }
        checkpointWaits.Clear(runtime.VehicleKey);
        LogDeterminateLoadFailureSettled(
            logger, runtime.AgvId, loading.Demand.DemandId, terminalReason, null);
        LogManualCarryAlarmRaised(
            logger, loading.Demand.DemandId, loading.Demand.TransportDemandKey, runtime.AgvId,
            ManualCarryAlarms.DeterminateLoadFailureNeedsManual, null);
        return true;
    }

    /// <summary>
    /// The terminal reason the live result of this journey's load names, read from the result as the vehicle
    /// sent it.
    /// </summary>
    private async Task<string?> DeterminateLoadFailureTerminalReasonAsync(
        JourneyStopDemand loading,
        CancellationToken cancellationToken)
    {
        string attemptId = loading.Membership.LoadSlotOperationAttemptId;
        string? resultId = await dbContext.OperationResults.AsNoTracking()
            .Where(row => row.SlotOperationAttemptId == attemptId &&
                          row.SupersededByResultId == null &&
                          !row.HistoricalOnly)
            .Select(row => row.ResultId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        ProtocolInboxRow? result = resultId is null
            ? null
            : await dbContext.ProtocolInbox.AsNoTracking()
                .SingleOrDefaultAsync(row => row.MessageId == resultId, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(result.RequestJson);
        return DeterminateLoadFailure.TerminalReason(SlotOutcomeReport.FromSlotResults(
            document.RootElement.GetProperty("payload").GetProperty("slotResults")));
    }

    /// <summary>
    /// Whether the vehicle's current safety evidence fails to show every slot door shut: it reports a
    /// door not closed (<c>LOCK_NOT_CLOSED</c>), or something unknown, or the server holds no reading
    /// at all. The server must not end a stop against evidence it cannot read.
    /// </summary>
    private static bool SlotDoorsNotProvenClosed(SessionRecoveryRow session)
    {
        if (session.SafetyUnknownPresent != false || session.SafetyReasonCodesJson is null)
        {
            return true;
        }
        string[] reasonCodes = JsonSerializer.Deserialize<string[]>(session.SafetyReasonCodesJson) ?? [];
        return reasonCodes.Contains("LOCK_NOT_CLOSED", StringComparer.Ordinal);
    }

    /// <summary>
    /// CV-PREDEPARTURE-SAFETY-EXPIRES: retires the current pre-departure check once it no longer
    /// speaks for the vehicle's safety, and asks again under a new identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A check expires when the safety state has moved past the version it asked about -- whether or
    /// not it was answered -- or when its answer's own window has closed. Either way the answer
    /// cannot authorize a departure, and before 2026-09-13 nothing replaced it: one check id per
    /// journey, judged invalid on every later poll, so the vehicle waited at the pickup for ever.
    /// </para>
    /// <para>
    /// Not asked again while the vehicle is unsafe: that check could only be answered UNSAFE, and it
    /// would be asked again on every poll. The next SafetyStateChanged that makes it safe moves the
    /// version on and expires the check then. The new identity is derived from the old one, so a
    /// restart mid-way derives the same ids and needs no column to remember a counter.
    /// </para>
    /// </remarks>
    /// <summary>
    /// 扣车时作废这一站已经发出的离站检查（control-server#385 审查 N2）：退役那一行、换一对新身份，不发。已经换过（这一站的检查
    /// 还没发）就什么也不做，所以扣车期间每一轮回到这里都是无操作。
    /// </summary>
    /// <remarks>
    /// 新身份按「旧身份 × door-hold」派生，同一站再扣一次就再换一对，不会撞上已经作废的那一张。旧检查的应答此后对不上当前停靠的
    /// 检查 id，运行时不会再读它（<see cref="FindSafeDepartureResultAsync"/> 按检查 id 找）。
    /// </remarks>
    private async Task RetireDepartureCheckForDoorHoldAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow? sent = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(row => row.MessageId == DepartureCheckMessageId(stops.Current), cancellationToken)
            .ConfigureAwait(false);
        if (sent is null)
        {
            return;
        }

        if (sent.AcknowledgedAt is null && sent.FencedAt is null)
        {
            sent.FencedAt = now;
        }
        string checkId = JourneyPlanBuilder.StableGuid(DepartureCheckId(stops.Current), "door-hold");
        string messageId = JourneyPlanBuilder.StableGuid(DepartureCheckMessageId(stops.Current), "door-hold");
        JourneyStopRow trackedStop = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == stops.Current.StopId, cancellationToken).ConfigureAwait(false);
        trackedStop.DepartureSafetyCheckId = checkId;
        trackedStop.DepartureSafetyCheckMessageId = messageId;
        stops.Current.DepartureSafetyCheckId = checkId;
        stops.Current.DepartureSafetyCheckMessageId = messageId;
        runtime.PreDepartureSafetyCheckId = checkId;
        runtime.PreDepartureSafetyCheckMessageId = messageId;
        runtime.UpdatedAt = now;
    }

    /// <summary>
    /// 这一站当前那张离站检查还没发（扣车时作废后换的新身份）就现在发，返回是否发了（control-server#385 审查 N2）。
    /// 已经发过的什么也不做：到这里的每一轮都先问一次，所以这一步只在放行之后的第一轮真正做事。
    /// </summary>
    private async Task<bool> PublishDepartureCheckNotYetSentAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        if (stops.Current.DepartureSafetyCheckMessageId is null ||
            await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == DepartureCheckMessageId(stops.Current), cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        await publisher.PublishPreDepartureSafetyCheckAsync(
            DepartureCheckMessageId(stops.Current),
            runtime.AgvId,
            session.SessionGeneration,
            new PreDepartureSafetyCheckCommand(
                DepartureCheckId(stops.Current),
                PreDepartureCheckPurposes.Departure,
                runtime.TransportColumn(runtime.DemandId),
                NextStopAfterCurrent(stops).MovementLegId,
                session.SafetyRevision ?? throw new InvalidDataException("Safety revision is required."),
                NextStopAfterCurrent(stops).StationId),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ReissueExpiredDepartureCheckAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (session.DepartureSafe != true || session.SafetyRevision is not long currentRevision)
        {
            return false;
        }

        ProtocolOutboxRow? check = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(row => row.MessageId == DepartureCheckMessageId(stops.Current), cancellationToken)
            .ConfigureAwait(false);
        if (check is null)
        {
            return false;
        }

        long askedRevision;
        using (JsonDocument document = JsonDocument.Parse(check.PayloadJson))
        {
            askedRevision = document.RootElement.GetProperty("payload")
                .GetProperty("expectedSafetyStateVersion").GetInt64();
        }

        bool expired = askedRevision != currentRevision;
        if (!expired)
        {
            ProtocolInboxRow[] answers = await dbContext.ProtocolInbox.AsNoTracking()
                .Where(row => row.MessageType == "PreDepartureSafetyCheckResult")
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (ProtocolInboxRow answer in answers)
            {
                using JsonDocument document = JsonDocument.Parse(answer.RequestJson);
                JsonElement payload = document.RootElement.GetProperty("payload");
                if (RequiredString(payload, "preDepartureSafetyCheckId") != DepartureCheckId(stops.Current))
                    continue;
                // A version change expires the check at once. A window that merely closed while the
                // version stayed put waits out the evidence age first: that answer lapses every couple
                // of seconds while a departure is held for another reason (the create gate refusing
                // the gate leg), and asking again each time would fill the outbox with checks.
                expired |= payload.GetProperty("safetyStateVersion").GetInt64() != currentRevision ||
                           now - payload.GetProperty("validUntil").GetDateTimeOffset() >= runtimeOptions.MaximumEvidenceAge;
            }
        }
        if (!expired)
        {
            return false;
        }

        if (check.AcknowledgedAt is null && check.FencedAt is null)
        {
            check.FencedAt = now;
        }
        // 换一对新身份，派生算法照旧（control-server#208 只改「从哪里读」）。写三处：库里的停靠行是权威，旅程行那两列
        // 跟着写，本轮 cursor 手上那个快照实例也跟着改——这一轮重发完会原地再判一次答复，判的必须是新身份。
        string reissuedCheckId = JourneyPlanBuilder.StableGuid(
            DepartureCheckId(stops.Current), "reissued-after-expiry");
        string reissuedMessageId = JourneyPlanBuilder.StableGuid(
            DepartureCheckMessageId(stops.Current), "reissued-after-expiry");
        JourneyStopRow trackedStop = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == stops.Current.StopId, cancellationToken).ConfigureAwait(false);
        trackedStop.DepartureSafetyCheckId = reissuedCheckId;
        trackedStop.DepartureSafetyCheckMessageId = reissuedMessageId;
        stops.Current.DepartureSafetyCheckId = reissuedCheckId;
        stops.Current.DepartureSafetyCheckMessageId = reissuedMessageId;
        // 旅程行那两列今天是重发后的权威，本票把权威搬到停靠行，但仍然照旧写它：库里落下的状态因此一字未变，
        // 而票面之外的读者（如果还有）也读不到过期的值。它们在 批次7-06 随多停靠退役。
        runtime.PreDepartureSafetyCheckId = reissuedCheckId;
        runtime.PreDepartureSafetyCheckMessageId = reissuedMessageId;
        await publisher.PublishPreDepartureSafetyCheckAsync(
            reissuedMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new PreDepartureSafetyCheckCommand(
                reissuedCheckId,
                PreDepartureCheckPurposes.Departure,
                runtime.TransportColumn(runtime.DemandId),
                NextStopAfterCurrent(stops).MovementLegId,
                currentRevision,
                NextStopAfterCurrent(stops).StationId),
            cancellationToken).ConfigureAwait(false);
        runtime.SetBlockReason("PREDEPARTURE_CHECK_EXPIRED", now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }



}
