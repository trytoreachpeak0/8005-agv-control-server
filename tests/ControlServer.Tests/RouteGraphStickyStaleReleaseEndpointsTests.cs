using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 路网黏住陈旧原因解除的 HTTP 入口（control-server#659）：故障人工恢复入口那把凭据；凭据没配不可用、不对什么都不做；没点名图 422；
/// GET 读出解除要绑定的快照状态、不写审计；解除了 200，拒绝 409 并列出全部理由码与中文说明。
/// </summary>
/// <remarks>判定本身是 <see cref="RouteGraphStickyStaleTests"/> 的事，这里证边界。挂不挂由故障恢复的开关决定，钉在
/// <see cref="StationExclusivityReleaseEndpointsTests.TheEntryIsMappedOnlyWithTheFaultRecoverySwitchOn"/>。</remarks>
public sealed class RouteGraphStickyStaleReleaseEndpointsTests
{
    private const int MapId = 25;
    private const string Fingerprint = "a1b2c3";

    private static readonly DateTimeOffset StaleSince = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnpopulatedCredentialVariableMakesBothVerbsUnavailable()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string missing = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");

        var read = await fixture.GetAsync(missing, "Bearer anything", MapId);
        var release = await fixture.PostAsync(missing, "Bearer anything", Request());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>(read.Result).StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>(release.Result).StatusCode);
        await fixture.AssertUntouchedAsync(audits: 0);
    }

    [Fact]
    public async Task AWrongBearerCredentialIsRejectedBeforeAnythingIsReadOrWritten()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CredentialScope scope = new();

        var read = await fixture.GetAsync(scope.Variable, "Bearer wrong", MapId);
        var release = await fixture.PostAsync(scope.Variable, "Bearer wrong", Request());

        Assert.IsType<UnauthorizedHttpResult>(read.Result);
        Assert.IsType<UnauthorizedHttpResult>(release.Result);
        await fixture.AssertUntouchedAsync(audits: 0);
    }

    [Fact]
    public async Task ARequestNamingNoMapIsIncomplete()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CredentialScope scope = new();

        var read = await fixture.GetAsync(scope.Variable, scope.Authorization, null);
        var release = await fixture.PostAsync(scope.Variable, scope.Authorization, Request() with { MapId = 0 });

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>(read.Result).StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>(release.Result).StatusCode);
        await fixture.AssertUntouchedAsync(audits: 0);
    }

    /// <summary>GET 读出解除要绑定的四项与设计态刷新时刻，标明是黏住原因；不写审计、不改快照。没有快照的图 404。</summary>
    [Fact]
    public async Task TheReadShowsTheStateAReleaseBindsToAndWritesNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CredentialScope scope = new();

        var read = await fixture.GetAsync(scope.Variable, scope.Authorization, MapId);
        var missing = await fixture.GetAsync(scope.Variable, scope.Authorization, 26);

        RouteGraphStickyStaleStateResponse state = Assert.IsType<Ok<RouteGraphStickyStaleStateResponse>>(read.Result).Value!;
        Assert.Equal(
            (RouteGraphStickyStaleReasons.EdgeGroupFingerprintChanged, StaleSince, true, Fingerprint, true, StaleSince.AddMinutes(10)),
            (state.StaleReason, state.StaleSince, state.Sticky, state.EdgeGroupFingerprint, state.DynamicRouteCostPresent,
                state.DesignRefreshedAt));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ProblemHttpResult>(missing.Result).StatusCode);
        await fixture.AssertUntouchedAsync(audits: 0);
    }

    /// <summary>缺核实记录、确认的指纹不对：409，理由码与中文说明都列出来，审计记下这次失败，原因不动。</summary>
    [Fact]
    public async Task ARefusalIsAConflictListingEveryCodeWithItsDescriptionAndIsAudited()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CredentialScope scope = new();

        var result = await fixture.PostAsync(
            scope.Variable, scope.Authorization, Request() with { SiteVerification = null, AcknowledgedEdgeGroupFingerprint = "other" });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        string[] expected =
        [
            RouteGraphStickyStaleReleaseCodes.SiteVerificationRequired,
            RouteGraphStickyStaleReleaseCodes.StateChangedSinceRead,
        ];
        Assert.Equal(expected, Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["codes"]));
        IReadOnlyDictionary<string, string> descriptions =
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(problem.ProblemDetails.Extensions["descriptions"]);
        Assert.Equal(expected.Select(code => RouteGraphStickyStaleReleaseCodes.Descriptions[code]), expected.Select(code => descriptions[code]));
        await fixture.AssertUntouchedAsync(audits: 1);
    }

    [Fact]
    public async Task AReleaseIsOkWithTheStateItReleasedAndItsAuditRecord()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CredentialScope scope = new();

        var result = await fixture.PostAsync(scope.Variable, scope.Authorization, Request());

        RouteGraphStickyStaleReleaseResponse body = Assert.IsType<Ok<RouteGraphStickyStaleReleaseResponse>>(result.Result).Value!;
        Assert.Equal(
            ("RELEASED", RouteGraphStickyStaleReasons.EdgeGroupFingerprintChanged, Fingerprint, (bool?)true),
            (body.Outcome, body.StaleReason, body.EdgeGroupFingerprint, body.DynamicRouteCostPresent));
        Assert.Empty(body.Codes);
        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Null((await read.RouteGraphSnapshots.SingleAsync(Token)).StaleReason);
        AdministratorAuditRecordRow audit = await read.Set<AdministratorAuditRecordRow>()
            .SingleAsync(row => row.Action == RouteGraphStickyStaleRelease.AuditAction, Token);
        Assert.Equal((body.AuditRecordId, GovernanceActionOutcome.Succeeded), (audit.AuditRecordId, audit.Outcome));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static RouteGraphStickyStaleReleaseHttpRequest Request() =>
        new(MapId, "OP-7", "边组改动已核对", "RIOT-MAP-2026-1012-01", null,
            RouteGraphStickyStaleReasons.EdgeGroupFingerprintChanged, StaleSince, Fingerprint, true);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection)
        {
            _connection = connection;
        }

        /// <summary>Map 25 stale on an edge-group change, dynamic cost present, the design state read ten minutes later.</summary>
        public static async Task<Fixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            Fixture fixture = new(connection);
            await using ControlServerDbContext context = fixture.NewContext();
            await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
            context.RouteGraphSnapshots.Add(new RouteGraphSnapshotRow
            {
                MapId = MapId,
                DesignRevision = 2,
                DesignRefreshedAt = StaleSince.AddMinutes(10),
                RuntimeRefreshedAt = StaleSince.AddMinutes(10),
                EdgeGroupFingerprint = Fingerprint,
                EdgeGroupRefreshedAt = StaleSince,
                DynamicRouteCostPresent = true,
                DynamicRouteCostObservedAt = StaleSince,
                StaleReason = RouteGraphStickyStaleReasons.EdgeGroupFingerprintChanged,
                StaleSince = StaleSince,
            });
            await context.SaveChangesAsync(Token);
            return fixture;
        }

        public ControlServerDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);

        public async Task<Results<Ok<RouteGraphStickyStaleStateResponse>, UnauthorizedHttpResult, ProblemHttpResult>> GetAsync(
            string credentialVariable, string authorization, int? mapId)
        {
            await using ControlServerDbContext context = NewContext();
            return await RouteGraphStickyStaleReleaseEndpoints.ReadAsync(
                Http(authorization), mapId, context, Options(credentialVariable), Token);
        }

        public async Task<Results<Ok<RouteGraphStickyStaleReleaseResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostAsync(
            string credentialVariable, string authorization, RouteGraphStickyStaleReleaseHttpRequest request)
        {
            await using ControlServerDbContext context = NewContext();
            return await RouteGraphStickyStaleReleaseEndpoints.ReleaseAsync(
                Http(authorization),
                request,
                context,
                new GovernanceStore(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
                Options(credentialVariable),
                new FixedClock(StaleSince.AddMinutes(20)),
                Token);
        }

        public async Task AssertUntouchedAsync(int audits)
        {
            await using ControlServerDbContext read = NewContext();
            Assert.Equal(
                RouteGraphStickyStaleReasons.EdgeGroupFingerprintChanged,
                (await read.RouteGraphSnapshots.SingleAsync(Token)).StaleReason);
            Assert.Equal(
                audits,
                await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == RouteGraphStickyStaleRelease.AuditAction, Token));
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

        private static DefaultHttpContext Http(string authorization)
        {
            DefaultHttpContext http = new();
            http.Request.Scheme = "http";
            http.Request.Headers.Authorization = authorization;
            return http;
        }

        private static IOptions<VehicleFaultRecoveryOptions> Options(string credentialVariable) =>
            Microsoft.Extensions.Options.Options.Create(
                new VehicleFaultRecoveryOptions { Enabled = true, CredentialEnvironmentVariable = credentialVariable });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A throwaway credential under a throwaway variable name, generated per test; never a real one.</summary>
    private sealed class CredentialScope : IDisposable
    {
        private readonly string _credential = Guid.NewGuid().ToString("N");

        public CredentialScope()
        {
            Environment.SetEnvironmentVariable(Variable, _credential);
        }

        public string Variable { get; } = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");

        public string Authorization => $"Bearer {_credential}";

        public void Dispose() => Environment.SetEnvironmentVariable(Variable, null);
    }
}
