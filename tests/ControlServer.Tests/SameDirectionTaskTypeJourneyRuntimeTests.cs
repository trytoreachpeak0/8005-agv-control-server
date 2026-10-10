using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Batch 10 (control-server#545, scope specification 5.3, REQ-0184): the four same-direction task types run like
/// WIRE_TO_GATE -- loaded at the demand's AREA machine station, unloaded at the station bound to the task type.
/// </summary>
public sealed class SameDirectionTaskTypeJourneyRuntimeTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private const int BoundStationRiotId = 401;

    private const string BoundStationName = "同向固定站";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> SameDirectionTaskTypes =>
    [
        TransportTaskTypes.DieToWireStaging,
        TransportTaskTypes.DieToOven,
        TransportTaskTypes.WireToOptical,
        TransportTaskTypes.WireToNitrogen,
    ];

    /// <summary>
    /// Bound, the demand is accepted with the rule and binding set versions it was judged under frozen; the journey
    /// loads at the AREA machine station into the slot group the demand's AREA is assigned (REAR here, slots 5-8),
    /// unloads at the bound station -- never the other way round -- and completes.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [MemberData(nameof(SameDirectionTaskTypes))]
    public async Task ABoundSameDirectionTaskTypeLoadsAtTheAreaMachineIntoItsGroupAndUnloadsAtTheBoundStation(string taskType)
    {
        await using RuntimeFixture fixture = await WithBoundAsync(taskType);
        (long ruleVersion, long bindingSetVersion) = await ActiveVersionsAsync(fixture);
        await fixture.ImportAreaAssignmentsAsync(
            [.. RuntimeFixture.DefaultAssignedAreas.Select(area => new AreaAssignment(area, fixture.Options.DispatchZone, "REAR"))]);
        fixture.Catalog.Set(SameDirection(fixture, taskType));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(DemandId)).ReasonCode);

        DemandTaskTypeStationFreeze? freeze = await new DemandTaskTypeStationFreezeStore(fixture.Context)
            .ReadAsync(DemandId, Token);
        Assert.Equal((ruleVersion, 25, bindingSetVersion), (freeze!.RuleVersion, freeze.MapId, freeze.BindingSetVersion));
        Assert.Equal(
            SlotOperationType.Load,
            await new WireToGateStore(fixture.Context).AreaEndOperationAsync(DemandId, taskType, Token));

        JourneyRuntimeRow runtime = await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(
            (12, "N1-1", BoundStationRiotId, BoundStationName),
            (runtime.PickupStationRiotId, runtime.PickupStationId, runtime.GateStationRiotId, runtime.GateStationId));
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        Assert.All(JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!, slot => Assert.InRange(slot, 5, 8));

        fixture.Riot.SetSuccessfulArrival("TO_GATE", BoundStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = BoundStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(Token);
        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        OrderIntentRow drop = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.DemandId == DemandId && row.Purpose == "TO_GATE", Token);
        Assert.Equal(BoundStationRiotId, drop.DestinationStationId);
    }

    /// <summary>
    /// The runtime's factory rules and map 25 with WIRE_TO_GATE (gate 210) and <paramref name="taskType"/> (station 401)
    /// bound and active, both on the map, and both allowed.
    /// </summary>
    private static async Task<RuntimeFixture> WithBoundAsync(string taskType)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AllowedWorkTypes = [TransportTaskTypes.WireToGate, taskType];
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, TaskTypeStationRuntimeSeed.GateStationName),
            new RiotMapStation(300, "等待点"),
            new RiotMapStation(BoundStationRiotId, BoundStationName));
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now,
            requiredTaskTypes: [TransportTaskTypes.WireToGate, taskType],
            bindings: [TaskTypeStationRuntimeSeed.GateBinding, Binding(taskType)]);
        return fixture;
    }

    private static TaskTypeStationBinding Binding(string taskType) =>
        new(taskType, BoundStationRiotId, BoundStationName, $"SITE-CHECK-{taskType}");

    private static async Task<(long RuleVersion, long BindingSetVersion)> ActiveVersionsAsync(RuntimeFixture fixture)
    {
        TaskTypeStationBindingSetVersion active = (await TaskTypeStationRuntimeSeed.Access(fixture.Context).Bindings
            .ReadActiveAsync(25, Token))!;
        return (active.RuleVersion, active.Version);
    }

    private static AcceptedDemandSnapshot SameDirection(RuntimeFixture fixture, string taskType, string area = "N1-1") =>
        fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10), area) with
        {
            WorkType = taskType,
            TransportDemandKey = $"SUBLOT-001|{taskType}",
        };
}
