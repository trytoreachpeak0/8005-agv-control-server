using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Host.Runtime.CreateGate;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// The admission chain's registry — the one place criteria are assembled.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new capability adds one criterion file and one line here.</b> That is the whole point of
/// the chain: B2 multi-vehicle, the RouteGraphSnapshot engine, the FP-C13 create gate and REQ-0207
/// each need to insert an admission rule, and before this they would all have edited the same
/// 255-line method.
/// </para>
/// <para>
/// Order lives on each criterion rather than in this list, so a registration cannot silently
/// reorder the chain. The list below is written in running order anyway, because a reader should
/// not have to collect ten <c>Order</c> properties to see the sequence.
/// </para>
/// </remarks>
public static class DispatchAdmissionCriteria
{
    /// <summary>Every criterion, in running order.</summary>
    public static IReadOnlyList<IDispatchAdmissionCriterion> Default(
        IOptions<JourneyRuntimeOptions> options,
        MapStationResolver stationResolver,
        IPackageCapacityStore packageCapacityStore,
        WireToGateStore store,
        IVehicleFaultStore faultStore,
        ISublotBoxCountReader boxCountReader,
        ILogger<SlotCapacityCriterion> slotCapacityLogger,
        RouteGraphAccess? routeGraph = null,
        CatalogAvailabilityAccess? catalog = null,
        PreCreateGate? createGate = null)
    {
        List<IDispatchAdmissionCriterion> criteria =
        [
            new AlreadyAcceptedCriterion(),
            // Required rather than optional, unlike the three appended below: a safety block a
            // caller may leave out is a safety block that will be left out.
            new VehicleFaultBlockCriterion(faultStore),
            new WorkTypeScopeCriterion(options),
            // Required rather than optional for the same reason as the fault block: B2's two
            // vehicle filters are fail-closed, and a fail-closed rule a caller may omit is one
            // that will be omitted by the caller that most needed it.
            new VehicleTaskTypeAdmissionCriterion(),
            new RequiredMesFactsCriterion(),
            // Required: it blocks nothing, and every batch 4 criterion behind it reads what it records.
            new AreaAssignmentLookupCriterion(),
            new AreaScopeCriterion(),
            new AreaEqpUniqueCriterion(),
            new StationResolutionCriterion(stationResolver, options),
            new DispatchZoneVehicleCriterion(),
            new PackageCapacityCriterion(packageCapacityStore),
            // Required, like the fault block: it is the only thing between a shared-map edit and
            // new work taken on under a policy nobody imported.
            new AdmissionPolicyDriftCriterion(),
            new VehicleDynamicFactsCriterion(options),
            new StationTaskTypeAdmissionCriterion(store),
            new SlotCapacityCriterion(boxCountReader, slotCapacityLogger),
        ];

        // Omitted when no engine is supplied, which is the same admission set as before the engine
        // existed. The criterion itself also passes when the engine is configured off, so the two
        // ways of not having it agree.
        if (routeGraph is not null)
        {
            criteria.Add(new RouteGraphReachabilityCriterion(routeGraph));
        }

        // FP-C13's two halves. Omitted when not supplied, which is what a unit test that is not
        // about the catalog gets; the host always supplies both, and there is no configuration
        // that turns them off -- see MapStationCatalogOptions for why REQ-0302 has no switch.
        if (catalog is not null)
        {
            criteria.Add(new CatalogAvailabilityCriterion(catalog));
        }

        if (createGate is not null)
        {
            criteria.Add(new PreCreateGateCriterion(createGate, options));
        }

        return criteria;
    }

    /// <summary>Registers the chain, its ranker and the dispatch round for the host.</summary>
    public static IServiceCollection AddDispatchAdmission(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDispatchAdmissionCriterion, AlreadyAcceptedCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, VehicleFaultBlockCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, WorkTypeScopeCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, VehicleTaskTypeAdmissionCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, DispatchZoneVehicleCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, RequiredMesFactsCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, AreaAssignmentLookupCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, AreaScopeCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, AreaEqpUniqueCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, CatalogAvailabilityCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, StationResolutionCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, PackageCapacityCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, AdmissionPolicyDriftCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, VehicleDynamicFactsCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, StationTaskTypeAdmissionCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, RouteGraphReachabilityCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, PreCreateGateCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, SlotCapacityCriterion>();

        services.AddScoped<DispatchAdmissionChain>();
        // Cost-ranked, falling back to first-seen when nothing was priced — which is what
        // REQ-0207 asks for when a cost is missing rather than a reachability. The layers are listed in
        // DispatchCandidateOrdering (control-server#209); a new layer is a line there, not here.
        services.AddScoped<IDispatchCandidateRanker>(_ => DispatchCandidateOrdering.Ranker());
        // The structural dispatch block (control-server#74): what can only be concluded across every vehicle.
        services.AddScoped<IDispatchRoundOutcomeSink, StructuralDispatchBlockSink>();
        // The round itself and the Onboard facts it shares with the advance side (control-server#209). Scoped, like
        // the engine: both must be handed the engine's own DbContext -- see DispatchRoundRunner.
        services.AddScoped<OnboardDispatchFactsReader>();
        services.AddScoped<DispatchRoundRunner>();
        return services;
    }
}
