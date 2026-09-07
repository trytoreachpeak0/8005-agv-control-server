using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime.CreateGate;

/// <summary>
/// Task-level block reasons (REQ-0308). Each one is about <em>this</em> demand's endpoints, judged
/// against a catalog that is itself fresh — never about the catalog being in doubt, which is the
/// other level and lives in <see cref="CatalogAvailabilityReasons"/>.
/// </summary>
public static class CreateGateReasons
{
    /// <summary>RIoT answered, and the answer is that this vehicle cannot reach that station.</summary>
    public const string StationUnreachable = "CREATE_GATE_STATION_UNREACHABLE";

    /// <summary>
    /// RIoT could not be asked. Not the same as unreachable: no claim about the Map was made, and
    /// recording one would put words in RIoT's mouth.
    /// </summary>
    public const string RouteCostUnavailable = "CREATE_GATE_ROUTE_COST_UNAVAILABLE";

    /// <summary>
    /// The two evidence sources disagreed about reachability. Blocked and alarmed, never resolved
    /// by preferring one.
    /// </summary>
    public const string EvidenceConflict = "CREATE_GATE_EVIDENCE_CONFLICT";

    /// <summary>
    /// A station this demand froze at creation time is gone from the current fresh catalog. This
    /// is REQ-0308's StructuralDispatchBlock: a real missing station, proven by a good snapshot.
    /// </summary>
    public const string FrozenStationAbsent = "CREATE_GATE_FROZEN_STATION_ABSENT";
}

/// <summary>One thing the gate is asked about: this vehicle, this station, now.</summary>
/// <param name="GraphTraversalCostMm">
/// What the route graph priced the same trip at, or null when the engine did not answer — either
/// because it is off, or because this call site never consults it. Null means "no second source",
/// not "the second source said no": the engine's own criterion already evicts a vehicle it found
/// unreachable, so a candidate arriving here with null was never judged by the graph at all.
/// </param>
/// <param name="TargetStationInCurrentCatalog">
/// Whether the target station is present in the catalog snapshot this round is being decided
/// against. False is only reachable for an endpoint frozen earlier that the catalog no longer has.
/// </param>
public sealed record CreateGateRequest(
    string DemandId,
    string TransportDemandKey,
    string AgvId,
    string VehicleKey,
    int MapId,
    int TargetStationId,
    long? GraphTraversalCostMm,
    bool TargetStationInCurrentCatalog = true);

/// <summary>What the gate decided, and the RIoT evidence it decided on.</summary>
public sealed record CreateGateOutcome(
    CreateGateVerdict Verdict,
    string? BlockReason,
    long? RiotRouteCostMm)
{
    public bool IsAllowed => Verdict == CreateGateVerdict.Allowed;
}

