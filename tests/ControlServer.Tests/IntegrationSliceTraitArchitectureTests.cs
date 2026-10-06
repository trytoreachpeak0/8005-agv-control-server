using System.Reflection;
using System.Text.Json;
using Xunit.v3;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the <c>IntegrationSlice</c> trait: every value is one of the sixteen slice
/// ids protocol v2 froze, running the sixteen filters leaves no traited test behind, and every test
/// that carries no slice at all is accounted for by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a partition, and saying so is the point.</b> Ticket 14 asked for "the union of the
/// sixteen slices equals the whole set and pairwise intersections are empty". Measured against the
/// suite on 2026-09-08, both halves are false and neither can be made true without destroying
/// something real: 40 of the 441 test methods carry two to four slice traits, because a test that
/// proves a durable replay of a load correction is evidence for the reliable-delivery family and
/// the pickup family at once; and 300 carry no slice trait, because section 7.5 of the
/// full-product scope specification names ten classes of work that deliberately have none --
/// multi-vehicle execution, the route-graph engine, the RIoT command surface, the create gate, and
/// the cross-cutting architecture guards among them. Forcing a partition would mean deleting
/// traits that say something and inventing traits that do not.
/// </para>
/// <para>
/// <b>What is checkable is closure on the slice-bearing surface</b>, and that is what this class
/// asserts: no value outside the frozen sixteen (so no filter misses a test and no <c>W2G-IS-NN</c>
/// survives the v2 rename), no vector claimed by a test whose own slices do not contain it (so a
/// slice filter runs everything that claims to prove that slice's vectors), and an explicit ledger
/// of what sits outside the family, which fails when the set changes rather than drifting.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster</b>, for the reason
/// <see cref="ProtocolVectorTestBindingArchitectureTests"/> gives.
/// </para>
/// </remarks>
public sealed class IntegrationSliceTraitArchitectureTests
{
    private const string SliceTrait = "IntegrationSlice";

    private const string VectorTrait = "ProtocolVector";

