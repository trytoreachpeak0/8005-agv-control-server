using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// 协议 v2 消息 7／8 的传输与序列化（<c>FP-IS-14</c> 服务端半边）。
/// </summary>
/// <remarks>
/// <para>
/// #15 停在纪律 6 的边上：业务语义与单元测试做完了，传输与序列化留给轨 A。本文件补的是那一半，
/// #15 的业务语义一个字没改——原子性、待补报态、补报收敛、指纹比对规则全在协调器里，传输层调它。
/// </para>
/// <para>
/// 向量 <c>CV-SLOT-CONFIGURATION-ACTIVATION</c> 的四步是「命令、结果、能力快照、ack」。本文件证
/// 前两步与补发；第三步 <c>CapabilitySnapshot</c> 的指纹核对是另一个文件的事。服务端这一侧要证的两条是
/// <c>VERIFY_FINGERPRINT_BEFORE_ACTIVATION</c> 与 <c>NEVER_GUESS_ACTIVATION_SUCCESS</c>。
/// </para>
/// </remarks>
public sealed class SlotConfigurationActivationWireTests
{
    private const string CredentialVariable = "CONTROL_SERVER_TEST_ACTIVATION_WIRE_CREDENTIAL";

    private const string Credential = "test-credential-not-for-production";

    private const string AgvId = "AGV-001";

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 命令按 RELIABLE 出去：先落库，再上线，指纹与目标版本都在 payload 里。
    /// </summary>
    /// <remarks>
    /// <c>durableBeforeSend</c> 的检验方式是看**发出去之前**库里有什么：激活记录在待补报态，发件箱
    /// 里有那条命令，而且那次激活记着它的 messageId。三样缺一样，断电重连后就有一台可能已经换了配置
    /// 而服务端不知道的车。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task TheCommandIsDurableBeforeItIsSentAndCarriesTheTargetVersionAndFingerprint()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        string model = await fixture.PublishModelAndBindAllSlotsAsync();

        SlotConfigurationActivationRow activation = await fixture.Dispatcher.IssueAsync(
            AgvId, model, fixture.State.SessionGeneration!.Value, Administrator(), Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(SlotConfigurationActivationState.PendingResult, activation.State);
        Assert.Equal("SLOT_CONFIGURATION", activation.RecoveryRole);
        Assert.NotNull(activation.CommandMessageId);

        ProtocolOutboxRow envelope = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == activation.CommandMessageId,
                TestContext.Current.CancellationToken);
        Assert.Equal("SlotConfigurationActivationCommand", envelope.MessageType);

