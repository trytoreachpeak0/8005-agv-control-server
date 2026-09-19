using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 建单冻结（control-server#160，REQ-0344）：受理事务内经 #159 的冻结端口记下规则版本与绑定集版本（任务类型在受理行、
/// 固定站在 <c>FrozenDemandStations</c> 的 <c>Dropoff</c>）；幂等重放把这两个新字段纳入比较。
/// </summary>
public sealed class DemandTaskTypeStationIntakeFreezeTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AcceptingADemandFreezesTheRuleAndBindingSetVersionsItWasJudgedUnderInTheSameTransaction()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        (long ruleVersion, long bindingSetVersion) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture, [TransportTaskTypes.WireToGate], [GateBinding]);

        await new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            Demand(), PickupIntent(), Plan(ruleVersion, bindingSetVersion), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            new DemandTaskTypeStationFreeze(DemandId, ruleVersion, 25, bindingSetVersion, Now),
            await fixture.Freezes.ReadAsync(DemandId, Token));
        Assert.Equal(
            TransportTaskTypes.WireToGate,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(Token)).WorkType);
    }

    /// <summary>同一需求重放同一次受理（两个版本相同）是同一次受理，只有一个旅程、一份冻结。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AReplayCarryingTheFrozenVersionsIsTheSameAcceptance()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        (long ruleVersion, long bindingSetVersion) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        WireToGateStore store = new(fixture.Context);
        await store.AcceptWithOrderIntentAsync(Demand(), PickupIntent(), Plan(ruleVersion, bindingSetVersion), Token);

        await store.AcceptWithOrderIntentAsync(Demand(), PickupIntent(), Plan(ruleVersion, bindingSetVersion), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Single(await fixture.Context.JourneyRuntimes.ToArrayAsync(Token));
        Assert.Equal(2, await fixture.Context.Set<ConfigurationConsumerBindingRow>()
            .CountAsync(row => row.ConsumerId == DemandId && row.ObjectKind != GovernedObjectKind.DispatchZoneAreaAssignment, Token));
    }

    /// <summary>
    /// 重放时版本不同（另一版绑定集、另一版规则、或者没带版本）就是另一次受理，与其它计划字段变了一样被拒；
    /// 已冻结的那一份不被改写。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AReplayNamingOtherVersionsIsADifferentAcceptanceAndTheFreezeStands()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        (long ruleVersion, long bindingSetVersion) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        WireToGateStore store = new(fixture.Context);
        await store.AcceptWithOrderIntentAsync(Demand(), PickupIntent(), Plan(ruleVersion, bindingSetVersion), Token);
        (long laterRules, long laterBindings) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture,
            [TransportTaskTypes.WireToGate],
            [GateBinding with { StationRiotId = 220, StationName = "关卡2" }]);

        foreach (JourneyExecutionPlan replay in new[]
                 {
                     Plan(laterRules, laterBindings),
                     Plan(ruleVersion, laterBindings),
                     Plan(null, null),
                 })
        {
            await Assert.ThrowsAsync<BusinessIdentityConflictException>(
                () => store.AcceptWithOrderIntentAsync(Demand(), PickupIntent(), replay, Token));
        }
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            new DemandTaskTypeStationFreeze(DemandId, ruleVersion, 25, bindingSetVersion, Now),
            await fixture.Freezes.ReadAsync(DemandId, Token));
    }

    /// <summary>重放声称冻结过版本、而原受理没冻结任何版本：没有耐久记录能证明原受理是按那一版判的，拒。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AReplayClaimingVersionsTheDemandNeverFrozeIsADifferentAcceptance()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        (long ruleVersion, long bindingSetVersion) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        WireToGateStore store = new(fixture.Context);
        await store.AcceptWithOrderIntentAsync(Demand(), PickupIntent(), Plan(null, null), Token);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.AcceptWithOrderIntentAsync(
            Demand(), PickupIntent(), Plan(ruleVersion, bindingSetVersion), Token));
    }

    /// <summary>
    /// 落点站与版本在同一个受理事务里冻结。库里若还留着一次被拒受理写下的旧冻结行（本票之前站点冻结在受理事务之外），
    /// 这条需求从没被受理过，那两行不是任何任务的终点：受理时换成本次计划的两端，不因「拒绝改写」让整轮失败。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AcceptingFreezesBothEndpointsInTheSameTransactionOverStaleRowsOfARefusedAttempt()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        (long ruleVersion, long bindingSetVersion) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture, [TransportTaskTypes.WireToGate], [GateBinding with { StationRiotId = 220, StationName = "关卡2" }]);
        await new CatalogAvailabilityStore(fixture.Context).FreezeDemandStationsAsync(
            DemandId,
            "SUBLOT-001|WIRE_TO_GATE",
            [new FrozenStationFact(FrozenStationRole.Pickup, 25, 12, "N1-1"), new FrozenStationFact(FrozenStationRole.Dropoff, 25, 210, "关卡")],
            20,
            Now.AddMinutes(-5),
            Token);
        fixture.Context.ChangeTracker.Clear();

        await new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            Demand(),
            PickupIntent(),
            Plan(ruleVersion, bindingSetVersion) with { GateStationId = "关卡2", GateStationRiotId = 220, StationCatalogRevision = 21 },
            Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            [
                new FrozenStationFact(FrozenStationRole.Pickup, 25, 12, "N1-1"),
                new FrozenStationFact(FrozenStationRole.Dropoff, 25, 220, "关卡2"),
            ],
            (await new CatalogAvailabilityStore(fixture.Context).ReadFrozenStationsAsync(DemandId, Token))
                .OrderBy(station => station.Role));
        Assert.All(
            await fixture.Context.FrozenDemandStations.AsNoTracking().Where(row => row.DemandId == DemandId).ToArrayAsync(Token),
            row => Assert.Equal(21, row.CatalogRevision));
    }

    /// <summary>
    /// control-server#198 ②：计划带规则版本与绑定集版本、却没有目录修订，受理照旧会冻结版本而不冻结落点，一声不吭。
    /// 现在整笔受理被拒：不写受理行、租约、意图、旅程，也不写任何冻结行；拒绝之后同一个上下文再保存一次，也不能把被拒的
    /// 那次受理带进库（引擎紧接着就会为这条需求写积压原因）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task APlanCarryingVersionsButNoCatalogRevisionIsRefusedWholeAndLeavesNothingBehind()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        (long ruleVersion, long bindingSetVersion) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture, [TransportTaskTypes.WireToGate], [GateBinding]);

        Exception? refused = await Record.ExceptionAsync(() => new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            Demand(), PickupIntent(), Plan(ruleVersion, bindingSetVersion) with { StationCatalogRevision = null }, Token));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        int accepted = await fixture.Context.AcceptedDemands.AsNoTracking().CountAsync(Token);
        int endpoints = await fixture.Context.FrozenDemandStations.AsNoTracking().CountAsync(row => row.DemandId == DemandId, Token);
        DemandTaskTypeStationFreeze? versions = await fixture.Freezes.ReadAsync(DemandId, Token);
        Assert.True(
            refused is JourneyPlanFreezeIncompleteException,
            $"exception: {refused?.GetType().Name ?? "none"}; accepted rows: {accepted}; versions frozen: {versions is not null}; endpoints frozen: {endpoints}");
        Assert.Equal(0, accepted);
        Assert.Equal(0, endpoints);
        Assert.Null(versions);
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.OrderIntents.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.VehicleDispatchLeases.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// control-server#198 ②：三者都没有的计划是 #160 之前受理的旧形状，照旧受理，什么也不冻结——新检查只拒「带版本缺目录修订」。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task APlanCarryingNoneOfTheVersionsNorACatalogRevisionIsAcceptedAsBefore()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();

        await new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            Demand(), PickupIntent(), Plan(null, null), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(DemandId, (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(Token)).DemandId);
        Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Null(await fixture.Freezes.ReadAsync(DemandId, Token));
        Assert.Empty(await fixture.Context.FrozenDemandStations.AsNoTracking().Where(row => row.DemandId == DemandId).ToArrayAsync(Token));
    }

    /// <summary>
    /// control-server#198 ②：受理层接住这种拒绝，报成这条需求自己的结果，不把异常抛出去。抛出去就会冒出
    /// <c>JourneyRuntimeEngine.ExecuteOnceAsync</c>：同一条需求每轮都被选中、每轮都抛，这一辆车后面的车和轮末汇总都走不到
    /// （#188 审查第 2 条那种整轮停摆）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task IntakeReportsAPlanWithoutACatalogRevisionAsThisDemandsOutcomeInsteadOfThrowing()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        (long ruleVersion, long bindingSetVersion) = await BoundFixedTaskStationResolverTests.ActivateAsync(
            fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        DemandIntakeService intake = new(new OneDemandCatalog(Demand()), new WireToGateStore(fixture.Context));

        DemandIntakeOutcome outcome = await intake.AcceptJourneyAsync(
            Demand(), PickupIntent(), Plan(ruleVersion, bindingSetVersion) with { StationCatalogRevision = null }, Token);

        Assert.Equal(DemandIntakeOutcome.JourneyPlanIncomplete, outcome);
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.AcceptedDemands.AsNoTracking().ToArrayAsync(Token));
    }

    /// <remarks>
    /// Carries a catalog revision whenever it carries the versions, as the engine's plans always do
    /// (<c>JourneyPlanBuilder.CreatePlan</c>); control-server#198 refuses a plan that has the versions without it.
    /// </remarks>
    private static JourneyExecutionPlan Plan(long? ruleVersion, long? bindingSetVersion) => new(
        "AGV-1",
        "BROKERX-0001",
        1,
        25,
        "MAP-25",
        "MAP-25-WIRE_TO_GATE",
        "MAPCAT-1",
        "N1-1",
        12,
        "关卡",
        210,
        2,
        [1, 2],
        "operation-session",
        "pickup-leg",
        $"W2G-{DemandId}-PICKUP-1",
        "gate-leg",
        $"W2G-{DemandId}-GATE-1",
        1,
        Now,
        TaskTypeStationRuleVersion: ruleVersion,
        TaskTypeStationBindingSetVersion: bindingSetVersion,
        StationCatalogRevision: ruleVersion is null && bindingSetVersion is null ? null : 20);

    private static AcceptedDemandSnapshot Demand() => new(
        DemandId,
        "SUBLOT-001|WIRE_TO_GATE",
        7,
        "11111111-1111-4111-8111-111111111111",
        21,
        Now,
        "SERIES-1",
        TransportTaskTypes.WireToGate,
        "SUBLOT-001",
        1,
        Now.AddMinutes(-10),
        Now.AddMinutes(-9),
        "TRACE-1",
        "COMMIT-1",
        new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now, "PDFN5×6-8L(12R)"));

    private static OrderIntent PickupIntent() => new(
        "pickup-leg",
        DemandId,
        $"W2G-{DemandId}-PICKUP-1",
        "TO_PICKUP",
        "N1-1",
        Now,
        "BROKERX-0001",
        25,
        12,
        1,
        1);

    /// <summary>A MesIngest catalog holding one unchanged demand, so intake reaches the store.</summary>
    private sealed class OneDemandCatalog(AcceptedDemandSnapshot demand) : IMesIngestCatalog
    {
        public Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DemandCatalogSnapshot(demand.HistoryEpoch, demand.CatalogRevision, [demand]));

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken) =>
            Task.FromResult<AcceptedDemandSnapshot?>(demand.DemandId == demandId ? demand : null);
    }
}
