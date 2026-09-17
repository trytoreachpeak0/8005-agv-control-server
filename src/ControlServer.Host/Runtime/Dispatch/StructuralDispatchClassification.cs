using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.RouteGraph;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>What a dispatch reason code means for the structural dispatch block (REQ-0210).</summary>
public enum DispatchReasonClass
{
    /// <summary>
    /// Ordinary backlog: waiting, another vehicle, a fixed read or a freed slot can clear it. Never alarmed.
    /// </summary>
    Backlog,

    /// <summary>
    /// A configured outcome rather than a problem (<see cref="DispatchReasonCodes.Silent"/>): backlog only,
    /// never structural, never alarmed.
    /// </summary>
    Silent,

    /// <summary>
    /// Structural on its own: the code is about the demand and this server's configuration, not about the
    /// vehicle that returned it, so every vehicle whose chain reaches the same criterion returns it too. One
    /// finished vehicle returning it is enough.
    /// </summary>
    Structural,

    /// <summary>
    /// Structural only when every vehicle of the fleet was asked this round and every one returned it. On one
    /// vehicle it is that vehicle's position.
    /// </summary>
    StructuralWhenEveryVehicleReturnsIt,

    /// <summary>
    /// Structural only when the demand needs more baskets than the largest physical slot count its slot group
    /// has on any vehicle of the fleet (REQ-0352), counted on the server's slot models whatever the slots'
    /// state. On one vehicle it is that vehicle's model.
    /// </summary>
    StructuralWhenNoVehicleHasTheSlots,
}

/// <summary>One reason code's class, where in the chain it is decided, and why it is in that class.</summary>
/// <param name="ReasonCode">The code as the chain or the engine writes it.</param>
/// <param name="Class">What it means for the structural dispatch block.</param>
/// <param name="ChainOrder">
/// The <see cref="IDispatchAdmissionCriterion.Order"/> of the criterion that returns it; null for a backlog
/// reason the engine writes after the chain, which never appears in a round's verdicts. A verdict with a
/// later order (or <see cref="DispatchAdmissionChain.Eligible"/>) is proof that everything decided earlier
/// in the chain passed for that candidate.
/// </param>
/// <param name="Rationale">Why the code is in its class.</param>
public sealed record DispatchReasonClassification(
    string ReasonCode,
    DispatchReasonClass Class,
    int? ChainOrder,
    string Rationale);

/// <summary>
/// Every reason code the dispatch chain and the intake behind it write to <c>JourneyBacklog</c>, sorted into
/// ordinary backlog, silent, and structural (REQ-0210, REQ-0352).
/// </summary>
/// <remarks>
/// <para>
/// <b>A code nobody put in here is ordinary backlog.</b> That is the safe side: a missed alarm is what the
/// backlog card still shows, a false alarm tells the floor to carry a demand by hand that a vehicle would have
/// taken. A test scans the criteria for codes and fails on one that is missing, so "not in here" should only
/// ever be a new code on its way in.
/// </para>
/// <para>
/// The test for "structural" is the one the ticket states: independent of every vehicle's dynamic state, so
/// that no other vehicle, and no amount of waiting, would let the demand through. A code that is only
/// structural across the whole fleet says so in its class, and the round-end summary checks the fleet.
/// Codes that could arguably be structural but are not proven to be are backlog, with the doubt written in
/// the rationale.
/// </para>
/// </remarks>
public static class StructuralDispatchClassification
{
    private const int AlreadyAccepted = 10;
    private const int FaultBlock = 15;
    private const int WorkTypeScope = 20;
    private const int VehicleTaskType = 25;
    private const int RequiredMesFacts = 30;
    private const int AreaScope = 40;
    private const int AreaEqpUnique = 50;
    private const int CatalogAvailability = 55;
    private const int StationResolution = 60;
    private const int DispatchZoneVehicle = 65;
    private const int PackageCapacity = 70;
    private const int VehicleDynamicFacts = 80;
    private const int StationTaskType = 90;
    private const int RouteGraphReachability = 95;
    private const int PreCreateGate = 96;
    private const int SlotCapacity = 100;

