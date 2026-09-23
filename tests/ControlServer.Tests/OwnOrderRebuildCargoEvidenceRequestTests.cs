using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// 有货清除之后服务端向车要一份新鲜的 <c>SafetyStateSnapshot</c>（REQ-0362，control-server#318 票上 2026-09-23 的范围补充）：
/// Host 收消息那一侧何时附带 <c>SafetyStateSnapshotRequested</c>，以及它的节流。
/// </summary>
/// <remarks>
/// <para>
/// 节流规则：同一个会话代次只要一次；那一次若是在会话未就绪时要的，会话在同一代次里变成就绪之后可以再要一次；重连后的新代次再要一次；
/// 快照证明了货在原仓（或重建已不在等证据）之后不再要。每条断言都同时看两处——应答里有没有那一行、库里记没记「已请求」——两者必须一致：
/// 记了没发，车永远等不到请求；发了没记，每条消息都会再要一次。
/// </para>
/// <para>
/// 握手之内绝不附带（与 control-server#202、#142 同一条规则），结束握手的那条恢复报告的应答上也不附带：那一次应答属于握手，
/// 紧接着的下一条消息（心跳）就会带上请求。
/// </para>
/// </remarks>
public sealed class OwnOrderRebuildCargoEvidenceRequestTests
{
    private const string CredentialVariable = "CONTROL_SERVER_TEST_CARGO_EVIDENCE_REQUEST_CREDENTIAL";

    private const string Credential = "test-credential-not-for-production";

    private const string AgvId = "AGV-001";

    private const string Requested = "SafetyStateSnapshotRequested";

    private static readonly DateTimeOffset Now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static readonly int[] CargoSlot = [3];

    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362ACargoRebuildWaitingForEvidenceAsksTheVehicleOncePerSession()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.AddCargoRebuildAsync();

        string[] first = Lines(await fixture.HeartbeatAsync());
        Assert.Equal(Requested, MessageType(first[^1]));
        using (JsonDocument request = JsonDocument.Parse(first[^1]))
        {
            Assert.Equal(fixture.State.SessionGeneration, request.RootElement.GetProperty("sessionGeneration").GetInt64());
        }
        Assert.Equal((fixture.State.SessionGeneration, true), await fixture.RequestedAsync());