        using JsonDocument document = JsonDocument.Parse(envelope.PayloadJson);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal(activation.ActivationId, payload.GetProperty("activationId").GetString());
        // 版本号不写死：夹具建模那几步自己也会推进版本，写死一个字面量证的是夹具的历史而不是消息的
        // 形状。要证的是线上那个字符串就是这次激活的那一版，且是不变文化格式化出来的。
        Assert.Equal(
            activation.ConfigurationVersion.ToString(CultureInfo.InvariantCulture),
            payload.GetProperty("targetSlotConfigurationVersion").GetString());
        Assert.Equal(activation.Fingerprint, payload.GetProperty("targetSlotConfigurationFingerprint").GetString());
        // 这台车还没装过任何一版，所以「预期当前版本」是 null 而不是一个编出来的 "0"。
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("expectedActiveSlotConfigurationVersion").ValueKind);
        Assert.Equal("op-7788", payload.GetProperty("administrator").GetProperty("operatorId").GetString());
        // correlationId 必须是 null：manifest 给这条消息的 correlationRule 是 MUST_BE_NULL，它是一条
        // 新命令，不是谁的回复。
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("correlationId").ValueKind);

        // 激活 id 是协议的 Id 形状，因为它是消息 7／8 的 businessDedupKey，要上线。
        Assert.True(Guid.TryParseExact(activation.ActivationId, "D", out _));
    }

    /// <summary>
    /// 结果回来，那次激活收敛，生效配置换成新的那一版。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task TheResultConvergesTheActivationAndTheActiveConfigurationBecomesTheOneJustActivated()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        string model = await fixture.PublishModelAndBindAllSlotsAsync();
        SlotConfigurationActivationRow activation = await fixture.Dispatcher.IssueAsync(
            AgvId, model, fixture.State.SessionGeneration!.Value, Administrator(), Now,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        string ack = await fixture.SendResultAsync(
            "00000000-0000-4000-8000-0000000000e1",
            Result(activation, "ACTIVATED"));

        using JsonDocument document = JsonDocument.Parse(ack);
        Assert.Equal("DurableAck", document.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(
            "SlotConfigurationActivationResult",
            document.RootElement.GetProperty("payload").GetProperty("acceptedMessageType").GetString());

        SlotConfigurationActivationRow settled = await fixture.Context.Set<SlotConfigurationActivationRow>()
            .AsNoTracking()
            .SingleAsync(row => row.ActivationId == activation.ActivationId,
                TestContext.Current.CancellationToken);
        Assert.Equal(SlotConfigurationActivationState.Activated, settled.State);

        ActiveSlotConfigurationRow active = await fixture.Context.Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(activation.Fingerprint, active.Fingerprint);
        Assert.Equal(activation.ActivationId, active.ActivationId);
    }

    /// <summary>
    /// <c>UNKNOWN</c> 不被当成成功。
    /// </summary>
    /// <remarks>
    /// 向量把 <c>unknown-as-success</c> 列为禁止的副作用，这一条就是它。车说「我不知道」时那次激活
    /// 收敛到失败侧：**生效配置一个字段都不动**，理由码记下 <c>UNKNOWN</c> 本身，好让「车说不知道」
    /// 和「车说失败」在库里不是同一样东西。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AnUnknownOutcomeIsNeverRecordedAsSuccessAndLeavesTheActiveConfigurationUntouched()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        string model = await fixture.PublishModelAndBindAllSlotsAsync();
        SlotConfigurationActivationRow activation = await fixture.Dispatcher.IssueAsync(
            AgvId, model, fixture.State.SessionGeneration!.Value, Administrator(), Now,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        await fixture.SendResultAsync("00000000-0000-4000-8000-0000000000f1", Result(activation, "UNKNOWN"));

        SlotConfigurationActivationRow settled = await fixture.Context.Set<SlotConfigurationActivationRow>()
            .AsNoTracking()
            .SingleAsync(row => row.ActivationId == activation.ActivationId,
                TestContext.Current.CancellationToken);
        Assert.Equal(SlotConfigurationActivationState.Failed, settled.State);
        Assert.Contains("UNKNOWN", settled.ResultJson!, StringComparison.Ordinal);
        Assert.Empty(await fixture.Context.Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 同一次激活补报两次，收敛一次。
    /// </summary>
    /// <remarks>
    /// 断线重连让同一份结果到达不止一次，那是补报机制正常工作的样子。第二份用的是另一个 messageId，
    /// 所以收件箱去重救不了这一次——收敛的幂等得由 <c>activationId</c> 这个 businessDedupKey 承担。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AReplayedResultUnderANewMessageIdConvergesTheSameActivationInsteadOfASecondOne()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        string model = await fixture.PublishModelAndBindAllSlotsAsync();
        SlotConfigurationActivationRow activation = await fixture.Dispatcher.IssueAsync(
            AgvId, model, fixture.State.SessionGeneration!.Value, Administrator(), Now,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        await fixture.SendResultAsync("00000000-0000-4000-8000-000000000101", Result(activation, "ACTIVATED"));
        // 补报：内容是同一份结论，messageId 是新的，而且这一次说的是 REJECTED——已经收敛的结论不被
        // 改写，第一份到达的那个才算数。
        await fixture.SendResultAsync("00000000-0000-4000-8000-000000000102", Result(activation, "REJECTED"));

        SlotConfigurationActivationRow settled = Assert.Single(
            await fixture.Context.Set<SlotConfigurationActivationRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SlotConfigurationActivationState.Activated, settled.State);
        Assert.Single(await fixture.Context.Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 重连时未结的激活命令跟着 <c>SLOT_CONFIGURATION</c> 这个恢复角色补发；已结的不补发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task APendingActivationCommandIsReplayedOnReconnectAndASettledOneIsNot()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        string model = await fixture.PublishModelAndBindAllSlotsAsync();
        SlotConfigurationActivationRow activation = await fixture.Dispatcher.IssueAsync(
            AgvId, model, fixture.State.SessionGeneration!.Value, Administrator(), Now,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            [activation.CommandMessageId!],
            await fixture.Dispatcher.PendingCommandMessageIdsAsync(
                AgvId, TestContext.Current.CancellationToken));

        await fixture.SendResultAsync("00000000-0000-4000-8000-000000000111", Result(activation, "ACTIVATED"));

        Assert.Empty(await fixture.Dispatcher.PendingCommandMessageIdsAsync(
            AgvId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 协议不认的 <c>outcome</c> 与不认的 <c>verificationMethod</c> 都在原地拒绝。
    /// </summary>
    /// <remarks>
    /// 一个进来一个出去，两处都是 fail-closed：读不懂的结果不会被当成某种默认结论悄悄收敛掉；形状
    /// 不对的命令不会被发出去等对端拒收——后者会让那次激活挂在待补报态上，看起来像车没回话。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task AnOutcomeOrVerificationMethodTheProtocolDoesNotDefineIsRefusedRatherThanDefaulted()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        string model = await fixture.PublishModelAndBindAllSlotsAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Dispatcher.IssueAsync(
            AgvId,
            model,
            fixture.State.SessionGeneration!.Value,
            new ProtocolOperatorContext("op-7788", "VIBES", Now),
            Now,
            TestContext.Current.CancellationToken));

        SlotConfigurationActivationRow activation = await fixture.Dispatcher.IssueAsync(
            AgvId, model, fixture.State.SessionGeneration!.Value, Administrator(), Now,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.SendResultAsync(
            "00000000-0000-4000-8000-000000000121", Result(activation, "PROBABLY")));

        SlotConfigurationActivationRow untouched = await fixture.Context.Set<SlotConfigurationActivationRow>()
            .AsNoTracking()
            .SingleAsync(row => row.ActivationId == activation.ActivationId,
                TestContext.Current.CancellationToken);
        Assert.Equal(SlotConfigurationActivationState.PendingResult, untouched.State);
    }

    private static ProtocolOperatorContext Administrator() =>
        new("op-7788", ProtocolOperatorContext.Badge, Now);

    private static object Result(SlotConfigurationActivationRow activation, string outcome) => new
    {
        activationId = activation.ActivationId,
        outcome,
        problem = (object?)null,
        activeSlotConfigurationVersion = outcome == "ACTIVATED"
            ? activation.ConfigurationVersion.ToString(CultureInfo.InvariantCulture)
            : "0",
        activeSlotConfigurationFingerprint = outcome == "ACTIVATED" ? activation.Fingerprint : new string('0', 64),
        verifiedAt = "2026-09-09T12:05:00Z"
    };

    private sealed class WireFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private WireFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            SlotConfigurationAuthorityStore authority,
            SlotConfigurationActivationDispatcher dispatcher,
            OnboardMessageProcessor processor)
        {
            _connection = connection;
            Context = context;
            Authority = authority;
            Dispatcher = dispatcher;
            Processor = processor;
        }

        public ControlServerDbContext Context { get; }

        public SlotConfigurationAuthorityStore Authority { get; }

        public SlotConfigurationActivationDispatcher Dispatcher { get; }

        public OnboardMessageProcessor Processor { get; }

        public OnboardConnectionState State { get; } = new();

        public static async Task<WireFixture> CreateAsync()
        {
            Environment.SetEnvironmentVariable(CredentialVariable, Credential);
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
            SlotConfigurationActivationDispatcher dispatcher = new(
                context,
                new SlotConfigurationActivationCoordinator(context, governedPublisher, governance),
                publisher,
                governance);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            return new WireFixture(
                connection,
                context,
                new SlotConfigurationAuthorityStore(context, governedPublisher),
                dispatcher,
                TestOnboardProcessorFactory.Create(context, store, time, configuration));
        }

        public async Task<string> PublishModelAndBindAllSlotsAsync()
        {
            string model = (await Authority.EnsureApprovedHardwareFactsAsync(
                Now, TestContext.Current.CancellationToken)).SlotModelVersionId;
            await Authority.PublishIoBindingsAsync(
                AgvId, model, ApprovedSlotHardwareFacts.IoBindings, Now,
                TestContext.Current.CancellationToken);
            return model;
        }

        public async Task HandshakeAsync() =>
            await Processor.ProcessAsync(
                Envelope(
                    "SessionHello",
                    "00000000-0000-4000-8000-000000000001",
                    null,
                    new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = Credential }),
                State,
                TestContext.Current.CancellationToken);

        public Task<string> SendResultAsync(string messageId, object payload) =>
            Processor.ProcessAsync(
                Envelope("SlotConfigurationActivationResult", messageId, State.SessionGeneration, payload),
                State,
                TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
            Environment.SetEnvironmentVariable(CredentialVariable, null);
        }
    }

    private static string Envelope(string messageType, string messageId, long? generation, object payload) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId = (string?)null,
            agvId = AgvId,
            sessionGeneration = generation,
            sentAt = "2026-09-09T12:00:00Z",
            payload = JsonSerializer.SerializeToElement(payload, WireJson)
        });

    private static object ReleaseIdentity() => new
    {
        repository = "8005-agv-protocol",
        releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        tag = ProtocolCandidateIdentity.Tag,
        commit = ProtocolCandidateIdentity.RepositoryCommit,
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
        vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256
    };

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
}