/// <summary>
/// The pre-create gate: no order is created for a station this vehicle cannot be shown to reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>getRouteCostsBy</c> and not the route graph.</b> The two answer different questions
/// and the split is not a compromise. Selecting a vehicle asks "what does station A to station B
/// cost", and <c>getRouteCostsBy</c> cannot answer it — its origin is always the vehicle's current
/// position. Creating an order asks "can <i>this vehicle</i> get to that station right now", which
/// is the only question that endpoint answers. Five baseline entries require the gate by name:
/// REQ-0147, REQ-0293, REQ-0302, REQ-0305 and REQ-0345.
/// </para>
/// <para>
/// <b>Disagreement blocks; it is never resolved.</b> When the graph found the station reachable and
/// RIoT says it is not, neither source is promoted. RIoT is the system that will actually move the
/// vehicle, so its "no" cannot be overridden; the graph is built from RIoT's own edge table, so its
/// "yes" is not noise either. Two sources contradicting each other is evidence that something is
/// wrong with one of them, and dispatching on the more convenient one buries that.
/// </para>
/// <para>
/// <b>Only reachability is compared, never the two costs.</b> That <c>MapEdge.cost</c> and
/// <c>getRouteCostsBy</c>'s <c>costs</c> are the same quantity in the same unit is an inference —
/// Round 43 never sampled both over one origin/destination pair — so a magnitude comparison would
/// rest a production block on something nobody measured. Both numbers are written to the audit side
/// by side, which is what makes the inference checkable later from real traffic instead of
/// load-bearing now.
/// </para>
/// </remarks>
public sealed class PreCreateGate(
    IRiotRouteCostProbe routeCostProbe,
    ICatalogAvailabilityStore store,
    TimeProvider timeProvider,
    ILogger<PreCreateGate> logger)
{
    private static readonly Action<ILogger, string, string, int, long?, Exception?> LogConflict =
        LoggerMessage.Define<string, string, int, long?>(
            LogLevel.Error,
            new EventId(2112, nameof(LogConflict)),
            "Create gate blocked demand {DemandId}: the route graph reached station {TargetStationId} " +
            "for vehicle {AgvId} at cost {GraphTraversalCostMm} mm, and RIoT reports it unreachable. " +
            "Neither source is preferred; no order is created.");

    private static readonly Action<ILogger, string, int, string, Exception?> LogBlocked =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Warning,
            new EventId(2113, nameof(LogBlocked)),
            "Create gate blocked demand {DemandId} for station {TargetStationId}: {Reason}.");

    public async Task<CreateGateOutcome> EvaluateAsync(
        CreateGateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.TargetStationInCurrentCatalog)
        {
            // REQ-0305: a frozen endpoint is never re-resolved and never remapped onto a similar
            // name. If the catalog no longer has it, the answer is to stop, not to find another.
            return await RecordAsync(
                request,
                new CreateGateOutcome(
                    CreateGateVerdict.BlockedFrozenStationAbsent,
                    CreateGateReasons.FrozenStationAbsent,
                    null),
                cancellationToken).ConfigureAwait(false);
        }

        RiotRouteCost? riot = await routeCostProbe.ReadRouteCostAsync(
            request.MapId,
            request.TargetStationId,
            request.VehicleKey,
            cancellationToken).ConfigureAwait(false);

        if (riot is null)
        {
            return await RecordAsync(
                request,
                new CreateGateOutcome(
                    CreateGateVerdict.BlockedRouteCostUnavailable,
                    CreateGateReasons.RouteCostUnavailable,
                    null),
                cancellationToken).ConfigureAwait(false);
        }

        // The graph answers at all only when it found the station reachable — its own criterion
        // evicts the vehicle otherwise — so a cost here is a second "yes" and null is silence.
        bool graphSaidReachable = request.GraphTraversalCostMm is not null;

        if (!riot.IsReachable && graphSaidReachable)
        {
            LogConflict(
                logger, request.DemandId, request.AgvId, request.TargetStationId,
                request.GraphTraversalCostMm, null);
            return await RecordAsync(
                request,
                new CreateGateOutcome(
                    CreateGateVerdict.BlockedEvidenceConflict,
                    CreateGateReasons.EvidenceConflict,
                    riot.CostsMm),
                cancellationToken).ConfigureAwait(false);
        }

        if (!riot.IsReachable)
        {
            return await RecordAsync(
                request,
                new CreateGateOutcome(
                    CreateGateVerdict.BlockedUnreachable,
                    CreateGateReasons.StationUnreachable,
                    riot.CostsMm),
                cancellationToken).ConfigureAwait(false);
        }

        return await RecordAsync(
            request,
            new CreateGateOutcome(CreateGateVerdict.Allowed, null, riot.CostsMm),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the audit row, but only when this verdict is a change from the one standing.
    /// </summary>
    /// <remarks>
    /// REQ-0308 forbids manufacturing an alarm per polling round or per waiting task, and the gate
    /// runs every round for every open demand. Appending an identical verdict once a second would
    /// bury the transition that matters under a shift's worth of rows saying the same thing.
    /// </remarks>
    private async Task<CreateGateOutcome> RecordAsync(
        CreateGateRequest request,
        CreateGateOutcome outcome,
        CancellationToken cancellationToken)
    {
        CreateGateVerdict? standing = await store
            .ReadLastVerdictAsync(request.DemandId, cancellationToken).ConfigureAwait(false);
        if (standing == outcome.Verdict)
        {
            return outcome;
        }

        if (!outcome.IsAllowed && outcome.Verdict != CreateGateVerdict.BlockedEvidenceConflict)
        {
            LogBlocked(logger, request.DemandId, request.TargetStationId, outcome.BlockReason!, null);
        }

        await store.RecordGateVerdictAsync(
            new CreateGateEvaluation(
                Guid.NewGuid().ToString("D"),
                request.DemandId,
                request.TransportDemandKey,
                request.AgvId,
                request.MapId,
                request.TargetStationId,
                outcome.Verdict,
                outcome.RiotRouteCostMm,
                request.GraphTraversalCostMm,
                request.GraphTraversalCostMm is not null,
                outcome.Verdict == CreateGateVerdict.BlockedEvidenceConflict
                    ? $"route graph reached station {request.TargetStationId} at " +
                      $"{request.GraphTraversalCostMm} mm; RIoT reported {outcome.RiotRouteCostMm}"
                    : null,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        return outcome;
    }
}
