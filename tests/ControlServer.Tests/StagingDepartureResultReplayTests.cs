using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// 出发答复的握手补发（control-server#713 独立审查 M2）：一篮没装点出发之后旅程已经收尾，引擎每轮的补发不再覆盖这辆车，这时断线只能靠握手之后
/// 那一轮补发（<c>OnboardRecoveryCoordinator.ReplayPendingCommandsAsync</c>）把答复送到；车确认过、代次变了的那一条由重复请求的重放送到
/// （<c>ReplayDepartureResultAsync</c>）。两条都经真处理器、真握手，像 <c>OnboardTcpServer</c> 那样先写应答、再冲延后的补发。
/// </summary>
public sealed class StagingDepartureResultReplayTests
{
    private const string AgvId = "AGV-001";
    private const string SessionCredentialVariable = "CONTROL_SERVER_TEST_STAGING_DEPARTURE_REPLAY_CREDENTIAL";
    private const string SessionCredential = "test-credential-not-for-production";
    private const string DepartureRequestId = "71300000-0000-4000-8000-0000000000d1";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string ResultId => CurrentStopDepartures.ResultMessageId(DepartureRequestId);

    /// <summary>
    /// G 代没确认的出发答复，在 G+1 握手之后补发：排在恢复报告的应答（DurableAck + SessionReadiness）之后，带的是新代次。
    /// 把握手补发里的出发答复那一项删掉，这条红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnUnacknowledgedDepartureResultIsReplayedAfterTheNextHandshakesReadiness()
    {
        await using Rig rig = await Rig.StartAsync();
        long first = rig.State.SessionGeneration ?? throw new InvalidOperationException("The handshake assigned no generation.");
        await rig.StageResultAsync(first);

        List<string> wire = await rig.ReconnectAsync();

        string[] types = [.. wire.Select(MessageTypeOf)];
        int readiness = Array.LastIndexOf(types, "SessionReadiness");
        int result = Array.IndexOf(types, CurrentStopDepartures.ResultType);
        Assert.True(readiness >= 0, string.Join(",", types));
        Assert.True(result > readiness, "The departure result must follow SessionReadiness: " + string.Join(",", types));
        using JsonDocument sent = JsonDocument.Parse(wire[result]);
        Assert.Equal(rig.State.SessionGeneration, sent.RootElement.GetProperty("sessionGeneration").GetInt64());
        Assert.NotEqual(first, rig.State.SessionGeneration);
    }

    /// <summary>
    /// 车确认过的出发答复，换代之后同号同内容的重复请求到了：重放发出的那一行就是发件箱里存下的那一行（代次改写后落库），车按它收到的那一行算的
    /// <c>DurableAck</c> 哈希对得上，确认被收下而不是被判内容冲突。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAcknowledgedResultReplayedAcrossGenerationsIsTheStoredLineAndItsAckMatches()
    {
        await using Rig rig = await Rig.StartAsync();
        long first = rig.State.SessionGeneration ?? throw new InvalidOperationException("The handshake assigned no generation.");
        object request = await rig.StageResultAsync(first);
        await rig.AcknowledgeAsync(rig.StoredLine());
        Assert.NotNull((await rig.ResultRowAsync()).AcknowledgedAt);
        await rig.ReconnectAsync();
        rig.Peer.Lines.Clear();

        await rig.SendDeferredAsync(CurrentStopDepartures.RequestType, request);

        string sent = Assert.Single(rig.Peer.Lines, line => MessageTypeOf(line) == CurrentStopDepartures.ResultType);
        Assert.Equal(rig.StoredLine(), sent);
        using (JsonDocument document = JsonDocument.Parse(sent))
        {
            Assert.Equal(rig.State.SessionGeneration, document.RootElement.GetProperty("sessionGeneration").GetInt64());
        }
        await rig.AcknowledgeAsync(sent);
        Assert.NotNull((await rig.ResultRowAsync()).AcknowledgedAt);
    }

