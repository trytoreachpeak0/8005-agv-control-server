using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 尚未建单的腿遵守暂停（control-server#160，REQ-0344 末句、REQ-0345）：关卡腿建单前查冻结需求的
/// <c>Map + TASK_TYPE</c> 是否暂停；暂停则不建单、写精确原因。已建单的 RIoT 订单不改单、不换站、不取消。
/// </summary>
public sealed class TaskTypeHoldGateLegTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 任务类型在本图暂停时，出发安全检查过了也不建关卡腿：旅程写上 <c>TASK_TYPE_HELD</c>、停在出发前，取货单不动；
    /// 解除暂停后下一轮照常建关卡腿、目标仍是冻结的关卡。本票之前受理、没有冻结行的旅程按 <c>WIRE_TO_GATE</c> 判。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHeldTaskTypeGetsNoGateLegUntilTheHoldIsReleased(bool frozen)
    {
        await using RuntimeFixture fixture = await StartedAsync();
        if (!frozen)
        {
            await fixture.Context.Set<ConfigurationConsumerBindingRow>()
                .Where(row => row.ConsumerId == DemandId && row.ObjectKind != GovernedObjectKind.DispatchZoneAreaAssignment)
                .ExecuteDeleteAsync(Token);
            Assert.Null(await new DemandTaskTypeStationFreezeStore(fixture.Context).ReadAsync(DemandId, Token));
        }
        TaskTypeStationHold hold = await RaiseHoldAsync(fixture);
        OrderIntentRow pickupBefore = await PickupAsync(fixture);

        JourneyRuntimeRow held = await fixture.AdvanceToGateArrivalAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, held.Stage);
        Assert.Equal(DispatchReasonCodes.TaskTypeHeld, held.BlockReasonCode);
        Assert.False(await fixture.Context.OrderIntents.AnyAsync(row => row.UpperId == held.GateUpperId, Token));
        Assert.Equal(pickupBefore.Status, (await PickupAsync(fixture)).Status);

        await TaskTypeStationRuntimeSeed.Access(fixture.Context).Holds.ReleaseAsync(hold.HoldId, "test", Now, Token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow released = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, released.Stage);
        Assert.Equal(
            TaskTypeStationRuntimeSeed.GateStationRiotId,
            (await fixture.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == released.GateUpperId, Token))
                .DestinationStationId);
    }

    /// <summary>暂停只管还没建单的腿：关卡腿已经建单之后才暂停，这张单不改、不换站、不取消，旅程照常到站卸货。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AHoldRaisedAfterTheGateLegWasCreatedLeavesThatOrderAlone()
    {
        await using RuntimeFixture fixture = await StartedAsync();
        JourneyRuntimeRow onTheWay = await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, onTheWay.Stage);
        OrderIntentRow gateBefore = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == onTheWay.GateUpperId, Token);
        await RaiseHoldAsync(fixture);

        fixture.Riot.SetSuccessfulArrival("TO_GATE", onTheWay.GateUpperId, TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, runtime.Stage);
        Assert.NotEqual(DispatchReasonCodes.TaskTypeHeld, runtime.BlockReasonCode);
        OrderIntentRow gateAfter = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == onTheWay.GateUpperId, Token);
        Assert.Equal(gateBefore.DestinationStationId, gateAfter.DestinationStationId);
        Assert.Equal(gateBefore.MovementLegId, gateAfter.MovementLegId);
    }

    private static async Task<RuntimeFixture> StartedAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        // Accepted before any hold: a hold in force at acceptance time refuses the demand itself.
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<TaskTypeStationHold> RaiseHoldAsync(RuntimeFixture fixture)
    {
        TaskTypeStationHold hold = await TaskTypeStationRuntimeSeed.Access(fixture.Context).Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "TEST_HOLD", "{}", "test", Now, Token);
        fixture.Context.ChangeTracker.Clear();
        return hold;
    }

    private static Task<OrderIntentRow> PickupAsync(RuntimeFixture fixture) =>
        fixture.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.Purpose == "TO_PICKUP", Token);
}
