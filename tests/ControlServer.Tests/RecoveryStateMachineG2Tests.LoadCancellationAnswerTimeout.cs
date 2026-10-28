using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>
/// control-server#837, the user's choice of 2026-10-27 (option a): a during-load cancellation authorized and never answered. Until
/// then nothing ended it on the server's side -- a recovery session needs the journey Blocked, a slot fault declaration is refused
/// while the cancellation is open, the load's own failure does not end the demand under it -- and only a database edit did. Past
/// <see cref="JourneyRuntimeOptions.LoadCancellationAnswerTimeout"/> the journey is Blocked and the load RecoveryRequired, and the
/// administrator's ways out are open.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    /// <summary>
    /// Before the timeout a session is refused (the journey is not Blocked); past it the session opens, the compensation and the
    /// fault cargo handoff are each offered and taken, and their result ends the demand, cancels the load and closes the journey.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("COMPENSATE_LOAD_ALL_EMPTY", "CANCELLED_BY_LOAD_COMPENSATION")]
    [InlineData("FAULT_CARGO_HANDOFF", "TERMINATED_BY_FAULT_CARGO_HANDOFF")]
    public async Task AfterAnUnansweredCancellationTimesOutTheAdministratorsWaysOutEndTheDemand(string action, string endingCode)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS837_ANSWER_TIMEOUT";
        const string proof = "cs837-answer-timeout-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (OnboardMessageProcessor processor, OnboardConnectionState state) =
                await AuthorizeInFlightCancellationAsync(context, proofVariable);
            Assert.Equal("ExceptionRecoverySessionRejected", MessageType(await processor.ProcessAsync(
                Altered(RecoverySessionRequest(proof), "b8370000-0000-4000-8000-000000000001"), state, token)));

            TimeSpan timeout = new JourneyRuntimeOptions().LoadCancellationAnswerTimeout;
            JourneyRuntimeRow waiting = await context.JourneyRuntimes.SingleAsync(token);
            Assert.False(await LoadCancellationAnswerTimeout.TryExpireAsync(
                context, waiting, Now + timeout - TimeSpan.FromSeconds(1), timeout, NullLogger.Instance, token));
            Assert.True(await LoadCancellationAnswerTimeout.TryExpireAsync(
                context, waiting, Now + timeout + TimeSpan.FromSeconds(1), timeout, NullLogger.Instance, token));
            context.ChangeTracker.Clear();
            Assert.Equal(StationOperationStatus.RecoveryRequired, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow blocked = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal((JourneyRuntimeStage.Blocked, LoadCancellationAnswerTimeout.BlockReason), (blocked.Stage, blocked.BlockReasonCode));

            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(RecoverySessionRequest(proof), state, token)));
            string[] offered = [.. (await LatestSessionSnapshotAsync(context, StableGuid(RequestId, "exception-recovery-session")))
                .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()!)];
            Assert.Contains(action, offered);
            string accepted = await processor.ProcessAsync(RecoveryAction(action), state, token);
            Assert.True(MessageType(accepted) == "RecoveryActionAccepted", accepted);
            string result = action == "COMPENSATE_LOAD_ALL_EMPTY"
                ? await AuthorizedAllEmptyCompensationAsync(processor, state)
                : HandedOffResult();
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result, state, token)));

            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow ended = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal((JourneyRuntimeStage.Completed, endingCode), (ended.Stage, ended.BlockReasonCode));
            RecoveryWorkflowRow cancellation = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == UnreconciledCancellationId, token);
            Assert.Equal(
                (RecoveryWorkflowState.RecoveryRequired, RecoveryWorkflowOutcomes.LoadCancellationAnswerTimedOut),
                (cancellation.State, cancellation.Outcome));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }
}
