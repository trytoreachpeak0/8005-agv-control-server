using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// The facts one dispatch round is decided against, read once before the round and unchanged
/// throughout it.
/// </summary>
/// <remarks>
/// Read once so that every candidate in a round is judged against the same catalog, the same
/// Map and the same clock. A criterion that re-read any of these could accept two candidates
/// against two different worlds.
/// </remarks>
/// <param name="Catalog">MesIngest's open transport demands.</param>
/// <param name="Map">The current Map/Station catalog snapshot.</param>
/// <param name="Gate">The gate station this journey family ends at.</param>
/// <param name="AcceptedDemandIds">
/// Demands this server has already accepted. A demand it has accepted is bound to its one journey
/// permanently and is never a candidate again, whatever stage that journey reached.
/// </param>
/// <param name="Now">The round's clock.</param>
public sealed record DispatchRoundFacts(
    DemandCatalogSnapshot Catalog,
    RiotMapStationCatalogSnapshot Map,
    RiotMapStation Gate,
    IReadOnlySet<string> AcceptedDemandIds,
    DateTimeOffset Now);

/// <summary>
/// One vehicle's facts for this round, plus the configuration slice that applies to it.
/// </summary>
/// <remarks>
/// The dispatch loop iterates vehicles serially, so everything vehicle-specific arrives through
/// here rather than off a single-vehicle configuration. With one configured vehicle this carries
/// exactly what the loop used to read directly, which is why N=1 stays behaviour-identical.
/// </remarks>
/// <param name="VehicleKey">The RIoT vehicle key this round segment is deciding for.</param>
/// <param name="Onboard">Onboard facts, or null when they are not ready.</param>
/// <param name="Vehicle">The RIoT vehicle observation.</param>
/// <param name="ObservedAt">When the dynamic facts above were read.</param>
public sealed record DispatchVehicleFacts(
    string VehicleKey,
    OnboardDispatchFacts? Onboard,
    RiotVehicleObservation Vehicle,
    DateTimeOffset ObservedAt);

/// <summary>Onboard-side facts a dispatch decision reads.</summary>
public sealed record OnboardDispatchFacts(
    long SessionGeneration,
    int[] AvailableSlots,
    bool DepartureSafe,
    bool VehicleStopped,
    bool AllTargetSlotsLocked,
    bool AllUnlockOutputsReset,
    bool UnknownPresent);

/// <summary>The route a candidate resolved to.</summary>
public sealed record ResolvedJourneyRoute(
    string DispatchZone,
    string RouteEvidenceId,
    string PickupStationId,
    int PickupStationRiotId);

/// <summary>
/// One candidate's evaluation as it moves down the criterion chain.
/// </summary>
/// <remarks>
/// The chain is not a set of independent predicates: later criteria need what earlier ones
/// resolved — the route, the frozen package capacity, the basket count. Those accumulate here,
/// and a criterion only ever runs after everything it depends on has been established, because
/// the chain short-circuits on the first block.
/// </remarks>
public sealed class DispatchCandidateEvaluation(
    AcceptedDemandSnapshot candidate,
    DispatchRoundFacts round,
    DispatchVehicleFacts vehicle)
{
    public AcceptedDemandSnapshot Candidate { get; } = candidate;

    public DispatchRoundFacts Round { get; } = round;

    public DispatchVehicleFacts Vehicle { get; } = vehicle;

    /// <summary>Set by the station-resolution criterion; every later criterion may rely on it.</summary>
    public ResolvedJourneyRoute? Route { get; set; }

    /// <summary>Frozen by the package-capacity criterion.</summary>
    public int? PackageCapacity { get; set; }

    /// <summary>Derived by the slot-capacity criterion from box count and package capacity.</summary>
    public int ExpectedBasketCount { get; set; }

    /// <summary>The slots this candidate would occupy, chosen by the slot-capacity criterion.</summary>
    public int[] TargetSlots { get; set; } = [];

    /// <summary>
    /// What the route graph priced this vehicle's trip to the pickup station at, in mm; null when
    /// the engine is off or did not price it.
    /// </summary>
    /// <remarks>
    /// Set by the reachability criterion and read by the ranker, which is why the two facts stay
    /// separate: REQ-0207 evicts a vehicle whose reachability is unconfirmed, but only drops a
    /// comparison layer when the cost is missing. <b>Not a RouteCost</b> — that is RIoT's fact,
    /// under RIoT's name, for the pre-create gate.
    /// </remarks>
    public long? GraphTraversalCostMm { get; set; }

    /// <summary>
    /// The catalog revision this round's endpoints are being taken from, set by the catalog
    /// criterion. What REQ-0305 freezes alongside the station ids.
    /// </summary>
    public long CatalogRevision { get; set; }

    /// <summary>
    /// What RIoT's <c>getRouteCostsBy</c> answered for this vehicle and pickup station, set by the
    /// pre-create gate.
    /// </summary>
    /// <remarks>
    /// <b>This one really is a RouteCost</b> — RIoT's number, under RIoT's name. It is kept beside
    /// <see cref="GraphTraversalCostMm"/> rather than merged with it because the whole point of the
    /// gate is that two independent sources answered, and a single field would record only that
    /// something did.
    /// </remarks>
    public long? RiotRouteCostMm { get; set; }
}

