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
        ITransportDemandSuppressionStore suppressions,
        ControlServerDbContext dbContext,
        RouteGraphAccess? routeGraph = null,
        CatalogAvailabilityAccess? catalog = null,
        PreCreateGate? createGate = null,
        IVehicleSlotLedger? slotLedger = null)
    {
        List<IDispatchAdmissionCriterion> criteria =
        [
            new AlreadyAcceptedCriterion(),
            // 按业务键抑制（批次7-05，control-server#210）。必填、不是可选，理由同下面的故障阻断：同键新 DemandId 过了全部
            // 判据就会在受理存储层撞唯一索引，一个漏传它们的调用方就是一个带着那个隐患的调用方。
            new TransportDemandKeySuppressedCriterion(suppressions),
            new TransportDemandKeyAlreadyAcceptedCriterion(dbContext),
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
            // REQ-0189（批次7-06）：同一份完整快照里一个 Sublot 命中多种任务类型，该 Sublot 全部候选都挡。
            new SublotTaskTypeConflictCriterion(),
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
            // Null is the idle vehicle's ledger, the session baseline -- what the host registers too.
            new SlotCapacityCriterion(boxCountReader, slotCapacityLogger, slotLedger ?? new SessionBaselineSlotLedger()),
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

    /// <summary>
    /// 在途车那条链（REQ-0205，批次7-06，control-server#211）：从空闲链派生，换掉动态事实那一条，加上追加的四道门。
    /// </summary>
    /// <remarks>
    /// <b>派生而不是另写一张表</b>，因为共用的那十几条判据是同一件事：一条需求能不能被这台服务器执行，与车在不在途无关。
    /// 另写一张表，下一个人往空闲链加判据时不会知道在途链也该加——而那正是「在途车放行了一条空闲车挡下的需求」的样子。
    /// </remarks>
    public static IReadOnlyList<IDispatchAdmissionCriterion> InTransit(
        IReadOnlyList<IDispatchAdmissionCriterion> idleChain,
        IOptions<JourneyRuntimeOptions> options,
        RouteGraphAccess? routeGraph = null)
    {
        ArgumentNullException.ThrowIfNull(idleChain);
        List<IDispatchAdmissionCriterion> criteria =
            [.. idleChain.Where(criterion => criterion is not VehicleDynamicFactsCriterion),
             new InTransitVehicleFactsCriterion(options),
             // 批次7-07（control-server#212）：装货阶段结束的车不再接追加。不依赖路网，所以不跟着下面那一条的条件走。
             new LoadingPhaseOpenCriterion()];
        if (routeGraph is not null)
        {
            criteria.Add(new EnRouteAppendCriterion(routeGraph));
        }

        return criteria;
    }

    /// <summary>Registers the chain, its ranker and the dispatch round for the host.</summary>
    public static IServiceCollection AddDispatchAdmission(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDispatchAdmissionCriterion, AlreadyAcceptedCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, TransportDemandKeySuppressedCriterion>();
        services.AddScoped<IDispatchAdmissionCriterion, TransportDemandKeyAlreadyAcceptedCriterion>();
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
        services.AddScoped<IDispatchAdmissionCriterion, SublotTaskTypeConflictCriterion>();
        // 在途链从注册好的空闲链派生（批次7-06，control-server#211）：换掉动态事实那一条，加上追加的四道门。
        services.AddScoped(provider => new InTransitDispatchAdmissionChain(
            InTransit(
                [.. provider.GetServices<IDispatchAdmissionCriterion>()],
                provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>(),
                provider.GetService<RouteGraphAccess>())));
        // Which slots on a side are free (control-server#209): the session baseline, until control-server#211 takes
        // away what a vehicle under way has reserved or loaded.
        // 会话基线减去本车自己已预留、已装的货（批次7-06，control-server#211）；空闲车没有旅程，减数是空集，
        // 答案与会话基线逐字相同。
        services.AddScoped<IVehicleSlotLedger, JourneyAwareSlotLedger>();
        // Cost-ranked, falling back to first-seen when nothing was priced — which is what
        // REQ-0207 asks for when a cost is missing rather than a reachability. The layers are listed in
        // DispatchCandidateOrdering (control-server#209); a new layer is a line there, not here.
        services.AddScoped<IDispatchCandidateRanker>(_ => DispatchCandidateOrdering.Ranker());
        // The structural dispatch block (control-server#74): what can only be concluded across every vehicle.
        services.AddScoped<IDispatchRoundOutcomeSink, StructuralDispatchBlockSink>();
        // 上一轮每辆在途车被「本车货物占侧」判满的那几侧（批次7-07，control-server#212）。单例：这一轮的派车写、下一轮的推进段读，
        // 每一轮是一个新的作用域。
        services.AddSingleton<SlotGroupFullnessBoard>();
        // The round itself and the Onboard facts it shares with the advance side (control-server#209). Scoped, like
        // the engine: both must be handed the engine's own DbContext -- see DispatchRoundRunner.
        services.AddScoped<OnboardDispatchFactsReader>();
        services.AddScoped<DispatchRoundRunner>();
        return services;
    }
}
