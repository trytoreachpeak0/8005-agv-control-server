using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// 协议 v2 消息 9 <c>OnboardAlarmSnapshot</c> 的传输与序列化（<c>FP-IS-15</c> 服务端半边）。
/// </summary>
/// <remarks>
/// <para>
/// #16 把「收到之后怎么存、怎么收敛、怎么显示」做完了，停在纪律 6 的边上：轨 A 当时还没到，线上那一
/// 半没做。本文件补的是那一半——线上一份 JSON 进来，落到 #16 的 store 里，回一个
/// <c>SnapshotAppliedAck</c>。业务语义一个字没改。
/// </para>
/// <para>
/// 向量 <c>CV-ONBOARD-ALARM-SNAPSHOT</c> 的四步是「快照、ack、快照、ack」，服务端这一侧要证的是
/// <c>ADOPT_ALARM_SNAPSHOT_BY_REVISION</c>：按修订号采纳，且后一份整体取代前一份。
/// </para>
/// </remarks>
public sealed class OnboardAlarmSnapshotWireTests
{
    private const string CredentialVariable = "CONTROL_SERVER_TEST_ALARM_WIRE_CREDENTIAL";

    private const string Credential = "test-credential-not-for-production";

    private const string AgvId = "AGV-001";

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions AlarmJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task ASnapshotOnTheWireIsAdoptedAndAckedUnderTheOnboardAlarmKindAtItsOwnRevision()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();

        string ack = await fixture.SendAsync(
            "00000000-0000-4000-8000-0000000000a1",
            Snapshot(7, Alarm("ONBOARD_FLEET_CLOCK_SKEW", "FLEET", null)));

        using JsonDocument document = JsonDocument.Parse(ack);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal("SnapshotAppliedAck", document.RootElement.GetProperty("messageType").GetString());
        // snapshotKind 是协议冻结的七个之一，ONBOARD_ALARM 就是这一条的那个值。
        Assert.Equal("ONBOARD_ALARM", payload.GetProperty("snapshotKind").GetString());
        Assert.Equal("00000000-0000-4000-8000-0000000000a1", payload.GetProperty("snapshotMessageId").GetString());
        // appliedRevision 报的是快照自己带的修订号，不是服务端的一个计数器。
        Assert.Equal(7, payload.GetProperty("appliedRevision").GetInt64());