    private static readonly DispatchReasonClassification[] Rows =
    [
        // ---- AlreadyAcceptedCriterion (10) --------------------------------------------------------------
        Backlog("DEMAND_ALREADY_ACCEPTED", AlreadyAccepted,
            "The demand is taken. Not a block at all; the summary clears any block on an accepted demand."),

        // ---- VehicleFaultBlockCriterion (15) ------------------------------------------------------------
        Backlog(VehicleFaultBlockCriterion.SuspectedReason, FaultBlock,
            "This vehicle's fault state; another vehicle, or the fault clearing, lets the demand through."),
        Backlog(VehicleFaultBlockCriterion.IsolatedReason, FaultBlock,
            "This vehicle's fault state; another vehicle, or releasing the isolation, lets the demand through."),
        Backlog(VehicleFaultBlockCriterion.IdentityUnresolvedReason, FaultBlock,
            "This vehicle's identity; says nothing about the demand."),

        // ---- WorkTypeScopeCriterion (20) ----------------------------------------------------------------
        Backlog("OUT_OF_SCOPE_WORK_TYPE", WorkTypeScope,
            "A work type this server does not execute. Not in the ticket's structural list and not marked silent " +
            "by #72; the catalog only carries WIRE_TO_GATE today. Doubtful, so backlog."),

        // ---- VehicleTaskTypeAdmissionCriterion (25) -----------------------------------------------------
        Backlog(VehicleTaskTypeAdmissionCriterion.VehicleNotInPolicyReason, VehicleTaskType,
            "This vehicle has no dispatch profile; another vehicle may."),
        Backlog(VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason, VehicleTaskType,
            "This vehicle's profile; another vehicle may admit the task type."),

        // ---- RequiredMesFactsCriterion (30) -------------------------------------------------------------
        Backlog("REQUIRED_MES_FACT_MISSING", RequiredMesFacts,
            "MES has not filled a field yet; the next catalog revision can."),

        // ---- AreaScopeCriterion (40), StationResolutionCriterion's fallback (60) ------------------------
        new(DispatchReasonCodes.OutOfScopeArea, DispatchReasonClass.Silent, AreaScope,
            "REQ-0191: an AREA the assignment table leaves unmapped is not executed here, by configuration. " +
            "Silent (DispatchReasonCodes.IsSilent): never structural, never alarmed."),

        // ---- AreaEqpUniqueCriterion (50) ----------------------------------------------------------------
        Backlog("AREA_EQP_NOT_UNIQUE", AreaEqpUnique,
            "Two open demands name one AREA with different EQPs; MES data that the next revision can settle."),

        // ---- CatalogAvailabilityCriterion (55) ----------------------------------------------------------
        Backlog(CatalogAvailabilityReasons.ParametersNotApproved, CatalogAvailability,
            "Blocks every demand at once and is about the Map/Station catalog, not about this demand; REQ-0210's " +
            "alarm is per task. Doubtful, so backlog."),
        Backlog(CatalogAvailabilityReasons.NeverConfirmed, CatalogAvailability,
            "The catalog has not been confirmed yet; the next refresh can."),
        Backlog(CatalogAvailabilityReasons.FreshnessExceeded, CatalogAvailability,
            "The catalog went stale; the next refresh clears it."),
        Backlog(CatalogAvailabilityReasons.BuildIncompatible, CatalogAvailability,
            "About the running build against the catalog, not about this demand; a re-confirmation clears it."),

        // ---- StationResolutionCriterion (60) ------------------------------------------------------------
        new("AREA_STATION_NOT_FOUND", DispatchReasonClass.Structural, StationResolution,
            "The fresh Map has no station for the demand's AREA. Same Map for every vehicle in the round."),
        new("AREA_STATION_NOT_UNIQUE", DispatchReasonClass.Structural, StationResolution,
            "The fresh Map has more than one station for the demand's AREA. Same Map for every vehicle."),
        new("DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING", DispatchReasonClass.Structural, StationResolution,
            "The zone the AREA is assigned is not among this server's allowed dispatch zones. Configuration, " +
            "the same for every vehicle."),
        Backlog("ROUTE_EVIDENCE_MISSING", StationResolution,
            "The route evidence id came out empty. It is derived from Map, stations, AREA and EQP and is not " +
            "expected to happen at all; not in the ticket's structural list. Doubtful, so backlog."),

        // ---- DispatchZoneVehicleCriterion (65) ----------------------------------------------------------
        new(DispatchZoneVehicleCriterion.ZoneNotConfiguredReason, DispatchReasonClass.Structural, DispatchZoneVehicle,
            "The dispatch policy names no vehicle for the demand's zone. Configuration, the same for every vehicle."),
        Backlog(DispatchZoneVehicleCriterion.VehicleNotInZoneReason, DispatchZoneVehicle,
            "The zone has vehicles and this is not one of them; one of them may take it."),

        // ---- PackageCapacityCriterion (70) --------------------------------------------------------------
        Backlog("PACKAGE_CAPACITY_NOT_UNIQUE", PackageCapacity,
            "The package's capacity is not resolved to one value. Master data that can be fixed without the " +
            "demand changing, and not in the ticket's structural list. Doubtful, so backlog."),

        // ---- VehicleDynamicFactsCriterion (80) ----------------------------------------------------------
        Backlog("ONBOARD_FACTS_NOT_READY", VehicleDynamicFacts, "This vehicle's session is not ready."),
        Backlog("ONBOARD_DEPARTURE_UNSAFE", VehicleDynamicFacts, "This vehicle's onboard safety state right now."),
        Backlog("RIOT_VEHICLE_NOT_AVAILABLE", VehicleDynamicFacts, "This vehicle is disconnected or disabled."),
        Backlog("RIOT_VEHICLE_BINDING_MISMATCH", VehicleDynamicFacts, "This vehicle's RIoT binding."),
        Backlog("RIOT_VEHICLE_NOT_IDLE", VehicleDynamicFacts, "This vehicle is busy."),
        Backlog("RIOT_VEHICLE_MAP_MISMATCH", VehicleDynamicFacts, "This vehicle is on another map right now."),
        Backlog("RIOT_VEHICLE_FACT_STALE", VehicleDynamicFacts, "This vehicle's observation is old."),
        Backlog("BATTERY_FACT_UNKNOWN", VehicleDynamicFacts, "This vehicle's battery is not reported."),
        Backlog("BATTERY_POLICY_NOT_SATISFIED", VehicleDynamicFacts, "This vehicle is charging or low."),
        Backlog("RIOT_VEHICLE_NOT_STOPPED", VehicleDynamicFacts, "This vehicle is moving."),
        Backlog("RIOT_VEHICLE_ORDER_OCCUPIED", VehicleDynamicFacts, "This vehicle holds an order."),

        // ---- StationTaskTypeAdmissionCriterion (90) -----------------------------------------------------
        Backlog("TASK_TYPE_NOT_ALLOWED_AT_STATION", StationTaskType,
            "The admission policy is re-applied from the Map every round; a station missing from it is " +
            "admitted on the next. Doubtful, so backlog."),

        // ---- RouteGraphReachabilityCriterion (95) -------------------------------------------------------
        Backlog(RouteGraphStaleReasons.NeverRefreshed, RouteGraphReachability, "The route graph is not loaded yet."),
        Backlog(RouteGraphStaleReasons.DesignStateExpired, RouteGraphReachability, "The route graph went stale."),
        Backlog(RouteGraphStaleReasons.RuntimeStateExpired, RouteGraphReachability, "The route graph went stale."),
        Backlog(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, RouteGraphReachability,
            "The route graph is being replaced."),
        Backlog(RouteGraphStaleReasons.DynamicRouteCostAppeared, RouteGraphReachability,
            "The route graph is being replaced."),
        Backlog(RouteGraphStaleReasons.RefreshFailed, RouteGraphReachability, "The route graph could not be read."),
        Backlog("ROUTE_GRAPH_VEHICLE_POSITION_UNKNOWN", RouteGraphReachability,
            "This vehicle's position is not known; not knowing is not unreachable."),
        new("ROUTE_GRAPH_PICKUP_UNREACHABLE", DispatchReasonClass.StructuralWhenEveryVehicleReturnsIt,
            RouteGraphReachability,
            "The graph has no path from this vehicle's position. Structural only when every vehicle of the fleet " +
            "finished the round and every one returned it (the ticket's \"all vehicles unreachable\")."),

        // ---- PreCreateGateCriterion (96) ----------------------------------------------------------------
        Backlog(CreateGateReasons.StationUnreachable, PreCreateGate,
            "RIoT says this vehicle cannot reach the station now. The ticket names the route graph's verdict " +
            "across all vehicles, not RIoT's; doubtful, so backlog."),
        Backlog(CreateGateReasons.RouteCostUnavailable, PreCreateGate, "RIoT could not be asked."),
        Backlog(CreateGateReasons.EvidenceConflict, PreCreateGate,
            "The graph and RIoT disagree; alarmed by the create gate itself, and it may settle."),
        Backlog(CreateGateReasons.FrozenStationAbsent, PreCreateGate,
            "About an endpoint a demand froze when it was taken; a candidate has frozen nothing, and the chain " +
            "never asks with a station outside the catalog. Doubtful, so backlog."),

        // ---- SlotCapacityCriterion (100) ----------------------------------------------------------------
        Backlog("SUBLOT_BOX_COUNT_UNAVAILABLE", SlotCapacity, "The box count read failed or is not there yet."),
        new("EXPECTED_BASKET_COUNT_OUT_OF_RANGE", DispatchReasonClass.StructuralWhenNoVehicleHasTheSlots, SlotCapacity,
            "More than eight baskets. No slot group of any vehicle has that many physical slots, so the fleet " +
            "check raises it as EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP."),
        Backlog(DispatchReasonCodes.AreaSlotGroupNotAssigned, SlotCapacity,
            "Defensive only: AreaScopeCriterion already refuses an AREA with no assignment, and an assignment row " +
            "always has a slot group. Doubtful, so backlog."),
        Backlog(DispatchReasonCodes.VehicleSlotModelUnresolved, SlotCapacity,
            "This vehicle has no slot model on record; another vehicle may, and binding one clears it."),
        new(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup,
            DispatchReasonClass.StructuralWhenNoVehicleHasTheSlots, SlotCapacity,
            "More baskets than this vehicle's group has physical slots. Structural only when no vehicle of the " +
            "fleet has that many in the group (REQ-0352), by slot model, whatever is disabled or occupied."),
        Backlog(DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, SlotCapacity,
            "Too few usable free slots in the group right now, disabled ones included: REQ-0352 says a temporary " +
            "shortfall is not structural."),

        // ---- written by the engine after the chain ------------------------------------------------------
        Backlog("FINAL_DYNAMIC_FACTS_NOT_READY", null, "The pre-intake re-read of this vehicle's facts failed."),
        Backlog("FINAL_CATALOG_CANDIDATE_GONE", null, "The demand left the catalog during intake."),
        Backlog("FINAL_CATALOG_DECISION_FACT_CHANGED", null,
            "A decision fact changed during intake: the MES catalog or the area assignment table, which the " +
            "backlog cannot tell apart."),
        Backlog("DEMAND_DECISION_FACT_CHANGED", null, "A decision fact changed since the demand was first seen."),
        Backlog(DispatchReasonCodes.DemandLeftCatalog, null,
            "Not a verdict: the round writes it before the vehicle loop for an unaccepted demand the catalog no " +
            "longer lists. Nothing is waiting; a block on that demand is cleared by the catalog-absence rule."),
    ];

