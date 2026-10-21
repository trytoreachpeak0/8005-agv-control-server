using System.Collections.Immutable;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// control-server#797 (ADR-cross-0066, REQ-0235 and REQ-0279 as CP-0022 revised them): with <c>RecoveryGrant:required</c> on, an
/// exception recovery session opens only on a grant in effect for the vehicle and the journey, every action in it is checked
/// against that grant, and the session ends when the grant runs out, is revoked or the server restarts -- the action under way
/// finishing first.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    private const string GrantProofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_GRANT";
    private const string GrantProof = "grant-proof-not-a-production-secret";
    private const string GrantVehicleKey = "VEHICLE-001";

    private static string SeededJourneyId => JourneyIdentity.ForAnchorDemand(DemandId);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task WithoutAGrantTheSessionIsRefusedUnderTheProofsCodeAndNothingIsOpened()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();

        string answer = await rig.ProcessAsync(RecoverySessionRequest(GrantProof));

        AssertSessionRejected(answer);
        Assert.Empty(await rig.Context.ExceptionRecoverySessions.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AGrantForTheVehicleAndJourneyOpensTheSessionOfferingOnlyTheRepairResume()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);

        string answer = await rig.ProcessAsync(RecoverySessionRequest(GrantProof));

        Assert.Equal("ExceptionRecoverySessionOpened", MessageType(answer));
        (string State, string[] Allowed, string? ClosedReason) snapshot = await LatestSessionSnapshotAsync(rig.Context);
        Assert.Equal("OPEN", snapshot.State);
        Assert.Equal(["RESUME_AFTER_REPAIR"], snapshot.Allowed);
    }

    /// <summary>Off, nothing is checked: the rigs and G3 open sessions as they always did.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task WithTheRequirementOffASessionOpensWithoutAGrantAndOffersEverything()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync(required: false);

        Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await rig.ProcessAsync(RecoverySessionRequest(GrantProof))));

        Assert.Contains("FORCED_MECHANICAL_RECOVERY", (await LatestSessionSnapshotAsync(rig.Context)).Allowed);
    }

    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("other-journey")]
    [InlineData("vehicle-wide")]
    [InlineData("no-session-action")]
    [InlineData("other-vehicle")]
    [InlineData("restarted")]
    public async Task AGrantThatDoesNotCoverTheSessionRefusesIt(string shape)
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        switch (shape)
        {
            case "expired":
                rig.Grant(SeededJourneyId, minutes: 30);
                rig.Clock.Current = rig.Clock.Current.AddMinutes(30);
                break;
            case "revoked":
                rig.Grant(SeededJourneyId);
                rig.Registry.Revoke(AgvId);
                break;
            case "other-journey":
                rig.Grant("journey:some-other-trip");
                break;
            case "vehicle-wide":
                rig.Grant(journeyId: null);
                break;
            case "no-session-action":
                rig.Grant(SeededJourneyId, actions: ["CLEAR_FAULT", "RESUME_HELD_ORDER"]);
                break;
            case "other-vehicle":
                rig.Registry.Grant(GrantFor("AGV-8005-02", "VEHICLE-002", SeededJourneyId, rig.Clock.Current));
                break;
            case "restarted":
                rig.Grant(SeededJourneyId);
                rig.Restart();
                break;
        }

        AssertSessionRejected(await rig.ProcessAsync(RecoverySessionRequest(GrantProof)));
        Assert.Empty(await rig.Context.ExceptionRecoverySessions.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A grant naming the repair resume alone does not let the session take a forced recovery (decision 4).</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnActionTheGrantDoesNotNameIsRefusedAndNothingIsSent()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);
        Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await rig.ProcessAsync(RecoverySessionRequest(GrantProof))));
        int sentBefore = rig.Peer.Lines.Count;

        string answer = await rig.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"));

        AssertActionRejected(answer, "RECOVERY_AUTHENTICATION_FAILED");
        Assert.Empty(await rig.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        // Nothing for the vehicle to carry out: at most the session's own snapshot again, never a command.
        Assert.All(rig.Peer.Lines.Skip(sentBefore), line => Assert.Equal("ExceptionRecoverySessionSnapshot", MessageType(line)));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnActionTheGrantNamesIsTaken()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);
        await rig.ProcessAsync(RecoverySessionRequest(GrantProof));

        Assert.Equal("RecoveryActionAccepted", MessageType(await rig.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR"))));
    }

    /// <summary>
    /// Each action is checked when it is submitted: a grant revoked, or expired, after the session opened refuses the next
    /// action even before the sweep has closed the session.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnActionAfterTheGrantEndedIsRefusedBeforeTheSweep(bool revoked)
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);
        await rig.ProcessAsync(RecoverySessionRequest(GrantProof));
        if (revoked) rig.Registry.Revoke(AgvId);
        else rig.Clock.Current = rig.Clock.Current.AddMinutes(31);

        AssertActionRejected(await rig.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR")), "RECOVERY_AUTHENTICATION_FAILED");
        Assert.Empty(await rig.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// REQ-0279 as CP-0022 revised it: an open session whose grant ran out, was revoked, or was lost to a restart is ended at
    /// once -- CLOSED under RECOVERY_AUTHENTICATION_FAILED, sent to the vehicle -- and takes no action after.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("restarted")]
    [InlineData("renewed-for-another-journey")]
    public async Task AnOpenSessionIsClosedTheMomentItsGrantEnds(string shape)
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);
        Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await rig.ProcessAsync(RecoverySessionRequest(GrantProof))));
        Assert.Equal(1, await rig.SweepAsync()); // still covered: nothing to tell
        Assert.Equal("OPEN", (await LatestSessionSnapshotAsync(rig.Context)).State);
        switch (shape)
        {
            case "expired":
                rig.Clock.Current = rig.Clock.Current.AddMinutes(30);
                break;
            case "revoked":
                rig.Registry.Revoke(AgvId);
                break;
            case "restarted":
                rig.Restart();
                break;
            case "renewed-for-another-journey":
                rig.Grant("journey:some-other-trip");
                break;
        }
        int sentBefore = rig.Peer.Lines.Count;

        await rig.SweepAsync();

        ExceptionRecoverySessionRow session = await rig.Context.ExceptionRecoverySessions.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("CLOSED", "RECOVERY_AUTHENTICATION_FAILED"), (session.State, session.ClosedReason));
        (string State, string[] Allowed, string? ClosedReason) snapshot = await LatestSessionSnapshotAsync(rig.Context);
        Assert.Equal(("CLOSED", "RECOVERY_AUTHENTICATION_FAILED"), (snapshot.State, snapshot.ClosedReason));
        Assert.Equal("ExceptionRecoverySessionSnapshot", MessageType(Assert.Single(rig.Peer.Lines.Skip(sentBefore))));
        AssertActionRejected(await rig.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR")), "RECOVERY_SESSION_NOT_OPEN");
        // A second sweep finds nothing more to do: one closing, one snapshot (the processor may resend it until it is acked).
        await rig.SweepAsync();
        Assert.Single(rig.Peer.Lines.Skip(sentBefore).Select(MessageId).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, (await rig.Context.ExceptionRecoverySessions.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken)).Revision);
    }

    /// <summary>
    /// The action under way is not cut off: a resume whose command is out when the grant ends is left to its result, which
    /// is reconciled, and the session then closes under RECOVERY_AUTHENTICATION_FAILED (ADR-cross-0066 decision 5: the session
    /// ended because its grant did; the action's own result is reconciled apart, on the workflow).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnActionUnderWayWhenTheGrantEndsFinishesAndItsResultClosesTheSession()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);
        await rig.ProcessAsync(RecoverySessionRequest(GrantProof));
        Assert.Equal("RecoveryActionAccepted", MessageType(await rig.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR"))));
        rig.Registry.Revoke(AgvId);

        await rig.SweepAsync();
        Assert.Equal("EXECUTING", (await rig.Context.ExceptionRecoverySessions.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken)).State);

        Assert.Equal("DurableAck", MessageType(await rig.ProcessAsync(Envelope(
            "e0000000-0000-4000-8000-000000007970",
            "OperationResult",
            OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED")))));
        ExceptionRecoverySessionRow session = await rig.Context.ExceptionRecoverySessions.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("CLOSED", "RECOVERY_AUTHENTICATION_FAILED"), (session.State, session.ClosedReason));
        Assert.Equal(RecoveryWorkflowState.Reconciled, (await rig.Context.RecoveryWorkflows.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken)).State);
    }

    /// <summary>
    /// cs#797 review M1: a compensation still awaiting its authorization when the grant ends has sent the vehicle nothing; the
    /// session closes and the compensation is spent with it (RecoveryRequired under RECOVERY_GRANT_ENDED, as an administrator's
    /// closing spends one), so it no longer counts as an open recovery of the vehicle -- the online withdrawal and the
    /// administrator closing would otherwise find a workflow nothing can end -- and its authorization asked afterwards is refused.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ACompensationAwaitingItsAuthorizationIsSpentWhenTheGrantEnds()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        (await rig.Context.SessionRecoveries.SingleAsync(token)).DepartureSafe = true;
        await rig.Context.SaveChangesAsync(token);
        rig.Context.ChangeTracker.Clear();
        rig.Grant(SeededJourneyId, actions: ["RESUME_AFTER_REPAIR", "COMPENSATE_LOAD_ALL_EMPTY"]);
        Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await rig.ProcessAsync(RecoverySessionRequest(GrantProof))));
        Assert.Equal("RecoveryActionAccepted", MessageType(await rig.ProcessAsync(RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"))));
        Assert.Equal("ACTION_SELECTED", (await rig.Context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);

        rig.Registry.Revoke(AgvId);
        await rig.SweepAsync();

        ExceptionRecoverySessionRow session = await rig.Context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
        RecoveryWorkflowRow workflow = await rig.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
        Assert.Equal(("CLOSED", "RECOVERY_AUTHENTICATION_FAILED"), (session.State, session.ClosedReason));
        Assert.Equal((RecoveryWorkflowState.RecoveryRequired, "RECOVERY_GRANT_ENDED"), (workflow.State, workflow.Outcome));
        Assert.False(await rig.Context.RecoveryWorkflows.AsNoTracking().AnyAsync(
            row => row.AgvId == AgvId && (row.State == RecoveryWorkflowState.AwaitingAuthorization ||
                                          row.State == RecoveryWorkflowState.CommandPending ||
                                          row.State == RecoveryWorkflowState.AwaitingResult), token));
        string late = await rig.ProcessAsync(CompensationAuthorizationRequest());
        Assert.Equal(("LoadCompensationRejected", ServerReasonCodes.RecoverySessionNotOpen),
            (MessageType(late), FirstPayload(late).GetProperty("problem").GetProperty("reasonCode").GetString()));
        Assert.Equal(0, await rig.Context.ProtocolOutbox.CountAsync(row => row.MessageType == "LoadCompensationCommand", token));
    }

    /// <summary>
    /// Brief step 8: what a real onboard reports for the whole drive while it carries a movement order of this server's -- the
    /// session not ready, DEPARTURE_SAFETY_NOT_READY on VEHICLE_NOT_READY -- changes nothing: a granted session opens offering
    /// what the grant names, and the sweep closes it when the grant ends (cs#797 review probe P2).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task OnASessionNotReadyOnTheServersOwnOrderTheGrantStillOpensAndEndsTheSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        SessionRecoveryRow recovery = await rig.Context.SessionRecoveries.SingleAsync(row => row.AgvId == AgvId, token);
        recovery.Readiness = SessionReadiness.RecoveryRequired;
        recovery.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        recovery.DepartureSafe = false;
        recovery.SafetyReasonCodesJson = $"[\"{OwnOrderNotReadyReason}\"]";
        recovery.SafetyUnknownPresent = true;
        await rig.Context.SaveChangesAsync(token);
        rig.Context.ChangeTracker.Clear();

        rig.Grant(SeededJourneyId);
        Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await rig.ProcessAsync(RecoverySessionRequest(GrantProof))));
        Assert.Equal(["RESUME_AFTER_REPAIR"], (await LatestSessionSnapshotAsync(rig.Context)).Allowed);

        rig.Registry.Revoke(AgvId);
        int sentBefore = rig.Peer.Lines.Count;
        await rig.SweepAsync();

        (string State, string[] _, string? ClosedReason) closed = await LatestSessionSnapshotAsync(rig.Context);
        Assert.Equal(("CLOSED", "RECOVERY_AUTHENTICATION_FAILED"), (closed.State, closed.ClosedReason));
        Assert.Equal("ExceptionRecoverySessionSnapshot", MessageType(Assert.Single(rig.Peer.Lines.Skip(sentBefore))));
    }

    /// <summary>A resend of an action accepted before the grant ended is answered as it was, and runs nothing a second time.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AResendOfAnAcceptedActionAfterTheGrantEndedIsAnsweredAsBefore()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);
        await rig.ProcessAsync(RecoverySessionRequest(GrantProof));
        Assert.Equal("RecoveryActionAccepted", MessageType(await rig.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR"))));
        rig.Registry.Revoke(AgvId);

        Assert.Equal("RecoveryActionAccepted", MessageType(await rig.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR"))));
        Assert.Single(await rig.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A renewal before the end keeps the session open; one naming more actions tells the vehicle what it now offers.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ARenewalKeepsTheSessionOpenAndOffersWhatItAdds()
    {
        await using GrantRig rig = await GrantRig.FailedLoadAsync();
        rig.Grant(SeededJourneyId);
        await rig.ProcessAsync(RecoverySessionRequest(GrantProof));
        rig.Clock.Current = rig.Clock.Current.AddMinutes(25);
        rig.Grant(SeededJourneyId, actions: ["RESUME_AFTER_REPAIR", "FORCED_MECHANICAL_RECOVERY"]);
        rig.Clock.Current = rig.Clock.Current.AddMinutes(25);

        await rig.SweepAsync();

        (string State, string[] Allowed, string? _) snapshot = await LatestSessionSnapshotAsync(rig.Context);
        Assert.Equal("OPEN", snapshot.State);
        Assert.Equal(["FORCED_MECHANICAL_RECOVERY", "RESUME_AFTER_REPAIR"], snapshot.Allowed.Order(StringComparer.Ordinal));
        Assert.Equal("RecoveryActionAccepted", MessageType(await rig.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"))));
    }

    /// <summary>
    /// control-server#797: with per-vehicle proofs listed, the vehicle's own standing proof is accepted and another vehicle's is
    /// not; the shared one only while its variable is set -- on the v2 instance, only while the 18-set-v2-recovery-window
    /// fallback holds the window open.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("own", true, true)]
    [InlineData("own", false, true)]
    [InlineData("other-vehicle", true, false)]
    [InlineData("shared", true, true)]
    [InlineData("shared", false, false)]
    public async Task WithPerVehicleProofsOnlyTheVehiclesOwnIsAccepted(string presented, bool windowOpen, bool accepted)
    {
        string own = "CONTROL_SERVER_TEST_RECOVERY_PROOF_VEHICLE_" + Guid.NewGuid().ToString("N");
        string other = "CONTROL_SERVER_TEST_RECOVERY_PROOF_VEHICLE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(own, "own-vehicle-proof");
        Environment.SetEnvironmentVariable(other, "other-vehicle-proof");
        try
        {
            await using GrantRig rig = await GrantRig.FailedLoadAsync(extra: new Dictionary<string, string?>
            {
                ["Recovery:VehicleProofs:0:vehicleKey"] = GrantVehicleKey,
                ["Recovery:VehicleProofs:0:proofEnvironmentVariable"] = own,
                ["Recovery:VehicleProofs:1:vehicleKey"] = "VEHICLE-002",
                ["Recovery:VehicleProofs:1:proofEnvironmentVariable"] = other,
            });
            rig.Grant(SeededJourneyId);
            if (!windowOpen) Environment.SetEnvironmentVariable(GrantProofVariable, null);
            string proof = presented switch
            {
                "own" => "own-vehicle-proof",
                "other-vehicle" => "other-vehicle-proof",
                _ => GrantProof,
            };

            string answer = await rig.ProcessAsync(RecoverySessionRequest(proof));

            if (accepted) Assert.Equal("ExceptionRecoverySessionOpened", MessageType(answer));
            else AssertSessionRejected(answer);
        }
        finally
        {
            Environment.SetEnvironmentVariable(own, null);
            Environment.SetEnvironmentVariable(other, null);
        }
    }

    /// <summary>The sweep runs on its own: started, it closes the session the moment a short grant runs out (real timers).</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheSweeperClosesTheSessionWhenTheGrantRunsOut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string database = $"Data Source=recovery-grant-sweeper-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using SqliteConnection keepAlive = new(database);
        await keepAlive.OpenAsync(token);
        IConfiguration configuration = GrantConfiguration(required: true);
        RecoveryGrantRegistry registry = new(TimeProvider.System);
        Environment.SetEnvironmentVariable(GrantProofVariable, GrantProof);
        try
        {
            await using (ControlServerDbContext seed = await CreateContextAsync(keepAlive))
            {
                RecordingPeer peer = new(seed);
                await SeedBlockedJourneyAsync(seed);
                OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                    seed, new WireToGateStore(seed), new FixedTimeProvider(Now), configuration, peer,
                    recoveryGrants: registry, roster: GrantRoster());
                OnboardConnectionState state = CurrentState(deferOutbound: true);
                await processor.ProcessAsync(Envelope(
                    "e0000000-0000-4000-8000-000000000010", "OperationResult",
                    OperationResultPayload(completed: false, journalCheckpoint: "RESULT_UNKNOWN_RECORDED")), state, token);
                await processor.FlushDeferredOutboundAsync(state, token);
                registry.Grant(GrantFor(AgvId, GrantVehicleKey, SeededJourneyId, DateTimeOffset.UtcNow,
                    lasting: TimeSpan.FromSeconds(2)));
                Assert.Equal("ExceptionRecoverySessionOpened",
                    MessageType(await processor.ProcessAsync(RecoverySessionRequest(GrantProof), state, token)));
            }

            ServiceCollection services = new();
            services.AddDbContext<ControlServerDbContext>(options => options.UseSerializedSqlite(database));
            services.AddScoped(provider =>
            {
                ControlServerDbContext context = provider.GetRequiredService<ControlServerDbContext>();
                return TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                    context, new WireToGateStore(context), new FixedTimeProvider(Now), configuration, null,
                    recoveryGrants: registry, roster: GrantRoster());
            });
            await using ServiceProvider provider = services.BuildServiceProvider();
            RecoveryGrantSweeper sweeper = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                registry,
                Options.Create(new RecoveryGrantOptions { Required = true }),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RecoveryGrantSweeper>.Instance);
            await sweeper.StartAsync(token);
            try
            {
                string? state = null;
                using CancellationTokenSource within = CancellationTokenSource.CreateLinkedTokenSource(token);
                within.CancelAfter(TimeSpan.FromSeconds(20));
                while (state != "CLOSED")
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), within.Token);
                    await using ControlServerDbContext read = await CreateContextAsync(keepAlive);
                    state = (await read.ExceptionRecoverySessions.AsNoTracking().SingleAsync(within.Token)).State;
                }
                Assert.Equal("CLOSED", state);
            }
            finally
            {
                await sweeper.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(GrantProofVariable, null);
        }
    }

    private static void AssertSessionRejected(string answer)
    {
        Assert.Equal("ExceptionRecoverySessionRejected", MessageType(answer));
        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("RECOVERY_AUTHENTICATION_FAILED", document.RootElement.GetProperty("payload").GetProperty("problem")
            .GetProperty("reasonCode").GetString());
    }

    private static void AssertActionRejected(string answer, string reasonCode)
    {
        Assert.Equal("RecoveryActionRejected", MessageType(answer));
        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal(reasonCode, document.RootElement.GetProperty("payload").GetProperty("problem")
            .GetProperty("reasonCode").GetString());
    }

    /// <summary>The newest session snapshot this server queued: its state, what it offers, and why it closed.</summary>
    private static async Task<(string State, string[] Allowed, string? ClosedReason)> LatestSessionSnapshotAsync(
        ControlServerDbContext context)
    {
        string[] payloads = await context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "ExceptionRecoverySessionSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        return payloads
            .Select(json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement payload = document.RootElement.GetProperty("payload");
                return (Revision: payload.GetProperty("recoverySessionRevision").GetInt64(),
                    State: payload.GetProperty("state").GetString()!,
                    Allowed: payload.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()!).ToArray(),
                    ClosedReason: payload.TryGetProperty("closedReason", out JsonElement reason) &&
                                  reason.ValueKind == JsonValueKind.String
                        ? reason.GetString()
                        : null);
            })
            .OrderByDescending(item => item.Revision)
            .Select(item => (item.State, item.Allowed, item.ClosedReason))
            .First();
    }

    private static IConfiguration GrantConfiguration(bool required, IDictionary<string, string?>? extra = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["Recovery:AuthenticationProofEnvironmentVariable"] = GrantProofVariable,
            [$"{OnboardTransportOptions.SectionName}:CredentialEnvironmentVariable"] = HandshakeCredentialVariable,
            [RecoveryGrantOptions.RequiredKey] = required ? "true" : "false",
        };
        foreach (KeyValuePair<string, string?> pair in extra ?? new Dictionary<string, string?>())
            values[pair.Key] = pair.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static VehicleRoster GrantRoster() => new(Options.Create(new JourneyRuntimeOptions
    {
        Fleet =
        [
            new FleetVehicleOptions { AgvId = AgvId, VehicleKey = GrantVehicleKey, AgvLifecycleGeneration = 1 },
            new FleetVehicleOptions { AgvId = "AGV-8005-02", VehicleKey = "VEHICLE-002", AgvLifecycleGeneration = 1 },
        ]
    }));

    private static RecoveryGrant GrantFor(
        string agvId,
        string vehicleKey,
        string? journeyId,
        DateTimeOffset grantedAt,
        IEnumerable<string>? actions = null,
        TimeSpan? lasting = null) =>
        new(
            Guid.NewGuid().ToString("D"),
            agvId,
            vehicleKey,
            journeyId,
            actions is null ? RecoveryGrantActions.Default : ImmutableSortedSet.CreateRange(StringComparer.Ordinal, actions),
            "maintenance-797",
            grantedAt,
            grantedAt + (lasting ?? TimeSpan.FromMinutes(30)),
            "audit-797");

    /// <summary>
    /// One vehicle whose seeded load failed (journey Blocked, load RecoveryRequired), its processor built with a grant registry on
    /// a clock of its own, and the grant requirement on unless said otherwise.
    /// </summary>
    private sealed class GrantRig : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IConfiguration _configuration;
        private OnboardMessageProcessor _processor;

        private GrantRig(SqliteConnection connection, ControlServerDbContext context, IConfiguration configuration)
        {
            _connection = connection;
            _configuration = configuration;
            Context = context;
            Peer = new RecordingPeer(context);
            Registry = new RecoveryGrantRegistry(Clock);
            _processor = Build();
        }

        public ControlServerDbContext Context { get; }

        public RecordingPeer Peer { get; }

        /// <summary>The grants' clock; the processor's own stays at <c>Now</c>.</summary>
        public MovableTimeProvider Clock { get; } = new(Now);

        public RecoveryGrantRegistry Registry { get; private set; }

        public OnboardConnectionState State { get; } = CurrentState(deferOutbound: true);

        public static async Task<GrantRig> FailedLoadAsync(
            bool required = true,
            IDictionary<string, string?>? extra = null)
        {
            Environment.SetEnvironmentVariable(GrantProofVariable, GrantProof);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            GrantRig rig = new(connection, context, GrantConfiguration(required, extra));
            Assert.Equal("DurableAck", MessageType(await rig.ProcessAsync(Envelope(
                "e0000000-0000-4000-8000-000000000010",
                "OperationResult",
                OperationResultPayload(completed: false, journalCheckpoint: "RESULT_UNKNOWN_RECORDED")))));
            return rig;
        }

        public void Grant(string? journeyId, IEnumerable<string>? actions = null, int minutes = 30) =>
            Registry.Grant(GrantFor(AgvId, GrantVehicleKey, journeyId, Clock.Current, actions, TimeSpan.FromMinutes(minutes)));

        /// <summary>A restart: the database stays, the grants are gone.</summary>
        public void Restart()
        {
            Registry = new RecoveryGrantRegistry(Clock);
            _processor = Build();
        }

        public async Task<string> ProcessAsync(string line)
        {
            string answer = await _processor.ProcessAsync(line, State, TestContext.Current.CancellationToken);
            await _processor.FlushDeferredOutboundAsync(State, TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
            return answer;
        }

        /// <summary>One sweep as the sweeper runs it; returns how many open sessions there were before it.</summary>
        public async Task<int> SweepAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            int open = await Context.ExceptionRecoverySessions.AsNoTracking().CountAsync(row => row.State != "CLOSED", token);
            OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                Context, new WireToGateStore(Context), new FixedTimeProvider(Now), _configuration, Peer,
                recoveryGrants: Registry, roster: GrantRoster());
            IReadOnlyList<string> told;
            await using (var transaction = await Context.Database.BeginTransactionAsync(token))
            {
                told = await coordinator.EndSessionsOutsideGrantsAsync(token);
                await transaction.CommitAsync(token);
            }
            foreach (string agvId in told)
                await coordinator.TrySendPendingSessionSnapshotsAsync(agvId, token);
            Context.ChangeTracker.Clear();
            return open;
        }

        public async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable(GrantProofVariable, null);
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private OnboardMessageProcessor Build() => TestOnboardProcessorFactory.Create(
            Context, new WireToGateStore(Context), new FixedTimeProvider(Now), _configuration, Peer,
            recoveryGrants: Registry, roster: GrantRoster());
    }
}
