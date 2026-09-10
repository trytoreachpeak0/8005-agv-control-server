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
/// <c>CapabilitySnapshot</c> 上的 <c>activeSlotConfigurationFingerprint</c>：车报它此刻装着哪一版
/// 仓位配置，服务端拿它和自己认定的那一版核对（<c>FP-IS-14</c>，REQ-0316）。
/// </summary>
/// <remarks>
/// <para>
/// 向量 <c>CV-SLOT-CONFIGURATION-ACTIVATION</c> 的第三、四步是「能力快照、ack」，服务端要证的是
/// <c>VERIFY_FINGERPRINT_BEFORE_ACTIVATION</c>。核对发生在**每一份**能力快照到达时，不是发起激活
/// 时才发生一次——否则中间那段时间里「服务端认定这台车装着 A」这句话没有任何东西在担保。
/// </para>
/// <para>
/// 稳定错误码 <c>SLOT_CONFIGURATION_FINGERPRINT_MISMATCH</c> 是向量点名的那一个。
/// </para>
/// </remarks>
public sealed class CapabilitySnapshotFingerprintTests
{
    private const string CredentialVariable = "CONTROL_SERVER_TEST_CAPABILITY_FINGERPRINT_CREDENTIAL";

    private const string Credential = "test-credential-not-for-production";

    private const string AgvId = "AGV-001";

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 指纹对得上：快照被采纳，回 <c>SnapshotAppliedAck</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AReportedFingerprintThatMatchesTheActivatedVersionIsAdoptedAndAcked()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        ActiveSlotConfigurationRow active = await fixture.ActivateAsync();

        string ack = await fixture.SendCapabilityAsync(
            "00000000-0000-4000-8000-000000000201", 3, active.Fingerprint);

