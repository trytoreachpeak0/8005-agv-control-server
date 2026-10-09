using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// control-server#483: the governed way out of an exception recovery session whose result will never come. A resume's
/// replacement OperationResult is refused whole, the vehicle abandons it (8005-agv-onboard-hmi#254) or goes away for good, and
/// the session would otherwise stay EXECUTING with no action, no second action and no new session.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    private const string StuckProofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_ADMINISTRATOR_CLOSE";
    private const string StuckProof = "administrator-close-proof-not-a-production-secret";
    private const string CloseOperator = "maintenance-483";

    /// <summary>The seeded vehicle's session generation, as a connected vehicle's presence reports it.</summary>
    private const long SeededSessionGeneration = 3;

    /// <summary>
    /// The defect and its exit, in one line of events. The replacement result is refused (RECOVERY_SCOPE_MISMATCH: it settles
    /// fewer slots than were authorized), and every way out the vehicle has is refused: the session stays EXECUTING with
    /// RESUME selected and no allowed action, a forced mechanical recovery in it is RECOVERY_ACTION_ALREADY_SELECTED, a new
    /// session is ACTION_NOT_ALLOWED_IN_STATE. The vehicle is not connected, and an administrator closes its session by naming
    /// the vehicle alone: the resume is judged RecoveryRequired under ADMINISTRATOR_CLOSED, its command is settled so it is not
    /// replayed, the session is CLOSED at a new revision, the CLOSED snapshot goes to the outbox, and the business stays
    /// exactly where the failed load left it -- demand RecoveryRequired, journey Blocked under its code, operation
    /// RecoveryRequired, lease and vehicle held, no ending written. The administrator on the vehicle then opens a new session,
    /// selects RESUME again, and the replacement result commits the load.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWhoseReplacementResultIsRefusedIsClosedByAnAdministratorSoTheVehicleCanRecoverInANewSession()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            (OnboardMessageProcessor processor, OnboardConnectionState state) = await StuckResumeAsync(context, peer);
            string sessionId = StableGuid(RequestId, "exception-recovery-session");

            ExceptionRecoverySessionRow stuck = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("EXECUTING", "RESUME_AFTER_REPAIR"), (stuck.State, stuck.SelectedAction));
            JsonElement executing = await LatestSessionSnapshotAsync(context, sessionId);
            Assert.Equal("EXECUTING", executing.GetProperty("state").GetString());
            Assert.Empty(executing.GetProperty("allowedActions").EnumerateArray());
            string forced = await processor.ProcessAsync(
                RecoveryAction(
                    "FORCED_MECHANICAL_RECOVERY",
                    messageId: "e0000000-0000-4000-8000-000000004831",
                    actionId: "51000000-0000-4000-8000-000000004831"),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal(ServerReasonCodes.RecoveryActionAlreadySelected,
                FirstPayload(forced).GetProperty("problem").GetProperty("reasonCode").GetString());
            string refusedSession = await processor.ProcessAsync(NextSessionRequest(StuckProof), state, token);
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("ExceptionRecoverySessionRejected", MessageType(refusedSession));
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState,
                FirstPayload(refusedSession).GetProperty("problem").GetProperty("reasonCode").GetString());
            BusinessPicture before = await BusinessPictureAsync(context);
            RecoveryWorkflowRow waiting = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(RecoveryWorkflowState.AwaitingResult, waiting.State);
            int linesBefore = peer.Lines.Count;

            RecoverySessionAdministratorCloseResult closed = await CloseAsync(context, peer, CloseRequest(sessionId: null));

            Assert.True(closed.Closed, string.Join(',', closed.Codes));
            Assert.Equal(([], sessionId), (closed.Codes, closed.ExceptionRecoverySessionId));
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("CLOSED", stuck.Revision + 1), (session.State, session.Revision));
            RecoveryWorkflowRow resume = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal((RecoveryWorkflowState.RecoveryRequired, OnboardRecoveryCoordinator.AdministratorClosedOutcome, (string?)null),
                (resume.State, resume.Outcome, resume.ResultMessageId));
            Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageId == waiting.CommandMessageId, token)).AcknowledgedAt);
            JsonElement snapshot = await LatestSessionSnapshotAsync(context, sessionId);
            Assert.Equal(("CLOSED", session.Revision), (snapshot.GetProperty("state").GetString(),
                snapshot.GetProperty("recoverySessionRevision").GetInt64()));
            // v3's closedReason (control-server#382/#385): an administrator closing is a closing without the action's result
            // reconciled, so the snapshot names it like any other, and the row keeps the same reason (review of #534, S2).
            Assert.Equal(
                (ServerReasonCodes.RecoveryActionResultNotReconciled, ServerReasonCodes.RecoveryActionResultNotReconciled),
                (snapshot.GetProperty("closedReason").GetString(), session.ClosedReason));
            Assert.Empty(snapshot.GetProperty("allowedActions").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("blockingFacts").EnumerateArray());
            Assert.Contains(peer.Lines.Skip(linesBefore), line =>
                line.Contains("\"ExceptionRecoverySessionSnapshot\"", StringComparison.Ordinal) &&
                line.Contains("\"CLOSED\"", StringComparison.Ordinal));
            Assert.Equal(
                before with { Sessions = $"{sessionId}:CLOSED:{session.Revision}:RESUME_AFTER_REPAIR", Snapshots = before.Snapshots + 1 },
                await BusinessPictureAsync(context));
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, before.Demand);
            Assert.Equal(JourneyRuntimeStage.Blocked, before.Stage);
            Assert.Equal(StationOperationStatus.RecoveryRequired, before.Operation);
            Assert.True(before.ClaimHeld);
            Assert.Equal(0, before.Completions);
            AdministratorAuditRecordRow audit = Assert.Single(await CloseAuditsAsync(context));
            Assert.Equal((closed.AuditRecordId, GovernanceActionOutcome.Succeeded, GovernedObjectKind.ExceptionRecoverySession, sessionId),
                (audit.AuditRecordId, audit.Outcome, audit.ObjectKind, audit.ObjectId));
            using (JsonDocument detail = JsonDocument.Parse(audit.DetailJson))
            {
                JsonElement root = detail.RootElement;
                Assert.Equal(sessionId, root.GetProperty("exceptionRecoverySessionId").GetString());
                Assert.Equal(CloseOperator, root.GetProperty("said").GetProperty("operatorId").GetString());
                JsonElement vehicle = root.GetProperty("read").GetProperty("vehicle");
                Assert.False(vehicle.GetProperty("connected").GetBoolean());
                Assert.Equal(SeededSessionGeneration, vehicle.GetProperty("recordedSessionGeneration").GetInt64());
                Assert.Equal(AttemptId, vehicle.GetProperty("unsettledSlotOperationAttemptId").GetString());
                Assert.Equal([AttemptId], vehicle.GetProperty("pendingAttemptIds").EnumerateArray().Select(item => item.GetString()));
            }

            // A new press after the closing: a new request and a new message. The refused one above is answered from the inbox
            // with its first response if sent again byte for byte.
            const string nextRequestId = "41000000-0000-4000-8000-000000004834";
            JsonNode pressAgain = JsonNode.Parse(NextSessionRequest(StuckProof))!;
            pressAgain["messageId"] = "e0000000-0000-4000-8000-000000004834";
            pressAgain["payload"]!["requestId"] = nextRequestId;
            string opened = await processor.ProcessAsync(pressAgain.ToJsonString(), state, token);
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));

            // And the recovery the stuck session could not finish goes through in the new one.
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            JsonNode resumeAgain = JsonNode.Parse(RecoveryAction(
                "RESUME_AFTER_REPAIR",
                messageId: "e0000000-0000-4000-8000-000000004835",
                actionId: "51000000-0000-4000-8000-000000004835"))!;
            resumeAgain["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            Assert.Equal("RecoveryActionAccepted",
                MessageType(await processor.ProcessAsync(resumeAgain.ToJsonString(), state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000004836",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED")),
                state,
                token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal(StationOperationStatus.Committed,
                (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == "51000000-0000-4000-8000-000000004835", token)).State);
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.ExceptionRecoverySessionId == nextSessionId, token)).State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// A replacement result that arrives after an administrator closed the session -- the vehicle came back after all -- is
    /// refused whole, BUSINESS_ID_CONTENT_CONFLICT, and the refusal says the session was closed (ADMINISTRATOR_CLOSED): no
    /// resume awaits it, so nothing is kept and nothing is settled. It must stay a refusal: an acknowledgement would make the
    /// vehicle delete the line, and a result that really happened would be gone. The same result before the closing would have
    /// committed the load (the control: it is the exact scope that was authorized).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AReplacementResultArrivingAfterAnAdministratorClosedTheSessionIsRefusedAndKeepsNothing()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            (OnboardMessageProcessor processor, OnboardConnectionState state) = await StuckResumeAsync(context, peer);
            Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId: null))).Closed);
            BusinessPicture closed = await BusinessPictureAsync(context);
            int results = await context.OperationResults.CountAsync(token);
            RecoveryWorkflowRow judged = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);

            string late = Envelope(
                "e0000000-0000-4000-8000-000000004833",
                "OperationResult",
                OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED"));
            string refusal = await processor.ProcessAsync(late, state, token);
            ProtocolProblemAssert.RefusedLine(refusal, "BUSINESS_ID_CONTENT_CONFLICT", late);
            Assert.Contains(RecoveryWorkflowOutcomes.AdministratorClosed,
                FirstPayload(refusal).GetProperty("problem").GetProperty("displayMessage").GetString(), StringComparison.Ordinal);
            await processor.FlushDeferredOutboundAsync(state, token);

            Assert.Equal(results, await context.OperationResults.CountAsync(token));
            Assert.Equal(closed, await BusinessPictureAsync(context));
            RecoveryWorkflowRow after = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal((judged.State, judged.Outcome, judged.ResultMessageId, judged.UpdatedAt),
                (after.State, after.Outcome, after.ResultMessageId, after.UpdatedAt));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    public static TheoryData<string> CloseRefusals => new()
    {
        "another session named",
        "vehicle without an open session",
        "session open",
        "action outside the table",
        "result no longer awaited",
        "connected vehicle reports the attempt",
        "connected vehicle reports the attempt as pending only",
        "connected vehicle reports the attempt as unsettled only",
        "connected vehicle reports a pending result",
        "vehicle in its handshake",
        "connected at another session generation",
        "missing operator, reason and verification",
        "too long",
    };

    /// <summary>
    /// Every unmet premise refuses with its code, writes nothing but the failed audit record, and sends the vehicle nothing:
    /// the session, its workflows, the business and the outbox are as they were. Only the actions in the closable table are
    /// closable (a resume since 2026-10-05, the other three since #484). A connected vehicle whose latest RecoveryStateReport still names the resume's attempt, or any
    /// pending result -- whose messageId does not say which attempt it settles -- may yet deliver the result, so it is not
    /// closed (review S1). Nor is a vehicle in its handshake, whose SessionHello has cleared the facts on file while it is
    /// about to replay its results, or one connected at another session generation than the facts on file (incremental
    /// review, item 1). "result no longer awaited" cannot be reached through the protocol -- a judged result closes the
    /// session first -- and is made by hand to pin the check that stands behind the session's state.
    /// </summary>
    [Theory]
    [MemberData(nameof(CloseRefusals))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnUnmetPremiseRefusesTheClosingWithItsCodeAndChangesNothing(string refusal)
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            RecoverySessionAdministratorCloseRequest request = CloseRequest(sessionId: null);
            long? connected = null;
            bool handshaking = false;
            string[] expected;
            string? judged = sessionId;
            switch (refusal)
            {
                case "session open":
                    await OpenSessionAfterFailedResultAsync(context, peer, action: null);
                    expected = [RecoverySessionAdministratorCloseCodes.SessionNotExecuting];
                    break;
                case "action outside the table":
                    // Every action a session can select is in the table since #484; an action outside it is made by hand to
                    // pin the check that keeps the table the one place that decides.
                    await OpenSessionAfterFailedResultAsync(context, peer, action: "FAULT_CARGO_HANDOFF");
                    ExceptionRecoverySessionRow unlisted = await context.ExceptionRecoverySessions.SingleAsync(token);
                    unlisted.SelectedAction = "LOAD_CORRECTION";
                    await context.SaveChangesAsync(token);
                    context.ChangeTracker.Clear();
                    expected = [RecoverySessionAdministratorCloseCodes.ActionNotClosable];
                    break;
                case "result no longer awaited":
                    await StuckResumeAsync(context, peer);
                    RecoveryWorkflowRow resume = await context.RecoveryWorkflows.SingleAsync(token);
                    resume.State = RecoveryWorkflowState.RecoveryRequired;
                    await context.SaveChangesAsync(token);
                    context.ChangeTracker.Clear();
                    expected = [RecoverySessionAdministratorCloseCodes.ResultNotAwaited];
                    break;
                case "connected vehicle reports the attempt":
                    // As seeded: the report names the attempt both as pending and as the unsettled one.
                    await StuckResumeAsync(context, peer);
                    connected = SeededSessionGeneration;
                    expected = [RecoverySessionAdministratorCloseCodes.ResultInFlightOnVehicle];
                    break;
                case "connected vehicle reports the attempt as pending only":
                    await StuckResumeAsync(context, peer);
                    await SetReportedPendingFactsAsync(context, attempts: [AttemptId], unsettled: null, results: []);
                    connected = SeededSessionGeneration;
                    expected = [RecoverySessionAdministratorCloseCodes.ResultInFlightOnVehicle];
                    break;
                case "connected vehicle reports the attempt as unsettled only":
                    await StuckResumeAsync(context, peer);
                    await SetReportedPendingFactsAsync(context, attempts: [], unsettled: AttemptId, results: []);
                    connected = SeededSessionGeneration;
                    expected = [RecoverySessionAdministratorCloseCodes.ResultInFlightOnVehicle];
                    break;
                case "vehicle in its handshake":
                    // As a SessionHello leaves it: the reported facts cleared, the connection not routable yet.
                    await StuckResumeAsync(context, peer);
                    await SetReportedPendingFactsAsync(context, attempts: [], unsettled: null, results: []);
                    handshaking = true;
                    expected = [RecoverySessionAdministratorCloseCodes.VehicleHandshakeInProgress];
                    break;
                case "connected at another session generation":
                    await StuckResumeAsync(context, peer);
                    await SetReportedPendingFactsAsync(context, attempts: [], unsettled: null, results: []);
                    connected = SeededSessionGeneration + 1;
                    expected = [RecoverySessionAdministratorCloseCodes.VehicleHandshakeInProgress];
                    break;
                case "connected vehicle reports a pending result":
                    await StuckResumeAsync(context, peer);
                    await SetReportedPendingFactsAsync(context, attempts: [], unsettled: null, results: ["e0000000-0000-4000-8000-000000004839"]);
                    connected = SeededSessionGeneration;
                    expected = [RecoverySessionAdministratorCloseCodes.ResultInFlightOnVehicle];
                    break;
                default:
                    await StuckResumeAsync(context, peer);
                    (request, expected, judged) = refusal switch
                    {
                        "another session named" => (
                            request with { ExceptionRecoverySessionId = "59000000-0000-4000-8000-000000004839" },
                            new[] { RecoverySessionAdministratorCloseCodes.SessionMismatch },
                            sessionId),
                        "vehicle without an open session" => (
                            request with { AgvId = "AGV-8005-02" },
                            new[] { RecoverySessionAdministratorCloseCodes.SessionNotFound },
                            (string?)null),
                        "missing operator, reason and verification" => (
                            request with { OperatorId = " ", Reason = null, SiteVerification = "" },
                            new[]
                            {
                                RecoverySessionAdministratorCloseCodes.OperatorRequired,
                                RecoverySessionAdministratorCloseCodes.ReasonRequired,
                                RecoverySessionAdministratorCloseCodes.SiteVerificationRequired
                            },
                            (string?)null),
                        "too long" => (
                            request with { Reason = new string('x', RecoverySessionAdministratorClose.MaxTextLength + 1) },
                            new[] { RecoverySessionAdministratorCloseCodes.FieldTooLong },
                            (string?)null),
                        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null)
                    };
                    break;
            }
            BusinessPicture before = await BusinessPictureAsync(context);
            string outbox = await OutboxAccountAsync(context);
            string workflows = await WorkflowAccountAsync(context);
            int lines = peer.Lines.Count;

            RecoverySessionAdministratorCloseResult result =
                await CloseAsync(context, peer, request, new FixedPresence(connected, handshaking));

            Assert.False(result.Closed);
            Assert.Equal(expected, result.Codes);
            Assert.Equal(judged, result.ExceptionRecoverySessionId);
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(outbox, await OutboxAccountAsync(context));
            Assert.Equal(workflows, await WorkflowAccountAsync(context));
            Assert.Equal(lines, peer.Lines.Count);
            AdministratorAuditRecordRow audit = Assert.Single(await CloseAuditsAsync(context));
            Assert.Equal((result.AuditRecordId, GovernanceActionOutcome.Failed), (audit.AuditRecordId, audit.Outcome));
            foreach (string code in expected)
                Assert.Contains(code, audit.DetailJson, StringComparison.Ordinal);
            if (connected is not null || handshaking)
            {
                using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
                JsonElement vehicle = detail.RootElement.GetProperty("read").GetProperty("vehicle");
                Assert.Equal((connected is not null, handshaking),
                    (vehicle.GetProperty("connected").GetBoolean(), vehicle.GetProperty("handshaking").GetBoolean()));
                if (connected is not null)
                    Assert.Equal(connected.Value, vehicle.GetProperty("connectedSessionGeneration").GetInt64());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// A connected vehicle whose latest report no longer names the resume's attempt or any pending result has nothing of it on
    /// its way, and its session is closed (review S1: only a result that may still arrive holds the closing back). The audit
    /// records that it was connected, at which generation, and the three facts it was judged on.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AConnectedVehicleWhoseReportNoLongerNamesTheResumeIsClosed()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            await StuckResumeAsync(context, peer);
            await SetReportedPendingFactsAsync(context, attempts: [], unsettled: null, results: []);

            RecoverySessionAdministratorCloseResult result = await CloseAsync(
                context, peer, CloseRequest(StableGuid(RequestId, "exception-recovery-session")),
                new FixedPresence(SeededSessionGeneration, handshaking: false));

            Assert.True(result.Closed, string.Join(',', result.Codes));
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
            using JsonDocument detail = JsonDocument.Parse(Assert.Single(await CloseAuditsAsync(context)).DetailJson);
            JsonElement vehicle = detail.RootElement.GetProperty("read").GetProperty("vehicle");
            Assert.True(vehicle.GetProperty("connected").GetBoolean());
            Assert.Equal(SeededSessionGeneration, vehicle.GetProperty("connectedSessionGeneration").GetInt64());
            Assert.Empty(vehicle.GetProperty("pendingAttemptIds").EnumerateArray());
            Assert.Empty(vehicle.GetProperty("pendingResultIds").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("unsettledSlotOperationAttemptId").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// Review S4: the closing is read, decided and written under one write transaction. The coordinator refuses to close
    /// without one -- so a caller that forgets to open it fails loudly instead of racing the inbox -- and the service opens it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheClosingIsDecidedOnlyUnderAWriteTransaction()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            await StuckResumeAsync(context, peer);
            BusinessPicture before = await BusinessPictureAsync(context);
            OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(StuckProofVariable), peer);

            Assert.Null(context.Database.CurrentTransaction);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CloseSessionAwaitingResultAsync(
                AgvId, null, null, false, _ => { }, token));
            context.ChangeTracker.Clear();
            Assert.Equal(before, await BusinessPictureAsync(context));

            Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId: null))).Closed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// Review S5: a closing that fails with an exception still leaves a failed audit record, written after the transaction
    /// rolled back, and the exception reaches the caller; nothing is closed. A request already cancelled writes none -- the
    /// caller has gone and the store may be going with it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AClosingThatFailsIsAuditedUnlessTheRequestWasCancelled(bool cancelled)
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            await StuckResumeAsync(context, peer);
            BusinessPicture before = await BusinessPictureAsync(context);
            using CancellationTokenSource request = new();
            if (cancelled) await request.CancelAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => Closing(context, peer, new ThrowingPresence())
                .CloseAsync(CloseRequest(sessionId: null), request.Token));
            context.ChangeTracker.Clear();

            Assert.Equal(before, await BusinessPictureAsync(context));
            AdministratorAuditRecordRow[] audits = await CloseAuditsAsync(context);
            if (cancelled)
            {
                Assert.Empty(audits);
            }
            else
            {
                AdministratorAuditRecordRow audit = Assert.Single(audits);
                Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
                using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
                Assert.Equal(("ERROR", typeof(InvalidOperationException).FullName), (
                    detail.RootElement.GetProperty("result").GetString(),
                    detail.RootElement.GetProperty("error").GetString()));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// Review S5, the path with the transaction open (incremental review, item 3): the closing has been decided and staged --
    /// the workflow judged, the command settled, the session CLOSED, the snapshot queued -- when writing its record fails.
    /// The transaction rolls back all of it, the failed audit record is written after the rollback, and the exception
    /// reaches the caller: the session is still EXECUTING and waiting, the command still unsettled, nothing queued.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AClosingThatFailsWithItsTransactionOpenRollsBackAndIsAuditedAsFailed()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            await StuckResumeAsync(context, peer);
            BusinessPicture before = await BusinessPictureAsync(context);
            string outbox = await OutboxAccountAsync(context);
            string workflows = await WorkflowAccountAsync(context);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Closing(context, peer, audit: store => new SuccessAuditFailingWriter(store))
                    .CloseAsync(CloseRequest(sessionId: null), token));
            context.ChangeTracker.Clear();

            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(outbox, await OutboxAccountAsync(context));
            Assert.Equal(workflows, await WorkflowAccountAsync(context));
            AdministratorAuditRecordRow audit = Assert.Single(await CloseAuditsAsync(context));
            Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
            using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
            Assert.Equal(("ERROR", typeof(InvalidOperationException).FullName), (
                detail.RootElement.GetProperty("result").GetString(),
                detail.RootElement.GetProperty("error").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// Once the closing has committed, failing to send the CLOSED snapshot changes nothing about it: the closing stands, the
    /// result says closed, and the snapshot stays in the outbox for the reconnect replay -- whatever the send threw.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AFailedSnapshotSendAfterTheClosingCommittedStillReportsTheClosing()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await StuckResumeAsync(context, new RecordingPeer(context));
            string sessionId = StableGuid(RequestId, "exception-recovery-session");

            RecoverySessionAdministratorCloseResult result =
                await CloseAsync(context, new FailingPeer(), CloseRequest(sessionId: null));

            Assert.True(result.Closed, string.Join(',', result.Codes));
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal("CLOSED", session.State);
            Assert.Equal("CLOSED", (await LatestSessionSnapshotAsync(context, sessionId)).GetProperty("state").GetString());
            Assert.Equal(GovernanceActionOutcome.Succeeded, Assert.Single(await CloseAuditsAsync(context)).Outcome);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// The HTTP entry's boundary: without a credential configured it is unavailable, with a wrong one it is unauthorized, and
    /// a request naming no vehicle is incomplete -- none of those writes an audit record or touches the session. An
    /// authenticated refusal is a 409 listing every code with its description, audited; an authenticated closing naming the
    /// vehicle alone is a 200 carrying the session it closed and its audit record.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheHttpEntryAuditsOnlyAuthenticatedRequestsAndClosesOnlyWithTheCredential()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        string credentialVariable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credentialVariable, "close-credential");
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            await StuckResumeAsync(context, peer);
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            RecoverySessionAdministratorCloseHttpRequest request =
                new(AgvId, null, CloseOperator, "车机已换，续作结果永远不会来", "SITE-483-01", "MAINTENANCE_ADMINISTRATOR");
            BusinessPicture before = await BusinessPictureAsync(context);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>((await PostCloseAsync(
                context, peer, "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N"), "Bearer close-credential",
                request)).Result).StatusCode);
            Assert.IsType<UnauthorizedHttpResult>(
                (await PostCloseAsync(context, peer, credentialVariable, "Bearer wrong", request)).Result);
            Assert.IsType<UnauthorizedHttpResult>(
                (await PostCloseAsync(context, peer, credentialVariable, null, request)).Result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>((await PostCloseAsync(
                context, peer, credentialVariable, "Bearer close-credential", request with { AgvId = " " })).Result).StatusCode);
            Assert.Empty(await CloseAuditsAsync(context));
            Assert.Equal(before, await BusinessPictureAsync(context));

            ProblemHttpResult refused = Assert.IsType<ProblemHttpResult>((await PostCloseAsync(
                context, peer, credentialVariable, "Bearer close-credential", request with { SiteVerification = null })).Result);
            Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
            Assert.Equal([RecoverySessionAdministratorCloseCodes.SiteVerificationRequired],
                Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.ProblemDetails.Extensions["codes"]));
            Assert.Equal(
                RecoverySessionAdministratorCloseCodes.Descriptions[RecoverySessionAdministratorCloseCodes.SiteVerificationRequired],
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(refused.ProblemDetails.Extensions["descriptions"])
                    [RecoverySessionAdministratorCloseCodes.SiteVerificationRequired]);
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await CloseAuditsAsync(context)).Outcome);

            // A vehicle id in the wrong case is another vehicle to SQLite: refused as having no open session, with the id
            // the server looked up echoed so the person can see the mistake (incremental review, item 2).
            ProblemHttpResult unknown = Assert.IsType<ProblemHttpResult>((await PostCloseAsync(
                context, peer, credentialVariable, "Bearer close-credential", request with { AgvId = " agv-8005-01 " })).Result);
            Assert.Equal(StatusCodes.Status409Conflict, unknown.StatusCode);
            Assert.Equal([RecoverySessionAdministratorCloseCodes.SessionNotFound],
                Assert.IsAssignableFrom<IReadOnlyList<string>>(unknown.ProblemDetails.Extensions["codes"]));
            Assert.Equal("agv-8005-01", unknown.ProblemDetails.Extensions["agvId"]);
            Assert.Contains("车号填错",
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(unknown.ProblemDetails.Extensions["descriptions"])
                    [RecoverySessionAdministratorCloseCodes.SessionNotFound], StringComparison.Ordinal);
            Assert.Equal("EXECUTING", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);

            RecoverySessionAdministratorCloseResponse body = Assert.IsType<Ok<RecoverySessionAdministratorCloseResponse>>(
                (await PostCloseAsync(context, peer, credentialVariable, "Bearer close-credential", request)).Result).Value!;
            Assert.Equal(("CLOSED", AgvId, sessionId), (body.Outcome, body.AgvId, body.ExceptionRecoverySessionId));
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
            AdministratorAuditRecordRow[] audits = await CloseAuditsAsync(context);
            Assert.Equal(3, audits.Length);
            Assert.Contains(audits, row => row.AuditRecordId == body.AuditRecordId &&
                                           row.Outcome == GovernanceActionOutcome.Succeeded &&
                                           row.ObjectId == sessionId &&
                                           row.ClaimedAdministratorRole == "MAINTENANCE_ADMINISTRATOR");
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The seeded load failed, a session opened on it and a resume was authorized; the resume's replacement result then
    /// settled only slot 1 of the two authorized and was refused whole (RECOVERY_SCOPE_MISMATCH), so nothing of it is kept and
    /// the session waits, EXECUTING, for a result that will not come.
    /// </summary>
    private static async Task<(OnboardMessageProcessor Processor, OnboardConnectionState State)> StuckResumeAsync(
        ControlServerDbContext context,
        RecordingPeer peer)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await SeedBlockedJourneyAsync(context);
        OnboardMessageProcessor processor = Processor(context, peer, StuckProofVariable);
        OnboardConnectionState state = CurrentState(deferOutbound: true);
        await AuthorizeResumeAfterFailedResultAsync(processor, context, state, StuckProof);
        string narrower = Envelope(
            "e0000000-0000-4000-8000-000000004830",
            "OperationResult",
            OperationResultPayload(slots: [1], journalCheckpoint: "RESUME_RESULT_RECORDED"));
        ProtocolProblemAssert.RefusedLine(
            await processor.ProcessAsync(narrower, state, token), "RECOVERY_SCOPE_MISMATCH", narrower);
        await processor.FlushDeferredOutboundAsync(state, token);
        Assert.Equal(RecoveryWorkflowState.AwaitingResult,
            (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        context.ChangeTracker.Clear();
        return (processor, state);
    }

    /// <summary>The seeded load failed and a session opened on it; <paramref name="action"/>, when given, was then selected.</summary>
    private static async Task OpenSessionAfterFailedResultAsync(
        ControlServerDbContext context,
        RecordingPeer peer,
        string? action)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await SeedBlockedJourneyAsync(context);
        OnboardMessageProcessor processor = Processor(context, peer, StuckProofVariable);
        OnboardConnectionState state = CurrentState(deferOutbound: true);
        Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
            Envelope(
                "e0000000-0000-4000-8000-000000000010",
                "OperationResult",
                OperationResultPayload(completed: false, journalCheckpoint: "RESULT_UNKNOWN_RECORDED")),
            state,
            token)));
        await processor.FlushDeferredOutboundAsync(state, token);
        Assert.Equal("ExceptionRecoverySessionOpened",
            MessageType(await processor.ProcessAsync(RecoverySessionRequest(StuckProof), state, token)));
        await processor.FlushDeferredOutboundAsync(state, token);
        if (action is not null)
        {
            Assert.Equal("RecoveryActionAccepted",
                MessageType(await processor.ProcessAsync(RecoveryAction(action), state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
        }
        context.ChangeTracker.Clear();
    }

    /// <summary>What the vehicle's latest RecoveryStateReport left on file, set by hand.</summary>
    private static async Task SetReportedPendingFactsAsync(
        ControlServerDbContext context, string[] attempts, string? unsettled, string[] results)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SessionRecoveryRow row = await context.SessionRecoveries.SingleAsync(token);
        row.PendingAttemptIdsJson = JsonSerializer.Serialize(attempts);
        row.UnsettledSlotOperationAttemptId = unsettled;
        row.PendingResultIdsJson = JsonSerializer.Serialize(results);
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
    }

    private static RecoverySessionAdministratorCloseRequest CloseRequest(string? sessionId) =>
        new(AgvId, sessionId, CloseOperator, "车机已换，续作结果永远不会来", "SITE-483-01", "MAINTENANCE_ADMINISTRATOR");

    private static RecoverySessionAdministratorClose Closing(
        ControlServerDbContext context,
        IOnboardPeer peer,
        IOnboardConnectionPresence? presence = null,
        Func<IGovernanceAuditWriter, IGovernanceAuditWriter>? audit = null) =>
        new(
            context,
            TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(StuckProofVariable), peer),
            presence ?? new FixedPresence(null, handshaking: false),
            (audit ?? (store => store))(new GovernanceStore(
                context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default)),
            new FixedTimeProvider(Now),
            NullLogger<RecoverySessionAdministratorClose>.Instance);

    private static async Task<RecoverySessionAdministratorCloseResult> CloseAsync(
        ControlServerDbContext context,
        IOnboardPeer peer,
        RecoverySessionAdministratorCloseRequest request,
        IOnboardConnectionPresence? presence = null)
    {
        RecoverySessionAdministratorCloseResult result =
            await Closing(context, peer, presence).CloseAsync(request, TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        return result;
    }

    private static async Task<Results<Ok<RecoverySessionAdministratorCloseResponse>, UnauthorizedHttpResult, ProblemHttpResult>>
        PostCloseAsync(
            ControlServerDbContext context,
            IOnboardPeer peer,
            string credentialVariable,
            string? authorization,
            RecoverySessionAdministratorCloseHttpRequest request)
    {
        DefaultHttpContext http = new();
        if (authorization is not null)
            http.Request.Headers.Authorization = authorization;
        var result = await RecoverySessionAdministratorCloseEndpoints.HandleAsync(
            http,
            request,
            Closing(context, peer),
            Options.Create(new VehicleFaultRecoveryOptions { Enabled = true, CredentialEnvironmentVariable = credentialVariable }),
            TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        return result;
    }

    private static Task<AdministratorAuditRecordRow[]> CloseAuditsAsync(ControlServerDbContext context) =>
        context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == RecoverySessionAdministratorClose.AuditAction)
            .ToArrayAsync(TestContext.Current.CancellationToken);

    private static async Task<string> WorkflowAccountAsync(ControlServerDbContext context) =>
        string.Join(';', (await context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.WorkflowId, StringComparer.Ordinal)
            .Select(row => $"{row.WorkflowId}:{row.State}:{row.Outcome}:{row.ResultMessageId}:{row.UpdatedAt}"));

    /// <summary>
    /// A vehicle connected at <paramref name="generation"/>, or not connected when it is null; and in its handshake or not.
    /// </summary>
    private sealed class FixedPresence(long? generation, bool handshaking) : IOnboardConnectionPresence
    {
        public long? ConnectedSessionGeneration(string agvId) => generation;

        public bool IsHandshaking(string agvId) => handshaking;
    }

    /// <summary>A presence that fails: the closing breaks before anything is decided.</summary>
    private sealed class ThrowingPresence : IOnboardConnectionPresence
    {
        public long? ConnectedSessionGeneration(string agvId) =>
            throw new InvalidOperationException("The connection table could not be read.");

        public bool IsHandshaking(string agvId) => false;
    }

    /// <summary>An audit writer whose record of a successful closing fails; every other record is written.</summary>
    private sealed class SuccessAuditFailingWriter(IGovernanceAuditWriter inner) : IGovernanceAuditWriter
    {
        public Task<string> WriteBusinessAsync(
            GovernanceAuditEntry entry, DateTimeOffset recordedAt, CancellationToken cancellationToken) =>
            inner.WriteBusinessAsync(entry, recordedAt, cancellationToken);

        public Task<string> WriteAdministratorAsync(
            GovernanceAuditEntry entry, DateTimeOffset recordedAt, CancellationToken cancellationToken) =>
            entry.Outcome == GovernanceActionOutcome.Succeeded
                ? throw new InvalidOperationException("The audit store refused the record.")
                : inner.WriteAdministratorAsync(entry, recordedAt, cancellationToken);
    }

    /// <summary>A peer whose every send fails with something other than a lost connection.</summary>
    private sealed class FailingPeer : IOnboardPeer
    {
        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The line could not be written.");
    }
}
