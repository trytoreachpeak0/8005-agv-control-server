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

    /// <summary>
    /// The defect and its exit, in one line of events. The replacement result is refused (RECOVERY_SCOPE_MISMATCH: it settles
    /// fewer slots than were authorized), and every way out the vehicle has is refused: the session stays EXECUTING with
    /// RESUME selected and no allowed action, a forced mechanical recovery in it is RECOVERY_ACTION_ALREADY_SELECTED, a new
    /// session is ACTION_NOT_ALLOWED_IN_STATE. An administrator then closes it: the resume is judged RecoveryRequired under
    /// ADMINISTRATOR_CLOSED, its command is settled so it is not replayed, the session is CLOSED at a new revision and the
    /// vehicle is sent the CLOSED snapshot, and the business stays exactly where the failed load left it -- demand
    /// RecoveryRequired, journey Blocked under its code, operation RecoveryRequired, lease and vehicle held, no ending
    /// written. The administrator on the vehicle then opens a new session.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWhoseReplacementResultIsRefusedIsClosedByAnAdministratorSoTheVehicleCanOpenAnotherSession()
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

            RecoverySessionAdministratorCloseResult closed = await CloseAsync(context, peer, CloseRequest(sessionId));

            Assert.True(closed.Closed, string.Join(',', closed.Codes));
            Assert.Empty(closed.Codes);
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
            Assert.Contains(CloseOperator, audit.DetailJson, StringComparison.Ordinal);

            // A new press after the closing: a new request and a new message. The refused one above is answered from the inbox
            // with its first response if sent again byte for byte.
            JsonNode pressAgain = JsonNode.Parse(NextSessionRequest(StuckProof))!;
            pressAgain["messageId"] = "e0000000-0000-4000-8000-000000004834";
            pressAgain["payload"]!["requestId"] = "41000000-0000-4000-8000-000000004834";
            string opened = await processor.ProcessAsync(pressAgain.ToJsonString(), state, token);
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// A replacement result that arrives after an administrator closed the session -- the vehicle came back after all -- is
    /// refused whole, BUSINESS_ID_CONTENT_CONFLICT: no resume awaits it, so nothing is kept and nothing is settled. The same
    /// result before the closing would have committed the load (the control: it is the exact scope that was authorized).
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
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId))).Closed);
            BusinessPicture closed = await BusinessPictureAsync(context);
            int results = await context.OperationResults.CountAsync(token);
            RecoveryWorkflowRow judged = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);

            string late = Envelope(
                "e0000000-0000-4000-8000-000000004833",
                "OperationResult",
                OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED"));
            ProtocolProblemAssert.RefusedLine(
                await processor.ProcessAsync(late, state, token), "BUSINESS_ID_CONTENT_CONFLICT", late);
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
        "unknown session",
        "another vehicle",
        "session open",
        "fault cargo handoff",
        "result no longer awaited",
        "missing operator, reason and verification",
        "too long",
    };

    /// <summary>
    /// Every unmet premise refuses with its code, writes nothing but the failed audit record, and sends the vehicle nothing:
    /// the session, its workflows, the business and the outbox are as they were. Only a resume is closable (the coordinator's
    /// decision of 2026-10-05); "result no longer awaited" cannot be reached through the protocol -- a judged result closes
    /// the session first -- and is made by hand to pin the check that stands behind the session's state.
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
            RecoverySessionAdministratorCloseRequest request = CloseRequest(sessionId);
            string[] expected;
            switch (refusal)
            {
                case "session open":
                    await OpenSessionAfterFailedResultAsync(context, peer, action: null);
                    expected = [RecoverySessionAdministratorCloseCodes.SessionNotExecuting];
                    break;
                case "fault cargo handoff":
                    await OpenSessionAfterFailedResultAsync(context, peer, action: "FAULT_CARGO_HANDOFF");
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
                default:
                    await StuckResumeAsync(context, peer);
                    (request, expected) = refusal switch
                    {
                        "unknown session" => (
                            request with { ExceptionRecoverySessionId = "59000000-0000-4000-8000-000000004839" },
                            new[] { RecoverySessionAdministratorCloseCodes.SessionNotFound }),
                        "another vehicle" => (
                            request with { AgvId = "AGV-8005-02" },
                            new[] { RecoverySessionAdministratorCloseCodes.SessionNotFound }),
                        "missing operator, reason and verification" => (
                            request with { OperatorId = " ", Reason = null, SiteVerification = "" },
                            new[]
                            {
                                RecoverySessionAdministratorCloseCodes.OperatorRequired,
                                RecoverySessionAdministratorCloseCodes.ReasonRequired,
                                RecoverySessionAdministratorCloseCodes.SiteVerificationRequired
                            }),
                        "too long" => (
                            request with { Reason = new string('x', RecoverySessionAdministratorClose.MaxTextLength + 1) },
                            new[] { RecoverySessionAdministratorCloseCodes.FieldTooLong }),
                        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null)
                    };
                    break;
            }
            BusinessPicture before = await BusinessPictureAsync(context);
            string outbox = await OutboxAccountAsync(context);
            string workflows = await WorkflowAccountAsync(context);
            int lines = peer.Lines.Count;

            RecoverySessionAdministratorCloseResult result = await CloseAsync(context, peer, request);

            Assert.False(result.Closed);
            Assert.Equal(expected, result.Codes);
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(outbox, await OutboxAccountAsync(context));
            Assert.Equal(workflows, await WorkflowAccountAsync(context));
            Assert.Equal(lines, peer.Lines.Count);
            AdministratorAuditRecordRow audit = Assert.Single(await CloseAuditsAsync(context));
            Assert.Equal((result.AuditRecordId, GovernanceActionOutcome.Failed), (audit.AuditRecordId, audit.Outcome));
            foreach (string code in expected)
                Assert.Contains(code, audit.DetailJson, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// The HTTP entry's boundary: without a credential configured it is unavailable, with a wrong one it is unauthorized, and
    /// a request naming no vehicle or session is incomplete -- none of those writes an audit record or touches the session. An
    /// authenticated refusal is a 409 listing every code with its description, audited; an authenticated closing is a 200
    /// with its audit record.
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
                new(AgvId, sessionId, CloseOperator, "车机已换，续作结果永远不会来", "SITE-483-01", "MAINTENANCE_ADMINISTRATOR");
            BusinessPicture before = await BusinessPictureAsync(context);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>((await PostCloseAsync(
                context, peer, "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N"), "Bearer close-credential",
                request)).Result).StatusCode);
            Assert.IsType<UnauthorizedHttpResult>(
                (await PostCloseAsync(context, peer, credentialVariable, "Bearer wrong", request)).Result);
            Assert.IsType<UnauthorizedHttpResult>(
                (await PostCloseAsync(context, peer, credentialVariable, null, request)).Result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>((await PostCloseAsync(
                context, peer, credentialVariable, "Bearer close-credential",
                request with { ExceptionRecoverySessionId = " " })).Result).StatusCode);
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

            RecoverySessionAdministratorCloseResponse body = Assert.IsType<Ok<RecoverySessionAdministratorCloseResponse>>(
                (await PostCloseAsync(context, peer, credentialVariable, "Bearer close-credential", request)).Result).Value!;
            Assert.Equal(("CLOSED", AgvId, sessionId), (body.Outcome, body.AgvId, body.ExceptionRecoverySessionId));
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
            AdministratorAuditRecordRow[] audits = await CloseAuditsAsync(context);
            Assert.Equal(2, audits.Length);
            Assert.Contains(audits, row => row.AuditRecordId == body.AuditRecordId &&
                                           row.Outcome == GovernanceActionOutcome.Succeeded &&
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

    private static RecoverySessionAdministratorCloseRequest CloseRequest(string sessionId) =>
        new(AgvId, sessionId, CloseOperator, "车机已换，续作结果永远不会来", "SITE-483-01", "MAINTENANCE_ADMINISTRATOR");

    private static RecoverySessionAdministratorClose Closing(ControlServerDbContext context, IOnboardPeer peer) =>
        new(
            context,
            TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(StuckProofVariable), peer),
            new GovernanceStore(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            new FixedTimeProvider(Now),
            NullLogger<RecoverySessionAdministratorClose>.Instance);

    private static async Task<RecoverySessionAdministratorCloseResult> CloseAsync(
        ControlServerDbContext context,
        IOnboardPeer peer,
        RecoverySessionAdministratorCloseRequest request)
    {
        RecoverySessionAdministratorCloseResult result =
            await Closing(context, peer).CloseAsync(request, TestContext.Current.CancellationToken);
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
}
