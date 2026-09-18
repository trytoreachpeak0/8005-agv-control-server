using System.Text.Json.Nodes;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Split out of the one partial JourneyRuntimeWorkerTests class so xunit runs it in parallel with the
/// rest (control-server#130); the shared fixture is <see cref="JourneyRuntimeWorkerTestKit"/>.
/// </summary>
public sealed class JourneyRuntimeWorkerSnapshotRedeliveryTests
{
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
}
