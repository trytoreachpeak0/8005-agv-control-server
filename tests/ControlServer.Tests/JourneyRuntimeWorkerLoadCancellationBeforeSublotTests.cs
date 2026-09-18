using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// The cancellation before a sublot is entered, server half (ADR-cross-0046 first case,
/// control-server#83): authorized only while the stop still waits for its entry, exclusive with the entry
/// and the station deadline, and ending the stop only on the vehicle's ALL_EMPTY result.
/// </summary>
/// <remarks>
/// Driven through the real runtime and the real message processor, on two contexts over one database, the
/// way the host runs them: the connection's scope authorizes and settles, the runtime's scope starts loads
/// and ends stops.
/// </remarks>
public sealed class JourneyRuntimeWorkerLoadCancellationBeforeSublotTests
{
    private const string BeforeSublotDemandId = "10000000-0000-4000-8000-000000000001";
    private const string BeforeSublotCancellationId = "c1000000-0000-4000-8000-000000000001";

    /// <summary>
    /// CV-LOAD-CANCELLATION-BEFORE-LOAD, all four steps. The authorization names no attempt and no slot,
    /// and on its own changes nothing about the demand -- MVP ended the demand on the authorization, which
    /// ADR-cross-0057 records as not what ADR-cross-0046 decided. The ALL_EMPTY result with no slot results
    /// is what ends the stop, in one change: demand cancelled by the operator, lease and vehicle released,
    /// the entry request nobody answered settled, the journey completed, and no slot operation anywhere.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ACancellationBeforeAnySublotIsAuthorizedWithNoSlotsAndEndsTheStopOnlyOnAnAllEmptyResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();

