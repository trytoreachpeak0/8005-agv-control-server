using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
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

    // The area-named machine stations on the fixture's map, the ones the admission seed is made of.
    private static readonly string[] AreaStations = ["N1-1", "N1-2_N1-3"];

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
    /// REQ-0335: an unbound same-direction task type is refused as <c>TASK_TYPE_BINDING_MISSING</c> and stops only
    /// itself. Its demand is the older one, so it is scored first; the WIRE_TO_GATE demand in the same round is still
    /// accepted and the vehicle goes to its pickup.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [MemberData(nameof(SameDirectionTaskTypes))]
    public async Task AnUnboundSameDirectionTaskTypeStopsOnlyItselfAndWireToGateIsAcceptedInTheSameRound(string taskType)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AllowedWorkTypes = [.. TransportTaskTypes.All];
        const string gateDemand = "10000000-0000-4000-8000-000000000002";
        fixture.Catalog.Set(
            SameDirection(fixture, taskType),
            fixture.Demand(gateDemand, "SUBLOT-002", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.BoxCounts.Set("SUBLOT-002", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(DispatchReasonCodes.TaskTypeBindingMissing, (await fixture.BacklogAsync(DemandId)).ReasonCode);
        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(gateDemand)).ReasonCode);
        Assert.Equal(gateDemand, (await fixture.RuntimeAsync()).DemandId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// The station admission seed pairs every area-named machine station with all six task types once the four are
    /// executable: the AREA machine end is where each of them is admitted.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task TheAdmissionSeedPairsEveryAreaStationWithAllSixTaskTypes()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(Token);

        StationTaskTypeAdmissionRow[] seeded = await fixture.Context.StationTaskTypeAdmissions.AsNoTracking()
            .ToArrayAsync(Token);
        Assert.Equal(
            [.. AreaStations
                .SelectMany(station => TransportTaskTypes.All.Select(taskType => (station, taskType)))
                .Order()],
            seeded.Select(row => (row.StationId, row.TaskType)).Order().ToArray());
    }

    /// <summary>
    /// The drift this ticket's version bump exists for (docs/defects/20260915-admission-policy-drift-halts-runtime.md):
    /// a store whose version 2 holds the seed the previous build bound -- each area station with WIRE_TO_GATE and
    /// STAGING_TO_WIRE only -- read by this build under the same version 2 is drift, and no demand is taken on. Under
    /// version 3 the new seed binds and the demand is accepted.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task ThePreviousBuildsSeedUnderTheSameVersionIsDriftAndTheRaisedVersionIsNot()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await BindPreviousBuildsSeedAsync(fixture, version: 2);
        fixture.Options.AdmissionPolicyVersion = 2;
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(AdmissionPolicyDriftCriterion.Reason, (await fixture.BacklogAsync(DemandId)).ReasonCode);
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);

        fixture.Options.AdmissionPolicyVersion = 3;
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(DemandId)).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// Rolling back the package does not roll back the store. Once this build has bound version 3, the build before it
    /// can bind its seed under neither version 2 (the store refuses a version moving backwards) nor version 3 (bound to
    /// other content): the engine reads either refusal as drift and takes on no demand. Only a version above the
    /// store's binds it, and moving forward again then needs one above that. scripts/parallel/README.md tells the
    /// operator this; the ticket had expected version 2 to be accepted on rollback, and it is not.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AfterVersionThreeIsBoundTheRolledBackBuildBindsOnlyUnderAHigherVersion()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AdmissionPolicyVersion = 3;
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(3, (await fixture.Context.AdmissionPolicyState.AsNoTracking().SingleAsync(Token)).Version);

        BusinessIdentityConflictException backwards = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => BindPreviousBuildsSeedAsync(fixture, version: 2));
        Assert.Equal("Admission policy version cannot move backwards.", backwards.Message);
        BusinessIdentityConflictException sameVersion = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => BindPreviousBuildsSeedAsync(fixture, version: 3));
        Assert.Equal(
            "Admission policy version is already bound to different content or deployment identity.",
            sameVersion.Message);

        await BindPreviousBuildsSeedAsync(fixture, version: 4);
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            await fixture.Context.StationTaskTypeAdmissions.AsNoTracking()
                .Select(row => row.TaskType).Distinct().OrderBy(taskType => taskType).ToArrayAsync(Token));

        // Forward again: this build under the rollback's version 4 is drift, under 5 it binds. Installing a package
        // restarts the service, so the engine starts over with nothing tracked from before the rollback.
        await fixture.RecreateEngineAsync();
        fixture.Options.AdmissionPolicyVersion = 4;
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(AdmissionPolicyDriftCriterion.Reason, (await fixture.BacklogAsync(DemandId)).ReasonCode);

        fixture.Options.AdmissionPolicyVersion = 5;
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(DemandId)).ReasonCode);
    }

    /// <summary>
    /// Binds what the build before batch 10 seeded -- the fixture map's two area stations, each with WIRE_TO_GATE and
    /// STAGING_TO_WIRE -- under <paramref name="version"/>, through the store the engine binds with.
    /// </summary>
    private static async Task BindPreviousBuildsSeedAsync(RuntimeFixture fixture, long version)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        await new WireToGateStore(context).ApplyAdmissionPolicyAsync(PreviousBuildsSeed(fixture, version), Token);
    }

    private static AdmissionPolicyDefinition PreviousBuildsSeed(RuntimeFixture fixture, long version) => new(
        version,
        fixture.Options.AdmissionPolicyDeploymentId,
        [
            .. AreaStations.SelectMany(station => new[]
            {
                new StationTaskTypeAdmission(station, TransportTaskTypes.StagingToWire),
                new StationTaskTypeAdmission(station, TransportTaskTypes.WireToGate),
            }),
        ],
        Now);

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
