using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// The server's half of the premises 8005-agv-onboard-hmi#236 rests on. The vehicle asks again for a compensation or
/// correction authorization after a reconnect, and shows no "result unknown" timeout, on the strength of two server
/// behaviours: an authorized command that never reached the vehicle goes out again in the next session, and a
/// repeated authorization request re-sends the command it already earned rather than authorizing a second time.
/// Nothing pinned either end to end until this.
/// </summary>
/// <remarks>
/// Should one of these turn red, the vehicle's design is what is wrong, not this file: a server that does not replay
/// leaves a request that did arrive stranded, and one that authorizes twice turns the vehicle's resend into a second
/// door-opening command.
/// </remarks>
public sealed partial class RecoveryStateMachineG2Tests
{
    /// <summary>
    /// A compensation authorized in one session whose command the vehicle never acknowledged goes out again, the same
    /// command, when the vehicle reconnects.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnAuthorizedCompensationCommandTheVehicleNeverAcknowledgedIsReplayedInTheNextSession()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_REPLAYED_ON_RECONNECT";
        const string proof = "compensate-replayed-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            _ = await ReachCompensationResultAsync(processor, state, proof);
            ProtocolOutboxRow command = await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageType == "LoadCompensationCommand", token);
            Assert.Null(command.AcknowledgedAt);

            await AdvanceSessionGenerationAsync(context, state, 4);
            peer.Lines.Clear();
            await processor.ProcessAsync(RecoveryStateReport(4, unsettledAttemptId: null), state, token);

            string replayed = Assert.Single(peer.Lines, line => MessageType(line) == "LoadCompensationCommand");
            Assert.Equal(command.MessageId, MessageId(replayed));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A second <c>LoadCompensationRequested</c> for a compensation already authorized -- a new messageId, the same
    /// payload, as the vehicle resends it -- re-sends the command it already earned and authorizes nothing again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ARepeatedCompensationRequestResendsTheCommandItEarnedAndAuthorizesNothingAgain()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_REQUESTED_TWICE";
        const string proof = "compensate-requested-twice-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            _ = await ReachCompensationResultAsync(processor, state, proof);
            RecoveryWorkflowRow authorized = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.NotNull(authorized.CommandMessageId);
            long revision = (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).Revision;

            peer.Lines.Clear();
            string response = await processor.ProcessAsync(
                CompensationRequest("90000000-0000-4000-8000-000000000022"), state, token);

            // Answered as the request already made: no refusal, which the vehicle would take as the compensation's
            // end and drop the vector it is about to carry out (8005-agv-onboard-hmi#236).
            Assert.Equal(string.Empty, response);
            string resent = Assert.Single(peer.Lines, line => MessageType(line) == "LoadCompensationCommand");
            Assert.Equal(authorized.CommandMessageId, MessageId(resent));
            Assert.Equal(1, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "LoadCompensationCommand", token));
            RecoveryWorkflowRow after = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(authorized.CommandMessageId, after.CommandMessageId);
            Assert.Equal(authorized.State, after.State);
            Assert.Equal(revision, (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A correction authorized in one session whose command the vehicle never acknowledged goes out again, the same
    /// command, when the vehicle reconnects.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AnAuthorizedCorrectionCommandTheVehicleNeverAcknowledgedIsReplayedInTheNextSession()
    {
        const string correctionId = "b2000000-0000-4000-8000-000000000011";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCorrectableLoadAsync(context, JourneyRuntimeStage.AwaitingStationDeparture);
        RecordingPeer peer = new(context);
        OnboardMessageProcessor processor = Processor(context, peer, CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        await processor.ProcessAsync(CorrectionRequest(correctionId), state, token);
        ProtocolOutboxRow command = await context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "LoadCorrectionCommand", token);
        Assert.Null(command.AcknowledgedAt);

        await AdvanceSessionGenerationAsync(context, state, 4);
        peer.Lines.Clear();
        await processor.ProcessAsync(RecoveryStateReport(4, unsettledAttemptId: null), state, token);

        string replayed = Assert.Single(peer.Lines, line => MessageType(line) == "LoadCorrectionCommand");
        Assert.Equal(command.MessageId, MessageId(replayed));
    }

    /// <summary>
    /// A second <c>LoadCorrectionRequested</c> for a correction already authorized -- a new messageId, the same
    /// payload -- re-sends the command it already earned and authorizes nothing again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ARepeatedCorrectionRequestResendsTheCommandItEarnedAndAuthorizesNothingAgain()
    {
        const string correctionId = "b2000000-0000-4000-8000-000000000012";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCorrectableLoadAsync(context, JourneyRuntimeStage.AwaitingStationDeparture);
        RecordingPeer peer = new(context);
        OnboardMessageProcessor processor = Processor(context, peer, CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        await processor.ProcessAsync(CorrectionRequest(correctionId), state, token);
        RecoveryWorkflowRow authorized = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
        Assert.NotNull(authorized.CommandMessageId);

        peer.Lines.Clear();
        string response = await processor.ProcessAsync(
            WithMessageId(CorrectionRequest(correctionId), "b2000000-0000-4000-8000-000000000020"), state, token);

        Assert.NotEqual("LoadCorrectionRejected", string.IsNullOrEmpty(response) ? null : MessageType(response));
        string resent = Assert.Single(peer.Lines, line => MessageType(line) == "LoadCorrectionCommand");
        Assert.Equal(authorized.CommandMessageId, MessageId(resent));
        Assert.Equal(1, await context.ProtocolOutbox.CountAsync(
            row => row.MessageType == "LoadCorrectionCommand", token));
        RecoveryWorkflowRow after = Assert.Single(await context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(authorized.CommandMessageId, after.CommandMessageId);
    }

    /// <summary>
    /// Another operator pressing again is still the same request -- the operator is not one of the manifest's business
    /// keys -- so it is answered the same way, and who asked again, and when, is written down (8005-agv-onboard-hmi#236).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationRequestedAgainByAnotherOperatorIsTheSameRequestAndIsAudited()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_OTHER_OPERATOR";
        const string proof = "compensate-other-operator-proof-not-a-production-secret";
        const string repeatMessageId = "90000000-0000-4000-8000-000000000024";
        const string otherOperatorId = "maintenance-002";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(proofVariable),
                peer, recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            _ = await ReachCompensationResultAsync(processor, state, proof);
            RecoveryWorkflowRow authorized = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id == 2129);

            peer.Lines.Clear();
            string response = await processor.ProcessAsync(
                Envelope(
                    repeatMessageId,
                    "LoadCompensationRequested",
                    new
                    {
                        recoveryActionId = ActionId,
                        exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
                        demandId = DemandId,
                        slotOperationAttemptId = AttemptId,
                        @operator = new { operatorId = otherOperatorId, verificationMethod = "BADGE", verifiedAt = Now.AddMinutes(5) }
                    }),
                state,
                token);

            Assert.Equal(string.Empty, response);
            string resent = Assert.Single(peer.Lines, line => MessageType(line) == "LoadCompensationCommand");
            Assert.Equal(authorized.CommandMessageId, MessageId(resent));
            Assert.Equal(1, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "LoadCompensationCommand", token));
            (LogLevel Level, EventId EventId, string Message) audit = Assert.Single(
                log.Entries, entry => entry.EventId.Id == 2129);
            Assert.Contains(otherOperatorId, audit.Message, StringComparison.Ordinal);
            Assert.Contains(repeatMessageId, audit.Message, StringComparison.Ordinal);
            Assert.Contains(ActionId, audit.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The edges of the idempotent answer above: a repeated compensation request is answered as already made only for
    /// the same compensation, with its command bound, in a session still open. Everything else stays refused, and no
    /// command is authorized for it (8005-agv-onboard-hmi#236). The fifth edge, another compensation of the session
    /// awaiting its outcome, is <see cref="ASecondCompensationIsNotAuthorizedWhileTheFirstAwaitsItsOutcome"/>.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [InlineData("unknown-action", ServerReasonCodes.ActionNotAllowedInState)]
    [InlineData("other-demand", ServerReasonCodes.ActionNotAllowedInState)]
    [InlineData("other-attempt", ServerReasonCodes.ActionNotAllowedInState)]
    [InlineData("other-session", ServerReasonCodes.ActionNotAllowedInState)]
    [InlineData("result-reported", ServerReasonCodes.ActionNotAllowedInState)]
    [InlineData("session-closed", ServerReasonCodes.RecoverySessionNotOpen)]
    public async Task ARepeatedCompensationRequestOutsideTheSameOpenBoundCompensationIsStillRefused(
        string variant,
        string reasonCode)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_REPEAT_REFUSED";
        const string proof = "compensate-repeat-refused-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            string failedResult = await ReachCompensationResultAsync(processor, state, proof);
            Assert.NotNull((await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).CommandMessageId);
            switch (variant)
            {
                case "result-reported":
                    Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(failedResult, state, token)));
                    break;
                case "session-closed":
                    // Closed by something other than this compensation's own result, which would have moved the
                    // workflow past AwaitingResult and be the case above.
                    ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.SingleAsync(token);
                    session.State = "CLOSED";
                    await context.SaveChangesAsync(token);
                    break;
            }

            RecoveryWorkflowRow before = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            int commands = await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "LoadCompensationCommand", token);
            JsonNode request = JsonNode.Parse(CompensationRequest("90000000-0000-4000-8000-000000000023"))!;
            JsonNode payload = request["payload"]!;
            switch (variant)
            {
                case "unknown-action":
                    payload["recoveryActionId"] = "50000000-0000-4000-8000-0000000000ff";
                    break;
                case "other-demand":
                    payload["demandId"] = "20000000-0000-4000-8000-0000000000ff";
                    break;
                case "other-attempt":
                    payload["slotOperationAttemptId"] = "30000000-0000-4000-8000-0000000000ff";
                    break;
                case "other-session":
                    payload["exceptionRecoverySessionId"] = "60000000-0000-4000-8000-0000000000ff";
                    break;
            }

            string response = await processor.ProcessAsync(request.ToJsonString(), state, token);

            Assert.Equal("LoadCompensationRejected", MessageType(response));
            Assert.Equal(reasonCode, FirstPayload(response).GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.Equal(commands, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "LoadCompensationCommand", token));
            RecoveryWorkflowRow after = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(before.State, after.State);
            Assert.Equal(before.CommandMessageId, after.CommandMessageId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>The <c>LoadCompensationRequested</c> <see cref="ReachCompensationResultAsync"/> sends, under another id.</summary>
    private static string CompensationRequest(string messageId) => Envelope(
        messageId,
        "LoadCompensationRequested",
        new
        {
            recoveryActionId = ActionId,
            exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            @operator = Operator()
        });

    /// <summary>
    /// The same line under another messageId, every other byte kept: the server hashes the payload's raw text, and the
    /// vehicle's resend carries it byte for byte, so re-serializing here would test a request the vehicle never sends.
    /// </summary>
    private static string WithMessageId(string wire, string messageId)
    {
        string current = MessageId(wire);
        Assert.Equal(1, wire.Split(current).Length - 1);
        return wire.Replace(current, messageId, StringComparison.Ordinal);
    }

    private static string MessageId(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("messageId").GetString()!;
    }
}