    /// <summary>Every classified reason code.</summary>
    public static IReadOnlyDictionary<string, DispatchReasonClassification> ByCode { get; } =
        Rows.ToDictionary(row => row.ReasonCode, StringComparer.Ordinal);

    /// <summary>The class of <paramref name="reasonCode"/>; an unlisted code is ordinary backlog.</summary>
    public static DispatchReasonClass ClassOf(string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(reasonCode);
        if (DispatchReasonCodes.IsSilent(reasonCode))
        {
            return DispatchReasonClass.Silent;
        }
        return ByCode.TryGetValue(reasonCode, out DispatchReasonClassification? row)
            ? row.Class
            : DispatchReasonClass.Backlog;
    }

    /// <summary>
    /// Whether a verdict of <paramref name="reasonCode"/> means the chain got at least as far as
    /// <paramref name="chainOrder"/> for that candidate. False for a code whose place in the chain is unknown.
    /// </summary>
    public static bool Reached(string reasonCode, int chainOrder)
    {
        ArgumentNullException.ThrowIfNull(reasonCode);
        if (string.Equals(reasonCode, DispatchAdmissionChain.Eligible, StringComparison.Ordinal))
        {
            return true;
        }
        return ByCode.TryGetValue(reasonCode, out DispatchReasonClassification? row)
            && row.ChainOrder is { } order
            && order >= chainOrder;
    }

    private static DispatchReasonClassification Backlog(string reasonCode, int? chainOrder, string rationale) =>
        new(reasonCode, DispatchReasonClass.Backlog, chainOrder, rationale);
}
