using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

public sealed class OnboardJourneyPublisherTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task JourneySnapshotIsPersistedBeforeSendReplayedByteForByteAndAcknowledgedByExactHash()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);
        RecordingPeer peer = new(context);
        AdvancingTimeProvider clock = new();
        OnboardJourneyPublisher publisher = new(store, peer, clock);
        const string messageId = "00000000-0000-4000-8000-000000000301";
        VehicleBusinessProjection projection = new(
            4,
            "READY",
            false,
            "SUFFICIENT",
            [],
            clock.GetUtcNow());

        await publisher.PublishVehicleBusinessStateAsync(
            messageId,
            "AGV-001",
            7,
            projection,
            TestContext.Current.CancellationToken);
        await publisher.PublishVehicleBusinessStateAsync(
            messageId,
            "AGV-001",
            7,
            projection,
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 1], peer.OutboxCountsAtSend);
        Assert.Equal(2, peer.Lines.Count);
        Assert.Equal(peer.Lines[0], peer.Lines[1]);
        ProtocolOutboxRow row = await context.ProtocolOutbox.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(row.PayloadJson + "\n", peer.Lines[0]);
        Assert.Null(row.AcknowledgedAt);

        string contentHash = Sha256(row.PayloadJson);
        OnboardMessageProcessor processor = new(
            store,
            clock,
            new ConfigurationBuilder().Build());
        OnboardConnectionState state = new()
        {
            AgvId = "AGV-001",
            SessionGeneration = 7
        };
        string response = await processor.ProcessAsync(
            Envelope(
                "SnapshotAppliedAck",
                "00000000-0000-4000-8000-000000000302",
                messageId,
                7,
                new
                {
                    snapshotMessageId = messageId,
                    snapshotKind = "VEHICLE_BUSINESS_STATE",
                    appliedRevision = 4,
                    appliedContentSha256 = contentHash
                }),
            state,
            TestContext.Current.CancellationToken);

        Assert.Empty(response);
        Assert.Equal(clock.LastReturned, row.AcknowledgedAt);

        await publisher.PublishVehicleBusinessStateAsync(
            messageId,
            "AGV-001",
            7,
            projection,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, peer.Lines.Count);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AllJourneyProjectionKindsEmitFormalReleaseEnvelopesAndPersistExactWire()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        RecordingPeer peer = new(context);
        AdvancingTimeProvider clock = new();
        OnboardJourneyPublisher publisher = new(new WireToGateStore(context), peer, clock);
        const string demandId = "00000000-0000-4000-8000-000000000321";
        const string operationSessionId = "00000000-0000-4000-8000-000000000322";

        await publisher.PublishVehicleBusinessStateAsync(
            "00000000-0000-4000-8000-000000000323",
            "AGV-001",
            9,
            new VehicleBusinessProjection(3, "READY", false, "SUFFICIENT", [], clock.GetUtcNow()),
            TestContext.Current.CancellationToken);
        await publisher.PublishCurrentStopWorklistAsync(
            "00000000-0000-4000-8000-000000000324",
            "AGV-001",
            9,
            new CurrentStopWorklistProjection(
                "PICKUP-01",
                5,
                operationSessionId,
                [new CurrentStopWorklistItem(
                    demandId, "SUBLOT-001|WIRE_TO_GATE", "SUBLOT-001", "WIRE_TO_GATE", "PICKUP", 2)]),
            TestContext.Current.CancellationToken);
        await publisher.PublishUpcomingStopPlanAsync(
            "00000000-0000-4000-8000-000000000325",
            "AGV-001",
            9,
            new UpcomingStopPlanProjection(
                7,
                demandId,
                [new UpcomingMovementLeg(
                    "00000000-0000-4000-8000-000000000326",
                    "TO_PICKUP",
                    1,
                    "PICKUP-01",
                    "25",
                    "PLANNED")]),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, await context.ProtocolOutbox.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, peer.Lines.Count);
        (string Type, string RevisionName, long Revision)[] expected =
        [
            ("VehicleBusinessStateSnapshot", "vehicleBusinessStateRevision", 3),
            ("CurrentStopWorklistSnapshot", "worklistRevision", 5),
            ("UpcomingStopPlanSnapshot", "planRevision", 7)
        ];
        for (int index = 0; index < expected.Length; index++)
        {
            using JsonDocument envelope = JsonDocument.Parse(peer.Lines[index]);
            JsonElement root = envelope.RootElement;
            Assert.Equal(ProtocolCandidateIdentity.ReleaseVersion,
                root.GetProperty("protocolReleaseVersion").GetString());
            Assert.Equal(ProtocolCandidateIdentity.ManifestSha256,
                root.GetProperty("protocolReleaseManifestSha256").GetString());
            Assert.Equal(expected[index].Type, root.GetProperty("messageType").GetString());
            Assert.Equal(expected[index].Revision,
                root.GetProperty("payload").GetProperty(expected[index].RevisionName).GetInt64());
            Assert.Equal(9, root.GetProperty("sessionGeneration").GetInt64());
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task SnapshotReplayWithDifferentContentOrAcknowledgementHashIsRejected()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);
        RecordingPeer peer = new(context);
        AdvancingTimeProvider clock = new();
        OnboardJourneyPublisher publisher = new(store, peer, clock);
        const string messageId = "00000000-0000-4000-8000-000000000311";
        VehicleBusinessProjection original = new(1, "READY", false, "SUFFICIENT", [], clock.GetUtcNow());
        await publisher.PublishVehicleBusinessStateAsync(
            messageId, "AGV-001", 1, original, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProtocolContentConflictException>(() =>
            publisher.PublishVehicleBusinessStateAsync(
                messageId,
                "AGV-001",
                1,
                original with { BatteryState = "LOW" },
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ProtocolContentConflictException>(() =>
            store.AcknowledgeOutboundEnvelopeAsync(
                messageId,
                "VehicleBusinessStateSnapshot",
                new string('0', 64),
                appliedRevision: 1,
                clock.GetUtcNow(),
                TestContext.Current.CancellationToken));
        ProtocolOutboxRow row = await context.ProtocolOutbox.SingleAsync(
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ProtocolContentConflictException>(() =>
            store.AcknowledgeOutboundEnvelopeAsync(
                messageId,
                "VehicleBusinessStateSnapshot",
                Sha256(row.PayloadJson),
                appliedRevision: 2,
                clock.GetUtcNow(),
                TestContext.Current.CancellationToken));

        Assert.Single(peer.Lines);
        Assert.Null(row.AcknowledgedAt);
    }

    private static string Envelope(
        string messageType,
        string messageId,
        string? correlationId,
        long generation,
        object payload) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId,
            agvId = "AGV-001",
            sessionGeneration = generation,
            sentAt = "2026-08-26T08:00:00Z",
            payload
        });

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingPeer(ControlServerDbContext context) : IOnboardPeer
    {
        public List<string> Lines { get; } = [];
        public List<int> OutboxCountsAtSend { get; } = [];

        public async Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            OutboxCountsAtSend.Add(await context.ProtocolOutbox.CountAsync(cancellationToken));
            Lines.Add(Encoding.UTF8.GetString(ndjsonLine.Span));
        }
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _seconds;

        public DateTimeOffset LastReturned { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            LastReturned = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero)
                .AddSeconds(Interlocked.Increment(ref _seconds));
            return LastReturned;
        }
    }
}
