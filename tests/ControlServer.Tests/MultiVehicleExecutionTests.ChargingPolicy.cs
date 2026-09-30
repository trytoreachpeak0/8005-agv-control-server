using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 逐车投运（control-server#400，批次9-02；REQ-0282，规格 8.6）：没有「已批准、已激活、覆盖它」的策略版本的车不承接新用途，
/// 其它车照常；不整机拒绝启动。
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 两车、策略只覆盖第二辆：第一辆（轮次里排在前面）被新原因码挡下，需求由第二辆承接。在默认夹具（两车都有策略）下同一轮是第一辆承接。
    /// </summary>
    [Fact]
    public async Task AVehicleThePolicyDoesNotCoverIsRefusedWithTheNewReasonAndTheOtherVehicleTakesTheDemand()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2]);
        fixture.ChargingPolicy = TestChargingPolicies.Only(FleetFixture.VehicleKeys[1]);
        await fixture.RecreateEngineAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(
            [DispatchReasonCodes.ChargingPolicyNotApproved, DispatchAdmissionChain.Eligible],
            outcome.CompletedVehicles.Select(vehicle => Assert.Single(vehicle.Verdicts).ReasonCode).ToArray());
        Assert.Equal(
            [FleetFixture.VehicleKeys[1]],
            fixture.Context.JourneyRuntimes.Select(row => row.VehicleKey).ToArray());
    }

    /// <summary>
    /// 一版策略都没有：服务端照常跑轮次（不整机拒绝），每辆车都被挡，需求留在积压里、原因是新原因码，没有任何建单。
    /// </summary>
    [Fact]
    public async Task WithNoPolicyAtAllTheRoundStillRunsAndEveryVehicleIsRefused()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2]);
        fixture.ChargingPolicy = TestChargingPolicies.None;
        await fixture.RecreateEngineAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.All(outcome.CompletedVehicles, vehicle =>
            Assert.Equal(DispatchReasonCodes.ChargingPolicyNotApproved, Assert.Single(vehicle.Verdicts).ReasonCode));
        Assert.Empty(fixture.Context.JourneyRuntimes);
        Assert.Empty(fixture.Riot.Creates);
        Assert.Equal(
            DispatchReasonCodes.ChargingPolicyNotApproved,
            Assert.Single(fixture.Context.JourneyBacklog).ReasonCode);
    }

    /// <summary>
    /// 途中追加链由空闲链派生，新判据随之进去——同一个实例，同一个次序；一辆被挡的车在途中追加上同样被挡，不必另写一行。
    /// </summary>
    [Fact]
    public async Task TheEnRouteChainCarriesTheSameCommissioningCriterionAndRefusesTheSameVehicle()
    {
        IReadOnlyList<IDispatchAdmissionCriterion> idle = DispatchAdmissionCriteria.Default(
            Options.Create(new JourneyRuntimeOptions()),
            new MapStationResolver(),
            packageCapacityStore: null!,
            store: null!,
            faultStore: null!,
            boxCountReader: null!,
            NullLogger<SlotCapacityCriterion>.Instance,
            suppressions: null!,
            dbContext: null!,
            chargingPolicy: TestChargingPolicies.Only("BROKERX-0001"));
        IReadOnlyList<IDispatchAdmissionCriterion> enRoute =
            DispatchAdmissionCriteria.InTransit(idle, Options.Create(new JourneyRuntimeOptions()));

        ChargingPolicyCommissioningCriterion criterion = Assert.Single(idle.OfType<ChargingPolicyCommissioningCriterion>());
        Assert.Same(criterion, Assert.Single(enRoute.OfType<ChargingPolicyCommissioningCriterion>()));
        // Behind the fault block (15) and the idle return commitment (16, control-server#389): vehicle-side verdicts first.
        Assert.Equal(17, criterion.Order);
        Assert.Equal(
            [nameof(VehicleFaultBlockCriterion), nameof(IdleReturnCommitmentCriterion)],
            enRoute.Where(item => item.Order is 15 or 16).OrderBy(item => item.Order).Select(item => item.GetType().Name));
        Assert.Equal(DispatchReasonCodes.ChargingPolicyNotApproved, await criterion.EvaluateAsync(Evaluation("BROKERX-0002"), TestContext.Current.CancellationToken));
        Assert.Equal(DispatchAdmissionChain.Eligible, await criterion.EvaluateAsync(Evaluation("BROKERX-0001"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 判据只认「投运」：解析器读库出错回 <see cref="ChargingPolicyCommissioningReasons.Unreadable"/> 时，判据同样回新原因码，不放行
    /// （审查变异 MC：放行 Unreadable 时其余用例全绿存活）。
    /// </summary>
    [Fact]
    public async Task AnUnreadablePolicyDecisionIsRefusedLikeAMissingOne()
    {
        ChargingPolicyCommissioningCriterion criterion = new(new UnreadableResolver(), Options.Create(new JourneyRuntimeOptions()));

        Assert.Equal(
            DispatchReasonCodes.ChargingPolicyNotApproved,
            await criterion.EvaluateAsync(Evaluation("BROKERX-0001"), TestContext.Current.CancellationToken));
    }

    private sealed class UnreadableResolver : IChargingPolicyResolver
    {
        public Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(string vehicleKey, CancellationToken cancellationToken) =>
            Task.FromResult(new VehicleChargingPolicyDecision(
                vehicleKey, ChargingPolicyCommissioningReasons.Unreadable, null, "database is locked"));

        public Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>宿主注册了判据与它读的判定（经 <see cref="IChargingPolicyStore"/>），在途链从注册好的空闲链派生，因此也带上它。</summary>
    [Fact]
    public void TheHostRegistersTheCriterionAndTheResolverItReads()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDispatchAdmissionCriterion)
            && descriptor.ImplementationType == typeof(ChargingPolicyCommissioningCriterion));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IChargingPolicyResolver)
            && descriptor.ImplementationType == typeof(ChargingPolicyResolver));
    }

    private static DispatchCandidateEvaluation Evaluation(string vehicleKey) =>
        new(
            FleetFixture.Demand(0, "N1-1", 0),
            null!,
            new DispatchVehicleFacts(vehicleKey, "AGV-" + vehicleKey, null, null!, DateTimeOffset.UnixEpoch));
}