    private static string MessageTypeOf(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private sealed class RecordingPeerLines : IOnboardPeer
    {
        public List<string> Lines { get; } = [];

        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            Lines.Add(Encoding.UTF8.GetString(ndjsonLine.Span).TrimEnd('\n'));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = SessionCredentialVariable
                })
                .Build();
            Processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), new FixedClock(Now), configuration, Peer);
        }

        public SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public RecordingPeerLines Peer { get; } = new();

        public OnboardMessageProcessor Processor { get; }

        public OnboardConnectionState State { get; private set; } = new() { DeferOutboundUntilResponseWritten = true };

        public static async Task<Rig> StartAsync()
        {
            Environment.SetEnvironmentVariable(SessionCredentialVariable, SessionCredential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(Token);
            Rig rig = new(connection, context);
            await rig.HandshakeAsync([]);
            return rig;
        }

        /// <summary>存一条本代次的出发答复（ACCEPTED），返回车发的那条请求的载荷，重复请求用。</summary>
        public async Task<object> StageResultAsync(long generation)
        {
            await OnboardJourneyPublisher.StageCurrentStopDepartureResultAsync(
                new WireToGateStore(Context), ResultId, AgvId, generation, DepartureRequestId, problem: null,
                currentWorklistRevision: 3, Now, Token);
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
            return new
            {
                departureRequestId = DepartureRequestId,
                operationSessionId = (string?)null,
                stationId = "派工待送取货",
                worklistRevision = 3,
                @operator = new { operatorId = "OP-713", verificationMethod = "BADGE", verifiedAt = Now }
            };
        }

        public async Task<ProtocolOutboxRow> ResultRowAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.ProtocolOutbox.AsNoTracking().SingleAsync(row => row.MessageId == ResultId, Token);
        }

        public string StoredLine()
        {
            Context.ChangeTracker.Clear();
            return Context.ProtocolOutbox.AsNoTracking().Single(row => row.MessageId == ResultId).PayloadJson;
        }

        /// <summary>车按它收到的那一行算哈希、回 DurableAck。</summary>
        public async Task AcknowledgeAsync(string line)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant();
            await SendDeferredAsync("DurableAck", new
            {
                acceptedMessageId = ResultId,
                acceptedMessageType = CurrentStopDepartures.ResultType,
                acceptedContentSha256 = hash
            }, correlationId: ResultId);
        }

        /// <summary>新连接、新代次、完整握手；返回线上按先后的每一行（应答与补发交织，像 TCP 服务端那样先写应答、再冲延后的补发）。</summary>
        public async Task<List<string>> ReconnectAsync()
        {
            State = new OnboardConnectionState { DeferOutboundUntilResponseWritten = true };
            List<string> wire = [];
            await HandshakeAsync(wire);
            return wire;
        }

        private async Task HandshakeAsync(List<string> wire)
        {
            await SendDeferredAsync(
                "SessionHello", new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = SessionCredential }, wire: wire);
            await SendDeferredAsync(
                "CapabilitySnapshot", new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) },
                wire: wire);
            await SendDeferredAsync("SafetyStateSnapshot", new
            {
                safetyStateVersion = 1,
                observedAt = Now.AddMinutes(-5),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }).ToArray()
            }, wire: wire);
            await SendDeferredAsync("RecoveryStateReport", new
            {
                reportId = Guid.NewGuid().ToString("D"),
                unsettledSlotOperationAttemptId = (string?)null,
                provenRecoveryCheckpoint = (string?)null,
                activeUnlockSlots = Array.Empty<int>(),
                forcedRecoveryGeneration = 0,
                pendingResults = Array.Empty<object>()
            }, wire: wire);
        }

        /// <summary>处理一行：应答记进线上，再冲这一行延后的补发（补发由对端记下），与 <c>OnboardTcpServer</c> 的先后一致。</summary>
        public async Task SendDeferredAsync(
            string messageType, object payload, string? correlationId = null, List<string>? wire = null)
        {
            int before = Peer.Lines.Count;
            string response = await Processor.ProcessAsync(
                JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    profileId = ProtocolCandidateIdentity.ProfileId,
                    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    messageType,
                    messageId = Guid.NewGuid().ToString("D"),
                    correlationId,
                    agvId = AgvId,
                    sessionGeneration = State.SessionGeneration,
                    sentAt = Now,
                    payload = JsonSerializer.SerializeToElement(payload, WireJson)
                }),
                State,
                Token);
            wire?.AddRange(Peer.Lines.Skip(before));
            wire?.AddRange(response.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            int flushedFrom = Peer.Lines.Count;
            await Processor.FlushDeferredOutboundAsync(State, Token);
            wire?.AddRange(Peer.Lines.Skip(flushedFrom));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }

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
    }
}
