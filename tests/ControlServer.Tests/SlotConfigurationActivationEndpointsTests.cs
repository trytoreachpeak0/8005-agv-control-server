using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 激活发起入口：协议 v2 消息 7 的唯一进程外来源（<c>FP-IS-14</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 在这个入口存在之前，<c>SlotConfigurationActivationDispatcher.IssueAsync</c> 的调用者只有测试，
/// 于是运行中的系统里没有任何途径可以发起一次激活——这也正是 <c>FP-IS-14</c> 的 <c>G3</c> 一度跑不
/// 起来的原因：受控装置里的服务端手上永远不会有已批准版本，指纹核验只会走「没有可比对的对象」那条
/// 分支。
/// </para>
/// <para>
/// 这里证的是入口本身的四条边界：没有凭据不可用、凭据不对拒收、协议不认的
/// <c>verificationMethod</c> 在命令被构造之前就挡下、这台车没有会话就发不出去。以及成功那一条的
/// 形状——**202 不是 200**，因为命令上线之后这次激活处在 <c>PENDING_RESULT</c>，车还没报结果。
/// </para>
/// </remarks>
public sealed class SlotConfigurationActivationEndpointsTests
{
    private const string AgvId = "AGV-001";

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>没有配置凭据：入口不可用，一次激活都不许落库。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AnUnpopulatedCredentialVariableMakesTheEntryPointUnavailable()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");