/// <summary>
/// One admission criterion: given a candidate and the round's facts, either pass or name one
/// blocking reason.
/// </summary>
/// <remarks>
/// A criterion returns the reason code it blocks on, or <see cref="DispatchAdmissionChain.Eligible"/>
/// to pass. It may write what it resolved into the evaluation for later criteria to use. It must
/// not decide anything a later criterion is responsible for, and it must not swallow a failure
/// into a pass — the chain is fail-closed, and a criterion that cannot establish its fact blocks.
/// </remarks>
public interface IDispatchAdmissionCriterion
{
    /// <summary>Stable order within the chain. Lower runs first.</summary>
    int Order { get; }

    Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken);
}

/// <summary>
/// The fail-closed admission chain: criteria in order, short-circuiting on the first block.
/// </summary>
/// <remarks>
/// <para>
/// <b>This registry is the single assembly point.</b> A new capability adds one criterion file and
/// one registration, which is the entire reason this chain exists: B2 multi-vehicle, the
/// RouteGraphSnapshot engine, the FP-C13 create gate and REQ-0207 all need to insert logic into
/// what used to be one 255-line method, and they would collide there.
/// </para>
/// <para>
/// Short-circuiting is not just an optimisation. The first criterion refuses an already-accepted
/// demand ahead of everything else, and the route, package, box-count and vehicle reads below it
/// would otherwise be spent on a decision that is already made.
/// </para>
/// </remarks>
public sealed class DispatchAdmissionChain(IEnumerable<IDispatchAdmissionCriterion> criteria)
{
    /// <summary>The reason code that means "not blocked".</summary>
    public const string Eligible = "ELIGIBLE";

    private readonly IDispatchAdmissionCriterion[] _criteria =
        criteria.OrderBy(criterion => criterion.Order).ToArray();

    /// <summary>
    /// Runs the chain and returns either <see cref="Eligible"/> or the first blocking reason.
    /// </summary>
    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        foreach (IDispatchAdmissionCriterion criterion in _criteria)
        {
            string reason = await criterion.EvaluateAsync(evaluation, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(reason, Eligible, StringComparison.Ordinal))
            {
                return reason;
            }
        }

        return Eligible;
    }
}

/// <summary>A candidate that cleared the whole chain, with what the chain resolved for it.</summary>
public sealed record EligibleDispatchCandidate(
    AcceptedDemandSnapshot Snapshot,
    ResolvedJourneyRoute Route,
    int ExpectedBasketCount,
    int[] TargetSlots,
    DateTimeOffset FirstSeenAt,
    long? GraphTraversalCostMm = null,
    long CatalogRevision = 0);

/// <summary>
/// Picks which eligible candidate a vehicle takes this round.
/// </summary>
/// <remarks>
/// Separated from the chain because they answer different questions: the chain decides whether a
/// candidate may be taken at all, the ranker decides which of the survivors this vehicle takes.
/// The engine ticket replaces the implementation with real path cost; the chain does not change
/// when it does.
/// </remarks>
public interface IDispatchCandidateRanker
{
    EligibleDispatchCandidate SelectNext(IReadOnlyList<EligibleDispatchCandidate> eligible);
}

/// <summary>
/// Oldest-first with a total order: first seen, then created, then demand id.
/// </summary>
/// <remarks>
/// The demand id tie-break is what makes this deterministic rather than merely stable — two
/// demands can share both timestamps, and a dispatch decision that depends on enumeration order
/// is not reproducible from the evidence afterwards.
/// </remarks>
public sealed class FirstSeenDispatchCandidateRanker : IDispatchCandidateRanker
{
    public EligibleDispatchCandidate SelectNext(IReadOnlyList<EligibleDispatchCandidate> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        if (eligible.Count == 0)
        {
            throw new ArgumentException("The ranker is only called with at least one candidate.", nameof(eligible));
        }

        return eligible
            .OrderBy(item => item.FirstSeenAt)
            .ThenBy(item => item.Snapshot.CreatedAt)
            .ThenBy(item => item.Snapshot.DemandId, StringComparer.Ordinal)
            .First();
    }
}
