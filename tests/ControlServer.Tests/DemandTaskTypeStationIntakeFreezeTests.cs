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
        TaskTypeStationBindingSetVersion: bindingSetVersion);

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
}
