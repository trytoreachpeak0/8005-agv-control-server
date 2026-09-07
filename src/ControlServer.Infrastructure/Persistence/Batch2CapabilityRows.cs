using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Persistence foundation for the four batch-2 track-B capability lanes: B2 multi-vehicle
/// execution, the <c>RouteGraphSnapshot</c> engine, the <c>FP-C11</c> command surface and fault
/// isolation model, and <c>FP-C13</c> catalog availability.
/// </summary>
/// <remarks>
/// <para>
/// These tables land in one migration on purpose. EF Core's model snapshot is a single file, so
/// two capability lanes each adding their own migration collide, and the second one's designer
/// snapshot depends on the first already existing. Landing them together is the only thing that
/// makes those lanes parallel.
/// </para>
/// <para>
/// <b>Structure only.</b> Nothing here implements behaviour — no refresh loop, no staleness
/// decision, no admission rule. Each lane's own ticket fills those in, and none of them needs a
/// migration or a <c>Ports.cs</c> change to do it.
/// </para>
/// </remarks>
internal static class Batch2CapabilityModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureMultiVehicle(modelBuilder);
        ConfigureRouteGraph(modelBuilder);
        ConfigureFaultAndCommands(modelBuilder);
        ConfigureCatalogAvailability(modelBuilder);
    }

    // ---- B2 multi-vehicle execution (ticket 09) ----------------------------------------

    private static void ConfigureMultiVehicle(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VehicleTaskTypeAdmissionRow>()
            .HasKey(row => new { row.AgvId, row.TaskType });
        modelBuilder.Entity<DispatchZoneVehicleRow>()
            .HasKey(row => new { row.Zone, row.AgvId });
        modelBuilder.Entity<VehicleDispatchBudgetRow>().HasKey(row => row.AgvId);

        // Vehicle-occupancy uniqueness, moved down from the lease table onto OrderIntents
        // (specification 5.1). Two deliberate choices here:
        //
        // 1. The filter also requires ClaimedAt to be set. OrderIntents keeps historical rows, so
        //    the same VehicleKey already appears many times over; an unconditional unique index
        //    would be violated by existing data on the first migration. Nothing writes ClaimedAt
        //    yet, so every current and newly inserted row sits outside the index and behaviour is
        //    unchanged — exactly what ticket 06 requires. Ticket 09 turns the constraint on by
        //    writing the two timestamps; no schema change is needed then.
        // 2. The lease table's own unique index stays. Dropping it here would be a behaviour
        //    change in the batch that promised none, and the two are not in conflict: a lease is
        //    one demand's claim, an occupancy is one vehicle's in-flight order.
        modelBuilder.Entity<OrderIntentRow>()
            .HasIndex(row => row.VehicleKey)
            .IsUnique()
            .HasFilter("VehicleOccupancyClaimedAt IS NOT NULL AND VehicleOccupancyReleasedAt IS NULL");
    }

    // ---- RouteGraphSnapshot engine (ticket 12) -----------------------------------------

    private static void ConfigureRouteGraph(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RouteGraphSnapshotRow>().HasKey(row => row.MapId);
        modelBuilder.Entity<RouteGraphSnapshotRow>().Property(row => row.MapId).ValueGeneratedNever();
        // The design-state revision is the concurrency token: a refresh that raced another one
        // must fail rather than interleave half of each graph.
        modelBuilder.Entity<RouteGraphSnapshotRow>()
            .Property(row => row.DesignRevision)
            .IsConcurrencyToken();

        modelBuilder.Entity<RouteGraphEdgeRow>().HasKey(row => new { row.MapId, row.EdgeId });
        modelBuilder.Entity<RouteGraphEdgeRow>().HasIndex(row => new { row.MapId, row.StartNode });
        modelBuilder.Entity<RouteGraphEdgeRow>().HasIndex(row => new { row.MapId, row.EndNode });

        modelBuilder.Entity<RouteGraphStationRow>().HasKey(row => new { row.MapId, row.StationId });
        modelBuilder.Entity<RouteGraphStationRow>().HasIndex(row => new { row.MapId, row.ResolvedNode });

        modelBuilder.Entity<RouteGraphRemovedEdgeRow>().HasKey(row => new { row.MapId, row.EdgeId });
        modelBuilder.Entity<RouteGraphRemovedStationRow>().HasKey(row => new { row.MapId, row.StationId });

        // (MapId, GroupName, EdgeId), not (GroupName, EdgeId): Round 43 observed one group name
        // spanning several Maps — "老厂电梯" belongs to both map 14 and map 19 — so the group name
        // alone does not identify a group.
        modelBuilder.Entity<RouteGraphEdgeGroupRow>()
            .HasKey(row => new { row.MapId, row.GroupName, row.EdgeId });
    }

    // ---- FP-C11 command surface and fault isolation (tickets 10 and 11) ------------------

    private static void ConfigureFaultAndCommands(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VehicleFaultStateRow>().HasKey(row => row.AgvId);
        modelBuilder.Entity<VehicleFaultStateRow>().Property(row => row.Level).HasConversion<string>();
        // Level and generation are concurrency tokens: an escalation racing a clearance must not
        // silently overwrite the other's decision. REQ-0246 escalates immediately, so these two
        // writes really do race.
        modelBuilder.Entity<VehicleFaultStateRow>().Property(row => row.Level).IsConcurrencyToken();
        modelBuilder.Entity<VehicleFaultStateRow>().Property(row => row.FaultGeneration).IsConcurrencyToken();

        modelBuilder.Entity<FaultedVehicleCargoRow>().HasKey(row => row.CargoBindingId);
        // One live cargo binding per vehicle per fault generation. A binding released after a
        // repair-continue stays as the record of what the vehicle was carrying, and steps out of
        // the uniqueness scope rather than being deleted (REQ-0238's binding must not change).
        modelBuilder.Entity<FaultedVehicleCargoRow>()
            .HasIndex(row => new { row.AgvId, row.FaultGeneration })
            .IsUnique()
            .HasFilter("ReleasedAt IS NULL");
        modelBuilder.Entity<FaultedVehicleCargoRow>().HasIndex(row => row.DemandId);

        modelBuilder.Entity<RiotOrderCommandAuditRow>().HasKey(row => row.CommandAuditId);
        // "Called it once" has to be decidable from this table, so the natural key of an attempt
        // is unique: one row per (command, target order, attempt). A retry is a new attempt
        // number, never an overwrite — REQ-0248 retries triggerEmergency until confirmed, and the
        // reconciliation has to be able to tell one attempt from three.
        modelBuilder.Entity<RiotOrderCommandAuditRow>()
            .HasIndex(row => new { row.CommandType, row.TargetUpperId, row.AttemptNumber })
            .IsUnique();
        modelBuilder.Entity<RiotOrderCommandAuditRow>().HasIndex(row => new { row.AgvId, row.IssuedAt });
        modelBuilder.Entity<RiotOrderCommandAuditRow>().Property(row => row.Outcome).HasConversion<string>();
    }

    // ---- FP-C13 catalog availability and create gate (ticket 13) -------------------------

    private static void ConfigureCatalogAvailability(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MapStationCatalogStateRow>().HasKey(row => row.MapId);
        modelBuilder.Entity<MapStationCatalogStateRow>().Property(row => row.MapId).ValueGeneratedNever();
        modelBuilder.Entity<MapStationCatalogStateRow>().Property(row => row.State).HasConversion<string>();

        // Frozen endpoints are per demand and per role. REQ-0305 requires that a later catalog
        // refresh cannot rewrite them, which is why they live here rather than being re-resolved.
        modelBuilder.Entity<FrozenDemandStationRow>().HasKey(row => new { row.DemandId, row.Role });
        modelBuilder.Entity<FrozenDemandStationRow>().Property(row => row.Role).HasConversion<string>();
        modelBuilder.Entity<FrozenDemandStationRow>().HasIndex(row => row.TransportDemandKey);

        modelBuilder.Entity<CreateGateAuditRow>().HasKey(row => row.GateAuditId);
        modelBuilder.Entity<CreateGateAuditRow>().HasIndex(row => new { row.DemandId, row.EvaluatedAt });
        modelBuilder.Entity<CreateGateAuditRow>().Property(row => row.Verdict).HasConversion<string>();
    }
}