        // Steps 1 and 2.
        string authorization = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        JsonElement authorized = FirstLinePayload(authorization, out string authorizationType);
        Assert.Equal("LoadCancellationAuthorization", authorizationType);
        Assert.Equal("AUTHORIZED", authorized.GetProperty("decision").GetString());
        Assert.Equal(BeforeSublotCancellationId, authorized.GetProperty("cancellationId").GetString());
        Assert.Equal(BeforeSublotDemandId, authorized.GetProperty("demandId").GetString());
        Assert.Equal(JsonValueKind.Null, authorized.GetProperty("slotOperationAttemptId").ValueKind);
        Assert.Equal(0, authorized.GetProperty("slots").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, authorized.GetProperty("problem").ValueKind);
        RecoveryWorkflowRow workflow = await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
        Assert.Equal("LOAD_CANCELLATION", workflow.WorkflowType);
        Assert.Equal(RecoveryWorkflowState.AwaitingResult, workflow.State);
        Assert.Null(workflow.SlotOperationAttemptId);
        Assert.Equal("[]", workflow.SlotsJson);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);

        // Steps 3 and 4.
        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        string ack = await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, "c1000000-0000-4000-8000-000000000101", generation: 1),
            state,
            token);

        JsonElement durable = FirstLinePayload(ack, out string ackType);
        Assert.Equal("DurableAck", ackType);
        Assert.Equal("LoadCancellationResult", durable.GetProperty("acceptedMessageType").GetString());
        await AssertStopEndedByOperatorAsync(fixture, waiting, receivedAt);

        // The runtime finds nothing left to do for the stop.
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(0, await fixture.Context.StationOperations.CountAsync(token));
    }

    /// <summary>
    /// The four ways the stop is past the point a cancellation without an attempt could be about it: the
    /// vehicle has not arrived yet, an entry for the stop is already durable, a load was commanded, or a
    /// cancellation is already open. Each is refused with ACTION_NOT_ALLOWED_IN_STATE and records nothing.
    /// The authorized case is the vector test above; the last case also shows the first cancellation is
    /// untouched by the refusal of the second.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [InlineData("before-arrival")]
    [InlineData("entry-durable")]
    [InlineData("load-commanded")]
    [InlineData("cancellation-open")]
    public async Task ACancellationWithoutAnAttemptIsRefusedOnceTheStopIsNoLongerWaitingForItsEntry(string situation)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(BeforeSublotDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        int workflowsBefore = 0;
        switch (situation)
        {
            case "before-arrival":
                await fixture.Engine.ExecuteOnceAsync(token);
                Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
                break;
            case "entry-durable":
                await fixture.AdvanceToSublotWaitAsync();
                await processor.ProcessAsync(SublotEntry(fixture, await fixture.RuntimeAsync(), "SUBLOT-001"), state, token);
                Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
                break;
            case "load-commanded":
                await fixture.AdvanceToSublotWaitAsync();
                await fixture.SubmitSublotAsync("SUBLOT-001");
                await fixture.Engine.ExecuteOnceAsync(token);
                Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
                break;
            case "cancellation-open":
                await fixture.AdvanceToSublotWaitAsync();
                string first = await processor.ProcessAsync(
                    CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);
                Assert.Equal("AUTHORIZED", FirstLinePayload(first, out _).GetProperty("decision").GetString());
                workflowsBefore = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation), situation, null);
        }

        string response = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, "c1000000-0000-4000-8000-000000000002", generation: 1),
            state,
            token);

        JsonElement refused = FirstLinePayload(response, out string type);
        Assert.Equal("LoadCancellationAuthorization", type);
        Assert.Equal("REJECTED", refused.GetProperty("decision").GetString());
        Assert.Equal(0, refused.GetProperty("slots").GetArrayLength());
        Assert.Equal(
            ServerReasonCodes.ActionNotAllowedInState,
            refused.GetProperty("problem").GetProperty("reasonCode").GetString());
        RecoveryWorkflowRow[] workflows = await fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token);
        Assert.Equal(workflowsBefore, workflows.Length);
        Assert.All(workflows, row =>
        {
            Assert.Equal(BeforeSublotCancellationId, row.WorkflowId);
            Assert.Equal(RecoveryWorkflowState.AwaitingResult, row.State);
        });
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    /// <summary>
    /// Two things that do not hold the stop against the operator. An entry the runtime would not act on --
    /// here a sublot that is not the demand's -- starts no load, so it leaves the cancellation open. And a
    /// request naming the cancellation already on file, under a new messageId, is answered from the record:
    /// judged afresh, the open cancellation it created would refuse it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AMismatchedEntryDoesNotHoldTheStopAndARepeatedCancellationIsAnsweredFromItsRecord()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        await processor.ProcessAsync(SublotEntry(fixture, await fixture.RuntimeAsync(), "SUBLOT-OTHER"), state, token);

        string first = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);
        string repeated = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        Assert.Equal("AUTHORIZED", FirstLinePayload(first, out _).GetProperty("decision").GetString());
        Assert.Equal("AUTHORIZED", FirstLinePayload(repeated, out _).GetProperty("decision").GetString());
        Assert.Equal(RecoveryWorkflowState.AwaitingResult,
            (await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
    }

    /// <summary>
    /// A refusal is not a load, so it does not hold the stop either (control-server#82, decided there
    /// rather than here). The operator has just been shown on the vehicle why their scan did not stand;
    /// cancelling the stop before any sublot is the next thing they are likely to want, and the stored
    /// refusal is the record that no load will follow it.
    /// </summary>
    /// <remarks>
    /// Without this the two refusals would also disagree: a sublot outside the dispatch scope never
    /// matched the demand's and so never held the stop, while one inside it that failed BR-013 did.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ARefusedEntryDoesNotHoldTheStopAgainstACancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        // BR-013 cannot be re-established for this entry: the box count it is revalidated against is gone.
        fixture.BoxCounts.Remove("SUBLOT-001");
        await processor.ProcessAsync(SublotEntry(fixture, waiting, "SUBLOT-001"), state, token);
        await fixture.Engine.ExecuteOnceAsync(token);

        using JsonDocument refusal = JsonDocument.Parse(
            (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageType == "SublotRejected", token)).PayloadJson);
        Assert.Equal(
            ServerReasonCodes.SublotBoxCountUnavailable,
            refusal.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);

        string authorization = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization, out _).GetProperty("decision").GetString());
    }

    /// <summary>
    /// The other side of the same rule: an entry that has not been refused <b>yet</b> is still an entry.
    /// The refusal is read from the store, so until it is durable nothing has been decided about the scan,
    /// and the cancellation loses to it -- the conservative side of the race, and the one that cannot load
    /// and cancel at the same time.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnEntryThatHasNotBeenRefusedYetStillHoldsTheStopAgainstACancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        fixture.BoxCounts.Remove("SUBLOT-001");
        await processor.ProcessAsync(SublotEntry(fixture, waiting, "SUBLOT-001"), state, token);
        // Deliberately no engine iteration: the refusal this entry will draw has not been made durable.

        string response = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        JsonElement refused = FirstLinePayload(response, out string type);
        Assert.Equal("LoadCancellationAuthorization", type);
        Assert.Equal("REJECTED", refused.GetProperty("decision").GetString());
        Assert.Equal(
            ServerReasonCodes.ActionNotAllowedInState,
            refused.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Empty(await fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));
    }

    /// <summary>
    /// A cancellation the refusal allowed still decides the stop: fixing the data and scanning again does
    /// not turn the next entry into a load. The runtime reads the inbox before the open cancellation, so an
    /// entry that arrives after it loses to it however good the entry is.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ARefusalThatAllowedTheCancellationDoesNotLetALaterEntryLoad()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        fixture.BoxCounts.Remove("SUBLOT-001");
        await processor.ProcessAsync(SublotEntry(fixture, waiting, "SUBLOT-001"), state, token);
        await fixture.Engine.ExecuteOnceAsync(token);
        string authorization = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization, out _).GetProperty("decision").GetString());

        // The box count is back and the operator scans a sublot that would now load.
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await processor.ProcessAsync(SublotEntry(fixture, waiting, "SUBLOT-001"), state, token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(token);
        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, held.Stage);
        Assert.Null(held.ConsumedSublotMessageId);
        Assert.Null(held.BlockReasonCode);
        Assert.Equal(0, await fixture.Context.StationOperations.CountAsync(token));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
    }

    /// <summary>
    /// Exclusive the other way (ADR-cross-0055's first-persisted-wins, applied to ADR-cross-0046): once the
    /// cancellation is durable, an entry that arrives afterwards starts no load, and the station deadline
    /// passing -- with the doors proven shut and the vehicle heard from, so nothing else would hold the stop
    /// -- does not end it. The stop waits for the vehicle's result, and that result ends it as the operator's.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnOpenCancellationHoldsTheStopAgainstALaterEntryAndTheStationDeadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        await processor.ProcessAsync(SublotEntry(fixture, waiting, "SUBLOT-001"), state, token);
        await fixture.Engine.ExecuteOnceAsync(token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(token);
        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, held.Stage);
        Assert.Null(held.ConsumedSublotMessageId);
        Assert.Null(held.BlockReasonCode);
        Assert.Equal(0, await fixture.Context.StationOperations.CountAsync(token));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);

        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, "c1000000-0000-4000-8000-000000000101", generation: 1),
            state,
            token);
        await AssertStopEndedByOperatorAsync(fixture, waiting, receivedAt);
    }

    /// <summary>
    /// Anything but ALL_EMPTY with no slot results is not a proof that nothing was loaded: FAILED, UNKNOWN,
    /// or an ALL_EMPTY that names a slot the authorization never covered. Each is recorded and acknowledged,
    /// so the vehicle stops resending it, and each leaves the demand for recovery rather than cancelling it.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [InlineData("FAILED", false)]
    [InlineData("UNKNOWN", false)]
    [InlineData("ALL_EMPTY", true)]
    public async Task AnythingButAnEmptyAllEmptyResultLeavesTheDemandForRecovery(string overallOutcome, bool namesASlot)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        string ack = await processor.ProcessAsync(
            CancellationBeforeSublotResult(
                fixture, "c1000000-0000-4000-8000-000000000101", generation: 1, overallOutcome, namesASlot),
            state,
            token);

        Assert.Equal("DurableAck", FirstLineType(ack));
        Assert.Single(await fixture.Context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(RecoveryWorkflowState.RecoveryRequired,
            (await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await fixture.DemandRowAsync()).Status);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("LoadCancellationResult_NOT_RECONCILED", runtime.BlockReasonCode);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
        ProtocolOutboxRow entryRequest = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == waiting.SublotRequestMessageId, token);
        Assert.Null(entryRequest.AcknowledgedAt);
    }

    /// <summary>
    /// The link drops after the authorization went out and before the result came back. The cancellation
    /// stays open through the disconnect and the reconnect -- the runtime neither ends the stop nor forgets
    /// it -- and the result the vehicle sends into the new session is taken and settles the stop. A resend
    /// of that same result into a later session, because its DurableAck was lost too, is answered from the
    /// first acceptance (control-server#77) and recorded once.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AResultSentAfterAReconnectIsTakenAndSettlesTheStop()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1),
            BeforeSublotConnection(fixture, generation: 1),
            token);

        await fixture.ReconnectAsync(2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(token);
        await fixture.AdvanceSessionAsync(2);
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(RecoveryWorkflowState.AwaitingResult,
            (await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);

        const string resultMessageId = "c1000000-0000-4000-8000-000000000101";
        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        string ack = await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, resultMessageId, generation: 2),
            BeforeSublotConnection(fixture, generation: 2),
            token);
        Assert.Equal("DurableAck", FirstLineType(ack));
        await AssertStopEndedByOperatorAsync(fixture, waiting, receivedAt);

        await fixture.ReconnectAsync(3);
        await fixture.AdvanceSessionAsync(3);
        string resent = await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, resultMessageId, generation: 3),
            BeforeSublotConnection(fixture, generation: 3),
            token);
        Assert.Equal("DurableAck", FirstLineType(resent));
        Assert.Single(await fixture.Context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// control-server#40 on the v2 line: the connection that cancels was opened, and had already read the
    /// journey, while the vehicle was still on its way to the pickup -- here by a cancellation refused
    /// there. The runtime then brings the journey to the stop on its own context. The cancellation on that
    /// same connection must judge the stop as it is now, and its result must settle the entry request the
    /// runtime sent after the connection first looked (8005-agv-program#56).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ACancellationOnAConnectionOpenedBeforeArrivalJudgesAndSettlesTheStopAsItIsNow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(BeforeSublotDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        await fixture.Engine.ExecuteOnceAsync(token);
        string early = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, "c1000000-0000-4000-8000-000000000009", generation: 1), state, token);
        Assert.Equal("REJECTED", FirstLinePayload(early, out _).GetProperty("decision").GetString());

        JourneyRuntimeRow waiting = await fixture.AdvanceToSublotWaitAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        await fixture.ProveSlotDoorsClosedAsync();
        string authorization = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization, out _).GetProperty("decision").GetString());

        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, "c1000000-0000-4000-8000-000000000101", generation: 1),
            state,
            token);
        await AssertStopEndedByOperatorAsync(fixture, waiting, receivedAt);
    }

    /// <summary>
    /// The station deadline and the cancellation interleaved (control-server#116 review, item 1). Each side
    /// decides under the write lock, so in the store one of them commits first and the other then refuses:
    /// a cancellation arriving after the deadline ended the stop is REJECTED. Should both still have been
    /// recorded -- the state a lost lock would leave, written here directly -- the vehicle's ALL_EMPTY result
    /// is taken and acknowledged, but the stop keeps the reason it ended with: CANCELLED_BY_STATION_TIMEOUT
    /// is not rewritten as the operator's, and nothing it released is released again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task WhenTheStationDeadlineEndsTheStopFirstTheCancellationDoesNotRewriteHowItEnded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();

        // The deadline commits first; the cancellation that follows is refused.
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", (await fixture.RuntimeAsync()).BlockReasonCode);
        string refused = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, "c1000000-0000-4000-8000-000000000002", generation: 1), state, token);
        Assert.Equal("REJECTED", FirstLinePayload(refused, out _).GetProperty("decision").GetString());

        // Both recorded anyway: an open cancellation on a stop the deadline has already ended.
        DateTimeOffset endedAt = (await fixture.LeaseAsync()).ReleasedAt!.Value;
        await using (ControlServerDbContext racer = fixture.OpenConnectionContext())
        {
            racer.RecoveryWorkflows.Add(new RecoveryWorkflowRow
            {
                WorkflowId = BeforeSublotCancellationId,
                WorkflowType = "LOAD_CANCELLATION",
                AgvId = fixture.Options.AgvId,
                DemandId = BeforeSublotDemandId,
                SlotOperationAttemptId = null,
                SlotsJson = "[]",
                ForcedRecoveryGeneration = 0,
                State = RecoveryWorkflowState.AwaitingResult,
                RequestMessageId = Guid.NewGuid().ToString("D"),
                RequestContentHash = new string('b', 64),
                CreatedAt = endedAt,
                UpdatedAt = endedAt
            });
            await racer.SaveChangesAsync(token);
        }
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));

        string ack = await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, "c1000000-0000-4000-8000-000000000101", generation: 1),
            state,
            token);

        Assert.Equal("DurableAck", FirstLineType(ack));
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.Equal(endedAt, (await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(endedAt, (await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == waiting.SublotRequestMessageId, token)).AcknowledgedAt);
        Assert.Single(await fixture.Context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
    }

    /// <summary>
    /// The other interleaving (control-server#116 review, item 2): a load commanded while a cancellation was
    /// open -- again written directly, since the entry check and the engine's read order are meant to prevent
    /// it. The ALL_EMPTY result cannot end a stop whose load went out, so the settlement re-checks and holds
    /// the demand for recovery instead of cancelling it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnAllEmptyResultDoesNotEndAStopWhoseLoadWasCommandedMeanwhile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        // Hide the open cancellation from one runtime pass so it loads, then put it back.
        await SetWorkflowStateAsync(fixture, RecoveryWorkflowState.HistoricalOnly);
        await fixture.SubmitSublotAsync("SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        await SetWorkflowStateAsync(fixture, RecoveryWorkflowState.AwaitingResult);

        string ack = await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, "c1000000-0000-4000-8000-000000000101", generation: 1),
            state,
            token);

        Assert.Equal("DurableAck", FirstLineType(ack));
        Assert.Equal(RecoveryWorkflowState.RecoveryRequired,
            (await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await fixture.DemandRowAsync()).Status);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("LoadCancellationResult_NOT_RECONCILED", runtime.BlockReasonCode);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
    }

    /// <summary>
    /// control-server#116 review, item 2: an entry made before a reconnect still holds the stop. The runtime
    /// may be loading it when the cancellation arrives on the next connection, so the cancellation, judged
    /// in a later generation, is refused all the same.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnEntryFromAnEarlierGenerationStillRefusesTheCancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        await processor.ProcessAsync(
            SublotEntry(fixture, await fixture.RuntimeAsync(), "SUBLOT-001"),
            BeforeSublotConnection(fixture, generation: 1),
            token);
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);

        string response = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 2),
            BeforeSublotConnection(fixture, generation: 2),
            token);

        JsonElement refused = FirstLinePayload(response, out _);
        Assert.Equal("REJECTED", refused.GetProperty("decision").GetString());
        Assert.Equal(ServerReasonCodes.ActionNotAllowedInState,
            refused.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Empty(await fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));
    }

    /// <summary>
    /// control-server#116 review, item 3: a cancellation is judged against the vehicle that sent it. Another
    /// vehicle naming this demand is refused and records nothing -- and a request naming a cancellation on
    /// file is answered from the record only for the vehicle that raised it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ACancellationFromAnotherVehicleIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState other = BeforeSublotConnection(fixture, generation: 1);
        other.AgvId = "AGV-SOMEONE-ELSE";

        string foreign = await processor.ProcessAsync(
            ForAgv(CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), other.AgvId),
            other,
            token);
        Assert.Equal("REJECTED", FirstLinePayload(foreign, out _).GetProperty("decision").GetString());
        Assert.Empty(await fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));

        string own = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1),
            BeforeSublotConnection(fixture, generation: 1),
            token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(own, out _).GetProperty("decision").GetString());
        string foreignRepeat = await processor.ProcessAsync(
            ForAgv(CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), other.AgvId),
            other,
            token);
        Assert.Equal("REJECTED", FirstLinePayload(foreignRepeat, out _).GetProperty("decision").GetString());
    }

    /// <summary>
    /// Ticket item 4 as the onboard does it: the result went out in generation 1 and was taken, the DurableAck
    /// was lost with the link, and the vehicle resends the same line into generation 2. The resend is answered
    /// from the first acceptance; the stop is settled once.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AResultTakenBeforeTheLinkDroppedAndResentAfterTheReconnectIsSettledOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        const string resultMessageId = "c1000000-0000-4000-8000-000000000101";
        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1),
            BeforeSublotConnection(fixture, generation: 1),
            token);
        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, resultMessageId, generation: 1),
            BeforeSublotConnection(fixture, generation: 1),
            token);

        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        string resent = await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, resultMessageId, generation: 2),
            BeforeSublotConnection(fixture, generation: 2),
            token);

        Assert.Equal("DurableAck", FirstLineType(resent));
        Assert.Single(await fixture.Context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
        await AssertStopEndedByOperatorAsync(fixture, waiting, receivedAt);
    }

    /// <summary>
    /// A cancellation request repeated under a new messageId after its stop has already ended -- same
    /// cancellationId, same content -- is answered AUTHORIZED from the record, not refused against the ended
    /// stop and not treated as a content conflict that drops the connection.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ACancellationRepeatedAfterItsStopEndedIsAnsweredFromTheRecord()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);
        await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, "c1000000-0000-4000-8000-000000000101", generation: 1),
            state,
            token);

        string repeated = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        Assert.Equal("AUTHORIZED", FirstLinePayload(repeated, out _).GetProperty("decision").GetString());
        Assert.Equal(RecoveryWorkflowState.Reconciled,
            (await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        await AssertStopEndedByOperatorAsync(fixture, waiting, receivedAt);
    }

    /// <summary>
    /// A block already on the stop survives an iteration the open cancellation holds (control-server#116,
    /// after #118): the entry read that iteration would record a different block, and that unsaved change
    /// is undone to what the store holds -- the code and the time the block began, not a restarted one.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnIterationHeldByAnOpenCancellationLeavesAnEarlierBlockAndItsStartAsTheyWere()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);

        await fixture.DropOnboardSessionAsync();
        await fixture.Engine.ExecuteOnceAsync(token);
        JourneyRuntimeRow blocked = await fixture.RuntimeAsync();
        Assert.Equal("ONBOARD_SESSION_NOT_READY", blocked.BlockReasonCode);
        Assert.NotNull(blocked.BlockReasonSince);

        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);
        await fixture.RestoreSessionReadyAsync();
        // An entry the runtime would record as SUBLOT_SUBMISSION_MISMATCH this iteration.
        await processor.ProcessAsync(SublotEntry(fixture, blocked, "SUBLOT-OTHER"), state, token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, held.Stage);
        Assert.Equal(blocked.BlockReasonCode, held.BlockReasonCode);
        Assert.Equal(blocked.BlockReasonSince, held.BlockReasonSince);
    }

    private static async Task SetWorkflowStateAsync(RuntimeFixture fixture, RecoveryWorkflowState state)
    {
        await using ControlServerDbContext context = fixture.OpenConnectionContext();
        RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.SingleAsync(TestContext.Current.CancellationToken);
        workflow.State = state;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string ForAgv(string line, string agvId)
    {
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(line)!;
        node["agvId"] = agvId;
        return node.ToJsonString();
    }

    private static async Task<RuntimeFixture> ReachSublotWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(BeforeSublotDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        await fixture.ProveSlotDoorsClosedAsync();
        return fixture;
    }

    private static async Task AssertStopEndedByOperatorAsync(
        RuntimeFixture fixture,
        JourneyRuntimeRow waiting,
        DateTimeOffset endedAt)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_OPERATOR", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.Equal(endedAt, (await fixture.LeaseAsync()).ReleasedAt);
        OrderIntentRow pickup = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.Purpose == "TO_PICKUP", token);
        Assert.Equal(endedAt, pickup.VehicleOccupancyReleasedAt);
        ProtocolOutboxRow entryRequest = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == waiting.SublotRequestMessageId, token);
        Assert.Equal(endedAt, entryRequest.AcknowledgedAt);
        Assert.Equal(RecoveryWorkflowState.Reconciled,
            (await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        Assert.Equal(0, await fixture.Context.StationOperations.CountAsync(token));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
    }

    private static OnboardMessageProcessor BeforeSublotProcessor(RuntimeFixture fixture, ControlServerDbContext connection) =>
        TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build());

    private static OnboardConnectionState BeforeSublotConnection(RuntimeFixture fixture, long generation) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = generation,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.Ready
    };

    private static string CancellationBeforeSublotRequest(RuntimeFixture fixture, string cancellationId, long generation) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", generation, new
        {
            cancellationId,
            demandId = BeforeSublotDemandId,
            slotOperationAttemptId = (string?)null,
            @operator = BeforeSublotOperator(fixture),
            reason = "Nothing to load at this stop."
        });

    private static string CancellationBeforeSublotResult(
        RuntimeFixture fixture,
        string messageId,
        long generation,
        string overallOutcome = "ALL_EMPTY",
        bool namesASlot = false) =>
        BeforeSublotEnvelope(fixture, messageId, "LoadCancellationResult", generation, new
        {
            cancellationId = BeforeSublotCancellationId,
            demandId = BeforeSublotDemandId,
            slotOperationAttemptId = (string?)null,
            overallOutcome,
            slotResults = namesASlot
                ? new object[]
                {
                    new
                    {
                        slotNo = 1,
                        outcome = "COMPLETED",
                        finalPhysicalState = "EMPTY",
                        lockState = "LOCKED",
                        unlockOutputState = "RESET",
                        reasonCodes = Array.Empty<string>()
                    }
                }
                : [],
            // Fixed, not the clock: a resend into a later session carries the first line's content.
            observedAt = Now
        });

    private static string SublotEntry(RuntimeFixture fixture, JourneyRuntimeRow runtime, string sublot) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "SublotSubmitted", generation: 1, new
        {
            operationSessionId = runtime.OperationSessionId,
            stationId = runtime.PickupStationId,
            worklistRevision = runtime.WorklistRevision,
            sublot,
            entryMethod = "SCANNER",
            @operator = BeforeSublotOperator(fixture)
        });

    private static JsonElement FirstLinePayload(string wire, out string messageType)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        messageType = document.RootElement.GetProperty("messageType").GetString()!;
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string FirstLineType(string wire)
    {
        _ = FirstLinePayload(wire, out string messageType);
        return messageType;
    }
}
