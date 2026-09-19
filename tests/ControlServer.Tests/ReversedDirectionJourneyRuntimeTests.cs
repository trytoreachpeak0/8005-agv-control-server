using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// STAGING_TO_WIRE driven through the engine (control-server#163, <c>FP-IS-11</c> server half): accepted under the
/// task type's rule, loaded at the dispatch staging station, unloaded at the AREA machine station, with the station
/// task type admission carried and frozen on the unload (scope specification 21.2 item 2).
/// </summary>
public sealed class ReversedDirectionJourneyRuntimeTests
{
    // The demand id the fixture's arrival helpers derive their upper ids from.
    private const string ReverseDemand = "10000000-0000-4000-8000-000000000001";

    private const int StagingStationRiotId = 305;

    private const string StagingStationName = "派工待送取货";

    private static readonly TaskTypeStationBinding StagingBinding =
        new(TransportTaskTypes.StagingToWire, StagingStationRiotId, StagingStationName, "SITE-CHECK-STAGING");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// CV-REVERSED-DIRECTION-JOURNEY. DERIVE_DIRECTION_FROM_TASK_TYPE_RULE: the plan snapshot the vehicle is sent
    /// first runs TO_PICKUP at the staging station bound to STAGING_TO_WIRE and TO_DROPOFF at the demand's AREA
    /// machine station, and the worklist snapshot that follows at the staging station is a PICKUP.
    /// NEVER_SWAP_ORIGIN_AND_DESTINATION: the journey freezes the staging station as its pickup and the machine as
    /// its drop-off, and its first move order goes to the staging station.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [Trait("ProtocolVector", "CV-REVERSED-DIRECTION-JOURNEY")]
    public async Task AStagingToWireJourneyIsPlannedFromTheStagingStationToTheAreaMachineAndNeverSwapped()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(ReverseDemand)).ReasonCode);
        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();

        // DERIVE_DIRECTION_FROM_TASK_TYPE_RULE
        JsonElement[] sent = [.. fixture.Peer.Lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())];
        int planAt = Array.FindIndex(sent, root => Type(root) == "UpcomingStopPlanSnapshot");
        int worklistAt = Array.FindIndex(sent, root => Type(root) == "CurrentStopWorklistSnapshot");
        Assert.InRange(planAt, 0, int.MaxValue);
        Assert.True(worklistAt > planAt, "The worklist snapshot must follow the plan snapshot.");
        Assert.Equal(
            [("TO_PICKUP", StagingStationName, true), ("TO_DROPOFF", "N1-1", true)],
            sent[planAt].GetProperty("payload").GetProperty("legs").EnumerateArray().Select(leg => (
                leg.GetProperty("legType").GetString(),
                leg.GetProperty("stationId").GetString(),
                leg.GetProperty("publicStationFunction").ValueKind == JsonValueKind.Null)));
        JsonElement worklist = sent[worklistAt].GetProperty("payload");
        Assert.Equal(StagingStationName, worklist.GetProperty("stationId").GetString());
        Assert.All(
            worklist.GetProperty("items").EnumerateArray(),
            item => Assert.Equal("PICKUP", item.GetProperty("stopRole").GetString()));

        // NEVER_SWAP_ORIGIN_AND_DESTINATION
        Assert.Equal(
            (StagingStationRiotId, StagingStationName, 12, "N1-1"),
            (runtime.PickupStationRiotId, runtime.PickupStationId, runtime.GateStationRiotId, runtime.GateStationId));
        IReadOnlyList<FrozenStationFact> frozen = await new CatalogAvailabilityStore(fixture.Context)
            .ReadFrozenStationsAsync(ReverseDemand, Token);
        Assert.Equal(
            [
                new FrozenStationFact(FrozenStationRole.Pickup, 25, StagingStationRiotId, StagingStationName),
                new FrozenStationFact(FrozenStationRole.Dropoff, 25, 12, "N1-1"),
            ],
            frozen.OrderBy(station => station.Role));
        OrderIntentRow pickup = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.DemandId == ReverseDemand && row.Purpose == "TO_PICKUP", Token);
        Assert.Equal(StagingStationRiotId, pickup.DestinationStationId);
    }

    /// <summary>
    /// I6 overturned on the engine side: the sublot entry at the staging station is admitted against the AREA
    /// machine station, the load carries no admission identity, and the unload at the machine carries and freezes
    /// it. The journey completes.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AStagingToWireJourneyFreezesItsAdmissionOnTheUnloadAtTheAreaMachineAndCompletes()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        JourneyRuntimeRow completed = await RunReverseToCompletionAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.Completed, completed.Stage);
        AdmissionDecisionSnapshotRow decision = Assert.Single(
            await fixture.Context.AdmissionDecisionSnapshots.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            (completed.UnloadSlotOperationAttemptId, "N1-1", TransportTaskTypes.StagingToWire, true),
            (decision.SlotOperationAttemptId, decision.StationId, decision.TaskType, decision.Allowed));
    }

    /// <summary>
    /// The recovery path's admission check looks at the AREA machine too. A durable sublot entry at the staging
    /// station stands -- the machine admits STAGING_TO_WIRE -- so it holds the stop against a cancellation before
    /// any sublot, exactly as a WIRE_TO_GATE entry does at its pickup. Judged against the staging station, which
    /// admits nothing, the entry would be discounted and the cancellation authorized.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ADurableEntryAtTheStagingStationHoldsTheStopAgainstACancellationBeforeAnySublot()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        JourneyRuntimeRow waiting = await fixture.AdvanceToSublotWaitAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        await fixture.ProveSlotDoorsClosedAsync();

        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build());
        OnboardConnectionState state = new()
        {
            AgvId = fixture.Options.AgvId,
            SessionGeneration = 1,
            CapabilityRevision = 1,
            SafetyRevision = 7,
            Readiness = SessionReadiness.Ready,
        };
        await processor.ProcessAsync(
            BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "SublotSubmitted", 1, new
            {
                operationSessionId = waiting.OperationSessionId,
                stationId = waiting.PickupStationId,
                worklistRevision = waiting.WorklistRevision,
                sublot = "SUBLOT-001",
                entryMethod = "SCANNER",
                @operator = BeforeSublotOperator(fixture),
            }),
            state,
            Token);

        string answer = await processor.ProcessAsync(
            BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", 1, new
            {
                cancellationId = "c1630000-0000-4000-8000-000000000001",
                demandId = ReverseDemand,
                slotOperationAttemptId = (string?)null,
                @operator = BeforeSublotOperator(fixture),
                reason = "Nothing to load at this stop.",
            }),
            state,
            Token);

        using JsonDocument document = JsonDocument.Parse(answer.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("LoadCancellationAuthorization", Type(document.RootElement));
        Assert.Equal("REJECTED", document.RootElement.GetProperty("payload").GetProperty("decision").GetString());
        Assert.Empty(await fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// The admission is decided and frozen at the machine, so it is asked there: a machine that no longer admits
    /// STAGING_TO_WIRE when the vehicle arrives holds the stop under TASK_TYPE_NOT_ALLOWED_AT_STATION -- no unload
    /// command, nothing frozen -- and the stop goes on once the machine admits it again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AMachineThatNoLongerAdmitsTheTaskTypeHoldsTheUnloadUntilItDoesAgain()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        StationTaskTypeAdmissionRow[] revoked = await RevokeStagingToWireAsync(fixture);

        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION"),
            (held.Stage, held.BlockReasonCode));
        Assert.False(await fixture.Context.StationOperations.AnyAsync(
            row => row.OperationType == SlotOperationType.Unload, Token));
        Assert.Empty(await fixture.Context.AdmissionDecisionSnapshots.AsNoTracking().ToArrayAsync(Token));

        fixture.Context.StationTaskTypeAdmissions.AddRange(revoked);
        await fixture.Context.SaveChangesAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// A restart between preparing the unload and saving the stage re-enters the arrival with the unload already
    /// prepared and its admission frozen. The frozen decision is what stands (ADR-cross-0050/0051): even with the
    /// machine no longer admitting the task type, the journey goes on to await the unload's result on the command
    /// it already has, instead of returning early at the admission check.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AfterARestartAPreparedUnloadGoesOnUnderItsFrozenAdmission()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow unloading = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, unloading.Stage);

        // The stage save is lost to a restart, and meanwhile the machine stops admitting the task type.
        unloading.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        await fixture.Context.SaveChangesAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        await RevokeStagingToWireAsync(fixture);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow resumed = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingUnloadResult, null), (resumed.Stage, resumed.BlockReasonCode));
        Assert.Single(await fixture.Context.StationOperations.AsNoTracking()
            .Where(row => row.OperationType == SlotOperationType.Unload).ToArrayAsync(Token));
        Assert.Single(await fixture.Context.AdmissionDecisionSnapshots.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// Pairing every area-named station with STAGING_TO_WIRE as well changes the admission seed's content, and a
    /// deployment whose store holds version 1 -- the version the WIRE_TO_GATE-only seed was bound to -- would read
    /// the new content under the same version as drift and take on no further demand until someone raised it by
    /// hand (docs/defects/20260915-admission-policy-drift-halts-runtime.md). The shipped configuration raises it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void TheShippedAdmissionPolicyVersionMovesPastTheOneTheWireToGateOnlySeedWasBoundTo()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ControlServer.sln")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        using JsonDocument settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root.FullName, "src", "ControlServer.Host", "appsettings.json")));

        long version = settings.RootElement.GetProperty("JourneyRuntime").GetProperty("admissionPolicyVersion").GetInt64();

        Assert.True(version >= 2, $"admissionPolicyVersion is {version}; the WIRE_TO_GATE-only seed was bound to 1.");
    }

    /// <summary>
    /// The runtime's factory rules and map 25 with both WIRE_TO_GATE (gate 210) and STAGING_TO_WIRE (staging
    /// station 305) bound and active, the staging station on the map, and both task types allowed.
    /// </summary>
    private static async Task<RuntimeFixture> WithStagingToWireBoundAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AllowedWorkTypes = [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire];
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, TaskTypeStationRuntimeSeed.GateStationName),
            new RiotMapStation(300, "等待点"),
            new RiotMapStation(StagingStationRiotId, StagingStationName));
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now,
            requiredTaskTypes: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings: [TaskTypeStationRuntimeSeed.GateBinding, StagingBinding]);
        return fixture;
    }

    /// <summary>Carries a reverse journey onto its second leg and into arrival at the AREA machine station.</summary>
    private static async Task<JourneyRuntimeRow> ArriveAtTheMachineAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        fixture.Riot.SetSuccessfulArrival("TO_GATE", runtime.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.GateStationRiotId };
        return runtime;
    }

    private static async Task<JourneyRuntimeRow> RunReverseToCompletionAsync(RuntimeFixture fixture)
    {
        await ArriveAtTheMachineAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await fixture.Engine.ExecuteOnceAsync(Token);
        return await fixture.RuntimeAsync();
    }

    /// <summary>
    /// Takes STAGING_TO_WIRE out of the station admissions the way a policy change does, and returns the rows so a
    /// test can put them back. The policy's version and hash stay, so the engine's per-round seed leaves it alone.
    /// </summary>
    private static async Task<StationTaskTypeAdmissionRow[]> RevokeStagingToWireAsync(RuntimeFixture fixture)
    {
        StationTaskTypeAdmissionRow[] rows = await fixture.Context.StationTaskTypeAdmissions
            .Where(row => row.TaskType == TransportTaskTypes.StagingToWire)
            .ToArrayAsync(Token);
        fixture.Context.StationTaskTypeAdmissions.RemoveRange(rows);
        await fixture.Context.SaveChangesAsync(Token);
        return
        [
            .. rows.Select(row => new StationTaskTypeAdmissionRow
            {
                StationId = row.StationId,
                TaskType = row.TaskType,
                PolicyVersion = row.PolicyVersion,
            }),
        ];
    }

    private static AcceptedDemandSnapshot Reverse(
        RuntimeFixture fixture,
        string demandId,
        string sublot,
        string area = "N1-1") =>
        fixture.Demand(demandId, sublot, Now.AddMinutes(-10), area) with
        {
            WorkType = TransportTaskTypes.StagingToWire,
            TransportDemandKey = $"{sublot}|{TransportTaskTypes.StagingToWire}",
        };

    private static string? Type(JsonElement root) => root.GetProperty("messageType").GetString();
}