        using JsonDocument document = JsonDocument.Parse(ack);
        Assert.Equal("SnapshotAppliedAck", document.RootElement.GetProperty("messageType").GetString());
        Assert.Equal("CAPABILITY", document.RootElement.GetProperty("payload").GetProperty("snapshotKind").GetString());

        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.AsNoTracking()
            .SingleAsync(row => row.AgvId == AgvId, TestContext.Current.CancellationToken);
        Assert.Equal(3, session.CapabilityRevision);
    }

    /// <summary>
    /// 指纹对不上：快照不被采纳，回一条 <c>ProtocolProblem</c>，那台车取不到业务就绪。
    /// </summary>
    /// <remarks>
    /// 不采纳是这里的要害。服务端认定这台车装着 A，车说自己装着 B——那么这台车此刻装着什么，双方都
    /// 不知道。采纳 B 等于服务端放弃自己的权威，照旧采纳 A 等于假装没看见。两条都不做：能力修订号
    /// 因此没被采纳，`DecideReadinessAsync` 那句 <c>CapabilityRevision is not null</c> 就不成立。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AReportedFingerprintThatDisagreesLeavesTheVehicleUnreadyButStillReachable()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        ActiveSlotConfigurationRow active = await fixture.ActivateAsync();
        string disagreeing = new('b', 64);

        string response = await fixture.SendCapabilityAsync(
            "00000000-0000-4000-8000-000000000211", 3, disagreeing);

        // **会话继续。**2026-09-10 之前这里回的是 ProtocolProblem、会话不建立；G3 跑出来的后果是一台
        // 被动过配置的车永远上不了线，而唯一能把它改回来的手段要走会话。
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.Equal("SnapshotAppliedAck", document.RootElement.GetProperty("messageType").GetString());

        // 车报的那一份如实记下来，不是丢掉也不是当成服务端自己的判断。
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.AsNoTracking()
            .SingleAsync(row => row.AgvId == AgvId, TestContext.Current.CancellationToken);
        Assert.Equal(disagreeing, session.ReportedSlotConfigurationFingerprint);

        // 就绪判定是拒绝发生的地方，而且这个原因排在其它原因之前——其它每一条都是关于这次会话自身
        // 的进展，运维读到那些会去错的地方找。
        WireToGateStore store = new(fixture.Context);
        SessionReadinessDecision decision = await store.DecideReadinessAsync(
            AgvId, session.SessionGeneration, TestContext.Current.CancellationToken);
        Assert.NotEqual(SessionReadiness.Ready, decision.Readiness);
        Assert.Equal(SlotConfigurationFingerprintVerdict.MismatchCode, decision.ReasonCode);
        // 稳定错误码来自协议那本封闭注册表，不是这里编的一个字符串。
        Assert.True(ProtocolErrorCodes.Contains(decision.ReasonCode));

        // 服务端认定的那一版一个字段都没动：不就绪不是「以车上的为准」。
        ActiveSlotConfigurationRow unchanged = await fixture.Context.Set<ActiveSlotConfigurationRow>()
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(active.Fingerprint, unchanged.Fingerprint);

        // **而这才是这次改动的理由**：不一致的车仍然收得到激活命令，也就是仍然有救。
        fixture.Context.ChangeTracker.Clear();
        SlotConfigurationActivationRow reissued = await fixture.Dispatcher.IssueAsync(
            AgvId,
            active.SlotModelVersionId,
            session.SessionGeneration,
            new ProtocolOperatorContext("op-7788", ProtocolOperatorContext.Badge, Now),
            Now.AddMinutes(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(SlotConfigurationActivationState.PendingResult, reissued.State);
        Assert.False(string.IsNullOrWhiteSpace(reissued.CommandMessageId));
    }

    /// <summary>
    /// 对不上要留在不可改写的那条审计流上。
    /// </summary>
    /// <remarks>
    /// 一次「车与服务端对不上」是治理事件，不是一行日志：查这台车为什么上不了业务的人，看的是审计，
    /// 不是某个进程的 stdout。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task TheDisagreementIsRecordedOnTheImmutableAuditStreamWithBothFingerprints()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        ActiveSlotConfigurationRow active = await fixture.ActivateAsync();

        await fixture.SendCapabilityAsync("00000000-0000-4000-8000-000000000221", 3, new string('c', 64));

        BusinessAuditRecordRow record = await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .SingleAsync(
                row => row.Action == "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH_OBSERVED",
                TestContext.Current.CancellationToken);
        Assert.Equal(AgvId, record.ObjectId);
        Assert.Contains(active.Fingerprint, record.DetailJson, StringComparison.Ordinal);
        Assert.Contains(new string('c', 64), record.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 服务端手上还没有生效版本时，这份指纹回答的是另一个问题：归档前的配置还能不能当恢复候选。
    /// </summary>
    /// <remarks>
    /// REQ-0316。没有可比对的对象就没有「对不上」可言——把「服务端没有」当成不匹配来拒收，会让一台从
    /// 没激活过的新车永远上不了线。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task WithNoActivatedVersionTheFingerprintDecidesTheRestorationCandidateInsteadOfBlockingTheVehicle()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();

        // 从没激活过的车：随便报什么指纹都不该被拒。
        string ack = await fixture.SendCapabilityAsync(
            "00000000-0000-4000-8000-000000000231", 1, new string('d', 64));
        Assert.Equal(
            "SnapshotAppliedAck",
            JsonDocument.Parse(ack).RootElement.GetProperty("messageType").GetString());

        // 归档过的车：指纹一致才认那份恢复候选，不一致时这份候选不成立——不是谁覆盖谁。
        fixture.Context.Set<AgvArchiveRow>().Add(new AgvArchiveRow
        {
            AgvId = AgvId,
            ArchiveReason = "DECOMMISSIONED_FOR_OVERHAUL",
            ArchivedAt = Now,
            ArchivedActiveSlotConfigurationFingerprint = new string('e', 64)
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        SlotConfigurationFingerprintVerdict accepted = await fixture.Dispatcher
            .ReconcileReportedFingerprintAsync(
                AgvId, new string('e', 64), Now, TestContext.Current.CancellationToken);
        Assert.True(accepted.Agrees);
        Assert.Equal(
            RecoveryCandidateVerdict.AcceptedCode,
            accepted.RestorationCandidate!.ReasonCode);

        SlotConfigurationFingerprintVerdict mismatched = await fixture.Dispatcher
            .ReconcileReportedFingerprintAsync(
                AgvId, new string('f', 64), Now, TestContext.Current.CancellationToken);
        Assert.True(mismatched.Agrees);
        Assert.Equal(
            RecoveryCandidateVerdict.FingerprintMismatchCode,
            mismatched.RestorationCandidate!.ReasonCode);
    }

    /// <summary>
    /// 还在握手、能力快照没到的车，不就绪的原因是握手没完成，不是指纹不符。
    /// </summary>
    /// <remarks>
    /// 指纹不符的原因码排在其它原因之前。所以「车还没报」必须不算不一致——否则服务端手上一旦有了
    /// 生效版本，每一台重连中的车都会先被报成指纹不符，运维会去追一台唯一的问题只是还在握手的车。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public async Task AVehicleThatHasNotReportedYetIsNotNamedAsAFingerprintMismatch()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        ActiveSlotConfigurationRow active = await fixture.ActivateAsync();

        fixture.Context.ChangeTracker.Clear();
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.AsNoTracking()
            .SingleAsync(row => row.AgvId == AgvId, TestContext.Current.CancellationToken);
        Assert.Null(session.ReportedSlotConfigurationFingerprint);

        WireToGateStore store = new(fixture.Context);
        SessionReadinessDecision beforeReport = await store.DecideReadinessAsync(
            AgvId, session.SessionGeneration, TestContext.Current.CancellationToken);
        Assert.NotEqual(SessionReadiness.Ready, beforeReport.Readiness);
        Assert.NotEqual(SlotConfigurationFingerprintVerdict.MismatchCode, beforeReport.ReasonCode);

        // And once the vehicle does report the version this server activated, it is still not named.
        await fixture.SendCapabilityAsync("00000000-0000-4000-8000-000000000221", 3, active.Fingerprint);
        fixture.Context.ChangeTracker.Clear();
        SessionReadinessDecision afterReport = await store.DecideReadinessAsync(
            AgvId, session.SessionGeneration, TestContext.Current.CancellationToken);
        Assert.NotEqual(SlotConfigurationFingerprintVerdict.MismatchCode, afterReport.ReasonCode);
    }

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
                new SlotConfigurationActivationDispatcher(
                    context,
                    new SlotConfigurationActivationCoordinator(context, governedPublisher, governance),
                    publisher,
                    governance),
                TestOnboardProcessorFactory.Create(context, store, time, configuration));
        }

        /// <summary>让这台车真的装上一版配置：下发、车报成功、生效配置落地。</summary>
        public async Task<ActiveSlotConfigurationRow> ActivateAsync()
        {
            string model = (await Authority.EnsureApprovedHardwareFactsAsync(
                Now, TestContext.Current.CancellationToken)).SlotModelVersionId;
            await Authority.PublishIoBindingsAsync(
                AgvId, model, ApprovedSlotHardwareFacts.IoBindings, Now,
                TestContext.Current.CancellationToken);
            SlotConfigurationActivationRow activation = await Dispatcher.IssueAsync(
                AgvId, model, State.SessionGeneration!.Value,
                new ProtocolOperatorContext("op-7788", ProtocolOperatorContext.Badge, Now),
                Now, TestContext.Current.CancellationToken);
            await Dispatcher.RecordResultAsync(
                new ActivationResultReport(activation.ActivationId, Succeeded: true, null, Now.AddMinutes(1)),
                TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
            return await Context.Set<ActiveSlotConfigurationRow>().AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
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

        public Task<string> SendCapabilityAsync(string messageId, long revision, string fingerprint) =>
            Processor.ProcessAsync(
                Envelope(
                    "CapabilitySnapshot",
                    messageId,
                    State.SessionGeneration,
                    new
                    {
                        capabilityVersion = revision,
                        observedAt = Now,
                        slotModelVersion = "SLOT-MODEL-1",
                        activeSlotConfigurationVersion = "1",
                        activeSlotConfigurationFingerprint = fingerprint,
                        slotStates = Array.Empty<object>(),
                        supportsBatchUnlock = true,
                        onboardJournalFormatVersion = 1
                    }),
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
            sentAt = Now.ToString("O", CultureInfo.InvariantCulture),
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
