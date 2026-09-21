using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Fleet;

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
/// <param name="FixedStations">
/// This round's fixed stations, answered per task type. Read once for the same reason as everything
/// else here: every candidate of the round is judged against the same bindings.
/// </param>
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
/// <param name="AdmissionPolicyDrifted">
/// Whether the store refused, this round, to bind the live Map's area-named stations to the
/// configured admission policy version. The bound policy stays in force for journeys under way;
/// <see cref="Criteria.AdmissionPolicyDriftCriterion"/> refuses new work on it. False by default, so
/// a round built outside the engine is the ordinary case.
/// </param>
public sealed record DispatchRoundFacts(
    DemandCatalogSnapshot Catalog,
    RiotMapStationCatalogSnapshot Map,
    IFixedTaskStationView FixedStations,
    IReadOnlySet<string> AcceptedDemandIds,
    DateTimeOffset Now,
    VehicleDispatchPolicy Policy,
    AreaAssignmentTableVersion? AreaAssignments = null,
    bool AdmissionPolicyDrifted = false)
{
    /// <summary>
    /// 每区派车参数的当前版本（REQ-0198、REQ-0203），一版都没有时为空——那时每个分区都未配置，途中追加一律禁止。
    /// </summary>
    /// <remarks>
    /// 与目录、策略、区域分配表同理读一次：一轮里每个候选都按同一版判，而每次追加决策记下的也正是这个版本号
    /// （批次7-06，control-server#211）。
    /// </remarks>
    public DispatchZoneParameterTableVersion? ZoneParameters { get; init; }

    /// <summary>
    /// The demands a vehicle claimed this round and intake then refused outright (control-server#242): they sit
    /// in <see cref="AcceptedDemandIds"/>, because the refusal leaves them bound to that attempt, but this server
    /// never accepted them and there is no <c>AcceptedDemands</c> row behind them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is the subtraction that separates the two meanings that set carries.</b> "Stop being a candidate for
    /// the vehicles behind in this round" is one; "count as accepted when the round ends" is the other, and only
    /// the first one holds for a demand intake refused. <see cref="StructuralDispatchBlockSink"/> reads this to
    /// keep from clearing a structural block on a demand nothing took — cleared, the same alarm is raised again as
    /// new the next round, a 2115/2114 pair per round for as long as the refusal repeats.
    /// </para>
    /// <para>
    /// <b><see cref="DemandIntakeOutcome.CandidateGone"/> is deliberately not in here.</b> That demand left the
    /// catalog, so a block standing against it is no longer about anything and clearing it is right. Keeping the
    /// claim is what clears it, and it is worth knowing that the catalog-absence rule in
    /// <see cref="StructuralDispatchBlockSink"/> does not do that job in this round: the sink judges against
    /// <see cref="Catalog"/>, read before the round started, and intake only found the demand gone on its re-read
    /// half way through. That rule clears the block a round later, so subtracting this outcome too would not lose
    /// the clearing — it would delay it, while an operator looks at an alarm about a demand that is already gone.
    /// The other three refusals say nothing about the block either way.
    /// </para>
    /// <para>
    /// A grow-while-the-round-runs set like <see cref="AcceptedDemandIds"/>, and sound for the same reason: the
    /// round's segments are strictly serial, so no segment reads it while another writes.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string> ClaimsIntakeRefused { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

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
/// <param name="Plan">
/// 这辆车此刻的计划，只有在途车有；空闲车为空（批次7-06，control-server#211）。它是两条资格链的分野本身：
/// 有计划的车问「这条需求插得进你的计划吗」，没有的车问「你现在能不能接一趟新的」。
/// </param>
public sealed record DispatchVehicleFacts(
    string VehicleKey,
    string AgvId,
    OnboardDispatchFacts? Onboard,
    RiotVehicleObservation Vehicle,
    DateTimeOffset ObservedAt,
    VehicleSlotPositions? SlotPositions = null,
    EnRouteVehiclePlan? Plan = null);

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
/// <remarks>
/// Built by <see cref="JourneyPlanBuilder.ResolveRoute"/>, which decides which end is the pickup and
/// which the dropoff; the plan takes both ends from here and nowhere else.
/// </remarks>
/// <param name="FixedStation">
/// The fixed station this route was built from, with the versions it was resolved under -- what an
/// acceptance freezes alongside the route.
/// </param>
public sealed record ResolvedJourneyRoute(
    string DispatchZone,
    string RouteEvidenceId,
    string PickupStationId,
    int PickupStationRiotId,
    string DropoffStationId,
    int DropoffStationRiotId,
    FixedTaskStationResolution FixedStation);

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

    /// <summary>
    /// This candidate's task type resolved against the round's fixed stations, set by the
    /// station-resolution criterion whether it resolved or was refused.
    /// </summary>
    public FixedTaskStationResolution? FixedStation { get; set; }

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
    /// 这条需求插进这辆在途车计划里的位置与边际成本，由 <see cref="Criteria.EnRouteAppendCriterion"/> 定；
    /// 空闲车与被拒的追加都是空（批次7-06，control-server#211）。
    /// </summary>
    public EnRouteAppendPlacement? AppendPlacement { get; set; }

    /// <summary>
    /// 这次追加决策所依据的每区参数版本，写进 <c>JourneyDemands.DispatchZoneParameterVersion</c>；
    /// 初始派车（不经追加门禁）为空。
    /// </summary>
    public long? DispatchZoneParameterVersion { get; set; }

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
/// 一辆车对<b>一条任务</b>给出的出价：它清了那条链，连同链为它算出的东西（批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 派车轮翻成任务优先之后（REQ-0200：先定任务、再只为该任务选车），要比较的对象从「这辆车的几条候选」变成了
/// 「这条任务的几辆车」。<see cref="EligibleDispatchCandidate"/> 描述的是前者，没有车；这个类型描述后者。
/// </para>
/// <para>
/// <see cref="MarginalCostMm"/> 是「加入之后相对原计划增加的行程代价」（REQ-0206，计划锚）：在途车是插入位带来的
/// 增量，空闲车是从当前位置走完这一趟的全程——它的原计划是空的，所以增量就是全程。两者因此可以直接比大小，
/// 空闲或在途的身份本身不加减分。
/// </para>
/// </remarks>
/// <param name="LastDispatchedAt">
/// 这辆车上一次成功接单的时刻，从既有旅程记录推出（<c>JourneyRuntimes</c> 按车取 <c>CreatedAt</c> 的最大值），
/// 从未接过单为空。轮次一次查齐，不挂在名册上——名册说的是这辆车是谁，这是轮次此刻读到的事实。
/// </param>
public sealed record EligibleVehicleOffer(
    FleetVehicle Vehicle,
    DispatchVehicleFacts Facts,
    EligibleDispatchCandidate Candidate,
    long? MarginalCostMm,
    EnRouteAppendPlacement? Placement,
    long? DispatchZoneParameterVersion,
    DateTimeOffset? LastDispatchedAt = null);

/// <summary>
/// 这一轮先派哪一条任务（REQ-0200；批次7-06，control-server#211 把它从「这辆车接哪一条」翻成了这个）。
/// </summary>
/// <remarks>
/// 与资格链分开，因为两者回答不同的问题：链决定一条需求能不能被执行，这个决定先做哪一条。它的唯一实现是
/// <see cref="LayeredDispatchCandidateRanker"/>，层在 <see cref="DispatchCandidateOrdering"/> 里列着；
/// 加一条排序规则是加一层，这个签名不跟着变。
/// </remarks>
public interface IDispatchCandidateRanker
{
    /// <summary>把这一轮的任务排出先后；每一条都要依次问一遍有没有车接得了。</summary>
    IReadOnlyList<DispatchTask> Order(IReadOnlyList<DispatchTask> tasks);
}