        var result = await fixture.PostAsync(variable, "Bearer anything", fixture.ValidRequest());

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Empty(await fixture.ActivationsAsync());
    }

    /// <summary>凭据不对：401，一次激活都不许落库。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AWrongBearerCredentialIsRejectedBeforeAnythingIsPersisted()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "expected-credential");

        var result = await fixture.PostAsync(variable, "Bearer wrong-credential", fixture.ValidRequest());

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Empty(await fixture.ActivationsAsync());
    }

    /// <summary>
    /// 协议把 <c>verificationMethod</c> 冻结在 <c>BADGE</c>／<c>SESSION</c> 上，第三个值在这里挡下。
    /// </summary>
    /// <remarks>
    /// 让它出去，车会拒收，而服务端这边已经留下了一次从未生效的激活记录。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AVerificationMethodTheProtocolDoesNotKnowIsRefusedBeforeTheCommandIsBuilt()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "governance-credential");

        var result = await fixture.PostAsync(
            variable,
            "Bearer governance-credential",
            fixture.ValidRequest() with
            {
                Administrator = new SlotConfigurationActivationOperator("op-7788", "FINGERPRINT", Now)
            });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        Assert.Empty(await fixture.ActivationsAsync());
    }

    /// <summary>这台车从来没有过会话：命令没有地方可去，409，不落库。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AVehicleWithNoSessionCannotBeSentAnActivation()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync(withSession: false);
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "governance-credential");

        var result = await fixture.PostAsync(
            variable, "Bearer governance-credential", fixture.ValidRequest());

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Empty(await fixture.ActivationsAsync());
    }

    /// <summary>
    /// 成功这一条：**202 而不是 200**，激活留在 <c>PENDING_RESULT</c>，会话代取自服务端自己那一行。
    /// </summary>
    /// <remarks>
    /// REQ-0264 的「不能猜测成功」就是 202 的理由：命令按 <c>RELIABLE</c> 上线之后车还没报结果，
    /// 200 会让调用方以为配置已经换好了。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AnAcceptedActivationIsPendingResultAndCarriesTheServersOwnSessionGeneration()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "governance-credential");

        var result = await fixture.PostAsync(
            variable, "Bearer governance-credential", fixture.ValidRequest());

        Accepted<SlotConfigurationActivationAcceptedResponse> accepted =
            Assert.IsType<Accepted<SlotConfigurationActivationAcceptedResponse>>(result.Result);
        SlotConfigurationActivationAcceptedResponse body =
            Assert.IsType<SlotConfigurationActivationAcceptedResponse>(accepted.Value);

        Assert.Equal(SlotConfigurationActivationState.PendingResult.ToString(), body.State);
        Assert.Equal(SlotConfigurationActivationDelivery.RecoveryRole, body.RecoveryRole);
        Assert.False(string.IsNullOrWhiteSpace(body.CommandMessageId));
        Assert.Equal(EndpointFixture.SessionGeneration, body.SessionGeneration);

        SlotConfigurationActivationRow stored = Assert.Single(await fixture.ActivationsAsync());
        Assert.Equal(body.ActivationId, stored.ActivationId);
        Assert.Equal(SlotConfigurationActivationState.PendingResult, stored.State);
        Assert.Equal(body.CommandMessageId, stored.CommandMessageId);
        // 指纹是两端共用的那套规范化摘要算出来的，不是治理快照对自己内容的摘要。
        Assert.Equal(
            SlotConfigurationFingerprint.Compute(ApprovedSlotHardwareFacts.IoBindings),
            body.Fingerprint);
    }

    private sealed class EndpointFixture : IAsyncDisposable
    {
        public const long SessionGeneration = 4;

        private readonly SqliteConnection _connection;

        private EndpointFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            SlotConfigurationActivationDispatcher dispatcher,
            string slotModelVersionId)
        {
            _connection = connection;
            Context = context;
            Dispatcher = dispatcher;
            SlotModelVersionId = slotModelVersionId;
        }

        public ControlServerDbContext Context { get; }

        public SlotConfigurationActivationDispatcher Dispatcher { get; }

        public string SlotModelVersionId { get; }

        public static async Task<EndpointFixture> CreateAsync(bool withSession = true)
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            GovernanceStore governance = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            GovernedConfigurationPublisher governedPublisher = new(governance, governance);
            WireToGateStore store = new(context);
            TimeProvider time = new FixedTimeProvider();
            OnboardJourneyPublisher publisher = new(store, new SilentPeer(), time);
            SlotConfigurationAuthorityStore authority = new(context, governedPublisher);

            string model = (await authority.EnsureApprovedHardwareFactsAsync(
                Now, TestContext.Current.CancellationToken)).SlotModelVersionId;
            await authority.PublishIoBindingsAsync(
                AgvId, model, ApprovedSlotHardwareFacts.IoBindings, Now,
                TestContext.Current.CancellationToken);

            if (withSession)
            {
                // 直接写这一行而不是走完整握手：这组用例证的是入口，不是握手。会话代要紧的是
                // 它**不是调用方传进来的**，所以这里刻意给一个非 1 的值——端点若擅自默认了什么，
                // 响应里的数字会和这一行对不上。
                context.SessionRecoveries.Add(new SessionRecoveryRow
                {
                    AgvId = AgvId,
                    SessionGeneration = SessionGeneration,
                    ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
                    ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    ProfileId = ProtocolCandidateIdentity.ProfileId,
                    UpdatedAt = Now
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            return new EndpointFixture(
                connection,
                context,
                new SlotConfigurationActivationDispatcher(
                    context,
                    new SlotConfigurationActivationCoordinator(context, governedPublisher, governance),
                    publisher,
                    governance),
                model);
        }

        public SlotConfigurationActivationRequest ValidRequest() => new(
            AgvId,
            SlotModelVersionId,
            new SlotConfigurationActivationOperator("op-7788", ProtocolOperatorContext.Badge, Now));

        public Task<Results<
            Accepted<SlotConfigurationActivationAcceptedResponse>,
            UnauthorizedHttpResult,
            ProblemHttpResult>> PostAsync(
            string credentialVariable,
            string authorization,
            SlotConfigurationActivationRequest request)
        {
            DefaultHttpContext context = new();
            context.Request.Scheme = "http";
            context.Request.Headers.Authorization = authorization;
            return SlotConfigurationActivationEndpoints.HandleAsync(
                context,
                request,
                Dispatcher,
                Context,
                new FixedTimeProvider(),
                Options.Create(new SlotConfigurationActivationOptions
                {
                    Enabled = true,
                    CredentialEnvironmentVariable = credentialVariable
                }),
                TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<SlotConfigurationActivationRow>> ActivationsAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.Set<SlotConfigurationActivationRow>().AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class SilentPeer : IOnboardPeer
    {
        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            _ = ndjsonLine;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? original;

        public EnvironmentVariableScope(string name, string value)
        {
            this.name = name;
            original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, original);
    }
}