        // 同一个动作第二次发生：同一代次、会话仍就绪，不再要。
        for (int message = 0; message < 3; message++)
        {
            Assert.DoesNotContain(Requested, Lines(await fixture.HeartbeatAsync()).Select(MessageType));
        }
        Assert.Equal((fixture.State.SessionGeneration, true), await fixture.RequestedAsync());
    }

    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362NoRequestInsideTheHandshakeAndOneAgainInTheNextSession()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddCargoRebuildAsync();

        // 握手的每一条应答都不带请求，库里也不记；结束握手的恢复报告的应答也不带。
        Assert.DoesNotContain(Requested, Lines(await fixture.HelloAsync()).Select(MessageType));
        Assert.DoesNotContain(Requested, Lines(await fixture.CapabilityAsync()).Select(MessageType));
        Assert.DoesNotContain(Requested, Lines(await fixture.SafetySnapshotAsync(1)).Select(MessageType));
        Assert.Equal((null, false), await fixture.RequestedAsync());
        string[] recovery = Lines(await fixture.RecoveryReportAsync());
        Assert.Equal("READY", Readiness(recovery));
        Assert.DoesNotContain(Requested, recovery.Select(MessageType));
        Assert.Equal((null, false), await fixture.RequestedAsync());

        long firstGeneration = fixture.State.SessionGeneration!.Value;
        Assert.Equal(Requested, MessageType(Lines(await fixture.HeartbeatAsync())[^1]));
        Assert.Equal((firstGeneration, true), await fixture.RequestedAsync());

        // 重连：新代次的握手里同样不带，握手之后的第一条消息再要一次。
        await fixture.HelloAsync();
        await fixture.CapabilityAsync();
        await fixture.SafetySnapshotAsync(1);
        await fixture.RecoveryReportAsync();
        Assert.NotEqual(firstGeneration, fixture.State.SessionGeneration);
        Assert.Equal((firstGeneration, true), await fixture.RequestedAsync());
        Assert.Equal(Requested, MessageType(Lines(await fixture.HeartbeatAsync())[^1]));
        Assert.Equal((fixture.State.SessionGeneration, true), await fixture.RequestedAsync());
        Assert.DoesNotContain(Requested, Lines(await fixture.HeartbeatAsync()).Select(MessageType));
    }

    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362ARequestMadeWhileNotReadyIsMadeOnceMoreWhenTheSessionBecomesReady()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.HelloAsync();
        await fixture.CapabilityAsync();
        await fixture.SafetySnapshotAsync(1, departureSafe: false);
        Assert.NotEqual("READY", Readiness(Lines(await fixture.RecoveryReportAsync())));
        await fixture.AddCargoRebuildAsync();

        Assert.Equal(Requested, MessageType(Lines(await fixture.HeartbeatAsync())[^1]));
        Assert.Equal((fixture.State.SessionGeneration, false), await fixture.RequestedAsync());
        // 会话一直未就绪：不刷请求。
        for (int message = 0; message < 3; message++)
        {
            Assert.DoesNotContain(Requested, Lines(await fixture.HeartbeatAsync()).Select(MessageType));
        }

        // 同一代次里变成就绪：这条消息的应答再带一次，之后不再带。
        string[] ready = Lines(await fixture.SafetyChangedAsync(2, departureSafe: true));
        Assert.Equal("READY", ReadinessOf(ready));
        Assert.Equal(Requested, MessageType(ready[^1]));
        Assert.Equal((fixture.State.SessionGeneration, true), await fixture.RequestedAsync());
        Assert.DoesNotContain(Requested, Lines(await fixture.HeartbeatAsync()).Select(MessageType));
    }

    [Theory]
    [InlineData("nothing-waits")]
    [InlineData("cargo-proven")]
    [InlineData("nothing-on-board")]
    [InlineData("stopped")]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362NothingIsAskedWhenNoCargoRebuildWaitsForEvidence(string state)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        if (state != "nothing-waits")
        {
            await fixture.AddCargoRebuildAsync(row =>
            {
                switch (state)
                {
                    case "cargo-proven":
                        row.CargoProvenAt = Now;
                        break;
                    case "nothing-on-board":
                        row.Source = OwnOrderRebuildSources.FaultClearedNothingOnBoard;
                        break;
                    default:
                        row.State = OwnOrderRebuildStates.Stopped;
                        break;
                }
            });
        }

        Assert.DoesNotContain(Requested, Lines(await fixture.HeartbeatAsync()).Select(MessageType));
        if (state != "nothing-waits")
        {
            Assert.Equal((null, false), await fixture.RequestedAsync());
        }
    }

    private static string[] Lines(string response) => response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string MessageType(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private static string Readiness(string[] lines) => ReadinessOf(lines);

    /// <summary>The readiness a response carries, from whichever of its lines is the SessionReadiness.</summary>
    private static string ReadinessOf(string[] lines)
    {
        string line = Assert.Single(lines, item => MessageType(item) == "SessionReadiness");
        using JsonDocument readiness = JsonDocument.Parse(line);
        return readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString()!;
    }

    private static object[] Slots() =>
        [.. Enumerable.Range(1, 8).Select(slot => new
        {
            slotNo = slot,
            operability = "OPERABLE",
            administrativeAvailability = "ENABLED",
            physicalState = slot == 3 ? "OCCUPIED" : "EMPTY",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = Array.Empty<string>()
        })];

    private static object Safety(bool departureSafe) => new
    {
        departureSafe,
        vehicleStopped = true,
        allTargetSlotsLocked = departureSafe,
        allUnlockOutputsReset = true,
        unknownPresent = false,
        reasonCodes = departureSafe ? Array.Empty<string>() : new[] { "LOCK_NOT_CLOSED" }
    };

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

    /// <summary>The server's clock; nothing here depends on time passing.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
            Clock = new FixedClock(Now);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            Processor = TestOnboardProcessorFactory.Create(context, new WireToGateStore(context), Clock, configuration);
        }

        public SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public TimeProvider Clock { get; }

        public OnboardMessageProcessor Processor { get; }

        public OnboardConnectionState State { get; } = new();

        public static async Task<Fixture> CreateAsync()
        {
            Environment.SetEnvironmentVariable(CredentialVariable, Credential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new Fixture(connection, context);
        }

        public async Task ReachReadyAsync()
        {
            await HelloAsync();
            await CapabilityAsync();
            await SafetySnapshotAsync(1);
            Assert.Equal("READY", Readiness(Lines(await RecoveryReportAsync())));
        }

        /// <summary>A cleared fault with cargo on board whose rebuild waits for the vehicle to show the cargo (REQ-0362).</summary>
        public async Task AddCargoRebuildAsync(Action<OwnOrderRebuildRow>? adjust = null)
        {
            await using ControlServerDbContext writing = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(Connection).Options);
            OwnOrderRebuildRow row = new()
            {
                RebuildId = OwnOrderRebuilds.RebuildIdFor("W2G-D-318-GATE-1"),
                JourneyId = "journey:D-318",
                DemandId = "D-318",
                AgvId = AgvId,
                VehicleKey = "KEY-" + AgvId,
                StopId = "stop:D-318:2",
                Source = OwnOrderRebuildSources.FaultClearedCargoOnBoard,
                EndedUpperId = "W2G-D-318-GATE-1",
                EndedOrderState = 4,
                IncidentAt = Now.AddMinutes(-2),
                RecordedAt = Now.AddMinutes(-1),
                DueAt = Now.AddSeconds(-30),
                OperatorId = "OP-318",
                NewUpperId = "W2G-D-318-REBUILD-1-1",
                NewMovementLegId = "rebuilt-leg-D-318",
                State = OwnOrderRebuildStates.Pending,
            };
            adjust?.Invoke(row);
            writing.OwnOrderRebuilds.Add(row);
            await writing.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>What the record says was asked: the session generation and whether that session was ready.</summary>
        public async Task<(long? Generation, bool WhileReady)> RequestedAsync()
        {
            await using ControlServerDbContext reading = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(Connection).Options);
            OwnOrderRebuildRow row = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            return (row.CargoEvidenceRequestedGeneration, row.CargoEvidenceRequestedWhileReady);
        }

        public Task<string> HelloAsync() => Send(
            "SessionHello",
            null,
            new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = Credential });

        public Task<string> HeartbeatAsync() => Send("Heartbeat", State.SessionGeneration, new { });

        public Task<string> CapabilityAsync() => Send(
            "CapabilitySnapshot",
            State.SessionGeneration,
            new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) });

        public Task<string> SafetySnapshotAsync(long revision, bool departureSafe = true) => Send(
            "SafetyStateSnapshot",
            State.SessionGeneration,
            new
            {
                safetyStateVersion = revision,
                observedAt = Clock.GetUtcNow(),
                safety = Safety(departureSafe),
                slotStates = Slots()
            });

        public Task<string> SafetyChangedAsync(long revision, bool departureSafe) => Send(
            "SafetyStateChanged",
            State.SessionGeneration,
            new
            {
                safetyStateVersion = revision,
                observedAt = Clock.GetUtcNow(),
                safety = Safety(departureSafe),
                affectedSlots = CargoSlot
            });

        public Task<string> RecoveryReportAsync() => Send(
            "RecoveryStateReport",
            State.SessionGeneration,
            new
            {
                reportId = Guid.NewGuid().ToString("D"),
                unsettledSlotOperationAttemptId = (string?)null,
                provenRecoveryCheckpoint = (string?)null,
                activeUnlockSlots = Array.Empty<int>(),
                forcedRecoveryGeneration = 0,
                pendingResults = Array.Empty<object>()
            });

        private Task<string> Send(string messageType, long? generation, object payload) =>
            Processor.ProcessAsync(
                JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    profileId = ProtocolCandidateIdentity.ProfileId,
                    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    messageType,
                    messageId = Guid.NewGuid().ToString("D"),
                    correlationId = (string?)null,
                    agvId = AgvId,
                    sessionGeneration = generation,
                    sentAt = Clock.GetUtcNow(),
                    payload = JsonSerializer.SerializeToElement(payload, WireJson)
                }),
                State,
                TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
            Environment.SetEnvironmentVariable(CredentialVariable, null);
        }
    }
}
