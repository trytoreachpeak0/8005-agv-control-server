using ControlServer.Domain;
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
    /// The attribution a row carries when its unknown safety evidence is explained by this server's own in-flight move order
    /// (control-server#139), so the card can say why the row was not sent to maintenance.
    /// </summary>
    internal const string OwnMovementOrderInFlight = "OWN_MOVEMENT_ORDER_IN_FLIGHT";

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
                    ownOrderInFlight.Contains(row.DemandId),
                    now))
                .ToArray()
        };
    }

    private object Fact(
        JourneyRuntimeRow row,
        SessionRecoveryRow? session,
        bool departedForGate,
        bool ownOrderInFlight,
        DateTimeOffset now)
    {
        TimeSpan? blockedFor = row.BlockReasonSince is DateTimeOffset since
            ? (now > since ? now - since : TimeSpan.Zero)
            : null;
        bool carriesSession = string.Equals(row.BlockReasonCode, SessionNotReadyReason, StringComparison.Ordinal);
        bool explained = carriesSession && OwnMovementOrderExplanation.Explains(
            row.BlockReasonCode,
            session?.ReasonCode,
            session?.SafetyReasonCodesJson,
            session?.SafetyUnknownPresent,
            ownOrderInFlight);
        BlockedJourneyEscalationLevel level =
            _escalation.Classify(blockedFor, carriesSession, session?.SafetyUnknownPresent, explained);
        return new
        {
            agvId = row.AgvId,
            demandId = row.DemandId,
            stage = row.Stage.ToString(),
            stationId = AtGateLeg(row.Stage, departedForGate) ? row.GateStationId : row.PickupStationId,
            pickupStationId = row.PickupStationId,
            gateStationId = row.GateStationId,
            blockReasonCode = row.BlockReasonCode,
            blockReasonSince = row.BlockReasonSince,
            blockedSeconds = blockedFor is TimeSpan elapsed ? (long?)elapsed.TotalSeconds : null,
            escalationLevel = level.ToString(),
            unknownExplainedBy = explained ? OwnMovementOrderInFlight : null,
            session = carriesSession
                ? new
                {
                    present = session is not null,
                    reasonCode = session?.ReasonCode,
                    safetyReasonCodesJson = session?.SafetyReasonCodesJson,
                    safetyUnknownPresent = session?.SafetyUnknownPresent
                }
                : null
        };
    }

    /// <summary>
    /// The journeys (by demand id) for which this server itself has a move order in flight on RIoT, by its own records.
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
                .Select(leg => leg.DemandId),
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
