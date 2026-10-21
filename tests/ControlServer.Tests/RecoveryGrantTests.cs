using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.VehicleFaultRecoveryTests;

namespace ControlServer.Tests;

/// <summary>
/// control-server#797 (ADR-cross-0066 decisions 1 to 4): the control host's recovery grant -- rehearsed for a confirmation code,
/// granted with it, audited, limited to one vehicle, one journey, a few actions and a while, revoked or renewed, and gone with
/// the process -- and the vehicle fault recovery entry checking every request against it.
/// </summary>
public sealed class RecoveryGrantTests
{
    private const string Credential = "recovery-grant-credential";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 下发、演练、确认码、审计 -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARehearsalShowsThePlanAndItsCodeAndWritesNothing()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();

        RecoveryGrantResult result = await rig.Issuance.GrantAsync(rig.Request(), Token);

        Assert.Equal(RecoveryGrantIssuance.Planned, result.Outcome);
        Assert.Matches("^[0-9A-F]{12}$", result.ConfirmationCode);
        Assert.Equal(["RESUME_AFTER_REPAIR"], result.Plan!.Actions);
        Assert.Equal(30, result.Plan.Minutes);
        Assert.Equal(rig.JourneyId, result.Plan.NamedJourney!.JourneyId);
        Assert.Equal(rig.JourneyId, result.Plan.VehicleJourney!.JourneyId);
        Assert.Null(rig.Registry.Current(rig.AgvId));
        Assert.Empty(await rig.AuditsAsync());
    }

    [Fact]
    public async Task GrantingWithTheRehearsalsCodePutsTheGrantInEffectForThirtyMinutesAndAuditsIt()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        string code = (await rig.Issuance.GrantAsync(rig.Request(), Token)).ConfirmationCode!;

        RecoveryGrantResult result = await rig.Issuance.GrantAsync(rig.Request() with { ConfirmationCode = code }, Token);

        Assert.Equal(RecoveryGrantIssuance.Granted, result.Outcome);
        RecoveryGrant grant = Assert.IsType<RecoveryGrant>(rig.Registry.Current(rig.AgvId));
        Assert.Equal(
            (rig.JourneyId, "maintenance-797", rig.Now.AddMinutes(30)),
            (grant.JourneyId, grant.OperatorId, grant.ExpiresAt));
        Assert.Equal(["RESUME_AFTER_REPAIR"], grant.Actions);
        AdministratorAuditRecordRow audit = Assert.Single(await rig.AuditsAsync());
        Assert.Equal(
            (RecoveryGrantIssuance.GrantAuditAction, GovernedObjectKind.RecoveryGrant, GovernanceActionOutcome.Succeeded),
            (audit.Action, audit.ObjectKind, audit.Outcome));
        Assert.Equal(result.AuditRecordId, audit.AuditRecordId);
        Assert.Contains("SITE-797-01", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("车上有货、卸货结果待恢复", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongCodeIsRefusedWithTheCurrentPlanAndCodeAndAudited()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        string code = (await rig.Issuance.GrantAsync(rig.Request(), Token)).ConfirmationCode!;

        RecoveryGrantResult result = await rig.Issuance.GrantAsync(rig.Request() with { ConfirmationCode = "000000000000" }, Token);

        Assert.Equal((RecoveryGrantIssuance.Rejected, code), (result.Outcome, result.ConfirmationCode));
        Assert.Equal([RecoveryGrantIssuance.ConfirmationStale], result.Codes);
        Assert.Null(rig.Registry.Current(rig.AgvId));
        Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await rig.AuditsAsync()).Outcome);
    }

    /// <summary>The code covers what the person was shown: a request asking for something else is stale.</summary>
    [Theory]
    [InlineData("actions")]
    [InlineData("minutes")]
    [InlineData("journey-stage")]
    [InlineData("replaces")]
    public async Task ACodeFromAnotherPlanIsStale(string change)
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        string code = (await rig.Issuance.GrantAsync(rig.Request(), Token)).ConfirmationCode!;
        RecoveryGrantRequest request = rig.Request() with { ConfirmationCode = code };
        switch (change)
        {
            case "actions":
                request = request with { Actions = ["RESUME_AFTER_REPAIR", "FORCED_MECHANICAL_RECOVERY"] };
                break;
            case "minutes":
                request = request with { Minutes = 60 };
                break;
            case "journey-stage":
                JourneyRuntimeStage stage = (await rig.Fixture.RuntimeAsync()).Stage;
                await rig.SetJourneyStageAsync(
                    stage == JourneyRuntimeStage.Blocked ? JourneyRuntimeStage.AwaitingPickupArrival : JourneyRuntimeStage.Blocked);
                break;
            case "replaces":
                rig.Registry.Grant(new RecoveryGrant(
                    "earlier", rig.AgvId, rig.VehicleKey, null, RecoveryGrantActions.Default, "someone", rig.Now, rig.Now.AddMinutes(5), "a"));
                break;
        }

        RecoveryGrantResult result = await rig.Issuance.GrantAsync(request, Token);

        Assert.Equal([RecoveryGrantIssuance.ConfirmationStale], result.Codes);
        Assert.NotEqual(code, result.ConfirmationCode);
    }

    [Fact]
    public async Task AGrantWithoutOperatorReasonOrSiteVerificationIsRefusedWithEveryCode()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();

        RecoveryGrantResult result = await rig.Issuance.GrantAsync(
            rig.Request() with { OperatorId = " ", Reason = null, SiteVerification = "", ConfirmationCode = "x" }, Token);

        Assert.Equal(
            [RecoveryGrantIssuance.OperatorRequired, RecoveryGrantIssuance.ReasonRequired, RecoveryGrantIssuance.SiteVerificationRequired],
            result.Codes);
        Assert.Null(rig.Registry.Current(rig.AgvId));
        Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await rig.AuditsAsync()).Outcome);
    }

    [Theory]
    [InlineData("vehicle-unknown", RecoveryGrantIssuance.VehicleUnknown)]
    [InlineData("action-unknown", RecoveryGrantIssuance.ActionUnknown)]
    [InlineData("zero-minutes", RecoveryGrantIssuance.DurationOutOfRange)]
    [InlineData("too-long", RecoveryGrantIssuance.DurationOutOfRange)]
    [InlineData("journey-of-nobody", RecoveryGrantIssuance.JourneyNotFound)]
    [InlineData("journey-completed", RecoveryGrantIssuance.JourneyCompleted)]
    public async Task ARehearsalOfWhatCannotBeGrantedSaysWhy(string shape, string code)
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        RecoveryGrantRequest request = rig.Request();
        switch (shape)
        {
            case "vehicle-unknown": request = request with { VehicleKey = "NOT-OURS" }; break;
            case "action-unknown": request = request with { Actions = ["OPEN_ALL_DOORS"] }; break;
            case "zero-minutes": request = request with { Minutes = 0 }; break;
            case "too-long": request = request with { Minutes = 481 }; break;
            case "journey-of-nobody": request = request with { JourneyId = "journey:not-this-vehicles" }; break;
            case "journey-completed": await rig.SetJourneyStageAsync(JourneyRuntimeStage.Completed); break;
        }

        RecoveryGrantResult result = await rig.Issuance.GrantAsync(request, Token);

        Assert.Equal(RecoveryGrantIssuance.Rejected, result.Outcome);
        Assert.Contains(code, result.Codes);
        Assert.Null(result.ConfirmationCode);
        Assert.Empty(await rig.AuditsAsync());
    }

    // ---- 过期、收回、续期、重启 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGrantRunsOutAtItsTime()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        await rig.GrantAsync(rig.Request() with { Minutes = 10 });

        rig.Clock.Current = rig.Now.AddMinutes(10).AddTicks(-1);
        Assert.NotNull(rig.Registry.Current(rig.AgvId));
        rig.Clock.Current = rig.Now.AddMinutes(10);
        Assert.Null(rig.Registry.Current(rig.AgvId));
        Assert.Empty(rig.Registry.InEffect());
    }

    [Fact]
    public async Task ARevocationEndsTheGrantAndIsAudited()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        await rig.GrantAsync(rig.Request());

        RecoveryGrantResult result = await rig.Issuance.RevokeAsync(
            new RecoveryGrantRevocationRequest(rig.VehicleKey, "maintenance-797", "现场处理完了", null), Token);

        Assert.Equal(RecoveryGrantIssuance.Revoked, result.Outcome);
        Assert.Null(rig.Registry.Current(rig.AgvId));
        Assert.Contains(await rig.AuditsAsync(), row =>
            row.Action == RecoveryGrantIssuance.RevokeAuditAction && row.Outcome == GovernanceActionOutcome.Succeeded);

        RecoveryGrantResult again = await rig.Issuance.RevokeAsync(
            new RecoveryGrantRevocationRequest(rig.VehicleKey, "maintenance-797", "再收一次", null), Token);
        Assert.Equal([RecoveryGrantIssuance.NotInEffect], again.Codes);
    }

    /// <summary>A grant before the old one runs out replaces it, its time counted from then (decision 2).</summary>
    [Fact]
    public async Task ARenewalReplacesTheGrantAndCountsItsTimeFromThen()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        await rig.GrantAsync(rig.Request());
        rig.Clock.Current = rig.Now.AddMinutes(25);

        RecoveryGrantResult renewal = await rig.GrantAsync(rig.Request() with { Actions = ["RESUME_AFTER_REPAIR", "FAULT_CARGO_HANDOFF"] });

        Assert.NotNull(renewal.Plan!.Replaces);
        RecoveryGrant grant = rig.Registry.Current(rig.AgvId)!;
        Assert.Equal(rig.Now.AddMinutes(55), grant.ExpiresAt);
        Assert.Equal(["FAULT_CARGO_HANDOFF", "RESUME_AFTER_REPAIR"], grant.Actions);
        Assert.Single(rig.Registry.InEffect());
    }

    /// <summary>Only in memory: a new process -- a restart -- has no grant at all (decision 2).</summary>
    [Fact]
    public async Task ARestartedServerHasNoGrant()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        await rig.GrantAsync(rig.Request());

        RecoveryGrantRegistry afterRestart = new(rig.Clock);

        Assert.Null(afterRestart.Current(rig.AgvId));
        Assert.Empty(await rig.Fixture.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == "RECOVERY_GRANT_RESTORED").ToArrayAsync(Token));
    }

    // ---- HTTP 入口 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGrantEntryNeedsTheCredential()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        using CredentialScope scope = new();

        Assert.IsType<UnauthorizedHttpResult>((await rig.PostGrantAsync(scope.Variable, "Bearer wrong", rig.HttpRequest())).Result);
        ProblemHttpResult unavailable = Assert.IsType<ProblemHttpResult>(
            (await rig.PostGrantAsync("CONTROL_SERVER_TEST_UNSET_" + Guid.NewGuid().ToString("N"), $"Bearer {Credential}", rig.HttpRequest())).Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Null(rig.Registry.Current(rig.AgvId));
        Assert.Empty(await rig.AuditsAsync());
    }

    [Fact]
    public async Task TheGrantEntryRehearsesGrantsAndRevokes()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        using CredentialScope scope = new();

        Ok<RecoveryGrantResponse> planned = Assert.IsType<Ok<RecoveryGrantResponse>>(
            (await rig.PostGrantAsync(scope.Variable, $"Bearer {Credential}", rig.HttpRequest())).Result);
        Assert.Equal("PLANNED", planned.Value!.Outcome);
        Ok<RecoveryGrantResponse> granted = Assert.IsType<Ok<RecoveryGrantResponse>>((await rig.PostGrantAsync(
            scope.Variable, $"Bearer {Credential}", rig.HttpRequest() with { ConfirmationCode = planned.Value.ConfirmationCode })).Result);
        Assert.Equal(("GRANTED", rig.JourneyId), (granted.Value!.Outcome, granted.Value.Grant!.JourneyId));

        Ok<IReadOnlyList<RecoveryGrantView>> listed = Assert.IsType<Ok<IReadOnlyList<RecoveryGrantView>>>(
            RecoveryGrantEndpoints.ListAsync(Http($"Bearer {Credential}"), rig.Registry, GrantFixture.RecoveryOptions(scope.Variable)).Result);
        Assert.Equal(rig.AgvId, Assert.Single(listed.Value!).AgvId);

        Ok<RecoveryGrantResponse> revoked = Assert.IsType<Ok<RecoveryGrantResponse>>((await RecoveryGrantEndpoints.RevokeAsync(
            Http($"Bearer {Credential}"),
            new RecoveryGrantRevocationHttpRequest(rig.VehicleKey, "maintenance-797", "处理完", null),
            rig.Issuance,
            GrantFixture.RecoveryOptions(scope.Variable),
            Token)).Result);
        Assert.Equal("REVOKED", revoked.Value!.Outcome);
        Assert.Null(rig.Registry.Current(rig.AgvId));
    }

    [Fact]
    public async Task TheGrantEntryAnswersAVehicleItDoesNotDrive404()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        using CredentialScope scope = new();

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>((await rig.PostGrantAsync(
            scope.Variable, $"Bearer {Credential}", rig.HttpRequest() with { VehicleKey = "NOT-OURS" })).Result);

        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    // ---- 故障恢复入口逐请求查授权 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutAGrantTheFaultRecoveryEntryRefuses403AndTouchesNothing()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        using CredentialScope scope = new();

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>((await rig.PostFaultRecoveryAsync(scope.Variable, "CLEAR_FAULT")).Result);

        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(["RECOVERY_GRANT_MISSING"], Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["reasons"]));
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(rig.Fixture)).Level);
    }

    [Theory]
    [InlineData("CLEAR_FAULT", "RESUME_AFTER_REPAIR", "journey", "RECOVERY_GRANT_ACTION_NOT_GRANTED")]
    [InlineData("RESUME_HELD_ORDER", "RESUME_HELD_ORDER", "other-journey", "RECOVERY_GRANT_JOURNEY_MISMATCH")]
    [InlineData("RESUME_HELD_ORDER", "RESUME_HELD_ORDER", "vehicle-wide", "RECOVERY_GRANT_JOURNEY_MISMATCH")]
    [InlineData("CLEAR_FAULT", "CLEAR_FAULT", "expired", "RECOVERY_GRANT_MISSING")]
    [InlineData("CLEAR_FAULT", "CLEAR_FAULT", "revoked", "RECOVERY_GRANT_MISSING")]
    public async Task AGrantThatDoesNotCoverTheRequestRefusesIt(string action, string granted, string scope, string code)
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        using CredentialScope credential = new();
        string? journeyId = scope switch
        {
            "other-journey" => "journey:another",
            "vehicle-wide" => null,
            _ => rig.JourneyId,
        };
        rig.Registry.Grant(new RecoveryGrant(
            "g", rig.AgvId, rig.VehicleKey, journeyId,
            System.Collections.Immutable.ImmutableSortedSet.Create(StringComparer.Ordinal, granted),
            "maintenance-797", rig.Now, rig.Now.AddMinutes(30), "a"));
        if (scope == "expired") rig.Clock.Current = rig.Now.AddMinutes(30);
        if (scope == "revoked") rig.Registry.Revoke(rig.AgvId);

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>((await rig.PostFaultRecoveryAsync(credential.Variable, action)).Result);

        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal([code], Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["reasons"]));
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(rig.Fixture)).Level);
    }

    /// <summary>
    /// A grant naming the action goes on to the decision itself; <c>CLEAR_FAULT</c> acts on the vehicle and needs no journey, the
    /// actions on the journey need the vehicle's own.
    /// </summary>
    [Theory]
    [InlineData("CLEAR_FAULT", false)]
    [InlineData("CLEAR_FAULT", true)]
    [InlineData("RESUME_HELD_ORDER", true)]
    public async Task AGrantNamingTheActionLetsTheRequestThroughToTheDecision(string action, bool withJourney)
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync();
        using CredentialScope credential = new();
        rig.Registry.Grant(new RecoveryGrant(
            "g", rig.AgvId, rig.VehicleKey, withJourney ? rig.JourneyId : null,
            System.Collections.Immutable.ImmutableSortedSet.Create(StringComparer.Ordinal, action),
            "maintenance-797", rig.Now, rig.Now.AddMinutes(30), "a"));

        var result = await rig.PostFaultRecoveryAsync(credential.Variable, action);

        if (result.Result is ProblemHttpResult problem)
        {
            Assert.NotEqual(StatusCodes.Status403Forbidden, problem.StatusCode);
            Assert.DoesNotContain(
                Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["reasons"]),
                reason => reason.StartsWith("RECOVERY_GRANT_", StringComparison.Ordinal));
        }
        else
        {
            Assert.IsType<Ok<VehicleFaultRecoveryResponse>>(result.Result);
        }
    }

    [Fact]
    public async Task WithTheRequirementOffTheFaultRecoveryEntryAsksForNoGrant()
    {
        await using GrantFixture rig = await GrantFixture.FaultedAsync(required: false);
        using CredentialScope credential = new();

        var result = await rig.PostFaultRecoveryAsync(credential.Variable, "CLEAR_FAULT");

        Assert.IsType<Ok<VehicleFaultRecoveryResponse>>(result.Result);
    }

    private static DefaultHttpContext Http(string? authorization)
    {
        DefaultHttpContext http = new();
        if (authorization is not null)
            http.Request.Headers.Authorization = authorization;
        return http;
    }

    private sealed class GrantFixture : IAsyncDisposable
    {
        private GrantFixture(RuntimeFixture fixture, string journeyId, bool required)
        {
            Fixture = fixture;
            JourneyId = journeyId;
            Now = fixture.Clock.GetUtcNow();
            Clock = new MovableClock(Now);
            Registry = new RecoveryGrantRegistry(Clock);
            GrantOptions = Options.Create(new RecoveryGrantOptions { Required = required });
            Issuance = new RecoveryGrantIssuance(
                fixture.Context,
                Roster,
                Registry,
                new GovernanceStore(
                    fixture.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
                GrantOptions,
                Clock);
        }

        public RuntimeFixture Fixture { get; }

        public string JourneyId { get; }

        public DateTimeOffset Now { get; }

        public MovableClock Clock { get; }

        public RecoveryGrantRegistry Registry { get; }

        public RecoveryGrantIssuance Issuance { get; }

        public IOptions<RecoveryGrantOptions> GrantOptions { get; }

        public string AgvId => Fixture.Options.AgvId;

        public string VehicleKey => Fixture.Options.VehicleKey;

        public VehicleRoster Roster => new(Options.Create(Fixture.Options));

        public static async Task<GrantFixture> FaultedAsync(bool required = true)
        {
            RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
            return new GrantFixture(fixture, (await fixture.RuntimeAsync()).JourneyId, required);
        }

        public RecoveryGrantRequest Request() => new(
            VehicleKey, JourneyId, null, null, "maintenance-797", "车上有货、卸货结果待恢复", "SITE-797-01", "MAINTENANCE_ADMINISTRATOR", null);

        public RecoveryGrantHttpRequest HttpRequest() => new(
            VehicleKey, JourneyId, null, null, "maintenance-797", "车上有货、卸货结果待恢复", "SITE-797-01", "MAINTENANCE_ADMINISTRATOR", null);

        public async Task<RecoveryGrantResult> GrantAsync(RecoveryGrantRequest request)
        {
            string code = (await Issuance.GrantAsync(request, Token)).ConfirmationCode!;
            RecoveryGrantResult result = await Issuance.GrantAsync(request with { ConfirmationCode = code }, Token);
            Assert.Equal(RecoveryGrantIssuance.Granted, result.Outcome);
            return result;
        }

        public Task<AdministratorAuditRecordRow[]> AuditsAsync() =>
            Fixture.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
                .Where(row => row.ObjectKind == GovernedObjectKind.RecoveryGrant)
                .ToArrayAsync(Token);

        public async Task SetJourneyStageAsync(JourneyRuntimeStage stage)
        {
            JourneyRuntimeRow row = await Fixture.Context.JourneyRuntimes.SingleAsync(item => item.JourneyId == JourneyId, Token);
            row.Stage = stage;
            await Fixture.Context.SaveChangesAsync(Token);
            Fixture.Context.ChangeTracker.Clear();
        }

        public static IOptions<VehicleFaultRecoveryOptions> RecoveryOptions(string credentialVariable) =>
            Options.Create(new VehicleFaultRecoveryOptions { Enabled = true, CredentialEnvironmentVariable = credentialVariable });

        public Task<Results<Ok<RecoveryGrantResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostGrantAsync(
            string credentialVariable, string authorization, RecoveryGrantHttpRequest request) =>
            RecoveryGrantEndpoints.GrantAsync(Http(authorization), request, Issuance, RecoveryOptions(credentialVariable), Token);

        public Task<Results<Ok<VehicleFaultRecoveryResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostFaultRecoveryAsync(
            string credentialVariable, string action) =>
            VehicleFaultRecoveryEndpoints.HandleAsync(
                Http($"Bearer {Credential}"),
                new VehicleFaultRecoveryHttpRequest(AgvId, "L1-OPERATOR-07", action, FaultRemedied: true, Note: "L1"),
                Service(Fixture, new SiteRiot(Fixture)),
                Roster,
                RecoveryOptions(credentialVariable),
                new RecoveryGrantGate(Registry, GrantOptions, Fixture.Context),
                Token);

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class CredentialScope : IDisposable
    {
        public CredentialScope()
        {
            Environment.SetEnvironmentVariable(Variable, Credential);
        }

        public string Variable { get; } = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");

        public void Dispose() => Environment.SetEnvironmentVariable(Variable, null);
    }
}
