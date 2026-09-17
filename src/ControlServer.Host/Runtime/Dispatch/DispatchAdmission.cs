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
/// <para>
/// <b>This set grows while the round runs.</b> Every vehicle in a round ranks the same catalog by
/// the same total order, so without this they would all select the same demand and every vehicle
/// after the first would collide on it in intake. The round's segments are strictly serial, which
/// is what makes adding to it sound: no segment reads it while another writes.
/// </para>
/// </param>
/// <param name="Now">The round's clock.</param>
/// <param name="Policy">
/// The fleet's dispatch policy: which vehicle may take which task type, and which vehicles serve
/// which zone. Read once for the same reason the catalog is — two vehicles judged against two
/// reads of the policy could both be admitted under rules that never held at the same instant.
/// </param>
/// <param name="AreaAssignments">
/// The current version of the area assignment table (REQ-0191, REQ-0349), or null when none was ever
/// imported. Read once for the same reason as the policy: a criterion reading it for itself could judge
/// two candidates of one round against two versions, and a demand must freeze the version its slot group
/// was chosen from (REQ-0350).
/// </param>
public sealed record DispatchRoundFacts(
    DemandCatalogSnapshot Catalog,
    RiotMapStationCatalogSnapshot Map,
    RiotMapStation Gate,
    IReadOnlySet<string> AcceptedDemandIds,
    DateTimeOffset Now,
    VehicleDispatchPolicy Policy,
    AreaAssignmentTableVersion? AreaAssignments = null);

/// <summary>
/// One vehicle's facts for this round, plus the configuration slice that applies to it.
/// </summary>
/// <remarks>
/// The dispatch loop iterates vehicles serially, so everything vehicle-specific arrives through
/// here rather than off a single-vehicle configuration. With one configured vehicle this carries
/// exactly what the loop used to read directly, which is why N=1 stays behaviour-identical.
/// </remarks>
/// <param name="VehicleKey">The RIoT vehicle key this round segment is deciding for.</param>
/// <param name="AgvId">
/// 8005's name for the same vehicle, resolved by the roster before the round segment starts.
/// Carried here rather than looked up per criterion: a criterion that resolves identity itself has
/// to be given the register, and two criteria resolving it separately can disagree. An empty
/// string is not a vehicle — it means identity was never resolved, and the fault criterion blocks
/// on it.
/// </param>
/// <param name="Onboard">Onboard facts, or null when they are not ready.</param>
/// <param name="Vehicle">The RIoT vehicle observation.</param>
/// <param name="ObservedAt">When the dynamic facts above were read.</param>
/// <param name="SlotPositions">
/// Which slot group each of this vehicle's physical slots belongs to, and how many physical slots each group
/// has; null when the vehicle's slot model is unresolved. Read once per vehicle per round from the server's
/// own record of the vehicle's model, never from what the vehicle reports (program#70 decision 4) — which is
/// why the onboard available-slot facts above stay as they are.
/// </param>
public sealed record DispatchVehicleFacts(
    string VehicleKey,
    string AgvId,
    OnboardDispatchFacts? Onboard,
    RiotVehicleObservation Vehicle,
    DateTimeOffset ObservedAt,
    VehicleSlotPositions? SlotPositions = null);

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

    /// <summary>
    /// The version of the area assignment table this candidate was looked up in, set by the area
    /// assignment lookup; null when no table was ever imported. What the demand freezes (REQ-0350).
    /// </summary>
    public long? AreaAssignmentVersion { get; set; }

    /// <summary>
    /// What that version assigns to this demand's AREA — its dispatch zone and slot group — or null when
    /// it names the AREA nowhere. Set by the area assignment lookup; nothing is refused on it there.
    /// </summary>
    public AreaAssignment? AreaAssignment { get; set; }

    /// <summary>
    /// The slot group this demand's baskets must go into, which is the one its AREA is assigned. Read straight
    /// off <see cref="AreaAssignment"/>, so it is always a function of the frozen version and the AREA and has
    /// no second writer; null until the area assignment lookup has run, and when the AREA has no assignment.
    /// </summary>
    /// <remarks>
    /// The slot capacity criterion chooses <see cref="TargetSlots"/> inside this group (control-server#73), so
    /// a plan's target slots are always in the group it carries.
    /// </remarks>
    public string? RequiredSlotPosition => AreaAssignment?.SlotPosition;

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

    // RIoT's own RouteCost is deliberately not carried here. The gate writes it straight into the
    // audit beside the graph's number, and nothing downstream in the round reads it -- unlike
    // GraphTraversalCostMm, which the ranker needs. A field nobody reads is a field that drifts.
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
    long CatalogRevision = 0,
    long? AreaAssignmentVersion = null,
    string? RequiredSlotPosition = null);

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
