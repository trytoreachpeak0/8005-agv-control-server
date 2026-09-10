using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

public sealed class JourneyRuntimeWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] UnknownReasonCodes = ["IO_FACT_UNKNOWN"];

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-04")]
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

        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
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
        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "SublotEntryRequested", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
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

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-04")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-04")]
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

        SingleDemandJourneyView accepted = await fixture.RuntimeAsync(next.DemandId);
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
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-04")]
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

        (string StationId, long Revision, string Role, DateTimeOffset? Deadline, DateTimeOffset SentAt)[] worklists =
            fixture.Peer.Lines
                .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)))
                .Where(document => document.RootElement.GetProperty("messageType").GetString()
                    == "CurrentStopWorklistSnapshot")
                .Select(document => (Root: document.RootElement, Payload: document.RootElement.GetProperty("payload")))
                .Select(message => (
                    message.Payload.GetProperty("stationId").GetString()!,
                    message.Payload.GetProperty("worklistRevision").GetInt64(),
                    message.Payload.GetProperty("items").EnumerateArray().First()
                        .GetProperty("stopRole").GetString()!,
                    message.Payload.GetProperty("stationDepartureDeadlineAt") is { ValueKind: JsonValueKind.Null }
                        ? (DateTimeOffset?)null
                        : message.Payload.GetProperty("stationDepartureDeadlineAt").GetDateTimeOffset(),
                    message.Root.GetProperty("sentAt").GetDateTimeOffset()))
                .Distinct()
                .ToArray();

        Assert.Equal(2, worklists.Length);
        Assert.Equal(2, worklists.Select(item => item.StationId).Distinct().Count());
        Assert.Equal(2, worklists.Select(item => item.Revision).Distinct().Count());

        // ADR-cross-0058 decision 3: the station departure deadline is the server's, and the
        // worklist is where the vehicle reads it. It exists only where an operator is waited for --
        // the pickup stop has one and it is still ahead of the message carrying it, the gate stop
        // has none at all (ADR-cross-0015: unloading has no operator wait to bound), and null there
        // is the value that tells the vehicle to show no countdown rather than an expired one.
        (string StationId, long Revision, string Role, DateTimeOffset? Deadline, DateTimeOffset SentAt) pickup =
            worklists.Single(item => item.Role == "PICKUP");
        Assert.NotNull(pickup.Deadline);
        Assert.True(pickup.Deadline > pickup.SentAt);
        Assert.Null(worklists.Single(item => item.Role == "GATE").Deadline);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-04")]
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

        SingleDemandJourneyView second = await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000002");
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
            // second. That is three distinct revisions, and the sequence may never step back.
            Assert.Equal(revisions.Order(), revisions);
            Assert.Equal(3, revisions.Distinct().Count());
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
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
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
        // to be holding the gate revision of all three snapshot types.
        foreach (IGrouping<string, long> stream in peer.Adopted.GroupBy(item => item.MessageType, item => item.Revision))
        {
            Assert.Equal(2, stream.Distinct().Count());
        }

        Assert.Equal(3, peer.Adopted.Select(item => item.MessageType).Distinct().Count());
        Assert.Empty(peer.Regressions);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
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
        Assert.All(peer.Regressions, item =>
        {
            Assert.Equal(1, item.Delivered);
            Assert.Equal(2, item.Held);
        });
    }

    [Theory]
    [InlineData(3, 7, true)]
    [InlineData(4, 7, false)]
    [InlineData(3, 6, false)]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
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
        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
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
    [Trait("IntegrationSlice", "W2G-IS-06")]
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
        SingleDemandJourneyView runtime = await fixture.AdvanceToDepartureSafetyAsync();

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
    [Trait("IntegrationSlice", "W2G-IS-06")]
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
        SingleDemandJourneyView runtime = await fixture.RunToCompletionAsync();

        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        string[] stillPending = await fixture.PendingOutboxMessageIdsAsync();

        // The safety check was answered by a PreDepartureSafetyCheckResult, the unload command by an
        // OperationResult. Neither answer is a DurableAck, so nothing else can settle these rows.
        Assert.DoesNotContain(runtime.PreDepartureSafetyCheckMessageId, stillPending);
        Assert.DoesNotContain(runtime.UnloadCommandMessageId, stillPending);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
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

        SingleDemandJourneyView runtime = await fixture.AdvanceToDepartureSafetyAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
        Assert.InRange(fixture.SaveChanges.Count, 1, 10);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
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
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
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

        SingleDemandJourneyView blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", blocked.BlockReasonCode);

        await fixture.DropOnboardSessionAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        SingleDemandJourneyView afterDrop = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, afterDrop.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", afterDrop.BlockReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ADeterminateLoadFailureDoesNotBlockTheJourney()
    {
        // The counterpart to the test above, and the whole point of ADR-cross-0058 decision 5.
        // Same station timeout, same vehicle, one difference: this time the vehicle could read its
        // slots and reported them -- empty, locked, unlock output reset. That is a complete account
        // of a failure, so no administrator is needed and the journey is not held. It stays in
        // AwaitingLoadResult, where LoadTaskCancellation settles the demand (ADR-cross-0015,
        // ADR-cross-0046); what stops the vehicle occupying the station forever is
        // ADR-cross-0055's StationDepartureWaitTimeout, not a block.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplyTimedOutResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load),
            SlotOperationType.Load,
            determinate: true);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        SingleDemandJourneyView settled = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, settled.Stage);
        Assert.NotEqual("LOAD_RESULT_REQUIRES_RECOVERY", settled.BlockReasonCode);
        Assert.Equal(
            StationOperationStatus.Failed,
            (await fixture.OperationAsync(SlotOperationType.Load)).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
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

        SingleDemandJourneyView blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", blocked.BlockReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task SupportsBatchUnlockFalseDoesNotBlockAdmission()
    {
        // protocol-v0.1.1 declares supportsBatchUnlock with no semantics and its own canonical
        // example sets it false. Whether the vehicle can operate a given slot set is decided
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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

        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task RestartKeepsOriginalVehicleLeaseAndStablePickupIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        SingleDemandJourneyView before = await fixture.RuntimeAsync();
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        SingleDemandJourneyView after = await fixture.RuntimeAsync();

        Assert.Equal(before.DemandId, after.DemandId);
        Assert.Equal(before.PickupMovementLegId, after.PickupMovementLegId);
        Assert.Equal(before.PickupUpperId, after.PickupUpperId);
        Assert.Equal(1, await fixture.Context.VehicleDispatchLeases.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task RestartReconcilesUnknownPickupCreateWithoutSecondOrderOrIdentityChange()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.LoseNextCreateResponse = true;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        SingleDemandJourneyView before = await fixture.RuntimeAsync();
        Assert.Equal("RESULT_UNKNOWN", (await fixture.Context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        SingleDemandJourneyView after = await fixture.RuntimeAsync();
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task RestartReenvelopesPendingServerMessagesForTheCurrentSessionWithoutChangingSemantics()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        SingleDemandJourneyView initialRuntime = await fixture.RuntimeAsync();
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
            Assert.Equal(2, document.RootElement.GetProperty("sessionGeneration").GetInt64());
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(originalPayloads[row.MessageId]),
                JsonNode.Parse(document.RootElement.GetProperty("payload").GetRawText())));
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task UnknownPreDepartureSafetyCannotCreateTheGateOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        SingleDemandJourneyView runtime = await fixture.AdvanceToDepartureSafetyAsync();
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

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task APickupStopWithNothingToLoadEndsTheDemandWhenTheSublotWaitExpiresAndFreesTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal(Now, runtime.SublotWaitStartedAt);

        // One minute short of the window: the stop is still open, and an operator walking back to
        // the vehicle must not find the demand gone.
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await fixture.DemandRowAsync()).Status);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.NotNull((await fixture.Context.VehicleDispatchLeases.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken)).ReleasedAt);
        // Nothing was ever commanded to a slot, which is what lets the server end this alone.
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        // The entry request is answered by the cancellation. Left unsettled it would replay into
        // the next session as a business id whose content changed and tear that session down.
        ProtocolOutboxRow request = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SublotEntryRequested", TestContext.Current.CancellationToken);
        Assert.NotNull(request.AcknowledgedAt);

        // The point of ending it: the vehicle takes the next demand rather than holding the stop.
        AcceptedDemandSnapshot next = fixture.Demand(
            "10000000-0000-4000-8000-000000000002",
            "SUBLOT-002",
            createdAt: fixture.Clock.GetUtcNow().AddMinutes(-1));
        fixture.Catalog.Set(next);
        fixture.BoxCounts.Set("SUBLOT-002", 7);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow nextBacklog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == "10000000-0000-4000-8000-000000000002",
                TestContext.Current.CancellationToken);
        Assert.Equal("ACCEPTED", nextBacklog.ReasonCode);
        SingleDemandJourneyView second = await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000002");
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, second.Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task ADisconnectVoidsTheStationDeadlineAndTheClockRefillsAfterTheHandshake()
    {
        // ADR-cross-0055: "倒计时期间断联使本轮截止时间失效，恢复握手和投影对账完成后重新计满."
        // The counterpart to the timeout test above. Same window, same stop -- the difference is
        // that the vehicle was gone for longer than the window. A wall clock that kept running
        // while nobody could scan would cancel the demand on the first pass after the vehicle came
        // back, and the operator would find it gone without ever having had the chance to load it.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal(Now, runtime.SublotWaitStartedAt);

        // Offline for nine minutes -- comfortably past the five-minute window -- and back with a
        // new session generation. This is the real reconnect path, not a poke at the session row:
        // BeginSessionRecoveryAsync is what the peer's SessionHello reaches.
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        await new WireToGateStore(fixture.Context).BeginSessionRecoveryAsync(
            new SessionIdentity(
                fixture.Options.AgvId,
                2,
                ProtocolCandidateIdentity.RepositoryCommit,
                ProtocolCandidateIdentity.ManifestSha256,
                ProtocolCandidateIdentity.ProfileId,
                ProtocolCandidateIdentity.ProtocolVersion),
            TestContext.Current.CancellationToken);

        // The generation change alone voids the round. Nothing has refilled it yet, because the
        // handshake is not finished -- readiness is back at HANDSHAKE_INCOMPLETE.
        Assert.Null((await fixture.RuntimeAsync()).SublotWaitStartedAt);

        // Handshake and projection reconciliation complete: capability and safety are back and the
        // session is Ready again.
        await fixture.AdvanceSessionAsync(2);
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // Refilled from now, not resumed from nine minutes ago -- so the demand survives and the
        // operator gets the whole window.
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal(Now.AddMinutes(9), runtime.SublotWaitStartedAt);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        // And the refilled window still expires on its own: this is a reset, not a reprieve.
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnExpiredStationDeadlineDoesNotCloseTheStopWhileASlotDoorIsStillOpen()
    {
        // ADR-cross-0058 decision 4, the square the baseline never covered. StopClosureCommit
        // presumes the vehicle can then leave, and ADR-cross-0011/0012 forbid moving with an open
        // slot door -- so closing the stop here would produce a journey that is finished on paper
        // and immobile in fact. The deadline instead raises an alarm and keeps waiting.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        Assert.Equal(Now, runtime.SublotWaitStartedAt);

        // The operator opened a slot, wandered off, and never pushed the door shut.
        await fixture.ReportDoorLeftOpenAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // Past the deadline, and deliberately still here: an alarm on an open stop, not a block.
        // Nothing about this needs an administrator -- it needs someone to close a door.
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        // Waiting does not decay into closing, however long it lasts.
        fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        // Door shut: the next pass settles against the reading of that moment, exactly as the ADR
        // says -- "闭合后立即按当时的真实 IO 读数结算".
        await fixture.ReportDoorClosedAsync();
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnExpiredStationDeadlineDoesNotCloseTheStopOnAnUnreadableSafetyPicture()
    {
        // The same guard, drawn at certainty rather than at the door: ADR-cross-0055 lists
        // "状态未知或断联" alongside an active slot operation as things that block the countdown
        // from closing the stop. A server that cannot read the physical world must not act on it.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.AdvanceToSublotWaitAsync("10000000-0000-4000-8000-000000000001", "SUBLOT-001");

        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(
            TestContext.Current.CancellationToken);
        session.SafetyUnknownPresent = true;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task TheSublotWaitTimeoutStopsApplyingOnceTheLoadIsUnderway()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        await fixture.SubmitSublotAsync(runtime, "SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);

        // Far past the window. A slot operation is outstanding, and only the peer can settle the
        // physical state it left behind -- timing out here would drop the demand mid-load.
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnExpiredDeadlineRaisesTheDoorAlarmWhileALoadIsStillUnderway()
    {
        // ADR-cross-0058 decision 4's other half. The alarm branch lived only in
        // TryTimeOutSublotWaitAsync, which never runs in AwaitingLoadResult -- and a slot operation
        // is only ever underway in AwaitingLoadResult. So the one stage where a door can actually
        // stand open past the deadline was the one stage that never said so, and the journey sat
        // there with nothing naming why.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        SingleDemandJourneyView runtime = await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);

        await fixture.ReportDoorLeftOpenAsync();
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // Inside the window an open door is not an alarm, it is a load in progress.
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", runtime.BlockReasonCode);
        // An alarm, not a block. Nothing here needs an administrator -- it needs someone to close
        // a door -- and the demand stays live while that happens.
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AnOpenDoorNoCommandOfOursExplainsLeavesTheSessionUnusable()
    {
        // Which of decision 4's two squares is reachable is decided outside AdvanceAsync entirely,
        // by whether DecideReadinessAsync can explain the open door -- so it has to be measured
        // through that path, not by writing the safety fields the fixture way.
        //
        // AwaitingSublot is the square where nothing explains it: the stage is defined by having
        // commanded no slot operation at all, so a door reading open here was opened by nobody this
        // server can name. That is departure safety the vehicle cannot vouch for, and the session
        // is correctly unusable -- the journey stops at the readiness gate and says so. Decision 4's
        // alarm does not belong here and never fires here; it belongs to the other square.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");

        SessionReadinessDecision decision = await fixture.ReportDoorLeftOpenThroughRealPathAsync();
        Assert.Equal(SessionReadiness.RecoveryRequired, decision.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", decision.ReasonCode);

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", runtime.BlockReasonCode);
        // The bottom line holds by a different route than the ADR drew: the gate stops the runtime
        // before anything can close a stop against an open door.
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ADoorOurOwnLoadHoldsOpenKeepsTheSessionReadyAndReachesTheAlarm()
    {
        // The other square, measured the same way. A load is underway, so the operation that
        // unlocked the slot is Prepared, IsUnsafetyExplainedByOwnCommandAsync grants the exemption,
        // and the session stays Ready -- which is what lets AdvanceAsync run far enough to raise
        // decision 4's alarm. This is the square ADR-cross-0058 names in its consequences.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();

        SessionReadinessDecision decision = await fixture.ReportDoorLeftOpenThroughRealPathAsync();
        Assert.Equal(SessionReadiness.Ready, decision.Readiness);
        Assert.Equal("READY", decision.ReasonCode);

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task TheDoorAlarmIsWithdrawnOnceTheDoorIsShutAgain()
    {
        // The alarm names a condition, not an event: once the door is shut the vehicle can settle
        // the slot itself -- past the deadline it drives it to a determinate failure -- and a stop
        // still reading STATION_TIMEOUT_DOOR_NOT_CLOSED against eight locked doors would send
        // someone looking for a door that is already closed.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ReportDoorLeftOpenAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.ReportDoorClosedAsync();
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        SingleDemandJourneyView runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task ASublotWaitTimeoutOfZeroLeavesTheStopOpenIndefinitely()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.SublotWaitTimeout = TimeSpan.Zero;
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);

        fixture.Clock.Advance(TimeSpan.FromHours(4));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    /// <summary>
    /// ADR-cross-0057's baseline shape: one journey, one stop, several demands. The operator scans
    /// the second sublot first, which is the point -- entry decides which demand loads, and the
    /// worklist offers the whole dispatch range rather than one expected string.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task OneJourneyLoadsSeveralDemandsAtOneStopAndUnloadsThemOneAtATime()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)),
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", createdAt: Now.AddMinutes(-9)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();

        // Both demands ride one journey, at one stop, holding disjoint slots.
        JourneyDemandRow[] demands = await fixture.DemandRowsAsync();
        Assert.Equal(2, demands.Length);
        Assert.Single(demands.Select(row => row.JourneyId).Distinct(StringComparer.Ordinal));
        Assert.Single(demands.Select(row => row.StopSequence).Distinct());
        Assert.Equal(
            [1, 2, 3, 4],
            demands.SelectMany(row => JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson)!).Order());
        Assert.Single(await fixture.Context.VehicleDispatchLeases
            .AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));

        // The entry request offers the whole range, not one expected sublot (FR-001 AC-3).
        Assert.Equal(
            ["SUBLOT-001", "SUBLOT-002"],
            (await fixture.SublotEntryRequestSublotsAsync()).Order(StringComparer.Ordinal));
        Assert.Equal(2, await fixture.WorklistItemCountAsync());

        // Scanned out of order on purpose: the sublot picks the demand.
        await fixture.LoadSublotAsync("SUBLOT-002");
        Assert.Equal(
            JourneyDemandState.Loaded,
            (await fixture.DemandRowsAsync())
                .Single(row => row.DemandId == "10000000-0000-4000-8000-000000000002").State);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.JourneyRowAsync()).Stage);
        Assert.Equal(["SUBLOT-001"], await fixture.SublotEntryRequestSublotsAsync());

        await fixture.LoadSublotAsync("SUBLOT-001");

        // Nothing left to load and nothing else on offer, so the journey leaves for the gate.
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.JourneyRowAsync()).Stage);
        await fixture.ConfirmDepartureSafeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.JourneyRowAsync()).Stage);

        await fixture.ArriveAtCurrentStopAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.JourneyRowAsync()).Stage);
        // One door at a time: only one unload is commanded before its batch closes.
        Assert.Single(await fixture.DemandRowsAsync(), row => row.UnloadCommandedAt is not null);

        await fixture.UnloadCurrentAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.JourneyRowAsync()).Stage);
        await fixture.UnloadCurrentAsync();

        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.JourneyRowAsync()).Stage);
        Assert.All(
            await fixture.DemandRowsAsync(),
            row => Assert.Equal(JourneyDemandState.Unloaded, row.State));
        Assert.All(
            await fixture.Context.AcceptedDemands.AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken),
            row => Assert.Equal(DemandExecutionStatus.Succeeded, row.Status));
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
    }

    /// <summary>
    /// A journey grows a stop sequence: demands whose pickup stations differ are visited in turn,
    /// each stop publishing its own snapshots, before the vehicle leaves for the gate.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task OneJourneyVisitsSeveralPickupStopsBeforeTheGate()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(
                "10000000-0000-4000-8000-000000000001", "SUBLOT-001",
                createdAt: Now.AddMinutes(-10), area: "N1-1", eqp: "EQP-01"),
            fixture.Demand(
                "10000000-0000-4000-8000-000000000002", "SUBLOT-002",
                createdAt: Now.AddMinutes(-9), area: "N1-2", eqp: "EQP-02"));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();

        // Two pickup stops at different stations, plus the gate. The gate keeps its fixed sequence
        // above every pickup stop so appending one never renumbers it.
        JourneyStopRow[] stops = await fixture.StopRowsAsync();
        Assert.Equal(3, stops.Length);
        Assert.Equal([12, 13, 210], stops.Select(row => row.StationRiotId).Order());
        Assert.Equal(9, stops.Single(row => row.Role == JourneyStopRole.Gate).Sequence);
        // Only the stop the vehicle is at offers entry; the other one is still planned.
        Assert.Equal(1, await fixture.WorklistItemCountAsync());
        // Entry is scoped to the dispatch range, not to the stop -- BR-001 makes it set membership.
        Assert.Equal(
            ["SUBLOT-001", "SUBLOT-002"],
            (await fixture.SublotEntryRequestSublotsAsync()).Order(StringComparer.Ordinal));

        await fixture.LoadSublotAsync("SUBLOT-001");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.JourneyRowAsync()).Stage);
        // Leaving for the second pickup stop, not the gate: loading is not over.
        JourneyRuntimeRow leaving = await fixture.JourneyRowAsync();
        Assert.Null(leaving.LoadingClosedReason);
        Assert.Equal(2, leaving.NextStopSequence);

        await fixture.ConfirmDepartureSafeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.JourneyRowAsync()).Stage);
        await fixture.ArriveAtCurrentStopAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.JourneyRowAsync()).Stage);

        await fixture.LoadSublotAsync("SUBLOT-002");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.JourneyRowAsync()).Stage);
        Assert.Equal(9, (await fixture.JourneyRowAsync()).NextStopSequence);

        // Every stop published at its own revision, strictly increasing: the peer refuses a
        // revision that does not advance, and two stops sharing one would look like that.
        long[] worklistRevisions = (await fixture.StopRowsAsync())
            .Where(row => row.WorklistRevision > 0)
            .Select(row => row.WorklistRevision)
            .ToArray();
        Assert.Equal(worklistRevisions.Distinct().Count(), worklistRevisions.Length);

        await fixture.ConfirmDepartureSafeAsync();
        await fixture.ArriveAtCurrentStopAsync();
        await fixture.UnloadCurrentAsync();
        await fixture.UnloadCurrentAsync();

        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.JourneyRowAsync()).Stage);
        Assert.All(
            await fixture.Context.AcceptedDemands.AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken),
            row => Assert.Equal(DemandExecutionStatus.Succeeded, row.Status));
        Assert.All(
            await fixture.StopRowsAsync(),
            row => Assert.Equal(JourneyStopState.Completed, row.State));
    }

    /// <summary>
    /// FR-001 AC-3: what the operator enters need not belong to the stop they are standing at, only
    /// to the dispatch range. BR-001 allows a range to span neighbouring stations, so a sublot
    /// entered here is loaded here -- and the stop it was planned for drops out of the itinerary.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task ASublotEnteredAwayFromItsPlannedStopIsLoadedWhereItWasEntered()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(
                "10000000-0000-4000-8000-000000000001", "SUBLOT-001",
                createdAt: Now.AddMinutes(-10), area: "N1-1", eqp: "EQP-01"),
            fixture.Demand(
                "10000000-0000-4000-8000-000000000002", "SUBLOT-002",
                createdAt: Now.AddMinutes(-9), area: "N1-2", eqp: "EQP-02"));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();

        JourneyStopRow[] planned = await fixture.StopRowsAsync();
        Assert.Equal(
            2,
            (await fixture.DemandRowsAsync()).Select(row => row.StopSequence).Distinct().Count());
        Assert.Equal(3, planned.Length);

        // The operator at the first stop is holding the second stop's sublot and scans it.
        await fixture.LoadSublotAsync("SUBLOT-002");
        JourneyRuntimeRow journey = await fixture.JourneyRowAsync();
        JourneyDemandRow moved = (await fixture.DemandRowsAsync())
            .Single(row => row.DemandId == "10000000-0000-4000-8000-000000000002");
        Assert.Equal(JourneyDemandState.Loaded, moved.State);
        Assert.Equal(journey.CurrentStopSequence, moved.StopSequence);

        // Its own sublot is still on offer, at this stop, and loads here too.
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, journey.Stage);
        Assert.Equal(["SUBLOT-001"], await fixture.SublotEntryRequestSublotsAsync());
        await fixture.LoadSublotAsync("SUBLOT-001");

        // Both are aboard from one stop, so the second pickup stop has nothing to do and the
        // vehicle heads straight for the gate.
        journey = await fixture.JourneyRowAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.Equal(9, journey.NextStopSequence);
        Assert.Equal(
            JourneyStopState.Planned,
            (await fixture.StopRowsAsync()).Single(row => row.Sequence == 2).State);
    }

    /// <summary>
    /// "Full" is not every slot occupied: it is too few free to take the next candidate whole, since
    /// a demand's baskets have to go on in one go.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task LoadingEndsWhenTheFreeSlotsCannotTakeTheNextDemandWhole()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        // 15 boxes at 4 per basket is 4 baskets, so two demands fill all eight slots and the third
        // cannot go on whole -- which is what "full" means here, not "no slot is empty".
        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)),
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", createdAt: Now.AddMinutes(-9)),
            fixture.Demand("10000000-0000-4000-8000-000000000003", "SUBLOT-003", createdAt: Now.AddMinutes(-8)));
        fixture.BoxCounts.Set("SUBLOT-001", 15);
        fixture.BoxCounts.Set("SUBLOT-002", 15);
        fixture.BoxCounts.Set("SUBLOT-003", 15);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();

        JourneyDemandRow[] demands = await fixture.DemandRowsAsync();
        Assert.Equal(2, demands.Length);
        Assert.All(demands, row => Assert.Equal(4, row.ExpectedBasketCount));
        Assert.DoesNotContain("10000000-0000-4000-8000-000000000003", demands.Select(row => row.DemandId));
        Assert.Equal(
            "SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE",
            (await fixture.BacklogAsync("10000000-0000-4000-8000-000000000003")).ReasonCode);

        await fixture.LoadSublotAsync("SUBLOT-001");
        await fixture.LoadSublotAsync("SUBLOT-002");

        JourneyRuntimeRow journey = await fixture.JourneyRowAsync();
        Assert.Equal("VEHICLE_FULL", journey.LoadingClosedReason);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
    }

    /// <summary>
    /// The holding limit runs from the first LoadBatch that closed safely, and ends the loading
    /// phase even while more cargo is on offer.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task LoadingEndsWhenTheVehicleHasHeldCargoTooLong()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.HoldingTimeout = TimeSpan.FromMinutes(30);
        // The station wait is the other deadline and would end the stop first. Off, so that what is
        // measured here is only the holding clock.
        fixture.Options.SublotWaitTimeout = TimeSpan.Zero;
        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)),
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", createdAt: Now.AddMinutes(-9)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();

        // Nothing is aboard yet, so no clock is running: this much time passing changes nothing.
        Assert.Null((await fixture.JourneyRowAsync()).HoldingStartedAt);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Null((await fixture.JourneyRowAsync()).LoadingClosedReason);

        await fixture.LoadSublotAsync("SUBLOT-001");
        DateTimeOffset? startedAt = (await fixture.JourneyRowAsync()).HoldingStartedAt;
        Assert.NotNull(startedAt);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.JourneyRowAsync()).Stage);

        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow journey = await fixture.JourneyRowAsync();
        Assert.Equal("HOLDING_TIMEOUT", journey.LoadingClosedReason);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        // The clock did not restart on the second batch: it is the first one that started it.
        Assert.Equal(startedAt, journey.HoldingStartedAt);
        // SUBLOT-002 never loaded, and it is not carried to the gate unloadable either: a hard
        // deadline terminates what it abandons, under the reason for "loading here is finished".
        Assert.Equal(
            JourneyDemandState.Cancelled,
            (await fixture.DemandRowsAsync())
                .Single(row => row.DemandId == "10000000-0000-4000-8000-000000000002").State);
        Assert.Equal(
            "CANCELLED_BY_STOP_COMPLETE",
            (await fixture.Context.TransportDemandSuppressions.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking()
            .Where(row => row.DemandId == "10000000-0000-4000-8000-000000000002")
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The holding limit does not interrupt a slot operation that is under way. ADR-cross-0057 puts
    /// the convergence of physical safety ahead of the deadline, the same way ADR-cross-0055 does.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task TheHoldingLimitWaitsForTheBatchInProgressToCloseSafely()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.HoldingTimeout = TimeSpan.FromMinutes(30);
        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)),
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", createdAt: Now.AddMinutes(-9)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();
        await fixture.LoadSublotAsync("SUBLOT-001");

        // The second batch is commanded and outstanding when the limit passes.
        await fixture.ScanSublotAsync("SUBLOT-002");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.JourneyRowAsync()).Stage);

        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.JourneyRowAsync()).Stage);
        Assert.Null((await fixture.JourneyRowAsync()).LoadingClosedReason);

        JourneyDemandRow loading = (await fixture.DemandRowsAsync())
            .Single(row => row.State == JourneyDemandState.Planned && row.LoadCommandedAt is not null);
        StationOperationRow load = await fixture.OperationForAsync(loading.LoadSlotOperationAttemptId);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // The batch closed, both demands are aboard, and only then does the phase end.
        JourneyRuntimeRow journey = await fixture.JourneyRowAsync();
        Assert.Equal("HOLDING_TIMEOUT", journey.LoadingClosedReason);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.All(
            await fixture.DemandRowsAsync(),
            row => Assert.Equal(JourneyDemandState.Loaded, row.State));
    }

    /// <summary>
    /// The station wait ends the stop, not the journey: ADR-cross-0055 and this are two independent
    /// deadlines, and cargo already aboard still goes to the gate.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task TheStationWaitEndsTheStopWhileTheCargoAlreadyAboardStillReachesTheGate()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)),
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", createdAt: Now.AddMinutes(-9)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();
        await fixture.LoadSublotAsync("SUBLOT-001");

        // Nobody scans the second one. The stop's own window runs out.
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyDemandRow[] demands = await fixture.DemandRowsAsync();
        Assert.Equal(
            JourneyDemandState.Cancelled,
            demands.Single(row => row.DemandId == "10000000-0000-4000-8000-000000000002").State);
        Assert.Equal(
            JourneyDemandState.Loaded,
            demands.Single(row => row.DemandId == "10000000-0000-4000-8000-000000000001").State);
        // FR-004: the cancellation bars the business key, and it is the station timeout that did it.
        Assert.Equal(
            "CANCELLED_BY_STATION_TIMEOUT",
            (await fixture.Context.TransportDemandSuppressions.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);

        // The journey is not over -- the first demand's cargo is aboard and bound for the gate.
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.JourneyRowAsync()).Stage);
        await fixture.ConfirmDepartureSafeAsync();
        await fixture.ArriveAtCurrentStopAsync();
        await fixture.UnloadCurrentAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.JourneyRowAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task ALowVehicleDrivesItselfToTheChargerAndBecomesAvailableAgainAtTheResumeLevel()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.EnableAutoCharging();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 15 };
        // A demand is waiting the whole time. It must not be accepted while the vehicle is on its
        // way to the pad, and it must be accepted once the run releases the vehicle.
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        AutoChargingRunRow run = await fixture.ChargingRunAsync();
        Assert.Equal(AutoChargingStage.AwaitingChargerArrival, run.Stage);
        Assert.Equal(211, run.ChargerStationRiotId);
        Assert.Equal("充电准备点1", run.ChargerStationId);
        Assert.Equal(15, run.TriggeredAtBatteryPercent);
        Assert.Null(run.BlockReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_CHARGER"));
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_CHARGER"));
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        // A second iteration on the way there must not raise a second order -- a duplicate here is
        // a duplicate real dispatch.
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_CHARGER"));
        Assert.Equal(AutoChargingStage.AwaitingChargerArrival, (await fixture.ChargingRunAsync()).Stage);

        fixture.Riot.SetSuccessfulArrival("TO_CHARGER", run.UpperId, run.ChargerStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with
        {
            CurrentStationId = run.ChargerStationRiotId,
            BatteryState = "CHARGING",
            BatteryPercent = 22
        };
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        run = await fixture.ChargingRunAsync();
        Assert.Equal(AutoChargingStage.Charging, run.Stage);
        Assert.Null(run.BlockReasonCode);

        // Above the demand floor but below the resume level: still charging, still not available.
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 55 };
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AutoChargingStage.Charging, (await fixture.ChargingRunAsync()).Stage);
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 80 };
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        run = await fixture.ChargingRunAsync();
        Assert.Equal(AutoChargingStage.Completed, run.Stage);
        Assert.Equal(80, run.ReleasedAtBatteryPercent);
        // The vehicle stays plugged in and keeps reporting CHARGING; the resume level is what ends
        // the refusal, so the waiting demand is taken in this very iteration.
        SingleDemandJourneyView journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, journey.Stage);
        Assert.Equal("CHARGING", fixture.Riot.Vehicle.BatteryState);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AVehicleStandingOnTheChargerWithoutDrawingCurrentIsNamedRatherThanWaitedOut()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.EnableAutoCharging();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 15 };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        AutoChargingRunRow run = await fixture.ChargingRunAsync();

        fixture.Riot.SetSuccessfulArrival("TO_CHARGER", run.UpperId, run.ChargerStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = run.ChargerStationRiotId };
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AutoChargingStage.Charging, (await fixture.ChargingRunAsync()).Stage);

        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        run = await fixture.ChargingRunAsync();
        Assert.Equal(AutoChargingStage.Charging, run.Stage);
        Assert.Equal("CHARGER_NOT_ENGAGED", run.BlockReasonCode);
        Assert.Null(run.ReleasedAtBatteryPercent);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AVehicleAlreadyOnTheChargerIsNotDispatchedToTheStationItIsStandingOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.EnableAutoCharging();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with
        {
            BatteryPercent = 15,
            BatteryState = "CHARGING",
            CurrentStationId = 211
        };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AutoChargingRuns.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.CreateCount("TO_CHARGER"));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AChargerMissingFromTheLiveMapStartsNoRunAndDispatchesNothing()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.EnableAutoCharging();
        // Same identity, wrong name: RequireFixedStation demands an exact pair, and a charger this
        // server cannot name exactly is not one it may drive to.
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(211, "充电准备点1B"));
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 15 };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AutoChargingRuns.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.CreateCount("TO_CHARGER"));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task NoChargingRunStartsWhileAJourneyStillHoldsTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.EnableAutoCharging();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 15 };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AutoChargingRuns.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task ChargingBelowTheResumeLevelKeepsRefusingDemandsWithAutoChargingOff()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 95, BatteryState = "CHARGING" };
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // Without the errand nothing ever drives the vehicle off the pad, so the old blanket
        // refusal is the safe reading: a charging vehicle is not available at any level.
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_POLICY_NOT_SATISFIED", backlog.ReasonCode);
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Cancelling the load in flight ends that demand, not the stop: the operator is still standing
    /// there with the rest of the worklist. The batch closed the other way round, so the stop picks
    /// up exactly where a completed batch leaves it -- a fresh round, and the whole station wait
    /// again for the next sublot.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task CancellingTheLoadInFlightLeavesTheStopFreeToLoadItsRemainingDemands()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(
                "10000000-0000-4000-8000-000000000001", "SUBLOT-001",
                createdAt: Now.AddMinutes(-10)),
            fixture.Demand(
                "10000000-0000-4000-8000-000000000002", "SUBLOT-002",
                createdAt: Now.AddMinutes(-9)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();
        // Both demands belong to the same station, so this is one stop with two of them on it.
        Assert.Single(await fixture.StopRowsAsync(), row => row.Role == JourneyStopRole.Pickup);

        await fixture.ScanSublotAsync("SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.JourneyRowAsync()).Stage);

        await fixture.CancelCommandedLoadAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // The journey is back where a closed batch would have left it, waiting on the next sublot.
        JourneyRuntimeRow journey = await fixture.JourneyRowAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, journey.Stage);
        Assert.Equal(["SUBLOT-002"], await fixture.SublotEntryRequestSublotsAsync());
        // And it carries no block reason: CANCELLED_BY_OPERATOR left standing on a journey that is
        // running again reads as "this journey was cancelled". What was cancelled is recorded on the
        // demand, its recovery workflow and the transport-demand suppression, none of which this
        // field holds.
        Assert.Null(journey.BlockReasonCode);

        // The LoadBatch command the cancellation answered is settled, the same way a LoadResult
        // settles it. Left pending it would be replayed into every later session under a new
        // session generation, which the peer refuses as a business id whose content changed.
        JourneyDemandRow cancelled = (await fixture.DemandRowsAsync())
            .Single(row => row.DemandId == "10000000-0000-4000-8000-000000000001");
        Assert.DoesNotContain(
            cancelled.LoadCommandMessageId, await fixture.PendingOutboxMessageIdsAsync());

        // And the second demand still loads normally.
        await fixture.LoadSublotAsync("SUBLOT-002");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.JourneyRowAsync()).Stage);
        JourneyDemandRow[] demands = await fixture.DemandRowsAsync();
        Assert.Equal(
            JourneyDemandState.Cancelled,
            demands.Single(row => row.DemandId == "10000000-0000-4000-8000-000000000001").State);
        Assert.Equal(
            JourneyDemandState.Loaded,
            demands.Single(row => row.DemandId == "10000000-0000-4000-8000-000000000002").State);
    }

    /// <summary>
    /// The same cancellation at a stop with nothing else to load ends the stop instead of asking
    /// for another sublot -- and the cargo already aboard from an earlier stop still reaches the
    /// gate. Which of the two happens is <c>TryContinueLoadingAtStopAsync</c>'s existing judgement,
    /// not a second rule written for cancellation.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task CancellingTheOnlyLoadAtAStopEndsTheStopAndTheCargoAboardStillReachesTheGate()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(
                "10000000-0000-4000-8000-000000000001", "SUBLOT-001",
                createdAt: Now.AddMinutes(-10), area: "N1-1", eqp: "EQP-01"),
            fixture.Demand(
                "10000000-0000-4000-8000-000000000002", "SUBLOT-002",
                createdAt: Now.AddMinutes(-9), area: "N1-2", eqp: "EQP-02"));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        fixture.BoxCounts.Set("SUBLOT-002", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.ArriveAtCurrentStopAsync();
        await fixture.LoadSublotAsync("SUBLOT-001");
        await fixture.ConfirmDepartureSafeAsync();
        await fixture.ArriveAtCurrentStopAsync();

        await fixture.ScanSublotAsync("SUBLOT-002");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.JourneyRowAsync()).Stage);

        await fixture.CancelCommandedLoadAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // Nothing left to load anywhere, so the stop ends and the vehicle heads for the gate with
        // the one demand it is already carrying.
        JourneyRuntimeRow journey = await fixture.JourneyRowAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.Equal("NO_FURTHER_CARGO", journey.LoadingClosedReason);
        Assert.Equal(9, journey.NextStopSequence);

        await fixture.ConfirmDepartureSafeAsync();
        await fixture.ArriveAtCurrentStopAsync();
        await fixture.UnloadCurrentAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.JourneyRowAsync()).Stage);
        Assert.Equal(
            DemandExecutionStatus.Succeeded,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(
                row => row.DemandId == "10000000-0000-4000-8000-000000000001",
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            DemandExecutionStatus.Cancelled,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(
                row => row.DemandId == "10000000-0000-4000-8000-000000000002",
                TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task ACancelledDemandStaysBarredWhenMesIngestReissuesItUnderANewDemandId()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);

        TransportDemandSuppressionRow suppression = await fixture.Context.TransportDemandSuppressions
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("SUBLOT-001|WIRE_TO_GATE", suppression.TransportDemandKey);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", suppression.ReasonCode);
        Assert.Equal("10000000-0000-4000-8000-000000000001", suppression.DemandId);

        // The whole point of keying on the business identity: MesIngest allocates a fresh DemandId
        // when a demand leaves its catalog and returns, and nothing writes back to MES, so the same
        // SUBLOT keeps reappearing. Barring the instance would stop nothing.
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000009",
            "SUBLOT-001",
            createdAt: fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == "10000000-0000-4000-8000-000000000009",
                TestContext.Current.CancellationToken);
        Assert.Equal("TRANSPORT_DEMAND_SUPPRESSED", backlog.ReasonCode);
        Assert.Empty(await fixture.Context.JourneyDemands.AsNoTracking()
            .Where(row => row.DemandId == "10000000-0000-4000-8000-000000000009")
            .ToArrayAsync(TestContext.Current.CancellationToken));
        // A different SUBLOT under the same work type is a different business key and is unaffected.
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000010",
            "SUBLOT-002",
            createdAt: fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.BoxCounts.Set("SUBLOT-002", 7);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingPickupArrival,
            (await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000010")).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task TheFirstSuppressionOfABusinessKeyIsTheOneThatStands()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.AdvanceToSublotWaitAsync("10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.HeartbeatAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        WireToGateStore store = new(fixture.Context);
        DateTimeOffset later = fixture.Clock.GetUtcNow().AddHours(1);
        await store.SuppressTransportDemandAsync(
            "SUBLOT-001|WIRE_TO_GATE",
            "10000000-0000-4000-8000-000000000009",
            "CANCELLED_BY_OPERATOR",
            later,
            TestContext.Current.CancellationToken);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The ban is already absolute; the reason and timestamp that explain it are the first ones.
        TransportDemandSuppressionRow suppression = await fixture.Context.TransportDemandSuppressions
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", suppression.ReasonCode);
        Assert.NotEqual(later, suppression.SuppressedAt);

        // A reason that does not represent a human decision must not be able to raise one at all:
        // a demand merely vanishing from the catalog may legitimately come back.
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            store.SuppressTransportDemandAsync(
                "SUBLOT-777|WIRE_TO_GATE",
                "10000000-0000-4000-8000-000000000011",
                "MES_DISAPPEARED",
                later,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task ASublotWhosePackageLostItsCapacityIsRefusedWithTheRealReasonAndOpensNoSlot()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");

        // The capacity table is a server-side rule table an operator maintains, and it can change
        // between dispatch and entry. BR-013 puts the recomputation after entry precisely for this.
        await fixture.SupersedePackageCapacityAsync("PDFN5×6-8L(12R)");
        await fixture.SubmitSublotAsync(runtime, "SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal("EXPECTED_BASKET_COUNT_MISMATCH", runtime.BlockReasonCode);
        // Not a single slot may be commanded or unlocked when the count cannot be established.
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());

        // The operator has to see why. "Not in the worklist" -- the peer's local guess before this
        // existed -- would be the wrong reason.
        ProtocolOutboxRow rejected = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SublotRejected", TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(rejected.PayloadJson);
        JsonElement problem = document.RootElement.GetProperty("payload").GetProperty("problem");
        Assert.Equal("EXPECTED_BASKET_COUNT_MISMATCH", problem.GetProperty("reasonCode").GetString());
        Assert.Contains("PACKAGE", problem.GetProperty("displayMessage").GetString()!, StringComparison.Ordinal);
        Assert.Equal(
            runtime.WorklistRevision,
            document.RootElement.GetProperty("payload").GetProperty("currentWorklistRevision").GetInt64());
        // A RESPONSE correlated to the submission it refuses. With a null correlationId the vehicle
        // throws CORRELATION_INVALID and the operator sees nothing (8005-agv-control-server#20);
        // this test read only the payload, which is how that shipped.
        ProtocolInboxRow submitted = await fixture.Context.ProtocolInbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SublotSubmitted", TestContext.Current.CancellationToken);
        Assert.Equal(submitted.MessageId, document.RootElement.GetProperty("correlationId").GetString());

        // Judged once, not once per poll: the refused submission stays in the inbox, and re-judging
        // it would re-run the remote box-count read on every iteration.
        int reads = fixture.BoxCounts.ReadCount;
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(reads, fixture.BoxCounts.ReadCount);
        Assert.Single(await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotRejected")
            .ToArrayAsync(TestContext.Current.CancellationToken));

        // Once the rule table is fixed, a fresh entry goes through -- the refusal blocked this
        // load, it did not kill the demand.
        await fixture.RestorePackageCapacityAsync("PDFN5×6-8L(12R)", 4);
        await fixture.SubmitSublotAsync(runtime, "SUBLOT-001");
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Single(await fixture.Context.StationOperations.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task ASublotWhoseBoxCountGrewSinceDispatchIsRefusedRatherThanLoadedIntoTooFewSlots()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SingleDemandJourneyView runtime = await fixture.AdvanceToSublotWaitAsync(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001");
        Assert.Equal(2, runtime.ExpectedBasketCount);

        // 7 boxes at 4 per basket reserved two slots. MES now says 12, which needs three -- and the
        // reservation this journey holds is still two.
        fixture.BoxCounts.Set("SUBLOT-001", 12);
        await fixture.SubmitSublotAsync(runtime, "SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal("EXPECTED_BASKET_COUNT_MISMATCH", runtime.BlockReasonCode);
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        ProtocolOutboxRow rejected = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SublotRejected", TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(rejected.PayloadJson);
        string message = document.RootElement.GetProperty("payload").GetProperty("problem")
            .GetProperty("displayMessage").GetString()!;
        Assert.Contains("2", message, StringComparison.Ordinal);
        Assert.Contains("3", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task TheEntryRequestNamesEverySublotTheOperatorMayEnter()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.AdvanceToSublotWaitAsync("10000000-0000-4000-8000-000000000001", "SUBLOT-001");

        ProtocolOutboxRow request = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SublotEntryRequested",
                TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(request.PayloadJson);
        JsonElement sublots = document.RootElement.GetProperty("payload").GetProperty("expectedSublots");

        // One entry while a journey carries one demand. It is a set rather than a string because
        // FR-001 AC-3 scopes entry to the dispatch range, not to the stop the vehicle is parked at.
        Assert.Equal(JsonValueKind.Array, sublots.ValueKind);
        Assert.Equal("SUBLOT-001", Assert.Single(sublots.EnumerateArray()).GetString());
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
            JourneyRuntimeOptions options,
            FixedTimeProvider clock,
            SaveChangesCounter saveChanges)
        {
            Connection = connection;
            Context = context;
            Catalog = catalog;
            BoxCounts = boxCounts;
            Riot = riot;
            Peer = peer;
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
        public JourneyRuntimeOptions Options { get; }
        public FixedTimeProvider Clock { get; }
        public SaveChangesCounter SaveChanges { get; }
        public JourneyRuntimeEngine Engine { get; private set; }

        public static async Task<RuntimeFixture> CreateAsync()
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
            RuntimeFixture fixture = new(
                connection, context, catalog, boxCounts, riot, peer, options, clock, saveChanges);
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
        public async Task<SingleDemandJourneyView> AdvanceToLoadResultAsync()
        {
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            SingleDemandJourneyView pickupRuntime = await RuntimeAsync();
            Riot.SetSuccessfulArrival("TO_PICKUP", pickupRuntime.PickupStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = pickupRuntime.PickupStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            SingleDemandJourneyView runtime = await RuntimeAsync();
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

        public async Task<SingleDemandJourneyView> AdvanceToDepartureSafetyAsync()
        {
            await AdvanceToLoadResultAsync();
            StationOperationRow load = await OperationAsync(SlotOperationType.Load);
            await ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>Carries the journey on to the gate, where the unload command is issued.</summary>
        public async Task<SingleDemandJourneyView> RunToGateUnloadAsync()
        {
            SingleDemandJourneyView runtime = await AdvanceToDepartureSafetyAsync();
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
        public async Task<SingleDemandJourneyView> RunToCompletionAsync()
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

        /// <summary>
        /// Puts the safety projection into the shape the vehicle reports while a slot door is still
        /// open: <c>LOCK_NOT_CLOSED</c>, nothing unknown. This is the field ADR-cross-0058 decision 4
        /// reads to decide whether an expired stop may be closed.
        /// </summary>
        public async Task ReportDoorLeftOpenAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SafetyReasonCodesJson = "[\"LOCK_NOT_CLOSED\"]";
            session.SafetyUnknownPresent = false;
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// The same open door as <see cref="ReportDoorLeftOpenAsync"/>, but reported the way the
        /// vehicle actually reports it: ApplySafetySnapshotAsync followed by DecideReadinessAsync,
        /// so readiness is recomputed rather than left at whatever the fixture built. Writing the
        /// row directly is what let both halves of ADR-cross-0058 decision 4 pass L1 while one of
        /// them could not be reached at all.
        /// </summary>
        public async Task<SessionReadinessDecision> ReportDoorLeftOpenThroughRealPathAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            long revision = (session.SafetyRevision ?? 0) + 1;
            WireToGateStore store = new(Context);
            await store.ApplySafetySnapshotAsync(
                Options.AgvId, session.SessionGeneration, revision, false, new string('a', 64),
                TestContext.Current.CancellationToken, ["LOCK_NOT_CLOSED"], unknownPresent: false);
            return await store.DecideReadinessAsync(
                Options.AgvId, session.SessionGeneration, TestContext.Current.CancellationToken);
        }

        /// <summary>The operator finally pushed the door shut; the projection carries no reasons.</summary>
        public async Task ReportDoorClosedAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SafetyReasonCodesJson = "[]";
            session.SafetyUnknownPresent = false;
            session.UpdatedAt = Clock.GetUtcNow();
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

        /// <summary>Carries the journey to the pickup stop, waiting for an operator to enter a sublot.</summary>
        /// <summary>
        /// The journey's stops and demands as they stand. A multi-demand journey has no single
        /// pickup station or single load command, so <see cref="SingleDemandJourneyView"/> refuses
        /// it -- these read the rows directly instead.
        /// </summary>
        public async Task<JourneyStopRow[]> StopRowsAsync()
        {
            JourneyStopRow[] rows = await Context.JourneyStops
                .AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken);
            return rows.OrderBy(row => row.Sequence).ToArray();
        }

        public async Task<JourneyDemandRow[]> DemandRowsAsync()
        {
            JourneyDemandRow[] rows = await Context.JourneyDemands
                .AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken);
            return rows.OrderBy(row => row.CreatedAt).ThenBy(row => row.DemandId, StringComparer.Ordinal)
                .ToArray();
        }

        public Task<JourneyRuntimeRow> JourneyRowAsync() => Context.JourneyRuntimes
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public Task<StationOperationRow> OperationForAsync(string attemptId) => Context.StationOperations
            .AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == attemptId, TestContext.Current.CancellationToken);

        /// <summary>
        /// What the newest entry request offers the operator. Read off the wire rather than from the
        /// runtime, because the whole point of <c>expectedSublots</c> is what reaches the vehicle.
        /// </summary>
        public async Task<string[]> SublotEntryRequestSublotsAsync()
        {
            JsonElement payload = await CurrentRoundPayloadAsync(WireToGateStore.SublotRequestId);
            return payload.GetProperty("expectedSublots")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();
        }

        public async Task<int> WorklistItemCountAsync()
        {
            JsonElement payload = await CurrentRoundPayloadAsync(WireToGateStore.WorklistId);
            return payload.GetProperty("items").GetArrayLength();
        }

        /// <summary>
        /// The payload the current stop published in its current load round. Addressed by derived id
        /// rather than by "the newest row": the test clock does not move between rounds, so ordering
        /// on a timestamp picks whichever of them the database happens to return first.
        /// </summary>
        private async Task<JsonElement> CurrentRoundPayloadAsync(Func<string, int, int, string> id)
        {
            JourneyRuntimeRow journey = await JourneyRowAsync();
            JourneyStopRow stop = (await StopRowsAsync())
                .Single(row => row.Sequence == journey.CurrentStopSequence);
            string messageId = id(journey.JourneyId, stop.Sequence, stop.LoadRound);
            ProtocolOutboxRow row = await Context.ProtocolOutbox
                .AsNoTracking()
                .SingleAsync(item => item.MessageId == messageId, TestContext.Current.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            return document.RootElement.GetProperty("payload").Clone();
        }

        /// <summary>
        /// Scans a sublot at the stop the vehicle is parked at. Which demand that names is the
        /// server's to work out -- the operator scans a label, not a demand id -- so the caller
        /// passes the sublot and nothing else.
        /// </summary>
        public async Task ScanSublotAsync(string sublot)
        {
            JourneyRuntimeRow journey = await JourneyRowAsync();
            JourneyStopRow stop = (await StopRowsAsync())
                .Single(row => row.Sequence == journey.CurrentStopSequence);
            JourneyDemandRow[] demands = await DemandRowsAsync();
            string[] demandIds = demands.Select(row => row.DemandId).ToArray();
            AcceptedDemandRow demand = await Context.AcceptedDemands
                .AsNoTracking()
                .SingleAsync(
                    row => row.Sublot == sublot && demandIds.Contains(row.DemandId),
                    TestContext.Current.CancellationToken);
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "SublotSubmitted",
                new
                {
                    demandId = demand.DemandId,
                    operationSessionId = journey.OperationSessionId,
                    stationId = stop.StationId,
                    worklistRevision = stop.WorklistRevision,
                    sublot,
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = Clock.GetUtcNow()
                    }
                });
        }

        /// <summary>
        /// Scans a sublot, lets the load be commanded, and closes that batch safely. One demand's
        /// whole loading step.
        /// </summary>
        public async Task LoadSublotAsync(string sublot)
        {
            await ScanSublotAsync(sublot);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            JourneyDemandRow loading = (await DemandRowsAsync())
                .Single(row => row.State == JourneyDemandState.Planned && row.LoadCommandedAt is not null);
            StationOperationRow load = await OperationForAsync(loading.LoadSlotOperationAttemptId);
            await ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Answers the pre-departure safety check the current stop is waiting on.</summary>
        public async Task ConfirmDepartureSafeAsync()
        {
            JourneyRuntimeRow journey = await JourneyRowAsync();
            JourneyStopRow stop = (await StopRowsAsync())
                .Single(row => row.Sequence == journey.CurrentStopSequence);
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "PreDepartureSafetyCheckResult",
                new
                {
                    preDepartureSafetyCheckId = stop.PreDepartureSafetyCheckId,
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
                stop.PreDepartureSafetyCheckMessageId);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Drives the vehicle to the stop it is currently heading for.</summary>
        public async Task ArriveAtCurrentStopAsync()
        {
            JourneyRuntimeRow journey = await JourneyRowAsync();
            JourneyStopRow stop = (await StopRowsAsync())
                .Single(row => row.Sequence == journey.CurrentStopSequence);
            Riot.SetSuccessfulArrival(
                stop.LegType == "TO_GATE" ? "TO_GATE" : "TO_PICKUP", stop.UpperId, stop.StationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = stop.StationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Closes the unload batch the gate stop is waiting on.</summary>
        public async Task UnloadCurrentAsync()
        {
            JourneyDemandRow unloading = (await DemandRowsAsync())
                .Single(row => row.State == JourneyDemandState.Loaded && row.UnloadCommandedAt is not null);
            StationOperationRow unload = await OperationForAsync(unloading.UnloadSlotOperationAttemptId);
            await ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        }

        public async Task<SingleDemandJourneyView> AdvanceToSublotWaitAsync(string demandId, string sublot)
        {
            Catalog.Set(Demand(demandId, sublot, createdAt: Clock.GetUtcNow().AddMinutes(-10)));
            BoxCounts.Set(sublot, 7);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            SingleDemandJourneyView pickup = await RuntimeAsync(demandId);
            Riot.SetSuccessfulArrival("TO_PICKUP", pickup.PickupUpperId, pickup.PickupStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = pickup.PickupStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync(demandId);
        }

        public async Task SubmitSublotAsync(SingleDemandJourneyView runtime, string sublot) =>
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "SublotSubmitted",
                new
                {
                    demandId = runtime.DemandId,
                    operationSessionId = runtime.OperationSessionId,
                    stationId = runtime.PickupStationId,
                    worklistRevision = runtime.WorklistRevision,
                    sublot,
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = Clock.GetUtcNow()
                    }
                });

        /// <summary>
        /// Turns the charging errand on and puts a charger on the live map. The station name is
        /// load bearing: RequireFixedStation matches identity and name exactly.
        /// </summary>
        public void EnableAutoCharging()
        {
            Options.AutoChargingEnabled = true;
            Options.ChargerStationId = "充电准备点1";
            Options.ChargerStationRiotId = 211;
            Riot.SetMapStations(
                new RiotMapStation(12, "N1-1"),
                new RiotMapStation(13, "N1-2_N1-3"),
                new RiotMapStation(210, "关卡"),
                new RiotMapStation(211, "充电准备点1"));
        }

        /// <summary>
        /// One inbound message from the current session, stamped now. Session liveness is bounded
        /// by MaximumEvidenceAge against the last thing the peer said, so a test that advances the
        /// clock past that window has to keep the peer talking -- in the field the heartbeat does
        /// exactly this. Without it the runtime reads ONBOARD_FACTS_NOT_READY, which is a dead
        /// peer, not the condition under test.
        /// </summary>
        public async Task HeartbeatAsync()
        {
            long generation = (await Context.SessionRecoveries.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).SessionGeneration;
            string messageId = Guid.NewGuid().ToString("D");
            DateTimeOffset now = Clock.GetUtcNow();
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = "Heartbeat",
                RequestJson = JsonSerializer.Serialize(new
                {
                    messageType = "Heartbeat",
                    messageId,
                    agvId = Options.AgvId,
                    sessionGeneration = generation,
                    sentAt = now,
                    payload = new { observedAt = now }
                }, SerializerOptions),
                ContentHash = new string('f', 64),
                FirstResponseJson = "{}",
                ReceivedAt = now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Retires the approved basket-capacity rule for a PACKAGE, the way an operator maintaining
        /// the rule table does. The rules are EF seed data rather than a double, so this edits the
        /// real table the store reads.
        /// </summary>
        public async Task SupersedePackageCapacityAsync(string package)
        {
            PackageCapacityRuleRow rule = await Context.PackageCapacityRules.SingleAsync(
                row => row.Pattern == package && row.SupersededAt == null,
                TestContext.Current.CancellationToken);
            rule.SupersededAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task RestorePackageCapacityAsync(string package, int capacity)
        {
            Context.PackageCapacityRules.Add(new PackageCapacityRuleRow
            {
                RuleId = Guid.NewGuid().ToString("D"),
                Pattern = package,
                MatchType = "exact",
                MaxBoxesPerBasket = capacity,
                Source = "test",
                EffectiveAt = Clock.GetUtcNow(),
                Version = 2
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public Task<AutoChargingRunRow> ChargingRunAsync() => Context.AutoChargingRuns
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public async Task<SingleDemandJourneyView> RuntimeAsync()
        {
            JourneyRuntimeRow runtime = await Context.JourneyRuntimes
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            return await ViewAsync(runtime);
        }

        public async Task<SingleDemandJourneyView> RuntimeAsync(string demandId)
        {
            JourneyDemandRow membership = await Context.JourneyDemands
                .AsNoTracking()
                .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);
            JourneyRuntimeRow runtime = await Context.JourneyRuntimes
                .AsNoTracking()
                .SingleAsync(row => row.JourneyId == membership.JourneyId, TestContext.Current.CancellationToken);
            return await ViewAsync(runtime);
        }

        private async Task<SingleDemandJourneyView> ViewAsync(JourneyRuntimeRow runtime) => new(
            runtime,
            await Context.JourneyStops
                .AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .ToArrayAsync(TestContext.Current.CancellationToken),
            await Context.JourneyDemands
                .AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .ToArrayAsync(TestContext.Current.CancellationToken));

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
            string json = JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                profileId = "WIRE_TO_GATE_MVP",
                protocolReleaseVersion = "0.1.1",
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
        /// <param name="determinate">
        /// Which kind of failure this is, once ADR-cross-0058 decision 5 split the two. A
        /// determinate failure names the state each slot ended in, so the server settles it as
        /// StationOperationStatus.Failed and the journey is not blocked. The default is the other
        /// half -- a slot whose physical state the vehicle could not establish -- which is what
        /// blocks the journey until a human drives the recovery handshake.
        /// </param>
        public async Task ApplyTimedOutResultAsync(
            StationOperationRow operation,
            SlotOperationType type,
            bool determinate = false)
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            SlotBusinessState state = determinate ? SlotBusinessState.Empty : SlotBusinessState.Unknown;
            await new WireToGateStore(Context).ApplyOperationResultAsync(
                new StationOperationResult(
                    Guid.NewGuid().ToString("D"),
                    operation.SlotOperationAttemptId,
                    operation.DemandId,
                    type,
                    "FAILED",
                    slots.Select(slot => new SlotPhysicalEvidence(slot, state, true, true)).ToArray(),
                    false,
                    Clock.GetUtcNow(),
                    new string('8', 64),
                    new string('9', 64)),
                Options.AgvId,
                0,
                TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Cancels the load this stop has already commanded, through the peer's own two-message
        /// handshake rather than by writing the terminal state directly: the authorisation first,
        /// then the result proving every slot came back empty. Writing it directly would skip
        /// OnboardRecoveryCoordinator, which is where the stage is left behind.
        /// </summary>
        public async Task CancelCommandedLoadAsync()
        {
            JourneyDemandRow loading = (await DemandRowsAsync())
                .Single(row => row.State == JourneyDemandState.Planned && row.LoadCommandedAt is not null);
            StationOperationRow operation = await OperationForAsync(loading.LoadSlotOperationAttemptId);
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            string cancellationId = Guid.NewGuid().ToString("D");
            object @operator = new
            {
                operatorId = "OP-001",
                verificationMethod = "BADGE",
                verifiedAt = Clock.GetUtcNow()
            };
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                Context, new WireToGateStore(Context), Clock, new ConfigurationBuilder().Build());
            OnboardConnectionState state = new()
            {
                AgvId = Options.AgvId,
                SessionGeneration = 1,
                CapabilityRevision = 1,
                SafetyRevision = 7,
                Readiness = SessionReadiness.Ready
            };
            await processor.ProcessAsync(
                PeerEnvelope(
                    "LoadCancellationStartRequested",
                    new
                    {
                        cancellationId,
                        demandId = loading.DemandId,
                        slotOperationAttemptId = loading.LoadSlotOperationAttemptId,
                        @operator,
                        reason = "The operator called the load off."
                    }),
                state,
                TestContext.Current.CancellationToken);
            await processor.ProcessAsync(
                PeerEnvelope(
                    "LoadCancellationResult",
                    new
                    {
                        cancellationId,
                        demandId = loading.DemandId,
                        slotOperationAttemptId = loading.LoadSlotOperationAttemptId,
                        overallOutcome = "ALL_EMPTY",
                        slotResults = slots.Select(slot => new
                        {
                            slotNo = slot,
                            outcome = "COMPLETED",
                            finalPhysicalState = "EMPTY",
                            lockState = "LOCKED",
                            unlockOutputState = "RESET",
                            reasonCodes = Array.Empty<string>()
                        }).ToArray(),
                        observedAt = Clock.GetUtcNow()
                    }),
                state,
                TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        private string PeerEnvelope(string messageType, object payload) => JsonSerializer.Serialize(
            new
            {
                protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                profileId = ProtocolCandidateIdentity.ProfileId,
                protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                messageType,
                messageId = Guid.NewGuid().ToString("D"),
                correlationId = (string?)null,
                agvId = Options.AgvId,
                sessionGeneration = 1,
                sentAt = Clock.GetUtcNow(),
                payload = JsonSerializer.SerializeToElement(payload, SerializerOptions)
            },
            SerializerOptions);

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

        private JourneyRuntimeEngine CreateEngine()
        {
            WireToGateStore store = new(Context);
            JourneyIntakeCoordinator intake = new(
                new DemandIntakeService(Catalog, store),
                new MovementDispatchService(store, Riot));
            OnboardJourneyPublisher publisher = new(store, Peer, Clock);
            return new JourneyRuntimeEngine(
                Context,
                Catalog,
                BoxCounts,
                new PackageCapacityStore(Context),
                Riot,
                Riot,
                new MapStationResolver(),
                intake,
                new MovementDispatchService(store, Riot),
                store,
                publisher,
                Microsoft.Extensions.Options.Options.Create(Options),
                Clock,
                NullLogger<JourneyRuntimeEngine>.Instance);
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
                // Matches the SafetyStateSnapshot this fixture puts in the inbox alongside it
                // (unknownPresent false, no reason codes). ApplySafetySnapshotAsync fills these two
                // on the real handshake path, and a session cannot reach Ready without going
                // through it -- leaving them null here modelled a session that cannot exist.
                SafetyUnknownPresent = false,
                SafetyReasonCodesJson = "[]",
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
            AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1"
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

        /// <summary>Remote reads performed. A refused entry must not re-read on every poll.</summary>
        public int ReadCount { get; private set; }

        public void Set(string sublot, int count) => _counts[sublot] = count;
        public void Remove(string sublot) => _counts.Remove(sublot);

        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReadCount++;
            BeforeRead?.Invoke();
            return Task.FromResult(_counts.TryGetValue(sublot, out int count) ? (int?)count : null);
        }
    }

    private sealed class RecordingRiot : IRiotMovementGateway, IRiotVehicleFacts, IRiotMapStationCatalog
    {
        private readonly JourneyRuntimeOptions _options;
        private readonly FixedTimeProvider _clock;
        private readonly Dictionary<string, int> _creates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _upperIds = new(StringComparer.Ordinal);
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
        /// The upperId of the last order created for a purpose. It used to be a literal built from
        /// the demand id; since ADR-cross-0057 an upperId names the journey and the stop, so the
        /// only way to know it is to have seen the order go out.
        /// </summary>
        private string UpperId(string purpose) => _upperIds.TryGetValue(purpose, out string? upperId)
            ? upperId
            : throw new InvalidOperationException($"No order has been created for '{purpose}' yet.");

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
            _upperIds[intent.Purpose] = intent.UpperId;
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
