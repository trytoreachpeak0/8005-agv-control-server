using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.Dispatch;
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
    /// control-server#198 c-2: the second leg of a reverse journey -- from the staging station, where it has loaded, to
    /// the AREA machine -- is a move order that does not exist yet, so it waits while STAGING_TO_WIRE is held on the
    /// Map (REQ-0344's last sentence, REQ-0345): no order, the journey under TASK_TYPE_HELD before departure, the
    /// pickup order untouched. Released, the next round creates the leg to the frozen drop-off.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AHeldStagingToWireGetsNoSecondLegUntilTheHoldIsReleased()
    {
        await using RuntimeFixture fixture = await AcceptedReverseAsync();
        TaskTypeStationHold hold = await RaiseHoldAsync(fixture, TransportTaskTypes.StagingToWire);
        string pickupBefore = await fixture.IntentStatusAsync("TO_PICKUP");

        JourneyRuntimeRow held = await fixture.AdvanceToGateArrivalAsync();

        Assert.Equal(
            (JourneyRuntimeStage.AwaitingDepartureSafety, DispatchReasonCodes.TaskTypeHeld),
            (held.Stage, held.BlockReasonCode));
        Assert.False(await fixture.Context.OrderIntents.AnyAsync(row => row.UpperId == held.GateUpperId, Token));
        Assert.Equal(pickupBefore, await fixture.IntentStatusAsync("TO_PICKUP"));

        await TaskTypeStationRuntimeSeed.Access(fixture.Context).Holds.ReleaseAsync(hold.HoldId, "test", Now, Token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow released = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, released.Stage);
        FrozenStationFact dropoff = Assert.Single(
            await new CatalogAvailabilityStore(fixture.Context).ReadFrozenStationsAsync(ReverseDemand, Token),
            station => station.Role == FrozenStationRole.Dropoff);
        Assert.Equal(
            dropoff.StationId,
            (await fixture.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == released.GateUpperId, Token))
                .DestinationStationId);
    }

    /// <summary>
    /// control-server#198 c-2: a hold is on the demand's own task type. Holding WIRE_TO_GATE does not reach a
    /// STAGING_TO_WIRE journey: its second leg is created as usual.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task HoldingWireToGateDoesNotHoldAStagingToWireSecondLeg()
    {
        await using RuntimeFixture fixture = await AcceptedReverseAsync();
        await RaiseHoldAsync(fixture, TransportTaskTypes.WireToGate);

        JourneyRuntimeRow runtime = await fixture.AdvanceToGateArrivalAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.NotEqual(DispatchReasonCodes.TaskTypeHeld, runtime.BlockReasonCode);
        Assert.True(await fixture.Context.OrderIntents.AnyAsync(row => row.UpperId == runtime.GateUpperId, Token));
    }

    /// <summary>
    /// control-server#198 item 8, decided by the user on 2026-09-19: a reverse journey held at the AREA machine because
    /// the machine stopped admitting STAGING_TO_WIRE has the goods on board and was held there silently for ever. Past
    /// the threshold -- ten minutes by default -- it is blocked under its own code, shows on the dashboard's blocked
    /// journeys, and is Blocked, which is the stage an administrator opens a recovery session on. The count is from the
    /// first round that held it, and a later round neither restarts it nor escalates it twice. The block keeps the start
    /// time of the first hold: the dashboard's own ladder (shift leader at 10 minutes, maintenance at 30) is measured from
    /// it, and restarting it at the escalation would send the card back to its lowest tier.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAdmissionStillRevokedPastTheThresholdBlocksTheJourneyForManualRecovery()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), fixture.Options.AreaEndAdmissionRevokedTimeout);
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        // A live vehicle keeps heartbeating while it waits; without it the arrival stops being trusted.
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
        using JsonDocument dashboard = JsonDocument.Parse(JsonSerializer.Serialize(
            await new BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions.Default, fixture.Clock)
                .ReadAsync(fixture.Context, Token)));
        JsonElement card = Assert.Single(dashboard.RootElement.GetProperty("journeys").EnumerateArray());
        Assert.Equal(
            (ReverseDemand, "Blocked", "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", "N1-1", 600L, "ShiftLeader"),
            (card.GetProperty("demandId").GetString(), card.GetProperty("stage").GetString(),
                card.GetProperty("blockReasonCode").GetString(), card.GetProperty("stationId").GetString(),
                card.GetProperty("blockedSeconds").GetInt64(), card.GetProperty("escalationLevel").GetString()));
        Assert.False(await fixture.Context.StationOperations.AnyAsync(
            row => row.OperationType == SlotOperationType.Unload, Token));

        // The same round again: still Blocked under the same code, from the same moment.
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        // A live vehicle keeps heartbeating while it waits; without it the arrival stops being trusted.
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
    }

    /// <summary>
    /// control-server#198 item 8: the count is from the first hold, and a session lost and regained on the way does not
    /// start it again. While the session is down the stop is still held for the same reason, so the hold is kept rather
    /// than overwritten by ONBOARD_SESSION_NOT_READY, the way a Blocked journey keeps the recovery it waits on. Restarted
    /// at every reconnect, a link that drops more often than the threshold would keep the loaded vehicle waiting for ever.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ASessionLostAndRegainedWhileHeldDoesNotRestartTheCount()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.DropOnboardSessionAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));

        await fixture.RestoreSessionReadyAsync();
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
    }

    /// <summary>
    /// control-server#228 (control-server#198 review S1): the onboard process hangs while the stop is held, so its session
    /// stays Ready but nothing is heard from it any more, and every later round finds the arrival untrusted. The count
    /// is still judged every round, ahead of the arrival check: past the threshold the journey is Blocked under the
    /// admission timeout from the first hold, instead of standing at AwaitingGateArrival for ever.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AHeldStopWhoseOnboardStopsHeartbeatingIsStillEscalatedOnTime()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();

        // Nothing is heard from the peer from here on: the arrival is no longer trusted, the session is still Ready.
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
        Assert.Equal(SessionReadiness.Ready, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
        Assert.False(await fixture.Context.StationOperations.AnyAsync(
            row => row.OperationType == SlotOperationType.Unload, Token));

        // A later round, still silent: escalated once, the block and its start stay as they are.
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
    }

    /// <summary>
    /// control-server#228 (control-server#198 review S1): RIoT reports the order the vehicle rode to the machine on as
    /// FAILED partway through the wait, so the journey names VEHICLE_ORDER_FAILED for a round and then comes back to the
    /// admission hold. Overwriting the block code does not restart the count: the threshold is measured from the first
    /// hold, and the escalated block carries that start for the dashboard's ladder.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnOrderFailureInTheMiddleOfTheWaitDoesNotRestartTheCount()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow arrived = await ArriveAtTheMachineAsync(fixture);
        await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await fixture.HearFromPeerAsync();
        fixture.Riot.FailOrder(arrived.GateUpperId);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "VEHICLE_ORDER_FAILED"),
            ((await StateAsync(fixture)).Stage, (await StateAsync(fixture)).Code));

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await fixture.HearFromPeerAsync();
        fixture.Riot.SetSuccessfulArrival("TO_GATE", arrived.GateStationRiotId);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION"),
            ((await StateAsync(fixture)).Stage, (await StateAsync(fixture)).Code));

        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));

        // The order fails again after the escalation: nothing writes its code over the escalated block.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();
        fixture.Riot.FailOrder(arrived.GateUpperId);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
    }

    /// <summary>
    /// control-server#228: the order failing in the very round the wait runs out does not win it. The escalation is judged
    /// first in the round and returns, so the order failure check never runs in it and the block is the escalated hold.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnOrderFailingInTheRoundTheWaitRunsOutIsEscalatedNotOverwritten()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow arrived = await ArriveAtTheMachineAsync(fixture);
        await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        await fixture.HearFromPeerAsync();
        fixture.Riot.FailOrder(arrived.GateUpperId);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
    }

    /// <summary>
    /// control-server#228: the start of the wait is stored with the first hold, in the same save as the hold itself, and a
    /// server restart followed by the vehicle reconnecting on a new session generation does not reset it -- the first
    /// round after both still counts from the first hold.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ARestartAndAReconnectWhileHeldDoNotRestartTheCount()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();
        Assert.Equal(
            ("TASK_TYPE_NOT_ALLOWED_AT_STATION", RevokedSinceText(heldSince)),
            ((await StateAsync(fixture)).Code, await StoredRevokedSinceAsync(fixture)));

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.RecreateEngineAsync();
        await fixture.AdvanceSessionAsync(2);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
        Assert.Equal(RevokedSinceText(heldSince), await StoredRevokedSinceAsync(fixture));

        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));
    }

    /// <summary>
    /// control-server#228: admitted again, the stop leaves AwaitingGateArrival and the stored start of the wait goes with
    /// it -- the one way it is ever cleared. A journey cannot come back to AwaitingGateArrival, so a later hold is always a
    /// new journey's, and starts from nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAdmissionRestoredClearsTheStoredStartOfTheWait()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        StationTaskTypeAdmissionRow[] revoked = await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(RevokedSinceText(heldSince), await StoredRevokedSinceAsync(fixture));

        fixture.Context.StationTaskTypeAdmissions.AddRange(revoked);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await StateAsync(fixture)).Stage);
        Assert.Null(await StoredRevokedSinceAsync(fixture));
    }

    /// <summary>
    /// control-server#198 item 8: admitted again inside the threshold, the journey goes on as it always did -- the unload
    /// is commanded and frozen under the admission -- and the count is gone with the hold. Up to the threshold nothing
    /// is escalated: a second short of it the journey is still waiting at the machine.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAdmissionRestoredInsideTheThresholdGoesOnAndClearsTheCount()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        StationTaskTypeAdmissionRow[] revoked = await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        // A live vehicle keeps heartbeating while it waits; without it the arrival stops being trusted.
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION", (DateTimeOffset?)heldSince),
            await StateAsync(fixture));

        fixture.Context.StationTaskTypeAdmissions.AddRange(revoked);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            (JourneyRuntimeStage.AwaitingUnloadResult, (string?)null, (DateTimeOffset?)null),
            await StateAsync(fixture));
        Assert.Single(await fixture.Context.AdmissionDecisionSnapshots.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// control-server#198 item 8: the threshold is configuration, and escalating touches no move order. The order the
    /// vehicle rode to the machine on is left exactly as it was -- not re-sent, not re-targeted, not cancelled -- and
    /// no new order is created.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheThresholdIsConfiguredAndEscalatingLeavesTheOrderToTheMachineAlone()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Options.AreaEndAdmissionRevokedTimeout = TimeSpan.FromMinutes(3);
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow arrived = await ArriveAtTheMachineAsync(fixture);
        OrderIntentRow[] ordersBefore = await OrdersAsync(fixture);
        int createsBefore = fixture.Riot.TotalCreateCount;
        await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        // A live vehicle keeps heartbeating while it waits; without it the arrival stops being trusted.
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.Blocked, (await StateAsync(fixture)).Stage);
        OrderIntentRow[] ordersAfter = await OrdersAsync(fixture);
        Assert.Equal(
            ordersBefore.Select(row => (row.UpperId, row.Status, row.DestinationStationId, row.OrderId)),
            ordersAfter.Select(row => (row.UpperId, row.Status, row.DestinationStationId, row.OrderId)));
        Assert.Contains(ordersAfter, row => row.UpperId == arrived.GateUpperId);
        Assert.Equal(createsBefore, fixture.Riot.TotalCreateCount);
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
    /// control-server#228, the crash point between admitting the stop and saving it: the admission returned, the unload
    /// was prepared and frozen, and the save that moves the stage on and clears the wait's start was lost. Restarted past
    /// the threshold, the row still says AwaitingGateArrival with the start of a wait that has in fact ended. The prepared
    /// unload is the proof it ended: the journey goes on to await the unload's result, it is not handed to a person with
    /// an unload command already out.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AfterARestartAPreparedUnloadIsNotEscalatedByAWaitThatHadAlreadyEnded()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await ArriveAtTheMachineAsync(fixture);
        StationTaskTypeAdmissionRow[] revoked = await RevokeStagingToWireAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset heldSince = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        await fixture.HearFromPeerAsync();
        fixture.Context.StationTaskTypeAdmissions.AddRange(revoked);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow unloading = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, unloading.Stage);

        // The stage save is lost to a restart: the row is back where it was before the admission returned.
        unloading.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        unloading.HoldForAreaEndAdmission(heldSince);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.RecreateEngineAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow resumed = await fixture.RuntimeAsync();
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingUnloadResult, (string?)null, (DateTimeOffset?)null),
            (resumed.Stage, resumed.BlockReasonCode, resumed.AreaEndAdmissionRevokedSince));
        Assert.Single(await fixture.Context.StationOperations.AsNoTracking()
            .Where(row => row.OperationType == SlotOperationType.Unload).ToArrayAsync(Token));
    }

    /// <summary>
    /// control-server#228: the start of the wait has no public setter, the way <c>BlockReasonSince</c> has none -- a write
    /// that bypasses the two write points would restart a wait that has not restarted, and it has to be a compile error
    /// rather than a silently wrong start. Held again while it already holds, it keeps the first time and hands it back,
    /// because the hold is written on every round the stop stands there.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheStartOfTheWaitIsOnlyWrittenThroughItsTwoWritePoints()
    {
        System.Reflection.PropertyInfo start = typeof(JourneyRuntimeRow)
            .GetProperty(nameof(JourneyRuntimeRow.AreaEndAdmissionRevokedSince))!;
        Assert.True(
            start.SetMethod is null || start.SetMethod.IsPrivate,
            "The start of the wait must have no public setter.");
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        DateTimeOffset at = Batch7JourneyFixture.Now;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-HOLD", "agv-01", "VK-01", at);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(Token);

        DateTimeOffset first = runtime.HoldForAreaEndAdmission(at);
        DateTimeOffset second = runtime.HoldForAreaEndAdmission(at.AddMinutes(7));

        Assert.Equal((at, at, (DateTimeOffset?)at), (first, second, runtime.AreaEndAdmissionRevokedSince));
        runtime.ReleaseAreaEndAdmissionHold();
        Assert.Null(runtime.AreaEndAdmissionRevokedSince);
        Assert.Equal(at.AddMinutes(9), runtime.HoldForAreaEndAdmission(at.AddMinutes(9)));
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

    private static async Task<(JourneyRuntimeStage Stage, string? Code, DateTimeOffset? Since)> StateAsync(RuntimeFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        return (runtime.Stage, runtime.BlockReasonCode, runtime.BlockReasonSince);
    }

    /// <summary>The stored start of the admission wait, read from the database as SQLite holds it.</summary>
    private static async Task<string?> StoredRevokedSinceAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext read = fixture.OpenConnectionContext();
        return await read.Database
            .SqlQueryRaw<string?>("SELECT AreaEndAdmissionRevokedSince AS \"Value\" FROM JourneyRuntimes")
            .SingleAsync(Token);
    }

    /// <summary>A time the way EF Core's SQLite provider stores a <see cref="DateTimeOffset"/>.</summary>
    private static string RevokedSinceText(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<OrderIntentRow[]> OrdersAsync(RuntimeFixture fixture) =>
        await fixture.Context.OrderIntents.AsNoTracking()
            .Where(row => row.DemandId == ReverseDemand)
            .OrderBy(row => row.UpperId)
            .ToArrayAsync(Token);

    /// <summary>A reverse journey accepted before any hold: a hold in force at acceptance refuses the demand itself.</summary>
    private static async Task<RuntimeFixture> AcceptedReverseAsync()
    {
        RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, ReverseDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<TaskTypeStationHold> RaiseHoldAsync(RuntimeFixture fixture, string taskType)
    {
        TaskTypeStationHold hold = (await TaskTypeStationRuntimeSeed.Access(fixture.Context).Holds.RaiseAsync(
            25, taskType, TaskTypeStationHoldSource.Manual, "TEST_HOLD", "{}", "test", Now, Token)).Hold;
        fixture.Context.ChangeTracker.Clear();
        return hold;
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