// ======== B2 multi-vehicle execution (ticket 09) ==========================================

/// <summary>Which task types a given vehicle may take. Fail-closed: absent means not allowed.</summary>
public sealed class VehicleTaskTypeAdmissionRow
{
    public required string AgvId { get; set; }
    public required string TaskType { get; set; }
    public bool Allowed { get; set; }
    public required string ConfigurationVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Which vehicles serve a dispatch zone — a hard set, not a preference.</summary>
public sealed class DispatchZoneVehicleRow
{
    public required string Zone { get; set; }
    public required string AgvId { get; set; }
    public required string ConfigurationVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Per-vehicle timeout budget for one dispatch round. The dispatch loop is a single worker
/// iterating vehicles serially, so one stuck vehicle would otherwise starve the rest; this budget
/// is what bounds that.
/// </summary>
public sealed class VehicleDispatchBudgetRow
{
    public required string AgvId { get; set; }
    public int RoundTimeoutMilliseconds { get; set; }
    public required string ConfigurationVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ======== RouteGraphSnapshot engine (ticket 12) ===========================================

/// <summary>
/// One Map's route-graph snapshot header: both refresh cycles' clocks, the edge-group
/// fingerprint, and the fields a staleness decision needs.
/// </summary>
/// <remarks>
/// Design state refreshes on <c>gmtUpdate</c> with a 10-minute TTL backstop; runtime state (the
/// removed sets) refreshes on its own 10-second cycle. A changed <c>mapEdgeGroup</c> fingerprint,
/// or dynamic route cost going from empty to non-empty, means stale. Stale means no dispatch this
/// round — the engine is a hard dependency of the dispatch chain, not a degradable layer.
/// </remarks>
public sealed class RouteGraphSnapshotRow
{
    public int MapId { get; set; }
    /// <summary>Bumped on every completed design-state replacement; also the concurrency token.</summary>
    public long DesignRevision { get; set; }
    public DateTimeOffset? DesignRefreshedAt { get; set; }
    /// <summary>Latest <c>gmtUpdate</c> seen across the design-state read, if RIoT reported one.</summary>
    public string? DesignSourceGmtUpdate { get; set; }
    public int DesignEdgeCount { get; set; }
    public int DesignStationCount { get; set; }
    public DateTimeOffset? RuntimeRefreshedAt { get; set; }
    public int RuntimeRemovedEdgeCount { get; set; }
    public int RuntimeRemovedStationCount { get; set; }
    /// <summary>
    /// Fingerprint over the Map's edge-group membership. Empty string means "no groups", which is
    /// map25's actual state — every edge group on the production RIoT belongs to another Map — so
    /// a staleness check must treat empty-to-non-empty as a change, not as "not fetched yet".
    /// </summary>
    public required string EdgeGroupFingerprint { get; set; }
    public DateTimeOffset? EdgeGroupRefreshedAt { get; set; }
    /// <summary>Whether the last dynamic-route-cost read came back non-empty.</summary>
    public bool DynamicRouteCostPresent { get; set; }
    public DateTimeOffset? DynamicRouteCostObservedAt { get; set; }
    /// <summary>Why the snapshot is currently considered stale; null when it is usable.</summary>
    public string? StaleReason { get; set; }
    public DateTimeOffset? StaleSince { get; set; }
}

/// <summary>One directed edge of a Map's design-state graph. Replaced wholesale per refresh.</summary>
public sealed class RouteGraphEdgeRow
{
    public int MapId { get; set; }
    public int EdgeId { get; set; }
    public int StartNode { get; set; }
    public int EndNode { get; set; }
    public double CostMm { get; set; }
    public int StartX { get; set; }
    public int StartY { get; set; }
    public int EndX { get; set; }
    public int EndY { get; set; }
    public int Direction { get; set; }
    public bool IsBackEdge { get; set; }
    public long DesignRevision { get; set; }
}

/// <summary>
/// One station of a Map's design state, with the node it was resolved onto.
/// </summary>
/// <remarks>
/// <c>ResolvedNode</c> is derived, not read off the wire: RIoT gives a station its <c>edge_id</c>
/// and coordinates, and the node is whichever of that edge's two endpoints the station sits
/// nearer. Round 43 measured this on map25 — 206 stations onto 206 distinct nodes, zero
/// collisions, every station at t=0 or t=1 and none mid-edge. <c>station_offset</c> is zero on all
/// of them and cannot be used for this.
/// </remarks>
public sealed class RouteGraphStationRow
{
    public int MapId { get; set; }
    public int StationId { get; set; }
    public required string Name { get; set; }
    public int EdgeId { get; set; }
    public double PosX { get; set; }
    public double PosY { get; set; }
    public int ResolvedNode { get; set; }
    /// <summary>Distance in mm from the station to the node it resolved onto; a quality signal.</summary>
    public double ResolutionResidualMm { get; set; }
    public long DesignRevision { get; set; }
}

/// <summary>An edge currently removed from the Map — runtime state, refreshed on its own cycle.</summary>
public sealed class RouteGraphRemovedEdgeRow
{
    public int MapId { get; set; }
    public int EdgeId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>A station currently removed from the Map — runtime state.</summary>
public sealed class RouteGraphRemovedStationRow
{
    public int MapId { get; set; }
    public int StationId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>One edge's membership in a named edge group — the fingerprint's source rows.</summary>
public sealed class RouteGraphEdgeGroupRow
{
    public int MapId { get; set; }
    public required string GroupName { get; set; }
    public int EdgeId { get; set; }
    /// <summary>Raw wire value, e.g. <c>SINGLE_VEHICLE_ONLY</c>. An unknown value must survive intact.</summary>
    public required string GroupType { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}

// ======== FP-C11 command surface and fault isolation (tickets 10 and 11) ===================

/// <summary>The two-level fault fact held for one vehicle.</summary>
public sealed class VehicleFaultStateRow
{
    public required string AgvId { get; set; }
    public VehicleFaultLevel Level { get; set; }
    /// <summary>Bumped on every entry into a fault; scopes the cargo binding and the audit trail.</summary>
    public long FaultGeneration { get; set; }
    /// <summary>The fact that produced the current level, as an evidence code.</summary>
    public string? EvidenceCode { get; set; }
    /// <summary>Whether <see cref="EvidenceCode"/> is on REQ-0233's closed automatic-confirmation whitelist.</summary>
    public bool EvidenceOnAutoConfirmWhitelist { get; set; }
    public DateTimeOffset? EnteredAt { get; set; }
    public DateTimeOffset? LastEvaluatedAt { get; set; }
    /// <summary>
    /// Whether the vehicle has been proven stopped by fresh combined evidence (REQ-0247). Never
    /// set from a single signal: order HELD, emergency-stop state and a one-shot query are each
    /// insufficient alone.
    /// </summary>
    public bool StopProven { get; set; }
    public DateTimeOffset? StopProvenAt { get; set; }
    /// <summary>Set when the vehicle could not be proven stopped and REQ-0246 escalated at once.</summary>
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    /// <summary>Why the block was cleared — REQ-0167 only permits this on independently provable safety.</summary>
    public string? ClearedReason { get; set; }
}

/// <summary>
/// A faulted vehicle's cargo binding (REQ-0238). Created when there is loading evidence or the
/// cargo state is unknown; the task and cargo binding do not change while it is live.
/// </summary>
public sealed class FaultedVehicleCargoRow
{
    public required string CargoBindingId { get; set; }
    public required string AgvId { get; set; }
    public long FaultGeneration { get; set; }
    public required string DemandId { get; set; }
    public string? MovementLegId { get; set; }
    public required string TransportDemandKey { get; set; }
    /// <summary>True when loading was witnessed; false when the cargo state is merely unknown.</summary>
    public bool LoadingWitnessed { get; set; }
    public DateTimeOffset BoundAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleasedReason { get; set; }
}

/// <summary>
/// One attempt at one RIoT order command, with enough identity to decide "it was called, with
/// these arguments, exactly once" (RIoTRetryReconciliation semantics).
/// </summary>
public sealed class RiotOrderCommandAuditRow
{
    public required string CommandAuditId { get; set; }
    /// <summary>CANCEL / OrderHold / OrderContinue / HangContinue / triggerEmergency / cancelEmergency.</summary>
    public required string CommandType { get; set; }
    public required string AgvId { get; set; }
    public required string TargetUpperId { get; set; }
    public string? TargetOrderId { get; set; }
    public int AttemptNumber { get; set; }
    /// <summary>Hash over the semantic arguments, so a repeat with different arguments is visible.</summary>
    public required string RequestSemanticSha256 { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public RiotOrderCommandOutcome Outcome { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public string? ReceiptJson { get; set; }
    /// <summary>Which fault generation this command belongs to, when it was a fault response.</summary>
    public long? FaultGeneration { get; set; }
}

// ======== FP-C13 catalog availability and create gate (ticket 13) =========================

/// <summary>
/// One Map's catalog availability state.
/// </summary>
/// <remarks>
/// Freshness is measured from the last <em>complete confirmation</em>, not the last attempt
/// (REQ-0302). The two parameters that govern it — sync period and maximum unconfirmed age — each
/// need separate approval before commissioning, and the maximum must exceed the period. Absent an
/// approved value, Map/Station-dependent business must not start at all.
/// </remarks>
public sealed class MapStationCatalogStateRow
{
    public int MapId { get; set; }
    public MapStationCatalogState State { get; set; }
    public DateTimeOffset? LastCompleteConfirmationAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastFailureReason { get; set; }
    public long CatalogRevision { get; set; }
    /// <summary>The approved values this state was evaluated against, for the audit trail.</summary>
    public int? ApprovedSyncPeriodSeconds { get; set; }
    public int? ApprovedMaxUnconfirmedSeconds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A station resolved and frozen at demand-creation time (REQ-0305). A later catalog refresh must
/// not rewrite these — that is the whole point of freezing them.
/// </summary>
public sealed class FrozenDemandStationRow
{
    public required string DemandId { get; set; }
    public FrozenStationRole Role { get; set; }
    public required string TransportDemandKey { get; set; }
    public int MapId { get; set; }
    public int StationId { get; set; }
    public required string StationName { get; set; }
    /// <summary>The catalog revision these endpoints were taken from.</summary>
    public long CatalogRevision { get; set; }
    public DateTimeOffset FrozenAt { get; set; }
}

/// <summary>
/// The audit trail of one pre-create gate evaluation, including the RouteCost evidence it rested
/// on. The gate is required by REQ-0147／REQ-0293／REQ-0302／REQ-0305／REQ-0345 and has never been
/// implemented — <c>RouteCost</c> is a zero-hit in <c>src/</c> today.
/// </summary>
public sealed class CreateGateAuditRow
{
    public required string GateAuditId { get; set; }
    public required string DemandId { get; set; }
    public required string TransportDemandKey { get; set; }
    public required string AgvId { get; set; }
    public int MapId { get; set; }
    public int TargetStationId { get; set; }
    public CreateGateVerdict Verdict { get; set; }
    /// <summary>RIoT's answer in mm; negative means unreachable. Null when the call was not made.</summary>
    public long? RiotRouteCostMm { get; set; }
    /// <summary>
    /// The self-built graph's station-to-station cost, when one was computed. Deliberately not
    /// named RouteCost anywhere: REQ-0207 forbids a weak substitute from impersonating RIoT's
    /// evidence, and CP-0001's revision keeps the two facts separately named.
    /// </summary>
    public long? GraphTraversalCostMm { get; set; }
    public bool GraphReachable { get; set; }
    public string? ConflictDetail { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
}