        OnboardAlarmSnapshotRow row = await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, row.SnapshotSequence);
        Assert.Contains("ONBOARD_FLEET_CLOCK_SKEW", row.AlarmsJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 向量的第三、四步：第二份快照到达，整体取代第一份。
    /// </summary>
    /// <remarks>
    /// 第一份里有而第二份里没有的那条告警必须消失。它要是还在，服务端就在把快照当增量事件流用，而那
    /// 正是 REQ-0269 禁止的那个东西——重连后不知道漏了什么，只能显示一个不确定新旧的旧值。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task TheSecondSnapshotReplacesTheFirstWholeAndARevisionThatGoesBackwardsIsNotAdopted()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();

        await fixture.SendAsync(
            "00000000-0000-4000-8000-0000000000b1",
            Snapshot(
                1,
                Alarm("ONBOARD_RULE_GATEWAY_DISCONNECTED", "FLEET", null),
                Alarm("ONBOARD_FLEET_CLOCK_SKEW", "FLEET", null)));
        await fixture.SendAsync(
            "00000000-0000-4000-8000-0000000000b2",
            Snapshot(2, Alarm("ONBOARD_FLEET_CLOCK_SKEW", "FLEET", null)));

        OnboardAlarmSnapshotRow adopted = await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, adopted.SnapshotSequence);
        Assert.DoesNotContain("ONBOARD_RULE_GATEWAY_DISCONNECTED", adopted.AlarmsJson, StringComparison.Ordinal);

        // 按修订号采纳：一份比库里更旧的快照到达时不被采纳，但它仍然被 ack——ack 说的是「这一份我收到
        // 并处理完了」，不是「我把它当成了当前事实」。
        string ack = await fixture.SendAsync(
            "00000000-0000-4000-8000-0000000000b3",
            Snapshot(1, Alarm("ONBOARD_RULE_GATEWAY_DISCONNECTED", "FLEET", null)));
        using JsonDocument document = JsonDocument.Parse(ack);
        Assert.Equal(1, document.RootElement.GetProperty("payload").GetProperty("appliedRevision").GetInt64());

        fixture.Context.ChangeTracker.Clear();
        OnboardAlarmSnapshotRow unchanged = await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, unchanged.SnapshotSequence);
        Assert.DoesNotContain("ONBOARD_RULE_GATEWAY_DISCONNECTED", unchanged.AlarmsJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 车重启后序号从 1 重来，那份快照仍然被采纳。
    /// </summary>
    /// <remarks>
    /// 车载端的告警板序号活在进程里，重启就归零。只按序号采纳的话，重启后那台车的快照全被当成「比库里
    /// 更旧」而静默忽略，看板停在重启前那一批——正是 REQ-0269 禁止的不确定新旧的旧值。车重启必然换一代
    /// 会话，所以采纳判据是 <c>(会话代, 序号)</c> 这一对。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [Trait("ProtocolVector", "CV-ONBOARD-ALARM-SNAPSHOT")]
    public async Task ASnapshotFromANewerSessionIsAdoptedEvenThoughItsRevisionWentBackToOne()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        await fixture.SendAsync(
            "00000000-0000-4000-8000-000000000301",
            Snapshot(9, Alarm("ONBOARD_RULE_GATEWAY_DISCONNECTED", "FLEET", null)));

        // 车重启：新一代会话，告警板从 1 重新开始。
        await fixture.HandshakeAsync("00000000-0000-4000-8000-000000000002");
        await fixture.SendAsync(
            "00000000-0000-4000-8000-000000000302",
            Snapshot(1, Alarm("ONBOARD_FLEET_CLOCK_SKEW", "FLEET", null)));

        OnboardAlarmSnapshotRow adopted = await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, adopted.SnapshotSequence);
        Assert.Contains("ONBOARD_FLEET_CLOCK_SKEW", adopted.AlarmsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ONBOARD_RULE_GATEWAY_DISCONNECTED", adopted.AlarmsJson, StringComparison.Ordinal);

        // 同一代之内序号不前进照旧忽略——补上会话代不是把那条规则拿掉。
        await fixture.SendAsync(
            "00000000-0000-4000-8000-000000000303",
            Snapshot(1, Alarm("ONBOARD_RULE_GATEWAY_DISCONNECTED", "FLEET", null)));
        fixture.Context.ChangeTracker.Clear();
        Assert.DoesNotContain(
            "ONBOARD_RULE_GATEWAY_DISCONNECTED",
            (await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).AlarmsJson,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>subjectType</c> 决定告警归哪一侧，认不出来的归看板。
    /// </summary>
    /// <remarks>
    /// 协议把 <c>subjectType</c> 留成开放字符串，所以服务端必然会遇到读不懂的取值。往可见方向倒：
    /// 未知归 <see cref="OnboardAlarmScope.Fleet"/>，也就是进看板。反过来归给三个车载类之一，等于让
    /// 一条服务端读不懂的告警只在车上出现，两边加起来就不再是全集了。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task TheSubjectTypeRoutesTheAlarmAndAnUnknownOneIsSeenOnTheDashboardRatherThanLost()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();
        await fixture.MarkReadyAsync();

        await fixture.SendAsync(
            "00000000-0000-4000-8000-0000000000c1",
            Snapshot(
                1,
                Alarm("ONBOARD_SLOT_LOCK_FEEDBACK_LOST", "SLOT", "3"),
                Alarm("ONBOARD_STATION_BLOCKED", "STATION", "ST-07"),
                Alarm("ONBOARD_OPERATION_STALLED", "SLOT_OPERATION", "00000000-0000-4000-8000-0000000000c9"),
                Alarm("ONBOARD_SOMETHING_THE_SERVER_HAS_NEVER_HEARD_OF", "TAROT_CARD", "THE-TOWER")));

        OnboardAlarmProjectionStore store = new(fixture.Context);
        VehicleAlarmProjection projection = Assert.Single(
            await store.ReadDashboardProjectionAsync(TestContext.Current.CancellationToken));

        // 三条与那台车的当下直接相关的归车载端界面，看板上看不到；读不懂的那条在看板上。
        Assert.Equal(
            ["ONBOARD_SOMETHING_THE_SERVER_HAS_NEVER_HEARD_OF"],
            projection.Alarms.Select(alarm => alarm.AlarmCode));

        OnboardAlarmEntry[] stored = JsonSerializer.Deserialize<OnboardAlarmEntry[]>(
            (await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).AlarmsJson,
            AlarmJson)!;
        Assert.Equal(
            [OnboardAlarmScope.CurrentVehicle, OnboardAlarmScope.CurrentStop, OnboardAlarmScope.CurrentOperation,
             OnboardAlarmScope.Fleet],
            stored.Select(alarm => alarm.Scope));
        // 主体 id 落到它对应的那个字段上，没有一个被塞进 Message 里凑合。
        Assert.Equal(3, stored[0].PhysicalSlotNumber);
        Assert.Equal("ST-07", stored[1].StationId);
        Assert.Equal("00000000-0000-4000-8000-0000000000c9", stored[2].SlotOperationAttemptId);
    }

    /// <summary>
    /// 告警码在整条路径上一次都不经过 <c>ErrorCode</c>。
    /// </summary>
    /// <remarks>
    /// 这一条在 #16 里是对领域模型断言的；线上那一半接好之后，同一件事要在**线上收进来的告警**上
    /// 再成立一次——传输层是最容易顺手把开放集合映射成封闭 enum 的地方。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AnAlarmCodeArrivingOnTheWireNeverPassesThroughTheClosedProtocolErrorCodeRegistry()
    {
        await using WireFixture fixture = await WireFixture.CreateAsync();
        await fixture.HandshakeAsync();

        await fixture.SendAsync(
            "00000000-0000-4000-8000-0000000000d1",
            Snapshot(1, Alarm("A_CODE_THE_REGISTRY_DOES_NOT_CONTAIN", "FLEET", null)));

        OnboardAlarmSnapshotRow row = await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains("A_CODE_THE_REGISTRY_DOES_NOT_CONTAIN", row.AlarmsJson, StringComparison.Ordinal);
        Assert.False(ProtocolErrorCodes.Contains("A_CODE_THE_REGISTRY_DOES_NOT_CONTAIN"));
    }

    private static object Snapshot(long revision, params object[] alarms) => new
    {
        alarmSnapshotRevision = revision,
        observedAt = "2026-09-09T12:00:00Z",
        alarms
    };

    private static object Alarm(string code, string subjectType, string? subjectId) => new
    {
        alarmId = Guid.NewGuid().ToString("D"),
        code,
        severity = "WARNING",
        raisedAt = "2026-09-09T11:59:00Z",
        subjectType,
        subjectId,
        displayMessage = (string?)null
    };

    private sealed class WireFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private WireFixture(SqliteConnection connection, ControlServerDbContext context)
        {
            _connection = connection;
            Context = context;
            WireToGateStore store = new(context);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            Processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
        }

        public ControlServerDbContext Context { get; }

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
            return new WireFixture(connection, context);
        }

        /// <summary>
        /// 每次握手要用不同的 messageId：收件箱按 messageId 去重，复用同一个会原样回放上一次的
        /// SessionAccepted，会话代根本不推进。
        /// </summary>
        public async Task HandshakeAsync(string messageId = "00000000-0000-4000-8000-000000000001") =>
            await Processor.ProcessAsync(
                Envelope(
                    "SessionHello",
                    messageId,
                    null,
                    new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = Credential }),
                State,
                TestContext.Current.CancellationToken);

        /// <summary>看板只显示在线的车，所以投影断言之前得让这台车在线。</summary>
        public async Task MarkReadyAsync()
        {
            SessionRecoveryRow row = await Context.SessionRecoveries.SingleAsync(
                candidate => candidate.AgvId == AgvId, TestContext.Current.CancellationToken);
            row.Readiness = SessionReadiness.Ready;
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        public Task<string> SendAsync(string messageId, object payload) =>
            Processor.ProcessAsync(
                Envelope("OnboardAlarmSnapshot", messageId, State.SessionGeneration, payload),
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
