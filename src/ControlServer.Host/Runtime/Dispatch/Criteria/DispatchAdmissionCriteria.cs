using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        ISublotBoxCountReader boxCountReader,
        ILogger<SlotCapacityCriterion> slotCapacityLogger) =>
    [
        new AlreadyAcceptedCriterion(),
        new WorkTypeScopeCriterion(options),
        new RequiredMesFactsCriterion(),
        new AreaScopeCriterion(),
        new AreaEqpUniqueCriterion(),
        new StationResolutionCriterion(stationResolver, options),
        new PackageCapacityCriterion(packageCapacityStore),
        new VehicleDynamicFactsCriterion(options),
        new StationTaskTypeAdmissionCriterion(store),
        new SlotCapacityCriterion(boxCountReader, slotCapacityLogger),
    ];

    /// <summary>Registers the chain and its ranker for the host.</summary>
    public static IServiceCollection AddDispatchAdmission(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDispatchAdmissionCriterion, AlreadyAcceptedCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, WorkTypeScopeCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, RequiredMesFactsCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, AreaScopeCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, AreaEqpUniqueCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, StationResolutionCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, PackageCapacityCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, VehicleDynamicFactsCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, StationTaskTypeAdmissionCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, SlotCapacityCriterion>();

        services.AddScoped<DispatchAdmissionChain>();
        services.AddScoped<IDispatchCandidateRanker, FirstSeenDispatchCandidateRanker>();
        return services;
    }
}