    /// <summary>
    /// Test classes in which no test carries a slice trait, each with why it sits outside the
    /// family.
    /// </summary>
    /// <remarks>
    /// The reasons are section 7.5's, not new ones: single-ended work has no cross-end slice to
    /// belong to, and a cross-cutting guard hung off a slice goes dark whenever that slice is
    /// deferred. An entry here is a claim that can be argued with, which is the difference between
    /// a ledger and a count.
    /// </remarks>
    private static readonly SortedDictionary<string, string> ClassesOutsideTheSliceFamily =
        new(StringComparer.Ordinal)
        {
            ["AgvRestorationTests"] = "batch 3 FP-C5 archive-and-restore lifecycle; server-internal, no wire message",
            ["AreaAssignmentDispatchTests"] = "batch 4 FP-C15 REQ-0191/REQ-0350 area assignment whitelist, route dispatch zone, startup check and acceptance freeze (control-server#72); server-internal, no wire message",
            ["AreaAssignmentImportTests"] = "batch 4 FP-C15 REQ-0350 FieldOps whole-table import of the area assignment table; a controlled operations entry point, no wire message",
            ["AreaAssignmentStoreTests"] = "batch 4 FP-C15 REQ-0350 area assignment table versions; the side is decided on the server only (spec 5.1 #10), no wire message",
            ["AreaEndAdmissionRevokedSinceMigrationTests"] = "cross-cutting migration guard for control-server#228's one-column migration and its back-fill of stops already held at their AREA machine; hanging it off a slice would defer the guard with the slice",
            ["AuditDatabaseImmutabilityTests"] = "cross-cutting migration guard for control-server#199's audit immutability triggers: the write-once rule enforced at the database layer, where no slice's code path can reach past it; hanging it off a slice would defer the guard with the slice",
            ["AuditExportTests"] = "batch 3 FP-C14 REQ-0271 audit query and export; server-internal, no wire message",
            ["Batch2CapabilityStoresTests"] = "batch 2 track B persistence foundation; server-internal, no wire message",
            ["Batch3MigrationDisciplineTests"] = "cross-cutting migration and startup guard; hanging it off a slice would defer the guard with the slice",
            ["Batch4MigrationDisciplineTests"] = "cross-cutting migration guard for batch 4's one migration; hanging it off a slice would defer the guard with the slice",
            ["Batch5MigrationDisciplineTests"] = "cross-cutting migration guard for batch 5's one migration (control-server#80); hanging it off a slice would defer the guard with the slice",
            ["Batch6MigrationDisciplineTests"] = "cross-cutting migration guard for batch 6's one migration (control-server#159); hanging it off a slice would defer the guard with the slice",
            ["Batch7CargoHoldingDashboardTests"] = "batch 7-12 REQ-0354/REQ-0355/REQ-0201/REQ-0202/REQ-0203/REQ-0210 the dashboard's cargo holding card, backlog tiers and multi-demand journey lists (control-server#217); read-only /api/dashboard/ over the server's own tables, nothing goes on the wire (spec 5.1 #10)",
            ["Batch7DemandJourneyLookupTests"] = "batch 7 pre-refactor control-server#207: \"which journey carries this demand\" is read from the demand memberships, so a demand other than the anchor finds its journey; server-internal persistence, no wire message changes",
            ["Batch7DemandTerminationTests"] = "batch 7 pre-refactor control-server#207: ending a demand and closing its journey are two steps, the lease goes with the last open demand, and the orphan check reads the demand memberships; server-internal, no wire message changes",
            ["Batch7JourneyAcceptanceTests"] = "batch 7 schema ticket control-server#206: the acceptance transaction writes stops, demand membership, purpose claim and revision counter in its one save, with every id and revision the peer sees unchanged; server-internal persistence, no wire message changes",
            ["Batch7MultiDemandCancellationTests"] = "batch 7 control-server#211: the cancellation before a sublot is judged against the demand the operator named rather than the journey, so a stop carrying several demands can cancel one while another loads; the wire messages are FP-IS-02's, already covered there, and this asserts which demand the authorisation is about",
            ["Batch7MultiDemandAdvanceTests"] = "batch 7 control-server#211: which demand a stop is loading right now, and a stop running its demands one after another; the wire messages it sends are the ones FP-IS-08 already covers, and this asserts the advance's own state machine rather than a vector, so it sits outside the slice family",
            ["Batch7LoadingPhaseDispatchTests"] = "batch 7-07 REQ-0354 the dispatch side of the loading phase -- the criterion that stops appends once loading closed, and the per-round board of sides full of the vehicle's own cargo (control-server#212); server-internal, no wire message",
            ["Batch7MigrationDisciplineTests"] = "cross-cutting migration guard for batch 7's one migration (control-server#206), including its back-fill of journeys in flight; hanging it off a slice would defer the guard with the slice",
            ["Batch7PersistencePortTests"] = "batch 7 schema ticket control-server#206: the five persistence ports later batch 7 tickets consume; server-internal, no wire message",
            ["Batch8FixedStationSingleOccupancyTests"] = "batch 8 ticket control-server#391 (REQ-0204): a public station is reserved, occupied and released on departure evidence through the station exclusivity primitive, refuses other vehicles' new tasks and gives a present vehicle a selection layer; server-internal dispatch and persistence, no wire message",
            ["Batch8MigrationDisciplineTests"] = "cross-cutting migration guard for batch 8's schema migration (control-server#386), including its back-fill of claims in flight and the column-order-preserving rebuild of VehiclePurposeClaims; hanging it off a slice would defer the guard with the slice",
            ["Batch8PersistencePortTests"] = "batch 8 schema ticket control-server#386: the purpose ledger, station exclusivity and waiting point registry ports later batch 8 tickets consume; server-internal persistence with no runtime reader yet, no wire message",
            ["Batch8OccupancyRetirementMigrationTests"] = "cross-cutting migration guard for batch 8's second migration (control-server#387): the check before dropping the lease table and the order occupancy columns, the claim history backfill, and the guard that nothing reads the retired names any more; hanging it off a slice would defer the guard with the slice",
            ["Batch8StationExclusivityManualReleaseTests"] = "batch 8 follow-up control-server#419: a public station's or waiting point's exclusivity is released by hand with an operator, a reason and a site verification, on the departure sweep's delete condition so only one of the two takes effect; server-internal governance and persistence, no wire message",
            ["Batch8VehicleOccupancyTests"] = "batch 8 ticket control-server#387: the vehicle's one occupancy is its purpose claim, taken by the acceptance's save and released with its record in the journey closure's save; server-internal occupancy, no wire message",
            ["Batch9MigrationDisciplineTests"] = "cross-cutting migration guard for batch 9's one migration (control-server#399): the seventeen new tables, the column-order-preserving rebuild of the two station exclusivity tables with CHARGER, and the columns appended to OrderIntents and JourneyRuntimes; hanging it off a slice would defer the guard with the slice",
            ["Batch9PersistencePortTests"] = "batch 9 schema ticket control-server#399: the charger roster, charging policy, charging cycle, hold, clearance, manual charging hold and field confirmation ports later batch 9 tickets consume; server-internal persistence with no runtime reader yet, no wire message",
            ["Batch7Req0185EutecticExclusionTests"] = "batch 7-09 REQ-0185 the eutectic exclusion is carried by the area assignment table (control-server#214): three table-and-map combinations run as whole rounds; server-internal admission, no wire message",
            ["Batch7StarvationCalibrationReportTests"] = "batch 7-09 REQ-0203 the calibration evidence report and its read-only HTTP endpoint (control-server#214); server-side query over the server's own tables, no wire message",
            ["Batch7StarvationEscalationTests"] = "batch 7-09 REQ-0210/REQ-0203 the anti-starvation escalation raised once per demand and persisted on the backlog row (control-server#214); server log and backlog only, never blockingFacts, no wire message",
            ["Batch7StationYieldTriggerTests"] = "batch 7-08 REQ-0355 who a commitment makes yield and that the trigger commits with the acceptance or append that caused it (control-server#213); server-side rows only, no wire message -- the snapshot the yield leads to is judged in Batch7StationYieldTests under FP-IS-08",
            ["Batch7StopDrivenAdvanceTests"] = "batch 7 pre-refactor control-server#208: what the word-for-word wire comparison cannot see -- a crash part way through a goto-case chain, the revision streams staying monotonic across two journeys on one vehicle, and which demand the pre-departure check names; the claim is that no wire message changes, so it sits outside the slice family for the reason Batch7StopDrivenAdvanceWireParityTests does",
            ["Batch7StopDrivenAdvanceWireParityTests"] = "batch 7 pre-refactor control-server#208: the advance is driven by the current stop, and the lines a single-demand journey sends are pinned word for word against what it sent before; it asserts that no wire message changes, so hanging it off one slice would file evidence of cross-slice sameness under a single slice",
            ["Batch7TaskPriorityOrderingTests"] = "batch 7-09 REQ-0201/REQ-0202/REQ-0203 the task-side priority band, waiting age and timeout tier of the dispatch ranking (control-server#214); server-internal ordering, no wire message",
            ["Batch7TransportDemandKeyStarvationTests"] = "batch 7-05 x 7-09 seam (control-server#210 review): a demand whose key is suppressed or already accepted under another DemandId is neither escalated nor put in the timeout layer; server-side ranking and log only, no wire message",
            ["Batch7TransportDemandKeyAdmissionTests"] = "batch 7-05 REQ-0155/REQ-0156/REQ-0211 the two criteria that keep a suppressed key, and a key another DemandId was accepted under, out of the candidates (control-server#210), plus their backlog registration and dashboard description; server and dashboard only, the reasons are never sent in blockingFacts",
            ["Batch7TransportDemandSuppressionTests"] = "batch 7-05 REQ-0155/REQ-0156 a local cancellation suppresses the business key in the same save as the ending, first writer wins (control-server#210); server-internal persistence, no wire message changes",
            ["Batch7VehicleOccupancyReleaseTests"] = "batch 7 schema ticket control-server#206: the purpose claim is released wherever a journey ends (with its record since control-server#387 retired the lease), so the vehicle takes its next journey after every ending; server-internal occupancy, no wire message",
            ["BatteryStateProjectionTests"] = "batch 9-05 REQ-0281 the VehicleBusinessStateSnapshot batteryState projection frozen with its revision, and a journey that crosses the mandatory charge line finishing (control-server#403); the conformance vectors carry no battery state",
            ["BatteryThresholdEligibilityTests"] = "batch 9-05 REQ-0208/REQ-0281/REQ-0290 battery eligibility boundaries, the mandatory charge judgement and the batteryState mapping table (control-server#403); server-internal, no wire message",
            ["BlockedJourneyDashboardTests"] = "batch 5 control-server#80 blocked-journey start time and dashboard card; server and dashboard only, the dashboard is disjoint from the protocol and nothing is pushed (REQ-0270)",
            ["ChargerRosterImportTests"] = "batch 9 REQ-0171/REQ-0288 governed whole-file import of the charger roster: emptying and re-enabling as versions, audits, work in progress listed and left alone (control-server#400); a controlled operations entry point, no wire message",
            ["ChargingAllocationTests"] = "batch 9-06 REQ-0170/0171/0172/0173/0290 charging allocation: who queues, the candidate chain, the battery-ordered queue, the atomic cycle-purpose-reservation commitment and the degradation to the server's manual charging hold when the roster is empty (control-server#404); the vector CV-AUTOMATIC-CHARGING-CYCLE is claimed by control-server#405, so it sits outside the slice family until then",
            ["ChargingDashboardRealRunTests"] = "batch 9-10 the charging dashboard read over states the engine really reached in the fleet fixture: empty roster, confirmed failure to charge, full vehicle left on its charger (control-server#408): read-only /api/dashboard/, nothing goes on the wire",
            ["ChargingDashboardTests"] = "batch 9-10 the dashboard's per-vehicle charging, charger, charging hold and charging alarm cards (control-server#408): read-only /api/dashboard/ over the server's own tables, nothing goes on the wire (spec 5.7, REQ-0268)",
            ["ChargingExecutionTests"] = "batch 9-06 REQ-0283/REQ-0173/REQ-0175 a committed charging journey up to en route: the CHARGER leg and CHARGING state it sends, the departure gate, create and reconciliation, an order cancelled in RIoT never rebuilt, HANG and FAILED on the way (control-server#404); the charging cycle's vector is claimed by control-server#405, not here",
            ["ChargingFieldOpsTests"] = "batch 9 REQ-0171/REQ-0282 FieldOps charger roster and charging policy verbs' process entry: shipped field files, JSON output, exit codes, read-only open (control-server#400); a controlled operations entry point, no wire message",
            ["ChargingPolicyGovernanceTests"] = "batch 9 REQ-0281/REQ-0282 charging policy import, approval, activation and per-cycle freeze, and the per-vehicle commissioning decision (control-server#400); server-internal, no wire message",
            ["CatalogBindingChangeClassifierTests"] = "batch 6 FP-C9a REQ-0341/REQ-0342 catalog change classification by stable station identity (control-server#162); server-internal, no wire message",
            ["CatalogBindingHoldEngineHookTests"] = "batch 6 FP-C9a REQ-0342 the engine runs catalog change convergence after each complete catalog confirmation (control-server#162); server-internal, no wire message",
            ["CatalogBindingHoldConvergenceTests"] = "batch 6 FP-C9a REQ-0342/REQ-0345 catalog change holds confined to the affected task type (control-server#162); server-internal, no wire message",
            ["ChargingOrderShapePathTests"] = "control-server#401 every path that builds an order intent carries its order shape (LegIntent authorisation, own-order rebuild); server-internal, no wire message",
            ["ChargingPolicyStartupCheckTests"] = "batch 9-05 REQ-0281 the startup refusal when a charging policy version in effect breaks the threshold relation or the rescue line (control-server#403); server-internal, no wire message",
            ["ControlServerSqliteConnectionTests"] = "cross-cutting guard on the one place the server's SQLite connection string is built; the busy timeout two processes share is a policy, not a slice",
            ["CreateGateTests"] = "7.5 #9, FP-C13 create gate and catalog availability; server-internal gate",
            ["DashboardActionTests"] = "batch 6 FP-C9a REQ-0340 dashboard write action convention, same-origin confirmation page (control-server#162); the dashboard is disjoint from the protocol",
            ["DashboardSkeletonTests"] = "batch 3 FP-C8 dashboard skeleton; the dashboard is disjoint from the protocol by construction and reads only /api/dashboard/",
            ["DemandAreaAssignmentFreezeTests"] = "batch 4 FP-C15 REQ-0350 demand freeze of the area assignment version; server-internal, no wire message",
            ["DemandTaskTypeStationFreezeTests"] = "batch 6 FP-C9b REQ-0344 demand freeze of the task type rule and binding set versions (control-server#159); server-internal, no wire message",
            ["DispatchAdmissionChainDerivationTests"] = "batch 7-06 REQ-0205 the in-transit admission chain is derived from the idle one (control-server#211); assembly only, server-internal, no wire message",
            ["DispatchBacklogDashboardTests"] = "batch 4 FP-C15 REQ-0210 dispatch backlog and structural dispatch block dashboard card (control-server#70); reads only /api/dashboard/, reasons never go on the wire (spec 5.1 #10)",
            ["DispatchCandidateOrderingTests"] = "batch 7-04 behaviour-preserving restructuring guard (control-server#209): the layered candidate ordering against the ranker it replaced; server-internal, no wire message",
            ["DispatchVehicleOrderingTests"] = "batch 7-06 REQ-0206/REQ-0207 vehicle-side ordering layers and their fixed order (control-server#211); server-internal, no wire message",
            ["DispatchChainSeamTests"] = "batch 4 FP-C15 dispatch chain seams (control-server#69): reason code names, the area assignment lookup and plan replay; server-internal, no wire message",
            ["DispatchGatePremiseArchitectureTests"] = "cross-cutting architecture guard (control-server#472): the server facts the v2 dispatch gate script's refusal rests on -- one RIoT create call site, both create paths gated first, the journey stage set, the active-journey query, the never-sent definition; hanging it off a slice would defer the guard with the slice",
            ["DispatchZoneParameterFieldOpsTests"] = "batch 7 REQ-0198/REQ-0203 FieldOps per-zone dispatch parameter verbs' process entry: arguments, JSON output, exit codes, read-only open (control-server#216); a controlled operations entry point, no wire message",
            ["DispatchZoneParameterImportTests"] = "batch 7 REQ-0198/REQ-0203 whole-table import of the per-zone dispatch parameters, versions, snapshot and audit (control-server#216); a controlled operations entry point, no wire message",
            ["EmergencyReleaseVersusOwnOrderRebuildTests"] = "control-server#349 REQ-0356's release on confirmation followed, in the real order, by an own order cancelled in RIoT and #299's clearance, asserting whether and when REQ-0360's rebuild creates a RIoT order and which demand it carries; single-ended server-to-RIoT, no wire message",
            ["EmergencyStopReleaseEndpointsTests"] = "REQ-0356 release-on-confirmation HTTP entry point (control-server#63); single-ended server-to-RIoT, no wire message",
            ["EmergencyStopSupervisorTests"] = "7.5 #8, FP-C11 fault isolation; single-ended server-to-RIoT",
            ["ExperimentalRiotCreateGateTests"] = "RIoT create experiment; single-ended server-to-RIoT",
            ["ExperimentalRiotCreateMigrationTests"] = "RIoT create experiment migration; server-internal",
            ["FakeOnboardFailedHandshakeTests"] = "L2 synthetic peer's teardown after a handshake that failed part-way (control-server#277); a test double's reconnect behaviour, not the product's wire surface",
            ["FakeOnboardRequestAnswerTests"] = "L2 synthetic peer's per-request answer cache (control-server#75 review); a test double's replay behaviour, not the product's wire surface",
            ["FakeOnboardSlotStateSeedTests"] = "L2 synthetic peer's handshake slot state seed (control-server#71); a test double's startup configuration, not the product's wire surface",
            ["FailedOrderBeforeConfirmationTests"] = "control-server#367 an ordinary leg's order RIoT reports FAILED before its creation was confirmed reaches the fault model, the hold and the emergency stop on either side of the readiness gate, its fault is cleared through the controlled entry and the order rebuilt on the same vehicle (REQ-0362); single-ended server-to-RIoT, nothing is sent to the onboard peer behind the gate",
            ["FailedOrderBehindSessionGateTests"] = "control-server#358 an in-transit order RIoT reports FAILED while the onboard session is not ready reaches the fault model, the hold and the emergency stop in the same round, and the stop's confirmation and REQ-0248 re-trigger go on behind the readiness gate; single-ended server-to-RIoT, nothing is sent to the onboard peer",
            ["FixedTaskStationResolverTests"] = "batch 6-01 behaviour-preserving restructuring guard (control-server#158): the per-round fixed station resolver seam and the named route endpoints; server-internal, no wire message",
            ["ForeignRunningOrderTests"] = "control-server#330 an order RIoT shows running on a vehicle of this server's that this server did not create is cancelled once and the vehicle held until it is read back ended (REQ-0148, REQ-0164 as revised in v1.5.0); single-ended server-to-RIoT, no wire message",
            ["GateHeldOnboardSendTests"] = "cross-cutting guard for control-server#334: nothing done while JourneyMutationGate is held may take unbounded time -- a write to a vehicle that stops reading ends within the write timeout, and the fault recovery paths make no RIoT call and no onboard send under the gate; it constrains every slice's senders at once, so hanging it off one slice would defer it with that slice",
            ["GovernanceSnapshotAndAuditTests"] = "batch 3 FP-C7/FP-C5 shared snapshot and audit mechanism; server-internal, no wire message",
            ["FaultedCargoBindingLifecycleTests"] = "control-server#376 a faulted vehicle's cargo binding is released when the cargo is shown to have left the vehicle, a new fault takes only its own journey's cargo, and no own-order rebuild waits without a way out; single-ended server-to-RIoT plus the existing exception recovery session messages, no new wire message",
            ["IdleReturnCommitmentTests"] = "batch 8-18 REQ-0290/0291/0292 idle return qualification, waiting point selection and the atomic purpose-and-reservation commitment (control-server#389); server-internal, nothing is sent to RIoT or the onboard peer",
            ["InTransitDoorEmergencyReleaseTests"] = "control-server#335 REQ-0167 a latch raised for a driving vehicle's doors is released automatically once they are fresh, known and locked, only past this server's own confirmed hold of that fault generation, the held order waits for a person's continue; single-ended server-to-RIoT, nothing is sent to the onboard peer",
            ["InTransitDoorLockFaultTests"] = "control-server#335 REQ-0246 a door not proven locked while the vehicle drives -- explicitly unlocked, unknown, unobserved or stale -- reaches the fault model, the hold on this order and the emergency stop, never a cancel, on either side of the readiness gate; a normal drive on the own order and a reconnect within the silence window raise nothing; single-ended server-to-RIoT, nothing is sent to the onboard peer",
            ["InTransitOrderStallTests"] = "control-server#316 an in-transit order RIoT reports HANG, SUSPENDED, CANCELLED or DELETED is named on the journey, and nothing is commanded; server-internal, no wire message",
            ["InTransitVehicleFactsTests"] = "batch 7-06 REQ-0205 the in-transit vehicle facts criterion against a vehicle actually under way, beside the idle chain's verdict on the same facts (control-server#211); server-internal, no wire message",
            ["IntegrationSliceTraitArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["JourneyClosureSingleExitArchitectureTests"] = "cross-cutting architecture guard (control-server#323): a journey becomes Completed in one place, the one that stages the closure snapshots; hanging it off a slice would defer the guard with the slice",
            ["JourneyPlanCharacterizationTests"] = "batch 6-01 behaviour-preserving restructuring guard (control-server#158): the route evidence id and one acceptance's plan pinned byte for byte; characterization, it proves no slice's wire behaviour",
            ["JourneyRuntimeVersionMigrationTests"] = "cross-cutting migration guard for control-server#357's one-column migration of the journey row's concurrency token, and that the token works on a migrated database; hanging it off a slice would defer the guard with the slice",
            ["JourneyStopEntryRequestIdTests"] = "batch 7-06 REQ-0205 which entry request the current stop settles, and what either getter does when the id is missing (control-server#211); server-internal, no wire message",
            ["LoadingPhaseMachineTests"] = "batch 7-07 REQ-0354 the loading phase decision table and when it announces (control-server#212); a pure function over server-side facts, no wire message -- the snapshots it leads to are judged in the FP-IS-08 classes",
            ["FakeRiotChargingTests"] = "control-server#402 batch 9-04 the fake RIoT's charging model -- act missions, enter/exit expansion, battery on an injected clock, departure act and the charging fault controls -- for batch 9's contract tests and L2; a test double's behaviour, not the product's wire surface",
            ["FakeRiotMapNameTests"] ="control-server#186 the fake RIoT's Map list and its rename and Map-list-only fault controls for L2; a test double's control plane, not the product's wire surface",
            ["HttpRiotChargingOrderGatewayTests"] = "control-server#401 batch 9-03 the RIoT adapter creates the charging order shape, refuses an unknown shape, reconciles orders RIoT expanded through an enter/exit point and reads an order's raw mission facts; single-ended server-to-RIoT, no wire message",
            ["HttpRiotMapNameCatalogTests"] = "control-server#186 REQ-0341 the RIoT adapter reads the Map list without mapJson and fails closed rather than answering an empty list; single-ended server-to-RIoT, no wire message",
            ["ManualChargingHoldReturnToServiceTests"] = "batch 9-06 the server-held manual charging hold is lifted by an accepted ManualChargingReturnToService decision in its own save, kept on a rejection and not lifted twice on a replay (control-server#404); the store's decision, below the wire -- the wire half is OnboardMessageProcessorTests under FP-IS-07",
            ["MapNameBaselineAcceptanceTests"] = "control-server#186 REQ-0340/REQ-0341 FieldOps accepts a Map's new name as its baseline with an audit, and hold release is refused while a rename is unaccepted; FieldOps only (spec 5.7), no wire message",
            ["MapNameBaselinesMigrationTests"] = "cross-cutting migration guard for control-server#186's one-table migration: it follows the previous migration, creates only MapNameBaselines and touches no other table; hanging it off a slice would defer the guard with the slice",
            ["MapRenameEngineTests"] = "control-server#186 REQ-0341 the engine reads Map names each round and a rename under the same mapId stops dispatch of every bound task type until accepted; server-to-RIoT and dispatch only, no wire message",
            ["MapRenameHoldConvergenceTests"] = "control-server#186 REQ-0341/REQ-0342 Map name baseline and Map level rename holds, confined to the renamed Map; server-internal, no wire message",
            ["MapStationResolverTests"] = "7.5 #6, RouteGraphSnapshot engine; single-ended server-to-RIoT",
            ["MultiVehicleExecutionTests"] = "7.5 #1, FP-C2 B2 multi-vehicle; the conformance vector format has no vehicle dimension",
            ["OnboardHandshakeReadinessArchitectureTests"] = "cross-cutting architecture guard (control-server#340): an answer takes a SessionReadiness line after it only through the one function that refuses to inside the reconnect handshake; hanging it off a slice would defer the guard with the slice",
            ["OnboardOutboundFunnelArchitectureTests"] = "cross-cutting architecture guard (control-server#259): every server-originated line reaches a socket through OnboardPeer's handshake gate; hanging it off a slice would defer the guard with the slice",
            ["OnboardVehicleSafetyEndpointsTests"] = "server-side safety projection endpoint; not a protocol wire message",
            ["OwnOrderRebuildCargoEvidenceRequestTests"] = "control-server#318 REQ-0362 after a fault with cargo on board is cleared the Host asks the vehicle for a SafetyStateSnapshot (the #142 request, VERSION_GAP), once per session and once more on becoming ready, never inside the handshake; the request line is the existing message, no new wire message",
            ["OwnOrderRebuildCargoProofTests"] = "control-server#366 CP-0007's REQ-0360 and REQ-0362: with cargo on board an own-order rebuild, cancelled or fault-cleared, is created only on the newest SafetyStateSnapshot received after the rebuild fell due and after the vehicle was last seen held, showing every loaded demand's slots in place; single-ended server-to-RIoT, the snapshot and its request are existing messages, no new wire message",
            ["OwnOrderRebuildTests"] = "control-server#318 an order of this server's that was cancelled in RIoT, or whose FAILED fault a person cleared, is rebuilt for the same vehicle and demand behind three guards; single-ended server-to-RIoT, no wire message",
            ["PackageCapacityStoreTests"] = "server-internal store; no wire message",
            ["StationExclusivityFieldOpsTests"] = "control-server#419 FieldOps release-station-exclusivity verb's process entry: against the database with the server stopped, through the Host entry with it running, JSON output, exit codes; a controlled operations entry point, no wire message",
            ["StationExclusivityReleaseEndpointsTests"] = "control-server#419 the station exclusivity release HTTP entry point (the fault recovery entry's credential and switch, every code listed); single-ended server, no wire message",
            ["StoppedRebuildExitTests"] = "control-server#345 a person's way out of a stopped own-order rebuild through #299's HTTP entry point, and the hand-over of a loaded stopped journey to the existing exception recovery session; the entry point is HTTP, and the session messages are the existing ones, no new wire message",
            ["ProtocolEnvelopeObserverArchitectureTests"] = "cross-cutting guard on the observation point every slice's outbound lines pass through (control-server#85); hanging it off one would defer the guard with that slice",
            ["ProtocolEnvelopeTests"] = "cross-cutting guard on the one place an outbound protocol line is built (control-server#85); every slice sends through it, so hanging it off one would defer the guard with that slice",
            ["ProtocolIdentityArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["ProtocolMessageSurfaceArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["ProtocolPayloadShapeArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["ProtocolVectorTestBindingArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["RiotAbsentAtObservationCreateExperimentOptionsTests"] = "options validation; no wire message",
            ["RiotCallAllowlistArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["RiotChargingOrderContractTests"] = "control-server#401 batch 9-03 the production gateway against the fake RIoT's charging model over HTTP: create, enter/exit expansion, HANG with the act result, the departure act; single-ended server-to-RIoT, no wire message",
            ["RiotCreateDispatchGateOptionsTests"] = "options validation; no wire message",
            ["RiotDispatchAuditTests"] = "7.5 #8, FP-C11 order command surface; single-ended server-to-RIoT",
            ["RiotOrderCommandSurfaceTests"] = "7.5 #8, FP-C11 order command surface; single-ended server-to-RIoT",
            ["RiotRequestSemanticDigestTests"] = "control-server#401 the create audit's request-semantic digest per order shape, single-move unchanged; server-internal audit, no wire message",
            ["RiotSdkPackageProvenanceTests"] = "vendored SDK package provenance; not a protocol fact",
            ["RiotSdkRegistrationTests"] = "SDK composition-root wiring; no wire message",
            ["RollbackAndImpactPreviewTests"] = "batch 3 rollback and impact preview; a rollback is a new activation of an existing frozen version, server-internal",
            ["RouteGraphDispatchTests"] = "7.5 #6, RouteGraphSnapshot engine; single-ended server-to-RIoT",
            ["RouteGraphEngineTests"] = "7.5 #6, RouteGraphSnapshot engine; single-ended server-to-RIoT",
            ["SchemaConformanceToolTests"] = "cross-cutting guard on the outbound gate's own validator (control-server#85), driven as its own process over the vendored contract; the gate stands behind every slice, so hanging it off one would defer it with that slice",
            ["SlotConfigurationAuthorityTests"] = "batch 3 FP-C7 slot configuration authority; two-layer versioning inside the server, no wire message",
            ["SlotConfigurationGateModeTests"] = "readiness gate mode validation; no wire message",
            ["SlotConfigurationReadinessGateTests"] = "batch 3 per-vehicle IO integrity gate; a server-side readiness predicate, no wire message",
            ["SlotConfigurationVersionLineArchitectureTests"] = "cross-cutting guard that every writer on the shared ActiveSlotConfiguration line allocates through SlotConfigurationVersionLine; hanging it off a slice would defer the guard with the slice",
            ["SlotConfigurationVersionLineCrossProcessTests"] = "batch 4 FP-C7/FP-C9b version allocation across two processes on one SQLite file; a server-internal write-ordering rule, no wire message",
            ["SlotConfigurationVersionLineTests"] = "batch 4 FP-C7/FP-C9b version allocation on the shared ActiveSlotConfiguration line; a server-internal write-ordering rule between two processes, no wire message",
            ["SlotGroupSelectionTests"] = "batch 4 FP-C15 REQ-0349/REQ-0351/REQ-0352 target slots chosen inside the demand's slot group (control-server#73); the side is decided on the server only (spec 5.1 #10), no wire message",
            ["StructuralDispatchBlockTests"] = "batch 4 REQ-0210/REQ-0352 structural dispatch block summarised across vehicles at the end of a round (control-server#74); server and dashboard only, never sent in blockingFacts (program#70 decision 2)",
            ["StructuralDispatchBlockStoreTests"] = "batch 4 REQ-0210 structural dispatch block storage; server and dashboard only, never sent in blockingFacts (program#70 decision 2)",
            ["TaskTypeBindingDashboardTests"] = "batch 6 FP-C9a REQ-0340/REQ-0268 task type binding and hold dashboard card (control-server#162); reads only /api/dashboard/, no wire message",
            ["TaskTypeHoldEndpointsTests"] = "batch 6 FP-C9a REQ-0340/REQ-0348 dashboard tightening entry on the server, this machine only (control-server#162); an HTTP operations entry, no wire message",
            ["TaskTypeStationActivationFollowUpTests"] = "batch 6 review follow-ups of control-server#191 (control-server#200): reconciliation audit pointerAfter, unattributed attempt back to ACTIVE, GovernanceStore audit writes that fail leave no tracked row, hold release ReleasedBy names its audit record; FieldOps and governance store only, no wire message",
            ["TaskTypeStationActivationTests"] = "batch 6 FP-C9b REQ-0337/REQ-0340/REQ-0345/REQ-0347/REQ-0348 FieldOps binding set activation, rollback, reconciliation, hold release and audit (control-server#161); FieldOps only (spec 5.7), no wire message",
            ["TaskTypeStationCatalogEvidenceTests"] = "batch 6 FP-C9b REQ-0338 FieldOps catalog fingerprint and freshness pinned to the server's (control-server#161); no wire message",
            ["TaskTypeStationConfigurationValidatorTests"] = "batch 6 FP-C9a REQ-0334/REQ-0338/REQ-0343 startup fail-closed validation of task type rules and bindings (control-server#159); binding reasons stay on the server (spec 5.3), no wire message",
            ["TaskTypeStationFieldOpsTests"] = "batch 6 FP-C9b FieldOps binding set verbs' process entry: arguments, JSON output, exit codes, read-only open (control-server#161); no wire message",
            ["TaskTypeStationHoldAndCatalogChangeStoreTests"] = "batch 6 FP-C9b REQ-0340/REQ-0341 task type holds and catalog change records (control-server#159); server-internal, no wire message",
            ["TaskTypeStationHoldWriteConcurrencyTests"] = "batch 6 FP-C9b REQ-0340 hold raise idempotency and conditional release (control-server#162); server-internal store, no wire message",
            ["TaskTypeStationStartupTests"] = "batch 6 FP-C9a REQ-0343 startup load of the controlled task type station preset (control-server#159); server startup only, no wire message",
            ["TaskTypeStationStoreTests"] = "batch 6 FP-C9b REQ-0337/REQ-0343 task type rule and per-map binding set versions (control-server#159); server-internal, no wire message",
            ["UnreadPreCreateReconciliationTests"] = "control-server#375 an order intent whose pre-create RIoT read answered nothing is created once RIoT answers it is absent, for the pickup leg, the gate leg and the same-vehicle rebuild, and a create already sent is never sent again; single-ended server-to-RIoT, no wire message",
            ["Batch7SublotTaskTypeConflictTests"] = "batch 7-06 REQ-0189 one Sublot hitting more than one task type in a snapshot refuses that Sublot's every candidate (control-server#211); a chain criterion over the round's own catalog, no database and no wire message",
            ["Batch7JourneyAppendPersistenceTests"] = "batch 7-06 REQ-0205 appending a demand to a journey under way is one all-or-nothing write (control-server#211): demand row, its stops, its membership and the resequencing of the stops it displaced; server-internal store, no wire message",
            ["Batch7JourneyAwareSlotLedgerTests"] = "batch 7-06 ADR-cross-0059 per-side slot ledger (control-server#211): a vehicle's free slots per side are the session baseline minus what its own journey holds; server-internal, no wire message",
            ["Batch7EnRouteAppendPlannerTests"] = "batch 7-06 REQ-0195/REQ-0196/REQ-0198 en-route append gates (control-server#211): a pure placement calculation, no database and no wire message; the slice's wire side is CV-MULTI-STOP-PLAN-NINE-LEGS in MultiStopPlanNineLegsVectorTests",
            ["Batch7DemandReleaseServiceTests"] = "batch 7-10 REQ-0328 release service (control-server#215): database rows and the RIoT order command audit; no wire message is sent by the service",
            ["Batch7DemandReleaseRulesTests"] = "batch 7-10 REQ-0328 release trigger and decision (control-server#215): pure functions, no database and no wire message",
            ["Batch7PlanRevisionStageTests"] = "batch 7-10 REQ-0197 plan revision staged after a demand ends (control-server#215): database rows only; the plan the vehicle sees is covered by Batch7ThreeStopJourneyTests",
            ["Batch7PlanRevisionTests"] = "batch 7-10 REQ-0197 plan revision (control-server#215): a pure stop removal and reorder calculation, no database and no wire message, plus one projection check that a removed stop yields no plan leg",
            ["Batch7RedispatchIdentityTests"] = "batch 7-10 REQ-0328 redispatch identity (control-server#215): id derivation and the acceptance store rows of a demand accepted a second time; no wire message is sent by either",
            ["VehicleFaultIsolationTests"] = "7.5 #8, FP-C11 fault isolation; single-ended server-to-RIoT",
            ["VehicleFaultRecoveryEndpointsTests"] = "control-server#299 the vehicle fault recovery HTTP entry point (REQ-0356's shape: off by default, shared bearer credential, named vehicle, every reason listed); single-ended server-to-RIoT, no wire message",
            ["VehicleFaultRecoveryTests"] = "control-server#299 a person clears a vehicle fault or continues a held order, with the journey disposed of in the same transaction (since control-server#318: kept on the vehicle and its order rebuilt); 7.5 #8, FP-C11 fault isolation, single-ended server-to-RIoT, no wire message",
            ["VehiclePurposeDashboardTests"] = "batch 8-21 the dashboard's vehicle purpose, waiting point, fixed task station and idle return cards (control-server#392): read-only /api/dashboard/ over the server's own tables, nothing goes on the wire (spec 5.1 #10)",
            ["VehicleSlotLedgerTests"] = "batch 7-04 slot ledger port (control-server#209): an idle vehicle's free slots per side equal the session baseline; server-internal, no wire message",
            ["VehicleSlotPositionReaderTests"] = "batch 4 FP-C15 server-authoritative SlotPosition per vehicle, never the onboard's report (program#70 decision 4); no wire message",
            ["WaitingJourneyBatteryWatchTests"] = "control-server#273 the waiting journey watch: a vehicle standing waiting for a person has its battery read from RIoT, recorded and logged past a threshold, and nothing is ever sent to it; server log and the server's own rows only, no wire message",
            ["WaitingJourneyDashboardTests"] = "control-server#273 the dashboard's waiting journey card: read-only /api/dashboard/ over the server's own tables, nothing goes on the wire (spec 5.1 #10)",
            ["WaitingJourneyWatchMigrationTests"] = "cross-cutting migration guard for control-server#273's one migration and its back-fill of when journeys already waiting began to wait; hanging it off a slice would defer the guard with the slice",
            ["WaitingPointFieldOpsTests"] = "batch 8 REQ-0289/REQ-0297 FieldOps waiting point verbs' process entry: arguments, JSON output, exit codes, read-only open (control-server#388); a controlled operations entry point, no wire message",
            ["WaitingPointImportTests"] = "batch 8 REQ-0289/REQ-0297 whole-map import of the waiting point registration, versions, snapshot and audit, and existing reservations kept (control-server#388); a controlled operations entry point, no wire message",
            ["WaitingPointStartupCheckTests"] = "batch 8 specification 5.4 the multi-vehicle startup refusal when waiting points cannot cover the fleet, and the eligibility predicate (control-server#388); 7.5 multi-vehicle execution, server-internal, no wire message",
        };

    /// <summary>
    /// Classes that are partly in the slice family, with how many of their tests are not, and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A count rather than a list of method names. The names would break on a rename, which teaches
    /// nothing; the count breaks when a test is <i>added</i> without a slice trait to a class where
    /// other tests have one, which is the case worth stopping. Each of these three splits is
    /// deliberate: the traited tests are the ones standing behind a wire message, the rest are the
    /// RIoT adapter's own behaviour or an options default.
    /// </para>
    /// <para>
    /// This is also the field that would have caught what the v2 retag found by hand:
    /// <c>OnboardJourneyPublisherTests</c> was in this list with one entry, and that one test was
    /// <c>SlotOperationRejectsInvalidCorrelationAndSlotOrderBeforePersistence</c> -- the negative
    /// counterpart of a test carrying four slice traits, sitting in no slice at all. It carries
    /// <c>FP-IS-02</c> and <c>FP-IS-04</c> now and the class is gone from here.
    /// </para>
    /// </remarks>
    private static readonly SortedDictionary<string, (int Count, string Reason)> SliceBearingClassesWithTestsOutside =
        new(StringComparer.Ordinal)
        {
            ["FakeRiotTests"] = (9, "the fake's own control plane and conflict behaviour; the traited tests are the shapes the production adapter parses"),
            ["HttpRiotMovementGatewayTests"] = (28, "RIoT adapter fail-closed and sanitisation behaviour (control-server#330 added the unfiltered order listing and the by-orderId state read; control-server#335 the listed order states; control-server#404 the non-final states the order reads ask for); the traited tests are the ones a wire message depends on"),
            ["JourneyRuntimeOptionsTests"] = (5, "option defaults, the retired minimumBatteryPercent key refusing to start (control-server#403), and the cargo holding timeout (control-server#206; read by the loading phase since control-server#212); the traited tests are the validations that fail a deployment closed"),
            ["OnboardAlarmProjectionTests"] = (1, "the dashboard self-registration convention #12 set up; the traited tests are the ones standing behind OnboardAlarmSnapshot")
        };

    private sealed record TestMethod(string ClassName, string MethodName, string[] Slices, string[] Vectors);

    /// <summary>
    /// Every slice trait names one of the sixteen ids the protocol froze.
    /// </summary>
    /// <remarks>
    /// This is the leg that catches a <c>W2G-IS-NN</c> the v2 rename missed, a typo, and a
    /// seventeenth slice somebody invented -- each of which is a test no slice filter would ever
    /// run. The ids come from the vendored index rather than a pattern, so the check is against
    /// what the protocol actually froze and not merely against a shape that looks right.
    /// </remarks>
    [Fact]
    public void EverySliceTraitNamesOneOfTheSixteenFrozenSlices()
    {
        HashSet<string> frozen = new(FrozenSliceIds(), StringComparer.Ordinal);
        Assert.Equal(16, frozen.Count);

        string[] offences =
        [
            .. TestMethods()
                .SelectMany(test => test.Slices.Select(slice => (test, slice)))
                .Where(pair => !frozen.Contains(pair.slice))
                .Select(pair => $"{pair.test.ClassName}.{pair.test.MethodName} carries {pair.slice}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "Tests carry an " + SliceTrait + " trait naming a slice the protocol did not freeze, so "
            + "no slice filter runs them: " + string.Join("; ", offences));
    }

    /// <summary>
    /// Running the sixteen slice filters leaves no traited test behind.
    /// </summary>
    /// <remarks>
    /// The direct reading of "the union of the sixteen loses nothing", stated over the tests rather
    /// than over the ids. It overlaps the check above by construction and reports differently: that
    /// one names the offending trait value, this one names the test that would silently stop being
    /// gate evidence.
    /// </remarks>
    [Fact]
    public void TheSixteenSliceFiltersRunEveryTestThatCarriesTheTrait()
    {
        HashSet<string> frozen = new(FrozenSliceIds(), StringComparer.Ordinal);
        TestMethod[] traited = [.. TestMethods().Where(test => test.Slices.Length > 0)];
        Assert.NotEmpty(traited);

        string[] reachedByNoFilter =
        [
            .. traited
                .Where(test => !test.Slices.Any(frozen.Contains))
                .Select(test => $"{test.ClassName}.{test.MethodName}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            reachedByNoFilter.Length == 0,
            "These tests carry an " + SliceTrait + " trait that no frozen slice filter selects: "
            + string.Join(", ", reachedByNoFilter));

        Assert.Equal(
            traited.Length,
            frozen.SelectMany(slice => traited.Where(test => test.Slices.Contains(slice, StringComparer.Ordinal)))
                .Distinct()
                .Count());
    }

    /// <summary>
    /// Every vector a test claims belongs to a slice that same test carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mechanised half of "review each test as you retag it". A test claiming
    /// <c>CV-EXCEPTION-RESUME</c> while filed under <c>FP-IS-05</c> and <c>FP-IS-06</c> is not
    /// wrong about what it proves -- it is missing <c>FP-IS-07</c>, the family the protocol files
    /// that vector under, and <c>CONTROL_SERVER_G2</c> for <c>FP-IS-07</c> would not have run it.
    /// The v2 retag found eight such tests and closed them by adding the slice, never by removing
    /// the vector.
    /// </para>
    /// <para>
    /// Deliberately one-directional. A test may carry slices beyond its vectors' -- that is how a
    /// single durable-replay test stands behind several families -- so the reverse would be false
    /// of a suite that is doing nothing wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryClaimedVectorIsCoveredByASliceItsOwnTestCarries()
    {
        Dictionary<string, string[]> vectorsBySlice = VectorsBySlice();

        string[] offences =
        [
            .. TestMethods()
                .SelectMany(test =>
                {
                    HashSet<string> covered = new(
                        test.Slices.SelectMany(slice => vectorsBySlice.GetValueOrDefault(slice, [])),
                        StringComparer.Ordinal);

                    return test.Vectors
                        .Where(vector => !covered.Contains(vector))
                        .Select(vector =>
                            $"{test.ClassName}.{test.MethodName} claims {vector} but carries only "
                            + (test.Slices.Length == 0 ? "no slice" : string.Join("/", test.Slices.Order(StringComparer.Ordinal))));
                })
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "These tests prove a frozen vector without being filed under any slice that vector "
            + "belongs to, so that slice's gate run would not execute them: "
            + string.Join("; ", offences));
    }

    /// <summary>
    /// Every test outside the slice family is accounted for, and the ledger holds nothing stale.
    /// </summary>
    [Fact]
    public void EveryTestOutsideTheSliceFamilyIsAccountedFor()
    {
        TestMethod[] tests = TestMethods();
        Assert.NotEmpty(tests);

        ILookup<string, TestMethod> byClass = tests.ToLookup(test => test.ClassName, StringComparer.Ordinal);

        Assert.Empty(ClassesOutsideTheSliceFamily.Keys
            .Intersect(SliceBearingClassesWithTestsOutside.Keys, StringComparer.Ordinal));

        string[] unaccounted = Unaccounted(tests);

        Assert.True(
            unaccounted.Length == 0,
            "These tests carry no " + SliceTrait + " trait and no ledger entry says why: "
            + string.Join(", ", unaccounted));

        string[] wronglyLedgered =
        [
            .. ClassesOutsideTheSliceFamily.Keys
                .Where(name => !byClass[name].Any() || byClass[name].Any(test => test.Slices.Length > 0))
                .Select(name => byClass[name].Any()
                    ? $"{name} now has tests inside the slice family"
                    : $"{name} has no tests at all")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            wronglyLedgered.Length == 0,
            "These classes are ledgered as wholly outside the slice family and are not: "
            + string.Join("; ", wronglyLedgered));

        string[] countDrift =
        [
            .. SliceBearingClassesWithTestsOutside
                .Select(entry => (entry.Key, entry.Value.Count,
                    Actual: byClass[entry.Key].Count(test => test.Slices.Length == 0),
                    Traited: byClass[entry.Key].Count(test => test.Slices.Length > 0)))
                .Where(row => row.Actual != row.Count || row.Traited == 0)
                .Select(row => row.Traited == 0
                    ? $"{row.Key} no longer has any test inside the slice family"
                    : $"{row.Key} has {row.Actual} tests outside the family, ledgered as {row.Count}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            countDrift.Length == 0,
            "The ledger of partly-outside classes no longer matches the suite: "
            + string.Join("; ", countDrift));
    }

    /// <summary>
    /// Proves the accounting check is not vacuous, by running it over a suite in which a real test
    /// lost its only slice trait.
    /// </summary>
    /// <remarks>
    /// A real test rather than a fabricated one:
    /// <see cref="OnboardMessageProcessorTests.ProtocolProblemIsRecordedWithoutAnsweringOrDroppingTheSession"/>
    /// carries exactly one slice trait today, so stripping it reproduces what a careless edit would
    /// do. The first assertion is the load-bearing half -- it says the real suite is reported clean
    /// by the same computation that reports the damaged one, rather than by an emptier one.
    /// </remarks>
    [Fact]
    public void TheAccountingCheckReportsATestThatLosesItsOnlySliceTrait()
    {
        TestMethod[] tests = TestMethods();
        const string victim = nameof(
            OnboardMessageProcessorTests.ProtocolProblemIsRecordedWithoutAnsweringOrDroppingTheSession);
        TestMethod real = Assert.Single(tests, test => test.MethodName == victim);
        Assert.Single(real.Slices);

        Assert.Empty(Unaccounted(tests));
        Assert.Equal(
            [$"{real.ClassName}.{real.MethodName}"],
            Unaccounted([.. tests.Where(test => test != real), real with { Slices = [] }]));
    }

    private static string[] Unaccounted(IEnumerable<TestMethod> tests) =>
    [
        .. tests
            .Where(test => test.Slices.Length == 0
                && !ClassesOutsideTheSliceFamily.ContainsKey(test.ClassName)
                && !SliceBearingClassesWithTestsOutside.ContainsKey(test.ClassName))
            .Select(test => $"{test.ClassName}.{test.MethodName}")
            .Order(StringComparer.Ordinal)
    ];

    private static TestMethod[] TestMethods() =>
    [
        .. typeof(IntegrationSliceTraitArchitectureTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(RunsAsATest)
            .Select(method =>
            {
                TraitAttribute[] traits =
                [
                    .. method.GetCustomAttributes<TraitAttribute>()
                        .Concat(method.DeclaringType!.GetCustomAttributes<TraitAttribute>())
                ];

                return new TestMethod(
                    method.DeclaringType!.Name,
                    method.Name,
                    [
                        .. traits.Where(trait => string.Equals(trait.Name, SliceTrait, StringComparison.Ordinal))
                            .Select(trait => trait.Value)
                    ],
                    [
                        .. traits.Where(trait => string.Equals(trait.Name, VectorTrait, StringComparison.Ordinal))
                            .Select(trait => trait.Value)
                    ]);
            })
    ];

    private static bool RunsAsATest(MethodInfo method) => method
        .GetCustomAttributes()
        .OfType<IFactAttribute>()
        .Any(fact => fact.Skip is null);

    private static string[] FrozenSliceIds() => [.. VectorsBySlice().Keys.Order(StringComparer.Ordinal)];

    private static Dictionary<string, string[]> VectorsBySlice()
    {
        using JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(),
            "vendor", "8005-agv-protocol", "integration-slices", "index.json")));

        return index.RootElement.GetProperty("slices").EnumerateArray().ToDictionary(
            slice => slice.GetProperty("integrationSliceId").GetString()!,
            string[] (slice) =>
            [
                .. slice.GetProperty("vectorIds").EnumerateArray().Select(vector => vector.GetString()!)
            ],
            StringComparer.Ordinal);
    }
}
