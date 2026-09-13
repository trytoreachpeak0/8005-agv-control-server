using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;

namespace ControlServer.Tests;

public sealed class JourneyRuntimeWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] UnknownReasonCodes = ["IO_FACT_UNKNOWN"];

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task ProductionRuntimeResumesOneJourneyThroughTrustedArrivalsAndAtomicCompletion()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            createdAt: Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal(2, runtime.ExpectedBasketCount);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId + 1 };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain("SublotEntryRequested", await fixture.OutboxTypesAsync());

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        // Two plans: the one sent once the pickup order was confirmed and the one sent at the
        // pickup. Their order is ThePlanGoesOutBeforeArrivalAndAgainAfterTheWorklistAtThePickup's.
        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "SublotEntryRequested", "UpcomingStopPlanSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            (await fixture.OutboxTypesAsync()).Order(StringComparer.Ordinal));

        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "SublotSubmitted",
            new
            {
                demandId = runtime.DemandId,
                operationSessionId = runtime.OperationSessionId,
                stationId = runtime.PickupStationId,
                worklistRevision = runtime.WorklistRevision,
                sublot = "SUBLOT-001",
                entryMethod = "SCANNER",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = Now
                }
            });
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        int[]? loadSlots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson);
        Assert.NotNull(loadSlots);
        Assert.Equal([1, 2], loadSlots);
        AdmissionDecisionSnapshotRow admission = await fixture.Context.AdmissionDecisionSnapshots
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(admission.Allowed);
        Assert.Equal(1, admission.AdmissionPolicyVersion);
        Assert.Equal(runtime.PickupStationId, admission.StationId);
        Assert.Equal("WIRE_TO_GATE", admission.TaskType);

        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = Now,
                safetyStateVersion = 7,
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            }, runtime.PreDepartureSafetyCheckMessageId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));

        fixture.Riot.SetSuccessfulArrival("TO_GATE", fixture.Options.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = fixture.Options.GateStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, runtime.Stage);

        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal(DemandExecutionStatus.Succeeded, (await fixture.DemandRowAsync()).Status);
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(2, await fixture.Context.StationOperations.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// REQ-0237 keeps ordinary mis-placement correction to the time before the vehicle leaves the
    /// pickup, and ADR-cross-0054/0055 make that time a server-owned wait after the load commits.
    /// Until 2026-09-13 this server asked for departure safety in the very iteration the load
    /// committed and created the gate order a moment later, so the window did not exist: the G3
    /// FP-IS-02 run pressed 「修正装货」 two seconds after the doors closed and the vehicle had
    /// already been sent away (docs/defects/20260913-no-pre-departure-correction-window.md).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AfterTheLoadCommitsTheVehicleWaitsOutTheStationDepartureWindowBeforeDeparting()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal(fixture.Clock.GetUtcNow(), runtime.StationDepartureWaitStartedAt);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Null(runtime.StationDepartureWaitStartedAt);
        Assert.Contains("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// ADR-cross-0054: a correction takes the vehicle out of the departure wait and stops the clock,
    /// and once every slot it touched is safely closed again the wait starts over from its full
    /// length. An open correction past the original deadline must therefore neither depart the
    /// vehicle nor leave it only the remainder of the old wait.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AnOpenLoadCorrectionHoldsTheVehicleAndTheWaitStartsOverWhenItCloses()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);

        RecoveryWorkflowRow correction = new()
        {
            WorkflowId = "70000000-0000-4000-8000-000000000001",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = runtime.AgvId,
            DemandId = runtime.DemandId,
            SlotOperationAttemptId = load.SlotOperationAttemptId,
            SlotsJson = load.TargetSlotsJson,
            State = RecoveryWorkflowState.AwaitingResult,
            RequestMessageId = "70000000-0000-4000-8000-000000000002",
            RequestContentHash = new string('c', 64),
            CreatedAt = fixture.Clock.GetUtcNow(),
            UpdatedAt = fixture.Clock.GetUtcNow()
        };
        fixture.Context.RecoveryWorkflows.Add(correction);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal("LOAD_CORRECTION_IN_PROGRESS", runtime.BlockReasonCode);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        correction.State = RecoveryWorkflowState.Reconciled;
        correction.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
        Assert.Contains("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task ACompletedDemandStillListedByMesIngestIsNeverOfferedForIntakeAgain()
    {
        // MesIngest publishes MES's own list of open transport demands; our journey completing does
        // not remove the demand from it. Discovery runs again as soon as no unresolved journey
        // remains, so the finished demand was scored as a fresh candidate, and intake met the
        // AcceptedDemands row it had written itself. The store refused that replay -- rightly: the
        // pickup intent discovery rebuilds stamps CreatedAt from the current clock, which no longer
        // matches the persisted intent. Every poll then failed closed.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot finished = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(finished);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToCompletionAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);

        // The field clock moves between polls and the fixture's does not. Without this the replayed
        // intent looks identical to the persisted one and the store lets the replay through, which
        // is what hid the defect from the suite.
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "DEMAND_ALREADY_ACCEPTED",
            (await fixture.BacklogAsync(finished.DemandId)).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(1, await fixture.Context.JourneyRuntimes.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(2, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task ACompletedDemandStillListedByMesIngestDoesNotBlockTheNextEligibleDemand()
    {
        // The operational cost of the replay refusal: it aborted the whole runtime iteration, so no
        // other demand could be accepted while the finished one remained in the catalog. Backlog
        // ordering is by FirstSeenAt, so the finished demand is always the one selected.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot finished = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(finished);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToCompletionAsync();
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        AcceptedDemandSnapshot next = fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5));
        fixture.Catalog.Set(finished, next);
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow accepted = await fixture.RuntimeAsync(next.DemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, accepted.Stage);
        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(next.DemandId)).ReasonCode);
        Assert.Equal(
            "DEMAND_ALREADY_ACCEPTED",
            (await fixture.BacklogAsync(finished.DemandId)).ReasonCode);

        // One pickup and one gate order for the finished journey, one pickup for the new one: the
        // finished demand is never dispatched a second time.
        Assert.Equal(3, fixture.Riot.TotalCreateCount);
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            JourneyRuntimeStage.Completed,
            (await fixture.RuntimeAsync(finished.DemandId)).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task EachStopPublishesItsWorklistUnderItsOwnRevision()
    {
        // The peer keys a snapshot's identity on its type and revision, so two worklists published
        // at the same revision are a claim that their content is identical. The pickup and gate
        // worklists carry a different station, a different role and a different stop: publishing
        // both at revision 1 made the peer reject the second, correctly, and the connection died on
        // that rejection with the unload command still queued behind it.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToGateUnloadAsync();

        (string StationId, long Revision)[] worklists = fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)))
            .Where(document => document.RootElement.GetProperty("messageType").GetString()
                == "CurrentStopWorklistSnapshot")
            .Select(document => document.RootElement.GetProperty("payload"))
            .Select(payload => (
                payload.GetProperty("stationId").GetString()!,
                payload.GetProperty("worklistRevision").GetInt64()))
            .Distinct()
            .ToArray();

        Assert.Equal(2, worklists.Length);
        Assert.Equal(2, worklists.Select(item => item.StationId).Distinct().Count());
        Assert.Equal(2, worklists.Select(item => item.Revision).Distinct().Count());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task ASecondJourneyOnTheSameVehicleNeverRepublishesAnAdoptedRevision()
    {
        // The peer journals the adopted revision of each snapshot type in SQLite keyed on the type
        // alone -- no demand, no session -- and never deletes the row: on reconnect it clears its
        // in-memory copy and immediately restores it from that journal. A revision below the one it
        // holds is refused as SNAPSHOT_REVISION_REGRESSION, an equal one whose payload differs as
        // SNAPSHOT_REVISION_CONTENT_CONFLICT, and both refusals raise a protocol problem and tear
        // the session down. The runtime row is created per demand, so a second journey on the same
        // vehicle restarted every revision at 1 while the peer already held 2.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToCompletionAsync();

        // The vehicle lease is released by an atomic completion, so the same vehicle taking a second
        // demand is the designed path, not a recovery case.
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow second = await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000002");
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", second.PickupUpperId, second.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = second.PickupStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingSublot,
            (await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000002")).Stage);

        // One entry per snapshot the server allocated a revision to, in the order it first sent
        // them. The fixture peer never acknowledges, so every pending outbox row is re-sent on each
        // iteration; a real peer acknowledges what it has adopted and only the first send counts.
        (string Type, long Revision)[] published = fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)).RootElement)
            .Select(root => (Type: root.GetProperty("messageType").GetString()!, Root: root))
            .Where(item => SnapshotRevisionProperty(item.Type) is not null)
            .DistinctBy(item => item.Root.GetProperty("messageId").GetString()!)
            .Select(item => (
                item.Type,
                item.Root.GetProperty("payload").GetProperty(SnapshotRevisionProperty(item.Type)!).GetInt64()))
            .ToArray();

        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            published.Select(item => item.Type).Distinct().Order(StringComparer.Ordinal));
        foreach (IGrouping<string, long> stream in published.GroupBy(item => item.Type, item => item.Revision))
        {
            long[] revisions = stream.ToArray();
            // Three stops have been published: pickup and gate on the first journey, pickup on the
            // second. That is three distinct revisions of the worklist and the vehicle state. The plan
            // also went out before each pickup arrival, so it has five: before, at the pickup and at
            // the gate on the first journey, before and at the pickup on the second. The sequence may
            // never step back.
            Assert.Equal(revisions.Order(), revisions);
            Assert.Equal(stream.Key == "UpcomingStopPlanSnapshot" ? 5 : 3, revisions.Distinct().Count());
        }
    }

    private static string? SnapshotRevisionProperty(string messageType) => messageType switch
    {
        "VehicleBusinessStateSnapshot" => "vehicleBusinessStateRevision",
        "CurrentStopWorklistSnapshot" => "worklistRevision",
        "UpcomingStopPlanSnapshot" => "planRevision",
        _ => null
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ALostAcknowledgementNeverLeavesASupersededSnapshotToBeRedelivered(int iterationLosingAcks)
    {
        // Every runtime iteration replays each unacknowledged outbox row before it does anything
        // else, oldest first. The pickup and the gate publish the same three snapshot types under
        // two revisions, so a pickup row still pending once the gate row exists would be a lower
        // revision queued behind a higher one -- and the peer journals an adopted revision under its
        // message type alone and refuses anything below it as SNAPSHOT_REVISION_REGRESSION. This
        // drives the journey against a peer that answers the way OnboardHmi_MVP@304e6ad does and
        // loses every acknowledgement that peer had in flight during one chosen iteration, the way a
        // dropped connection would. It stays green because the replay is also the repair: the peer
        // journals and acknowledges a duplicate at the revision it already holds, so the next
        // iteration settles the row the lost acknowledgement left behind, and it does so long before
        // the gate allocates the higher revision. Iteration 0 loses nothing and is the control.
        AdoptingPeer peer = await DriveToGateWithAdoptingPeerAsync(iteration => iteration != iterationLosingAcks);

        // A green run only means something if the peer actually reached the dangerous state: it has
        // to be holding the gate revision of all three snapshot types. The plan went out once more,
        // before the pickup arrival, so it adopted three revisions where the others adopted two.
        foreach (IGrouping<string, long> stream in peer.Adopted.GroupBy(item => item.MessageType, item => item.Revision))
        {
            Assert.Equal(stream.Key == "UpcomingStopPlanSnapshot" ? 3 : 2, stream.Distinct().Count());
        }

        Assert.Equal(3, peer.Adopted.Select(item => item.MessageType).Distinct().Count());
        Assert.Empty(peer.Regressions);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task OnlyAPeerThatNeverAcknowledgesIsShownASupersededSnapshot()
    {
        // The falsifiability of the theory above: the detector does fire, and what it takes is a
        // peer whose acknowledgements never arrive at all -- for the whole span from the pickup
        // publish to the gate publish, not for one dropped connection. That peer cannot exist in a
        // run that reaches the gate: SublotSubmitted and PreDepartureSafetyCheckResult travel the
        // same peer-to-server direction as the acknowledgements, and the journey cannot leave the
        // pickup without them. This fixture supplies both by writing the inbox directly, which is
        // what lets the journey advance here and is the artifact behind the interleaved
        // `1, 2, 1, 2` redelivery seen while closing the revision allocation defect.
        AdoptingPeer peer = await DriveToGateWithAdoptingPeerAsync(_ => false);

        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            peer.Regressions.Select(item => item.MessageType).Distinct().Order(StringComparer.Ordinal));
        // The pickup revision redelivered under the gate revision. For the plan that is 2 under 3: its
        // first revision, the one sent before the arrival, was retired at the pickup and is never
        // redelivered at all (ThePlanSentBeforeArrivalIsRetiredWhenThePickupPlanSupersedesIt).
        Assert.All(peer.Regressions, item =>
        {
            long gateRevision = item.MessageType == "UpcomingStopPlanSnapshot" ? 3 : 2;
            Assert.Equal(gateRevision, item.Held);
            Assert.Equal(gateRevision - 1, item.Delivered);
        });
    }

    [Theory]
    [InlineData(3, 7, true)]
    [InlineData(4, 7, false)]
    [InlineData(3, 6, false)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task TheRedeliveredRevisionNeedsEveryAcknowledgementFromThePickupToTheGateLost(
        int firstLostIteration,
        int lastLostIteration,
        bool expectRegressions)
    {
        // The exact boundary. The pickup allocates its revisions in iteration 2 and the gate
        // allocates the higher ones in iteration 6, and each iteration replays what is still pending
        // before it publishes anything, so the peer re-acknowledges the pickup rows once per
        // iteration. They only survive to be redelivered under the gate revision if every one of
        // those acknowledgements is lost, iterations 3 through 7 -- a single successful delivery
        // anywhere in that span settles them and the run stays clean. Five consecutive iterations of
        // a peer-to-server direction that is down while the server-to-peer direction still carries
        // snapshots is not a dropped connection; and that same span is where the journey consumes
        // SublotSubmitted and PreDepartureSafetyCheckResult, which travel in the direction that
        // would have to be down.
        AdoptingPeer peer = await DriveToGateWithAdoptingPeerAsync(
            iteration => iteration < firstLostIteration || iteration > lastLostIteration);

        Assert.Equal(expectRegressions, peer.Regressions.Count > 0);
    }

    /// <summary>
    /// Runs one journey to the gate against a peer modelled on the real one, asking
    /// <paramref name="deliversAcks"/> before each iteration whether the acknowledgements the peer
    /// buffered during the previous iteration reach the server or are lost with the connection.
    /// </summary>
    private static async Task<AdoptingPeer> DriveToGateWithAdoptingPeerAsync(Func<int, bool> deliversAcks)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        AdoptingPeer peer = new(fixture.Context, fixture.Clock);
        fixture.Peer.OnMessageSent = line =>
        {
            peer.Receive(line);
            return Task.CompletedTask;
        };

        int iteration = 0;
        async Task IterateAsync()
        {
            iteration++;
            if (deliversAcks(iteration))
            {
                await peer.DeliverBufferedAcksAsync();
            }
            else
            {
                peer.LoseBufferedAcks();
            }

            await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        }

        await IterateAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await IterateAsync();

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "SublotSubmitted",
            new
            {
                demandId = runtime.DemandId,
                operationSessionId = runtime.OperationSessionId,
                stationId = runtime.PickupStationId,
                worklistRevision = runtime.WorklistRevision,
                sublot = "SUBLOT-001",
                entryMethod = "SCANNER",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = Now
                }
            });
        await IterateAsync();

        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await IterateAsync();

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = Now,
                safetyStateVersion = 7,
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            },
            runtime.PreDepartureSafetyCheckMessageId);
        await IterateAsync();

        fixture.Riot.SetSuccessfulArrival("TO_GATE", fixture.Options.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = fixture.Options.GateStationRiotId };
        // The gate publishes the second revision of all three snapshot types.
        await IterateAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.RuntimeAsync()).Stage);

        // Two more iterations with the journey standing at the gate: whatever is still pending is
        // replayed, and the peer is holding the gate revision by now.
        await IterateAsync();
        await IterateAsync();
        return peer;
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
    public async Task CommandsAnsweredByABusinessResultAreNotLeftPendingForReplay()
    {
        // SublotEntryRequested, SlotOperationCommand and PreDepartureSafetyCheck are answered with a
        // business result, never a DurableAck, so their outbox rows stayed unacknowledged forever
        // and were replayed into every later session carrying a new session generation. The peer
        // refused that as a business id whose content had changed and dropped the connection, so a
        // journey standing at the gate was torn down on every reconnect by a command it had already
        // obeyed -- and the unload command queued behind it was never reached.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        string[] stillPending = await fixture.Context.ProtocolOutbox
            .Where(row => row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => row.MessageType)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        // The sublot request and the load command have both been answered by now.
        Assert.DoesNotContain("SublotEntryRequested", stillPending);
        Assert.DoesNotContain("SlotOperationCommand", stillPending);
        // The safety check has not been answered yet, so it is still legitimately pending.
        Assert.Contains("PreDepartureSafetyCheck", stillPending);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task CommandsAnsweredAfterTheGateDepartureAreSettledToo()
    {
        // The same commit settles four answered commands, but only the sublot request and the load
        // command were ever asserted -- and the test above asserts the safety check is *still*
        // pending, because at that point it has not been answered. The remaining two are settled
        // after the journey leaves AwaitingDepartureSafety, past the point every earlier test
        // stopped looking, so deleting either call kept the whole suite green while the outbox went
        // on replaying an already-obeyed command into every later session.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.RunToCompletionAsync();

        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        string[] stillPending = await fixture.PendingOutboxMessageIdsAsync();

        // The safety check was answered by a PreDepartureSafetyCheckResult, the unload command by an
        // OperationResult. Neither answer is a DurableAck, so nothing else can settle these rows.
        Assert.DoesNotContain(runtime.PreDepartureSafetyCheckMessageId, stillPending);
        Assert.DoesNotContain(runtime.UnloadCommandMessageId, stillPending);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task DepartureSafetyAnsweredPromptlyIsJudgedWhileItIsStillValid()
    {
        // The peer answers a pre-departure safety check in tens of milliseconds and stamps the
        // answer with a validity window of its own, which is shorter than one poll interval. The
        // engine used to publish the check and come back for the answer on its next iteration, by
        // which time the window had closed -- the journey stopped at AwaitingDepartureSafety with
        // PRE_DEPARTURE_SAFETY_NOT_VALID and no movement was ever authorized. Every earlier test
        // staged the answer with a window a minute wide, so none of them could show it.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Peer.OnMessageSent = async line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("messageType").GetString() != "PreDepartureSafetyCheck")
            {
                return;
            }
            DateTimeOffset answeredAt = fixture.Clock.GetUtcNow();
            await fixture.AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "PreDepartureSafetyCheckResult",
                new
                {
                    preDepartureSafetyCheckId = root.GetProperty("payload")
                        .GetProperty("preDepartureSafetyCheckId").GetString(),
                    outcome = "SAFE",
                    observedAt = answeredAt,
                    safetyStateVersion = 7,
                    // The window the real peer grants: shorter than the engine's poll interval.
                    validUntil = answeredAt.AddSeconds(2),
                    safety = new
                    {
                        departureSafe = true,
                        vehicleStopped = true,
                        allTargetSlotsLocked = true,
                        allUnlockOutputsReset = true,
                        unknownPresent = false,
                        reasonCodes = Array.Empty<string>()
                    }
                },
                // The real peer correlates by the check id, not by the messageId of the request.
                root.GetProperty("payload").GetProperty("preDepartureSafetyCheckId").GetString());
        };

        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
    }

    /// <summary>
    /// CV-PREDEPARTURE-SAFETY-EXPIRES: PreDepartureSafetyCheck, its SAFE result, then a
    /// SafetyStateChanged. The answer was true of a safety state that no longer holds, so it may
    /// not authorize the departure (NEVER_DEPART_ON_EXPIRED_CHECK) -- and the check it answered is
    /// spent (EXPIRE_CHECK_ON_SAFETY_STATE_CHANGE). This server used to stop there: one check id per
    /// journey, judged invalid on every later poll, and the journey waited at the pickup for ever
    /// with PRE_DEPARTURE_SAFETY_NOT_VALID. The check is now retired and asked again under a new
    /// identity against the current safety state, and that answer departs the vehicle.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ASafetyChangeAfterTheAnswerExpiresTheCheckAndTheServerAsksAgainUnderANewIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        string expiredCheckId = runtime.PreDepartureSafetyCheckId;
        string expiredMessageId = runtime.PreDepartureSafetyCheckMessageId;
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(expiredCheckId, 7, fixture.Clock.GetUtcNow()), expiredCheckId);
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: true);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.NotEqual(expiredCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.NotEqual(expiredMessageId, runtime.PreDepartureSafetyCheckMessageId);
        Assert.NotNull((await fixture.Context.ProtocolOutbox.SingleAsync(
            row => row.MessageId == expiredMessageId, TestContext.Current.CancellationToken)).FencedAt);
        Assert.Equal(8, await ExpectedSafetyStateVersionAsync(fixture, runtime));

        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(runtime.PreDepartureSafetyCheckId, 8, fixture.Clock.GetUtcNow()),
            runtime.PreDepartureSafetyCheckId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// The other way a check expires: the safety state moves on before any answer arrives, so the
    /// check itself names a version that is no longer current. It is asked again -- but not while
    /// the vehicle is unsafe, where a new check could only be answered UNSAFE and would be asked
    /// again on every poll.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task AnUnansweredCheckIsAskedAgainOnceSafetyHasMovedOnButNotWhileTheVehicleIsUnsafe()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        string firstCheckId = runtime.PreDepartureSafetyCheckId;

        await fixture.AddSafetyStateChangedAsync(8, departureSafe: false, vehicleStopped: true);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(firstCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));

        await fixture.AddSafetyStateChangedAsync(9, departureSafe: true, vehicleStopped: true);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.NotEqual(firstCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.Equal(9, await ExpectedSafetyStateVersionAsync(fixture, runtime));
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
    }

    private static object SafeDepartureAnswer(string checkId, long safetyStateVersion, DateTimeOffset observedAt) => new
    {
        preDepartureSafetyCheckId = checkId,
        outcome = "SAFE",
        observedAt,
        safetyStateVersion,
        validUntil = observedAt.AddMinutes(1),
        safety = new
        {
            departureSafe = true,
            vehicleStopped = true,
            allTargetSlotsLocked = true,
            allUnlockOutputsReset = true,
            unknownPresent = false,
            reasonCodes = Array.Empty<string>()
        }
    };

    private static async Task<long> ExpectedSafetyStateVersionAsync(RuntimeFixture fixture, JourneyRuntimeRow runtime)
    {
        ProtocolOutboxRow check = await fixture.Context.ProtocolOutbox.SingleAsync(
            row => row.MessageId == runtime.PreDepartureSafetyCheckMessageId, TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(check.PayloadJson);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal(runtime.PreDepartureSafetyCheckId, payload.GetProperty("preDepartureSafetyCheckId").GetString());
        return payload.GetProperty("expectedSafetyStateVersion").GetInt64();
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task HardAdmissionFiltersBeforeStableBacklogOrderingAndRemoteSideEffects()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot wrongWorkType = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "IGNORED", Now.AddMinutes(-30)) with
        {
            WorkType = "OTHER",
            TransportDemandKey = "IGNORED|OTHER"
        };
        AcceptedDemandSnapshot laterId = fixture.Demand(
            "10000000-0000-4000-8000-000000000003", "SUBLOT-003", Now.AddMinutes(-20));
        AcceptedDemandSnapshot stableWinner = fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-20));
        fixture.Catalog.Set(wrongWorkType, laterId, stableWinner);
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        fixture.BoxCounts.Set("SUBLOT-003", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(stableWinner.DemandId, (await fixture.RuntimeAsync()).DemandId);
        Assert.Equal(1, await fixture.Context.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow[] backlog = await fixture.Context.JourneyBacklog
            .OrderBy(row => row.DemandId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, backlog.Length);
        Assert.Equal("OUT_OF_SCOPE_WORK_TYPE", backlog[0].ReasonCode);
        Assert.Equal("ACCEPTED", backlog[1].ReasonCode);
        Assert.Equal("ELIGIBLE", backlog[2].ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task VolatileCatalogReadMetadataDoesNotOverwriteCurrentBacklogReason()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyBacklogRow initial = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", initial.ReasonCode);
        string initialFingerprint = initial.DecisionFingerprint;

        fixture.Catalog.Set(demand with
        {
            CatalogRevision = demand.CatalogRevision + 1,
            AcceptedAt = demand.AcceptedAt.AddMinutes(1)
        });
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow volatileRefresh = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", volatileRefresh.ReasonCode);
        Assert.Equal(initialFingerprint, volatileRefresh.DecisionFingerprint);

        AcceptedDemandSnapshot changedDecision = demand with
        {
            CatalogRevision = demand.CatalogRevision + 2,
            AcceptedAt = demand.AcceptedAt.AddMinutes(2),
            DemandRevision = demand.DemandRevision + 1
        };
        fixture.Catalog.Set(changedDecision);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow changed = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("DEMAND_DECISION_FACT_CHANGED", changed.ReasonCode);
        Assert.NotEqual(initialFingerprint, changed.DecisionFingerprint);
        string changedFingerprint = changed.DecisionFingerprint;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow stableAgain = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", stableAgain.ReasonCode);
        Assert.Equal(changedFingerprint, stableAgain.DecisionFingerprint);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task LargeCatalogBatchesBacklogPersistenceBeforeAcceptingEligibleJourney()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot[] outOfScope = Enumerable.Range(1, 250)
            .Select(index => fixture.Demand(
                $"20000000-0000-4000-8000-{index:D12}",
                $"IGNORED-{index:D3}",
                Now.AddMinutes(-20)) with
            {
                WorkType = "OTHER",
                TransportDemandKey = $"IGNORED-{index:D3}|OTHER"
            })
            .ToArray();
        AcceptedDemandSnapshot eligible = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10));
        fixture.Catalog.Set([.. outOfScope, eligible]);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.SaveChanges.Reset();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(eligible.DemandId, (await fixture.RuntimeAsync()).DemandId);
        Assert.Equal(251, await fixture.Context.JourneyBacklog.CountAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
        // 251 candidates, a bounded number of saves: what this pins is that backlog persistence is
        // batched rather than one save per candidate. The budget went from 10 to 13 with FP-C13,
        // which adds exactly three writes to an accepting round and none per candidate -- the
        // catalog confirmation, the gate verdict for the one demand that reached the gate, and the
        // freeze of its endpoints. A steady round that accepts nothing adds only the confirmation.
        // B2 adds three more, still none of them per candidate: applying the configured fleet
        // policy, which this first round does because the tables start empty and which costs two
        // saves, and the occupancy claim on the accepted journey's first order.
        Assert.InRange(fixture.SaveChanges.Count, 1, 15);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task VehicleReadsThatAdvanceClockUsePostReadTimeForAdmission()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        DateTimeOffset beforeVehicleReads = fixture.Clock.GetUtcNow();
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        AcceptedDemandRow accepted = await fixture.DemandRowAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        OrderIntentRow pickup = await fixture.Context.OrderIntents.AsNoTracking().SingleAsync(
            row => row.Purpose == "TO_PICKUP",
            TestContext.Current.CancellationToken);
        Assert.Equal(demand.DemandId, accepted.DemandId);
        Assert.Equal(DemandExecutionStatus.Accepted, accepted.Status);
        Assert.Equal(demand.DemandId, runtime.DemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal(demand.DemandId, pickup.DemandId);
        Assert.Equal("CONFIRMED", pickup.Status);
        Assert.Equal("ORDER-TO_PICKUP", pickup.OrderId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
        Assert.True(fixture.Clock.GetUtcNow() > beforeVehicleReads);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("ACCEPTED", backlog.ReasonCode);
        Assert.NotNull(backlog.AcceptedAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task SnapshotsFromEarlierInALiveSessionStillAdmit()
    {
        // Onboard sends CapabilitySnapshot and SafetyStateSnapshot once per session and the
        // protocol mandates no cadence, so their payload age is not evidence of anything. As long
        // as the session is still being heard from, admission must proceed.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AgeOnboardSnapshotPayloadsAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ACCEPTED", (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AdmissionSafetyFactsComeFromTheLatestChangeNotTheSessionSnapshot()
    {
        // Onboard sends SafetyStateSnapshot once per session and reports every later change with
        // SafetyStateChanged. A session established while the vehicle is still moving therefore
        // carries a snapshot that says so for as long as the session lives, and reading admission
        // facts from that snapshot alone left vehicleStopped false forever: the vehicle could stop
        // at the station and still never be admitted, while the session row -- which
        // SafetyStateChanged does update -- correctly showed Ready.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.EstablishSessionWhileVehicleIsMovingAsync();
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: true);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "ACCEPTED",
            (await fixture.BacklogAsync("10000000-0000-4000-8000-000000000001")).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ArrivalIsNotTrustedWhileTheLatestSafetyStateSaysTheVehicleIsMoving()
    {
        // The same staleness in the other direction, and the dangerous one: the session-start
        // snapshot says the vehicle is stopped, so every later arrival check went on believing it
        // after Onboard had reported the vehicle moving. Arrival must be judged against the newest
        // safety state, and departureSafe is not a proxy for it -- a moving vehicle can still
        // report that nothing blocks a departure, which is why the session row alone is not enough.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: false);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);

        await fixture.AddSafetyStateChangedAsync(9, departureSafe: true, vehicleStopped: true);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ABlockedJourneyKeepsTheReasonItWasBlockedForWhenTheSessionDrops()
    {
        // A station operation that times out blocks the journey and, per ADR-cross-0006 and
        // ADR-cross-0015, holds it there until a human drives the recovery handshake. The vehicle
        // is normally powered down for that repair, so the session leaves Ready -- and overwriting
        // BlockReasonCode with ONBOARD_SESSION_NOT_READY on the way through destroyed the only
        // record of why the journey is blocked and which recovery it is waiting for. Session
        // readiness is reported on its own row; the block reason is not recoverable once lost.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplyTimedOutResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", blocked.BlockReasonCode);

        await fixture.DropOnboardSessionAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow afterDrop = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, afterDrop.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", afterDrop.BlockReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AResultThatTurnsTheSessionRecoveryRequiredStillBlocksTheJourneyForItsOwnReason()
    {
        // 147f02c made a refused result move the session to RecoveryRequired, so the vehicle would
        // finally be told it needs recovery. AdvanceAsync reads the same row and returns early on
        // any session that is not Ready -- so that change also stops the runtime one step short of
        // the Blocked transition it was supposed to enable, with the result already durable in the
        // database. The recovery the journey is waiting for never gets named.
        //
        // Seeded from what L2 run 20260904-recovery-entry-after-announce-001 actually finished
        // with: OperationResults holding the refused load, StationOperations at RecoveryRequired,
        // SessionRecoveries at RecoveryRequired / OPERATION_RECOVERY_REQUIRED, and JourneyRuntimes
        // still reading AwaitingLoadResult / ONBOARD_SESSION_NOT_READY.
        //
        // A session in recovery is not an absent session. The vehicle is connected, the server
        // authorises recovery actions against this exact state, and the operator's entry on the
        // onboard HMI opens on it (8005-agv-onboard-hmi:
        // RecoveryRequiredAnnouncedOnAResultAckOpensTheRecoveryEntry). Refusing to name the block
        // is what leaves a stopped vehicle with nothing to recover from.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplyTimedOutResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load);
        await fixture.MarkSessionRecoveryRequiredByOperationAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", blocked.BlockReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ABlockedJourneyKeepsTheVehicleOutOfEveryOtherDemand()
    {
        // The single active slot is what makes the one-vehicle runtime safe. A blocked journey is
        // unresolved, not finished: the physical state of the slots is unproven, the dispatch
        // lease is still held, and admitting another demand would send the vehicle away on it. So
        // this is intended behaviour, recorded here so that no later change to Blocked quietly
        // turns it into a free vehicle. The way out is the recovery handshake, not discovery.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplyTimedOutResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Blocked, (await fixture.RuntimeAsync()).Stage);

        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)),
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task InboundEnvelopesWithoutASessionGenerationDoNotBreakLiveness()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AddInboxRowWithoutSessionGenerationAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ACCEPTED", (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task SupportsBatchUnlockFalseDoesNotBlockAdmission()
    {
        // The protocol declares supportsBatchUnlock with no semantics -- a bare boolean, unchanged
        // from protocol-v0.1.1 through the v2 candidate -- and its own canonical example sets it
        // false. Whether the vehicle can operate a given slot set is decided
        // against AvailableSlots when the command is sent, not by this flag.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.ClearSupportsBatchUnlockAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ACCEPTED", (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task CandidateProcessingThatExpiresDynamicFactsDoesNotAcceptOrDispatch()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.BoxCounts.BeforeRead = () => fixture.Clock.Advance(TimeSpan.FromSeconds(31));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("FINAL_DYNAMIC_FACTS_NOT_READY", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task FinalCatalogRefreshThatExpiresDynamicFactsDoesNotAcceptOrDispatch()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.Catalog.BeforeRead = readCount =>
        {
            if (readCount == 2)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            }
        };
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("FINAL_DYNAMIC_FACTS_NOT_READY", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task UnknownOrStaleDynamicFactsFailClosedWithoutAcceptingOrDispatching()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.ProtocolOutbox.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    [Theory]
    [InlineData("N1-1", "EQP-01", "N1-1", 12)]
    [InlineData("N1-2", "EQP-02", "N1-2_N1-3", 13)]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task DynamicMapPickupResolutionFreezesEveryMatchingDemandStation(
        string area,
        string eqp,
        string expectedStationName,
        int expectedStationId)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            area,
            eqp));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        OrderIntentRow pickup = await fixture.Context.OrderIntents.SingleAsync(
            row => row.Purpose == "TO_PICKUP", TestContext.Current.CancellationToken);
        Assert.Equal(expectedStationName, runtime.PickupStationId);
        Assert.Equal(expectedStationId, runtime.PickupStationRiotId);
        Assert.Equal(expectedStationName, pickup.TargetStationId);
        Assert.Equal(expectedStationId, pickup.DestinationStationId);
        Assert.Equal(25, pickup.MapId);
        Assert.Matches("^MAPCAT-[0-9a-f]{64}$", runtime.RouteEvidenceId);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AnUncommissionedCatalogCreatesNothingAtAll()
    {
        // REQ-0302's hard block, in the place it has to hold: a whole runtime iteration against a
        // server whose two approved values were never configured. The server runs, polls, and
        // writes the demand into the backlog under the block's own name -- and creates no journey
        // and no RIoT order. Specification 8.6 asks for exactly this negative evidence: take the
        // parameters away and show that it really blocks.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(catalogApproved: false);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("CATALOG_PARAMETERS_NOT_APPROVED", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        // The station was never resolved either: REQ-0303 forbids resolving execution stations for
        // a new demand while the catalog is unusable, not merely acting on the result.
        Assert.Empty(await fixture.Context.FrozenDemandStations.ToArrayAsync(
            TestContext.Current.CancellationToken));
        // And RIoT's RouteCost was never asked -- the block is ahead of the gate, not inside it.
        Assert.Empty(fixture.RouteCosts.Calls);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AcceptingADemandFreezesBothEndpointsAndALaterRenameDoesNotRewriteThem()
    {
        // REQ-0305: the endpoints are taken from the snapshot that was fresh at creation time and
        // frozen there. A later rename changes the catalog, not the identity of the station this
        // task was already sent to, and it must not re-resolve the task.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        FrozenDemandStationRow[] frozen = await fixture.Context.FrozenDemandStations
            .OrderBy(row => row.Role)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, frozen.Length);
        FrozenDemandStationRow pickup = frozen.Single(row => row.Role == FrozenStationRole.Pickup);
        FrozenDemandStationRow dropoff = frozen.Single(row => row.Role == FrozenStationRole.Dropoff);
        Assert.Equal(12, pickup.StationId);
        Assert.Equal("N1-1", pickup.StationName);
        Assert.Equal(210, dropoff.StationId);
        Assert.NotEqual(0, pickup.CatalogRevision);

        // The Map renames the pickup station under the running journey.
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        FrozenDemandStationRow reread = await fixture.Context.FrozenDemandStations
            .SingleAsync(row => row.Role == FrozenStationRole.Pickup, TestContext.Current.CancellationToken);
        Assert.Equal(12, reread.StationId);
        Assert.Equal("N1-1", reread.StationName);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task TheCatalogIsConfirmedOnAWholeReadAndOnlyOnAWholeRead()
    {
        // REQ-0302: what a confirmation is. A read that got as far as the gate station and the
        // machine stations is one; a read that threw is an attempt, and an attempt must not move
        // the freshness window.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        MapStationCatalogStateRow confirmed = await fixture.Context.MapStationCatalogStates
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MapStationCatalogState.Fresh, confirmed.State);
        Assert.Equal(Now, confirmed.LastCompleteConfirmationAt);
        Assert.Equal(30, confirmed.ApprovedSyncPeriodSeconds);
        Assert.Equal(300, confirmed.ApprovedMaxUnconfirmedSeconds);

        // Now the gate station disappears from the Map: the read completes, the resolution does
        // not, and that is not a confirmation.
        fixture.Riot.SetMapStations(new RiotMapStation(12, "N1-1"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        MapStationCatalogStateRow afterFailure = await fixture.Context.MapStationCatalogStates
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MapStationCatalogState.CandidateInvalid, afterFailure.State);
        Assert.Equal(Now, afterFailure.LastCompleteConfirmationAt);
        Assert.NotNull(afterFailure.LastFailureReason);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AnUnreachablePickupIsRefusedACreateAndSaysWhy()
    {
        // The gate at Order 96, in the runtime. RIoT says the vehicle cannot reach the pickup
        // station, so the demand is not taken -- REQ-0147/REQ-0293's pre-create RouteCost check.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.RouteCosts.Set(12, -1);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("CREATE_GATE_STATION_UNREACHABLE", backlog.ReasonCode);
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        CreateGateAuditRow audit = (await fixture.Context.CreateGateAudit
            .ToListAsync(TestContext.Current.CancellationToken)).Single();
        Assert.Equal(CreateGateVerdict.BlockedUnreachable, audit.Verdict);
        Assert.Equal(-1, audit.RiotRouteCostMm);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AmbiguousAreaStationMappingRemainsBackloggedAndNeverCreatesMovement()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.SetMapStations(
            new RiotMapStation(11, "N1-1"),
            new RiotMapStation(12, "N1-1_N1-2"),
            new RiotMapStation(210, "关卡"));
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("AREA_STATION_NOT_UNIQUE", backlog.ReasonCode);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Theory]
    [InlineData("D11-10", "OUT_OF_SCOPE_AREA")]
    [InlineData("Q18-10", "OUT_OF_SCOPE_AREA")]
    [InlineData("N22-1", "AREA_STATION_NOT_FOUND")]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task OutOfScopeOrMap25UnresolvableAreaNeverCreatesMissingPackageNoise(
        string area,
        string expectedReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            area,
            "EQP-01",
            "UNKNOWN-PACKAGE"));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedReason, backlog.ReasonCode);
        Assert.Empty(await fixture.Context.MissingPackages.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task InScopeResolvedUnknownPackageIsDeduplicatedAndNeverCreatesOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            package: "UNKNOWN-PACKAGE"));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        MissingPackageRow missing = await fixture.Context.MissingPackages.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN-PACKAGE", missing.Package);
        Assert.Equal("PENDING", missing.Status);
        Assert.Equal(Now, missing.FirstSeenAt);
        Assert.Equal(Now, missing.LastSeenAt);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task BatteryAtConfirmedThirtyPercentThresholdRemainsEligible()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MinimumBatteryPercent = 30;
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 30 };
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Single(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task MultipleEqpsForOneAreaRemainBackloggedAndNeverCreateMovement()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(
                "10000000-0000-4000-8000-000000000001",
                "SUBLOT-001",
                Now.AddMinutes(-10),
                "N1-1",
                "EQP-01"),
            fixture.Demand(
                "10000000-0000-4000-8000-000000000002",
                "SUBLOT-002",
                Now.AddMinutes(-9),
                "N1-1",
                "EQP-02"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.BoxCounts.Set("SUBLOT-002", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow[] backlog = await fixture.Context.JourneyBacklog
            .OrderBy(row => row.DemandId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, backlog.Length);
        Assert.All(backlog, row => Assert.Equal("AREA_EQP_NOT_UNIQUE", row.ReasonCode));
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Theory]
    [InlineData("route-missing", "AREA_STATION_NOT_FOUND")]
    [InlineData("zone-not-admitted", "DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING")]
    [InlineData("vehicle-not-idle", "RIOT_VEHICLE_NOT_IDLE")]
    [InlineData("vehicle-map-mismatch", "RIOT_VEHICLE_MAP_MISMATCH")]
    [InlineData("battery-low", "BATTERY_POLICY_NOT_SATISFIED")]
    [InlineData("riot-order-occupied", "RIOT_VEHICLE_ORDER_OCCUPIED")]
    [InlineData("box-count-missing", "SUBLOT_BOX_COUNT_UNAVAILABLE")]
    [InlineData("package-capacity-missing", "PACKAGE_CAPACITY_NOT_UNIQUE")]
    [InlineData("onboard-silent", "ONBOARD_FACTS_NOT_READY")]
    [InlineData("onboard-unsafe", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("slot-capacity", "SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE")]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task EveryAdmissionGateFailsClosedBeforeAcceptance(
        string scenario,
        string expectedReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            package: scenario == "package-capacity-missing" ? "UNKNOWN-PACKAGE" : "PDFN5×6-8L(12R)");
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 8);
        switch (scenario)
        {
            case "route-missing":
                fixture.Riot.SetMapStations(
                    new RiotMapStation(210, "关卡"),
                    new RiotMapStation(300, "等待点"));
                break;
            case "zone-not-admitted":
                fixture.Options.AllowedDispatchZones = [];
                break;
            case "vehicle-not-idle":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { ProcState = "EXECUTING" };
                break;
            case "vehicle-map-mismatch":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = "OTHER-MAP" };
                break;
            case "battery-low":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
                break;
            case "riot-order-occupied":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { LockStatus = 1, OrderTaskId = "ORDER-ACTIVE" };
                break;
            case "box-count-missing":
                fixture.BoxCounts.Remove("SUBLOT-001");
                break;
            case "package-capacity-missing":
                break;
            case "onboard-silent":
                await fixture.SilenceOnboardSessionAsync();
                break;
            case "onboard-unsafe":
                await fixture.SetOnboardUnknownAsync();
                break;
            case "slot-capacity":
                await fixture.KeepOnlyOneAvailableSlotAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        Assert.Equal(expectedReason, (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task RestartKeepsOriginalVehicleLeaseAndStablePickupIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();

        Assert.Equal(before.DemandId, after.DemandId);
        Assert.Equal(before.PickupMovementLegId, after.PickupMovementLegId);
        Assert.Equal(before.PickupUpperId, after.PickupUpperId);
        Assert.Equal(1, await fixture.Context.VehicleDispatchLeases.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// CV-DEMAND-ACCEPT-TO-PICKUP in the order its input.ndjson gives it: once the TO_PICKUP order
    /// exists the vehicle is told its plan -- the pickup leg ACTIVE, the drop-off leg PLANNED --
    /// before it arrives; at the pickup it is told the worklist first and the arrived plan after
    /// it, one revision up.
    /// </summary>
    /// <remarks>
    /// Until 2026-09-13 nothing went out before the arrival and a single plan went out after the
    /// worklist. No gate caught it: this suite compared the outbox as a sorted set of types, and the
    /// onboard end's G2 is written against a fake server that sends the vector's order, so each end
    /// passed against its own picture of the other. The order is read from the wire lines the peer
    /// received, because the fixture clock does not move and every outbox row shares a CreatedAt.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task ThePlanGoesOutBeforeArrivalAndAgainAfterTheWorklistAtThePickup()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));

        (string Type, string MessageId, JsonElement Payload)[] beforeArrival = JourneySnapshotsInFirstSentOrder(fixture);
        (string Type, string MessageId, JsonElement Payload) dispatchPlan = Assert.Single(beforeArrival);
        Assert.Equal("UpcomingStopPlanSnapshot", dispatchPlan.Type);
        Assert.Equal(runtime.PlanRevision, dispatchPlan.Payload.GetProperty("planRevision").GetInt64());
        Assert.Equal(
            [("TO_PICKUP", "ACTIVE"), ("TO_DROPOFF", "PLANNED")],
            dispatchPlan.Payload.GetProperty("legs").EnumerateArray()
                .Select(leg => (leg.GetProperty("legType").GetString()!, leg.GetProperty("state").GetString()!))
                .ToArray());
        Assert.Single(
            await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken));

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);

        (string Type, string MessageId, JsonElement Payload)[] sequence = JourneySnapshotsInFirstSentOrder(fixture);
        Assert.Equal(
            ["UpcomingStopPlanSnapshot", "CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot"],
            sequence.Select(item => item.Type).ToArray());
        Assert.Equal(dispatchPlan.MessageId, sequence[0].MessageId);
        Assert.Equal(runtime.PlanRevision + 1, sequence[2].Payload.GetProperty("planRevision").GetInt64());
        Assert.Equal(
            [("TO_PICKUP", "ARRIVED"), ("TO_DROPOFF", "PLANNED")],
            sequence[2].Payload.GetProperty("legs").EnumerateArray()
                .Select(leg => (leg.GetProperty("legType").GetString()!, leg.GetProperty("state").GetString()!))
                .ToArray());
    }

    /// <summary>
    /// The plan sent before the arrival is superseded by the one sent at the pickup, and a superseded
    /// snapshot is never worth delivering: replayed after the higher revision, it is a regression the
    /// vehicle refuses by tearing the session down. A peer that lost every acknowledgement from the
    /// first plan to the pickup is the one that would reach it, so the pickup retires it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task ThePlanSentBeforeArrivalIsRetiredWhenThePickupPlanSupersedesIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        AdoptingPeer peer = new(fixture.Context, fixture.Clock);
        fixture.Peer.OnMessageSent = line =>
        {
            peer.Receive(line);
            return Task.CompletedTask;
        };

        async Task IterateLosingAcksAsync()
        {
            peer.LoseBufferedAcks();
            await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        }

        await IterateLosingAcksAsync();
        await IterateLosingAcksAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await IterateLosingAcksAsync();
        await IterateLosingAcksAsync();
        await IterateLosingAcksAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain(peer.Regressions, item => item.MessageType == "UpcomingStopPlanSnapshot");
        ProtocolOutboxRow[] plans = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        ProtocolOutboxRow retired = Assert.Single(plans, row => PlanRevisionOf(row) == runtime.PlanRevision);
        Assert.NotNull(retired.FencedAt);
        Assert.Null(Assert.Single(plans, row => PlanRevisionOf(row) == runtime.PlanRevision + 1).FencedAt);
    }

    private static long PlanRevisionOf(ProtocolOutboxRow row)
    {
        using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
        return document.RootElement.GetProperty("payload").GetProperty("planRevision").GetInt64();
    }

    /// <summary>
    /// The plan and worklist snapshots the peer was sent, each message once, in the order it was
    /// first sent. Replays repeat a line; only the first send says where it stands in the sequence.
    /// </summary>
    private static (string Type, string MessageId, JsonElement Payload)[] JourneySnapshotsInFirstSentOrder(
        RuntimeFixture fixture) =>
        fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)).RootElement)
            .Where(root => root.GetProperty("messageType").GetString() is
                "UpcomingStopPlanSnapshot" or "CurrentStopWorklistSnapshot")
            .Select(root => (
                Type: root.GetProperty("messageType").GetString()!,
                MessageId: root.GetProperty("messageId").GetString()!,
                Payload: root.GetProperty("payload").Clone()))
            .DistinctBy(item => item.MessageId)
            .ToArray();

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task RestartReconcilesUnknownPickupCreateWithoutSecondOrderOrIdentityChange()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.LoseNextCreateResponse = true;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        Assert.Equal("RESULT_UNKNOWN", (await fixture.Context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        OrderIntentRow reconciled = await fixture.Context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(before.DemandId, after.DemandId);
        Assert.Equal(before.PickupUpperId, after.PickupUpperId);
        Assert.Equal("CONFIRMED", reconciled.Status);
        Assert.Equal("ORDER-TO_PICKUP", reconciled.OrderId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Single(await fixture.Context.VehicleDispatchLeases.ToArrayAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AcceptedDemandWithoutPersistedRuntimeFailsClosedInsteadOfBeingAdopted()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        await new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            demand,
            new OrderIntent(
                "LEGACY-PICKUP-LEG",
                demand.DemandId,
                "W2G-LEGACY-PICKUP-1",
                "TO_PICKUP",
                "N1-1",
                Now,
                fixture.Options.VehicleKey,
                fixture.Options.MapId,
                12,
                fixture.Options.AgvLifecycleGeneration,
                fixture.Options.DispatchGeneration),
            TestContext.Current.CancellationToken);

        BusinessIdentityConflictException error = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));

        Assert.Contains("has no production journey runtime", error.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task RestartReenvelopesPendingServerMessagesForTheCurrentSessionWithoutChangingSemantics()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow initialRuntime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", initialRuntime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = initialRuntime.PickupStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Dictionary<string, string> originalPayloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .ToDictionaryAsync(
                row => row.MessageId,
                row => JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("payload").GetRawText(),
                TestContext.Current.CancellationToken);
        int originalCount = originalPayloads.Count;

        await fixture.AdvanceSessionAsync(2);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        ProtocolOutboxRow[] replayed = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(originalCount, replayed.Length);
        foreach (ProtocolOutboxRow row in replayed)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            // A retired row is not pending, so it is neither replayed nor re-enveloped: it keeps the
            // generation it was sent in. The one retired here is the plan sent before the pickup
            // arrival, which the pickup plan superseded (and this fixture's peer never acknowledges).
            long expectedGeneration = row.FencedAt is null ? 2 : 1;
            Assert.Equal(expectedGeneration, document.RootElement.GetProperty("sessionGeneration").GetInt64());
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(originalPayloads[row.MessageId]),
                JsonNode.Parse(document.RootElement.GetProperty("payload").GetRawText())));
        }

        ProtocolOutboxRow retired = Assert.Single(replayed, row => row.FencedAt is not null);
        Assert.Equal("UpcomingStopPlanSnapshot", retired.MessageType);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    public async Task UnknownPreDepartureSafetyCannotCreateTheGateOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = Now,
                safetyStateVersion = 7,
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = true,
                    reasonCodes = UnknownReasonCodes
                }
            }, runtime.PreDepartureSafetyCheckMessageId);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        Assert.DoesNotContain(
            await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken),
            row => row.Purpose == "TO_GATE");
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            RecordingCatalog catalog,
            RecordingBoxCounts boxCounts,
            RecordingRiot riot,
            RecordingPeer peer,
            RecordingRouteCostProbe routeCosts,
            bool catalogApproved,
            JourneyRuntimeOptions options,
            FixedTimeProvider clock,
            SaveChangesCounter saveChanges)
        {
            CatalogApproved = catalogApproved;
            Connection = connection;
            Context = context;
            Catalog = catalog;
            BoxCounts = boxCounts;
            Riot = riot;
            Peer = peer;
            RouteCosts = routeCosts;
            Options = options;
            Clock = clock;
            SaveChanges = saveChanges;
            Engine = CreateEngine();
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public RecordingCatalog Catalog { get; }
        public RecordingBoxCounts BoxCounts { get; }
        public RecordingRiot Riot { get; }
        public RecordingPeer Peer { get; }
        public RecordingRouteCostProbe RouteCosts { get; }

        /// <summary>Whether the REQ-0302 pair is configured. False is the uncommissioned server.</summary>
        public bool CatalogApproved { get; }

        public JourneyRuntimeOptions Options { get; }

        /// <summary>
        /// Owned by the fixture rather than by the engine so that it survives
        /// <see cref="RecreateEngineAsync"/>, the way the process-wide singleton survives a scope.
        /// </summary>
        public CheckpointWaitLedger CheckpointWaits { get; } = new();

        public FixedTimeProvider Clock { get; }
        public SaveChangesCounter SaveChanges { get; }
        public JourneyRuntimeEngine Engine { get; private set; }

        public static async Task<RuntimeFixture> CreateAsync(bool catalogApproved = true)
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            SaveChangesCounter saveChanges = new();
            DbContextOptions<ControlServerDbContext> dbOptions =
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(connection)
                    .AddInterceptors(saveChanges)
                    .Options;
            ControlServerDbContext context = new(dbOptions);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeOptions options = ValidOptions();
            FixedTimeProvider clock = new(Now);
            RecordingCatalog catalog = new();
            RecordingBoxCounts boxCounts = new();
            RecordingRiot riot = new(options, clock);
            RecordingPeer peer = new();
            RecordingRouteCostProbe routeCosts = new();
            RuntimeFixture fixture = new(
                connection, context, catalog, boxCounts, riot, peer, routeCosts, catalogApproved,
                options, clock, saveChanges);
            await fixture.SeedRecoveredPeerAsync();
            saveChanges.Reset();
            return fixture;
        }

        public AcceptedDemandSnapshot Demand(
            string demandId,
            string sublot,
            DateTimeOffset createdAt,
            string area = "N1-1",
            string eqp = "EQP-01",
            string package = "PDFN5×6-8L(12R)") => new(
            demandId,
            $"{sublot}|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Clock.GetUtcNow(),
            $"SERIES-{demandId}",
            "WIRE_TO_GATE",
            sublot,
            1,
            createdAt,
            createdAt.AddMinutes(1),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet(area, eqp, "STEP-01", createdAt, package));

        public async Task RecreateEngineAsync()
        {
            Context.ChangeTracker.Clear();
            Engine = CreateEngine();
            await Task.CompletedTask;
        }

        public async Task AdvanceSessionAsync(long generation)
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SessionGeneration = generation;
            session.CapabilityRevision = 1;
            session.SafetyRevision = 7;
            session.Readiness = SessionReadiness.Ready;
            session.ReasonCode = "READY";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddCapabilityAndSafetyAsync(generation);
        }

        /// <summary>Carries the journey to the point where the load command is outstanding.</summary>
        public async Task<JourneyRuntimeRow> AdvanceToLoadResultAsync()
        {
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeRow pickupRuntime = await RuntimeAsync();
            Riot.SetSuccessfulArrival("TO_PICKUP", pickupRuntime.PickupStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = pickupRuntime.PickupStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeRow runtime = await RuntimeAsync();
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "SublotSubmitted",
                new
                {
                    demandId = runtime.DemandId,
                    operationSessionId = runtime.OperationSessionId,
                    stationId = runtime.PickupStationId,
                    worklistRevision = runtime.WorklistRevision,
                    sublot = "SUBLOT-001",
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = Clock.GetUtcNow()
                    }
                });
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        public async Task<JourneyRuntimeRow> AdvanceToDepartureSafetyAsync()
        {
            await AdvanceToLoadResultAsync();
            StationOperationRow load = await OperationAsync(SlotOperationType.Load);
            await ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>Carries the journey on to the gate, where the unload command is issued.</summary>
        public async Task<JourneyRuntimeRow> RunToGateUnloadAsync()
        {
            JourneyRuntimeRow runtime = await AdvanceToDepartureSafetyAsync();
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "PreDepartureSafetyCheckResult",
                new
                {
                    preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                    outcome = "SAFE",
                    observedAt = Clock.GetUtcNow(),
                    safetyStateVersion = 7,
                    validUntil = Clock.GetUtcNow().AddMinutes(1),
                    safety = new
                    {
                        departureSafe = true,
                        vehicleStopped = true,
                        allTargetSlotsLocked = true,
                        allUnlockOutputsReset = true,
                        unknownPresent = false,
                        reasonCodes = Array.Empty<string>()
                    }
                },
                runtime.PreDepartureSafetyCheckMessageId);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            Riot.SetSuccessfulArrival("TO_GATE", Options.GateStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = Options.GateStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>Carries the journey through the unload result to atomic completion.</summary>
        public async Task<JourneyRuntimeRow> RunToCompletionAsync()
        {
            await RunToGateUnloadAsync();
            StationOperationRow unload = await OperationAsync(SlotOperationType.Unload);
            await ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>
        /// Stores an inbound envelope whose <c>sessionGeneration</c> is null, which is what the
        /// inbox holds before a generation is assigned. Liveness scans every message type, so it
        /// must skip these rather than fail on the missing number.
        /// </summary>
        public async Task AddInboxRowWithoutSessionGenerationAsync()
        {
            string messageId = Guid.NewGuid().ToString("D");
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = "SessionHello",
                RequestJson = JsonSerializer.Serialize(new
                {
                    messageType = "SessionHello",
                    messageId,
                    agvId = Options.AgvId,
                    sessionGeneration = (long?)null,
                    sentAt = Now,
                    payload = new { }
                }, SerializerOptions),
                ContentHash = new string('b', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Clock.GetUtcNow()
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Simulates a peer that has gone quiet: the session row still says Ready and its
        /// snapshots are still on file, but nothing has been received from it for an hour.
        /// </summary>
        public async Task SilenceOnboardSessionAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                row.ReceivedAt = Clock.GetUtcNow().AddHours(-1);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Backdates only the snapshot payload timestamps, leaving the session live. Onboard sends
        /// each snapshot once per session, so this is the normal steady state, not a hazard.
        /// </summary>
        public async Task AgeOnboardSnapshotPayloadsAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                root["payload"]!["observedAt"] = Clock.GetUtcNow().AddHours(-1);
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Sets the undefined <c>supportsBatchUnlock</c> capability flag to false, which is the
        /// value Onboard ships and the value the protocol's own canonical example carries.
        /// </summary>
        public async Task ClearSupportsBatchUnlockAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                root["payload"]!["supportsBatchUnlock"] = false;
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task KeepOnlyOneAvailableSlotAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                JsonArray slots = root["payload"]!["slotStates"]!.AsArray();
                for (int index = 1; index < slots.Count; index++)
                {
                    slots[index]!["administrativeAvailability"] = "DISABLED";
                }
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Rewrites the session's SafetyStateSnapshot into what Onboard sends when the session is
        /// established while the vehicle is still moving. Slot states are untouched: movement
        /// changes neither their physical state nor their locks.
        /// </summary>
        public async Task EstablishSessionWhileVehicleIsMovingAsync()
        {
            ProtocolInboxRow row = await Context.ProtocolInbox.SingleAsync(
                item => item.MessageType == "SafetyStateSnapshot",
                TestContext.Current.CancellationToken);
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            root["payload"]!["safety"]!["departureSafe"] = false;
            root["payload"]!["safety"]!["vehicleStopped"] = false;
            row.RequestJson = root.ToJsonString(SerializerOptions);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Records a SafetyStateChanged the way OnboardMessageProcessor does: the envelope lands
        /// in the inbox and the session row takes the new revision and departure flag. Onboard
        /// sends the full snapshot once per session and reports every later change this way.
        /// </summary>
        public async Task AddSafetyStateChangedAsync(
            long safetyStateVersion,
            bool departureSafe,
            bool vehicleStopped,
            int[]? affectedSlots = null)
        {
            await AddRawInboxAsync("SafetyStateChanged", new
            {
                safetyStateVersion,
                observedAt = Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe,
                    vehicleStopped,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                affectedSlots = affectedSlots ?? []
            }, 1);
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SafetyRevision = safetyStateVersion;
            session.DepartureSafe = departureSafe;
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task SetOnboardUnknownAsync()
        {
            ProtocolInboxRow row = await Context.ProtocolInbox.SingleAsync(
                item => item.MessageType == "SafetyStateSnapshot",
                TestContext.Current.CancellationToken);
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            root["payload"]!["safety"]!["departureSafe"] = false;
            root["payload"]!["safety"]!["unknownPresent"] = true;
            row.RequestJson = root.ToJsonString(SerializerOptions);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public Task<JourneyRuntimeRow> RuntimeAsync() => Context.JourneyRuntimes
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public Task<JourneyRuntimeRow> RuntimeAsync(string demandId) => Context.JourneyRuntimes
            .AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);

        public Task<AcceptedDemandRow> DemandRowAsync() => Context.AcceptedDemands
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public Task<JourneyBacklogRow> BacklogAsync(string demandId) => Context.JourneyBacklog
            .AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);

        public Task<VehicleDispatchLeaseRow> LeaseAsync() => Context.VehicleDispatchLeases
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public async Task<string> IntentStatusAsync(string purpose) => (await Context.OrderIntents
            .AsNoTracking()
            .SingleAsync(row => row.Purpose == purpose, TestContext.Current.CancellationToken)).Status;

        public Task<StationOperationRow> OperationAsync(SlotOperationType type) => Context.StationOperations
            .AsNoTracking()
            .SingleAsync(row => row.OperationType == type, TestContext.Current.CancellationToken);

        public async Task<string[]> PendingOutboxMessageIdsAsync() => await Context.ProtocolOutbox
            .AsNoTracking()
            .Where(row => row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => row.MessageId)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        public async Task<string[]> OutboxTypesAsync() => await Context.ProtocolOutbox
            .AsNoTracking()
            .OrderBy(row => row.MessageType)
            .Select(row => row.MessageType)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        public async Task AddInboxAsync(
            string messageId,
            string messageType,
            object payload,
            string? correlationId = null)
        {
            JourneyRuntimeRow runtime = await RuntimeAsync();
            string json = JsonSerializer.Serialize(new
            {
                protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                profileId = ProtocolCandidateIdentity.ProfileId,
                protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                messageType,
                messageId,
                correlationId,
                agvId = Options.AgvId,
                sessionGeneration = 1,
                sentAt = Now,
                payload
            }, SerializerOptions);
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = messageType,
                RequestJson = json,
                ContentHash = new string('a', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task ApplySafeResultAsync(
            StationOperationRow operation,
            SlotOperationType type,
            SlotBusinessState state)
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            string resultId = Guid.NewGuid().ToString("D");
            await new WireToGateStore(Context).ApplyOperationResultAsync(
                new StationOperationResult(
                    resultId,
                    operation.SlotOperationAttemptId,
                    operation.DemandId,
                    type,
                    "COMPLETED",
                    slots.Select(slot => new SlotPhysicalEvidence(slot, state, true, true)).ToArray(),
                    true,
                    Now,
                    new string(type == SlotOperationType.Load ? 'b' : 'c', 64),
                    new string(type == SlotOperationType.Load ? 'd' : 'e', 64)),
                Options.AgvId,
                0,
                TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// The result Onboard reports when a station operation runs out its own operator timeout:
        /// the slots were never filled and the batch did not complete. Physical side effects are
        /// unproven from here on, which is what sends the operation to RecoveryRequired.
        /// </summary>
        public async Task ApplyTimedOutResultAsync(StationOperationRow operation, SlotOperationType type)
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            await new WireToGateStore(Context).ApplyOperationResultAsync(
                new StationOperationResult(
                    Guid.NewGuid().ToString("D"),
                    operation.SlotOperationAttemptId,
                    operation.DemandId,
                    type,
                    "FAILED",
                    slots.Select(slot => new SlotPhysicalEvidence(slot, SlotBusinessState.Empty, true, true)).ToArray(),
                    false,
                    Clock.GetUtcNow(),
                    new string('8', 64),
                    new string('9', 64)),
                Options.AgvId,
                0,
                TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Drops the peer the way powering the vehicle down for a repair does: the session row
        /// leaves Ready and stays behind with the reason it left for.
        /// </summary>
        public async Task DropOnboardSessionAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// What DecideReadinessAsync now does to the session on the way through applying a refused
        /// result: the vehicle is told it needs recovery, on the same row the runtime reads to
        /// decide whether it may advance at all. Unlike a dropped session, the peer is still there.
        /// </summary>
        public async Task MarkSessionRecoveryRequiredByOperationAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "OPERATION_RECOVERY_REQUIRED";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// The FP-C13 catalog gate, configured with approved values. Every runtime test faces a
        /// commissioned server, which is what these tests are about; the unapproved case is a gate
        /// test of its own (<see cref="CreateGateTests"/>) rather than a variant of all of them.
        /// </summary>
        private CatalogAvailabilityAccess CreateCatalogAccess() => new(
            new CatalogAvailabilityStore(Context),
            Microsoft.Extensions.Options.Options.Create(CatalogApproved
                ? new MapStationCatalogOptions
                {
                    ApprovedSyncPeriod = TimeSpan.FromSeconds(30),
                    ApprovedMaxUnconfirmed = TimeSpan.FromMinutes(5),
                }
                : new MapStationCatalogOptions()),
            new CatalogAlarmLedger(),
            Clock,
            NullLogger<CatalogAvailabilityAccess>.Instance);

        private JourneyRuntimeEngine CreateEngine()
        {
            WireToGateStore store = new(Context);
            JourneyIntakeCoordinator intake = new(
                new DemandIntakeService(Catalog, store),
                new MovementDispatchService(store, Riot));
            OnboardJourneyPublisher publisher = new(store, Peer, Clock);
            Microsoft.Extensions.Options.IOptions<JourneyRuntimeOptions> options =
                Microsoft.Extensions.Options.Options.Create(Options);
            return new JourneyRuntimeEngine(
                Context,
                Catalog,
                Riot,
                Riot,
                new MapStationResolver(),
                intake,
                new MovementDispatchService(store, Riot),
                store,
                publisher,
                new DispatchAdmissionChain(DispatchAdmissionCriteria.Default(
                    options,
                    new MapStationResolver(),
                    new PackageCapacityStore(Context),
                    store,
                    new VehicleFaultStore(Context),
                    BoxCounts,
                    NullLogger<SlotCapacityCriterion>.Instance,
                    routeGraph: null,
                    catalog: CreateCatalogAccess(),
                    createGate: CreateGate())),
                new FirstSeenDispatchCandidateRanker(),
                CreateCatalogAccess(),
                new CatalogAvailabilityStore(Context),
                CreateGate(),
                new VehicleRoster(options),
                new VehicleDispatchPolicyAccess(new VehicleDispatchPolicyStore(Context), options, Clock),
                Riot,
                CheckpointWaits,
                CreateFaultCoordinator(),
                options,
                Clock,
                NullLogger<JourneyRuntimeEngine>.Instance);
        }

        /// <summary>
        /// The fault model, reachable but never reached by these tests: the engine consults it
        /// only when RIoT reports a leg's order FAILED, and nothing here produces one. It is built
        /// over the real stores so that a test which ever does produce one fails on the behaviour
        /// rather than on a stub that was never taught to answer.
        /// </summary>
        private VehicleFaultCoordinator CreateFaultCoordinator()
        {
            VehicleFaultStore faults = new(Context);
            RiotOrderCommandAuditStore audit = new(Context);
            Microsoft.Extensions.Options.IOptions<VehicleFaultOptions> faultOptions =
                Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions());
            SilentCommandGateway gateway = new(Clock);
            return new VehicleFaultCoordinator(
                faults,
                gateway,
                Riot,
                Riot,
                audit,
                new RiotOrderCommandService(gateway, audit, Riot, Clock),
                new EmergencyStopSupervisor(
                    gateway,
                    gateway,
                    audit,
                    faults,
                    Microsoft.Extensions.Options.Options.Create(new RiotCommandOptions()),
                    Clock,
                    NullLogger<EmergencyStopSupervisor>.Instance),
                new VehicleMotionLedger(faultOptions),
                faultOptions,
                Clock,
                NullLogger<VehicleFaultCoordinator>.Instance);
        }

        private PreCreateGate CreateGate() => new(
            RouteCosts,
            new CatalogAvailabilityStore(Context),
            Clock,
            NullLogger<PreCreateGate>.Instance);

        /// <summary>
        /// A command surface that answers but is never asked here. Every method throws nothing and
        /// records nothing on purpose: if one of these tests ever does drive a leg to FAILED, the
        /// assertion that fails should be about the fault model, not about a double that was left
        /// unable to answer.
        /// </summary>
        private sealed class SilentCommandGateway(TimeProvider clock)
            : IRiotOrderCommandGateway, IRiotVehicleEmergencyFacts
        {
            public Task<RiotCommandCallResult> IssueOrderCommandAsync(
                RiotOrderCommandKind kind,
                string orderId,
                string? reason,
                CancellationToken cancellationToken)
            {
                _ = orderId;
                _ = reason;
                _ = cancellationToken;
                return Task.FromResult(new RiotCommandCallResult(
                    RiotCommandCallDisposition.Accepted,
                    new RiotOrderCallReceipt(
                        RiotCommandTypeNames.For(kind), "SdkAccepted", clock.GetUtcNow())));
            }

            public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
                RiotEmergencyCommandKind kind,
                string deviceKey,
                CancellationToken cancellationToken)
            {
                _ = deviceKey;
                _ = cancellationToken;
                return Task.FromResult(new RiotCommandCallResult(
                    RiotCommandCallDisposition.Accepted,
                    new RiotOrderCallReceipt(
                        RiotCommandTypeNames.For(kind), "SdkAccepted", clock.GetUtcNow())));
            }

            public Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
                string deviceKey,
                CancellationToken cancellationToken)
            {
                _ = cancellationToken;
                return Task.FromResult(new RiotVehicleEmergencyObservation(
                    deviceKey, RiotVehicleEmergencyObservation.Ok, clock.GetUtcNow()));
            }
        }

        private async Task SeedRecoveredPeerAsync()
        {
            Context.SessionRecoveries.Add(new SessionRecoveryRow
            {
                AgvId = Options.AgvId,
                SessionGeneration = 1,
                ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
                ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                ProfileId = ProtocolCandidateIdentity.ProfileId,
                ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                CapabilityRevision = 1,
                CapabilityHash = new string('1', 64),
                SafetyRevision = 7,
                SafetyHash = new string('2', 64),
                DepartureSafe = true,
                RecoveryReportId = Guid.NewGuid().ToString("D"),
                Readiness = SessionReadiness.Ready,
                ReasonCode = "READY",
                UpdatedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddCapabilityAndSafetyAsync(1);
        }

        private async Task AddCapabilityAndSafetyAsync(long generation)
        {
            await AddRawInboxAsync("CapabilitySnapshot", new
            {
                capabilityVersion = 1,
                observedAt = Now,
                slotModelVersion = "SLOT-MODEL-1",
                activeSlotConfigurationVersion = "SLOT-CONFIG-1",
                activeSlotConfigurationFingerprint = new string('0', 64),
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }),
                supportsBatchUnlock = true,
                onboardJournalFormatVersion = 1
            }, generation);
            await AddRawInboxAsync("SafetyStateSnapshot", new
            {
                safetyStateVersion = 7,
                observedAt = Now,
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                })
            }, generation);
        }

        private async Task AddRawInboxAsync(string messageType, object payload, long generation)
        {
            string messageId = Guid.NewGuid().ToString("D");
            string json = JsonSerializer.Serialize(new
            {
                messageType,
                messageId,
                agvId = Options.AgvId,
                sessionGeneration = generation,
                sentAt = Now,
                payload
            }, SerializerOptions);
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = messageType,
                RequestJson = json,
                ContentHash = new string('f', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        private static JourneyRuntimeOptions ValidOptions() => new()
        {
            Enabled = true,
            PollInterval = TimeSpan.FromSeconds(1),
            AgvId = "老厂前线新多仓位1",
            VehicleKey = "BROKERX-0c20ff0600d644869a6a80c186065d85",
            AgvLifecycleGeneration = 1,
            MapId = 25,
            MapIdentity = "MAP-25",
            GateStationId = "关卡",
            GateStationRiotId = 210,
            DispatchZone = "MAP-25-WIRE_TO_GATE",
            DispatchGeneration = 1,
            MinimumBatteryPercent = 40,
            MaximumEvidenceAge = TimeSpan.FromMinutes(2),
            SublotBoxCountPath = "/api/v2/sublot-box-count",
            AllowedWorkTypes = ["WIRE_TO_GATE"],
            AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
            AdmissionPolicyVersion = 1,
            AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1",
            // Off, so a journey leaves the pickup in the iteration its load commits, as every test
            // here was written against. The tests about the wait turn it on for themselves.
            StationDepartureWaitTimeout = TimeSpan.Zero
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class SaveChangesCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public void Reset() => Count = 0;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            Count++;
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingCatalog : IMesIngestCatalog
    {
        private AcceptedDemandSnapshot[] _items = [];
        private int _readCount;

        public Action<int>? BeforeRead { get; set; }

        public void Set(params AcceptedDemandSnapshot[] items) => _items = items;

        public Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            BeforeRead?.Invoke(++_readCount);
            string epoch = _items.FirstOrDefault()?.HistoryEpoch ?? "11111111-1111-4111-8111-111111111111";
            return Task.FromResult(new DemandCatalogSnapshot(epoch, 21, _items));
        }

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_items.SingleOrDefault(item => item.DemandId == demandId));
        }
    }

    private sealed class RecordingBoxCounts : ISublotBoxCountReader
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public Action? BeforeRead { get; set; }

        public void Set(string sublot, int count) => _counts[sublot] = count;
        public void Remove(string sublot) => _counts.Remove(sublot);

        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            BeforeRead?.Invoke();
            return Task.FromResult(_counts.TryGetValue(sublot, out int count) ? (int?)count : null);
        }
    }

    /// <summary>
    /// RIoT's RouteCost, answering reachable unless a test says otherwise.
    /// </summary>
    /// <remarks>
    /// Reachable is the default because these tests are about the journey, not about the gate: a
    /// commissioned server whose vehicle can reach its stations is the world they were written
    /// against. What the gate does with the other answers is <see cref="CreateGateTests"/>'s
    /// subject.
    /// </remarks>
    private sealed class RecordingRouteCostProbe : IRiotRouteCostProbe
    {
        private readonly Dictionary<int, long?> _byStation = [];

        public long DefaultCostMm { get; set; } = 12000;

        public List<(int MapId, int StationId, string VehicleKey)> Calls { get; } = [];

        /// <summary>Answer this station with a specific cost; a negative one means unreachable.</summary>
        public void Set(int stationId, long costMm) => _byStation[stationId] = costMm;

        /// <summary>Make the call itself fail for this station — no answer, not "unreachable".</summary>
        public void FailFor(int stationId) => _byStation[stationId] = null;

        public Task<RiotRouteCost?> ReadRouteCostAsync(
            int mapId,
            int stationId,
            string vehicleKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Calls.Add((mapId, stationId, vehicleKey));
            if (_byStation.TryGetValue(stationId, out long? configured))
            {
                return Task.FromResult(configured is null ? null : new RiotRouteCost(configured.Value));
            }

            return Task.FromResult<RiotRouteCost?>(new RiotRouteCost(DefaultCostMm));
        }
    }

    private sealed class RecordingRiot
        : IRiotMovementGateway, IRiotVehicleFacts, IRiotMapStationCatalog, IVehicleMotionFacts
    {
        private readonly JourneyRuntimeOptions _options;
        private readonly FixedTimeProvider _clock;
        private readonly Dictionary<string, int> _creates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RiotOrderObservation> _orders = new(StringComparer.Ordinal);
        private RiotMapStation[] _mapStations =
        [
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(300, "等待点")
        ];

        public RecordingRiot(JourneyRuntimeOptions options, FixedTimeProvider clock)
        {
            _options = options;
            _clock = clock;
            Vehicle = new RiotVehicleObservation(
                options.VehicleKey,
                Connected: true,
                Enabled: true,
                ProcState: "IDLE",
                CurrentMap: options.MapIdentity,
                CurrentStationId: 1,
                BatteryPercent: 80,
                BatteryState: "NO_CHARGE",
                Speed: 0,
                ObservedAt: clock.GetUtcNow(),
                LockStatus: 0,
                OrderTaskId: null);
        }

        public RiotVehicleObservation Vehicle { get; set; }

        /// <summary>
        /// What RIoT reports in <c>movementState</c>. Null is the ordinary case for these tests:
        /// they are not about motion, and a null state reads as Unknown exactly as a field RIoT
        /// did not send would.
        /// </summary>
        public string? MovementState { get; set; }

        public Action? BeforeReadVehicle { get; set; }
        public bool LoseNextCreateResponse { get; set; }
        public int TotalCreateCount => _creates.Values.Sum();

        public int CreateCount(string purpose) => _creates.GetValueOrDefault(purpose);

        public void SetMapStations(params RiotMapStation[] stations) => _mapStations = stations;

        public Task<RiotMapStationCatalogSnapshot> ReadMapStationsAsync(
            int mapId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new RiotMapStationCatalogSnapshot(
                mapId,
                _clock.GetUtcNow(),
                new string('c', 64),
                _mapStations));
        }

        public void SetSuccessfulArrival(string purpose, int stationId) =>
            SetSuccessfulArrival(purpose, UpperId(purpose), stationId);

        /// <summary>
        /// Takes the upperId from the runtime row, which is the only way to reach a journey whose
        /// demand is not the one <see cref="UpperId"/> hardcodes.
        /// </summary>
        public void SetSuccessfulArrival(string purpose, string upperId, int stationId)
        {
            _orders[upperId] = new RiotOrderObservation(
                upperId,
                RiotOrderObservationKind.Terminal,
                $"ORDER-{purpose}",
                OrderState: 5,
                VehicleKey: _options.VehicleKey,
                MapId: _options.MapId,
                DestinationStationId: stationId);
        }

        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            BeforeReadVehicle?.Invoke();
            return Task.FromResult(Vehicle with { VehicleKey = vehicleKey, ObservedAt = _clock.GetUtcNow() });
        }

        public Task<VehicleMotionSample> SampleMotionAsync(string deviceKey, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new VehicleMotionSample(
                deviceKey,
                HttpRiotMovementGateway.ReadMotion(MovementState, Vehicle.Speed),
                MovementState,
                Vehicle.Speed,
                Vehicle.CurrentMap,
                Vehicle.CurrentStationId,
                _clock.GetUtcNow()));
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_orders.TryGetValue(upperId, out RiotOrderObservation? order)
                ? order
                : new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _creates[intent.Purpose] = CreateCount(intent.Purpose) + 1;
            RiotOrderObservation active = new(
                intent.UpperId,
                RiotOrderObservationKind.Active,
                $"ORDER-{intent.Purpose}",
                OrderState: 3,
                VehicleKey: intent.VehicleKey,
                MapId: intent.MapId,
                DestinationStationId: intent.DestinationStationId);
            _orders[intent.UpperId] = active;
            if (LoseNextCreateResponse)
            {
                LoseNextCreateResponse = false;
                return Task.FromResult(new RiotOrderObservation(
                    intent.UpperId, RiotOrderObservationKind.Unknown, null));
            }
            return Task.FromResult(active);
        }

        private static string UpperId(string purpose) => purpose switch
        {
            "TO_PICKUP" => "W2G-10000000-0000-4000-8000-000000000001-PICKUP-1",
            "TO_GATE" => "W2G-10000000-0000-4000-8000-000000000001-GATE-1",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
    }

    private sealed class RecordingPeer : IOnboardPeer
    {
        public List<byte[]> Lines { get; } = [];

        /// <summary>
        /// Lets a test answer a command the moment it is sent, the way the real peer does. Without
        /// it an answer can only be staged before an iteration, which hides everything that depends
        /// on how long the server takes to come back and read it.
        /// </summary>
        public Func<string, Task>? OnMessageSent { get; set; }

        public async Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Lines.Add(ndjsonLine.ToArray());
            if (OnMessageSent is not null)
            {
                await OnMessageSent(System.Text.Encoding.UTF8.GetString(ndjsonLine.Span)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Answers snapshots the way <c>OnboardHmi_MVP@304e6ad</c> does.
    /// </summary>
    /// <remarks>
    /// That peer keys an adopted snapshot on its message type alone, journals it in SQLite and
    /// never deletes the row, so the revision it holds survives a reconnect. A revision below the
    /// one it holds is refused as SNAPSHOT_REVISION_REGRESSION and never acknowledged; a duplicate
    /// at the revision it already holds is journalled and acknowledged again, because the journal
    /// write and the acknowledgement are sequential statements with no early exit between them.
    /// Acknowledgements are buffered rather than applied on receipt so that a test can lose exactly
    /// the ones a peer had in flight when its connection dropped.
    /// </remarks>
    private sealed class AdoptingPeer(ControlServerDbContext context, TimeProvider clock)
    {
        private readonly Dictionary<string, long> _journal = new(StringComparer.Ordinal);
        private readonly List<(string MessageId, string MessageType, string ContentSha256, long Revision)> _buffered = [];

        public List<(string MessageType, long Delivered, long Held)> Regressions { get; } = [];

        public List<(string MessageType, long Revision)> Adopted { get; } = [];

        public void Receive(string ndjsonLine)
        {
            string wire = ndjsonLine.TrimEnd('\n');
            using JsonDocument document = JsonDocument.Parse(wire);
            JsonElement root = document.RootElement;
            string messageType = root.GetProperty("messageType").GetString()!;
            string? revisionProperty = SnapshotRevisionProperty(messageType);
            if (revisionProperty is null)
            {
                return;
            }

            long revision = root.GetProperty("payload").GetProperty(revisionProperty).GetInt64();
            if (_journal.TryGetValue(messageType, out long held) && revision < held)
            {
                Regressions.Add((messageType, revision, held));
                return;
            }

            if (!_journal.TryGetValue(messageType, out held) || revision != held)
            {
                Adopted.Add((messageType, revision));
            }

            _journal[messageType] = revision;
            _buffered.Add((
                root.GetProperty("messageId").GetString()!,
                messageType,
                Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(wire)))
                    .ToLowerInvariant(),
                revision));
        }

        public async Task DeliverBufferedAcksAsync()
        {
            WireToGateStore store = new(context);
            foreach ((string messageId, string messageType, string contentSha256, long revision) in _buffered)
            {
                await store.AcknowledgeOutboundEnvelopeAsync(
                    messageId,
                    messageType,
                    contentSha256,
                    revision,
                    clock.GetUtcNow(),
                    TestContext.Current.CancellationToken);
            }

            _buffered.Clear();
        }

        public void LoseBufferedAcks() => _buffered.Clear();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
