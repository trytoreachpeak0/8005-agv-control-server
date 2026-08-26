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
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context, store, clock, new ConfigurationBuilder().Build());
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

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    public async Task CoreJourneyCommandsEmitFormalSchemaPayloadsWithExactCorrelationRules()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        RecordingPeer peer = new(context);
        OnboardJourneyPublisher publisher = new(
            new WireToGateStore(context),
            peer,
            new AdvancingTimeProvider());
        const string demandId = "00000000-0000-4000-8000-000000000401";
        const string operationSessionId = "00000000-0000-4000-8000-000000000402";
        const string sublotMessageId = "00000000-0000-4000-8000-000000000403";
        const string slotMessageId = "00000000-0000-4000-8000-000000000404";
        const string safetyMessageId = "00000000-0000-4000-8000-000000000405";
        const string unloadMessageId = "00000000-0000-4000-8000-000000000409";

        await publisher.PublishSublotEntryRequestAsync(
            sublotMessageId,
            "AGV-001",
            11,
            new SublotEntryRequest(demandId, operationSessionId, "PICKUP-01", 5, "SUBLOT-001"),
            TestContext.Current.CancellationToken);
        await publisher.PublishSlotOperationCommandAsync(
            slotMessageId,
            "AGV-001",
            11,
            new SlotOperationCommand(
                sublotMessageId,
                demandId,
                "SUBLOT-001",
                operationSessionId,
                "00000000-0000-4000-8000-000000000406",
                SlotOperationType.Load,
                [1, 3],
                0,
                new string('a', 64)),
            TestContext.Current.CancellationToken);
        await publisher.PublishPreDepartureSafetyCheckAsync(
            safetyMessageId,
            "AGV-001",
            11,
            new PreDepartureSafetyCheckCommand(
                "00000000-0000-4000-8000-000000000407",
                demandId,
                "00000000-0000-4000-8000-000000000408",
                17,
                "GATE-01"),
            TestContext.Current.CancellationToken);
        await publisher.PublishSlotOperationCommandAsync(
            unloadMessageId,
            "AGV-001",
            11,
            new SlotOperationCommand(
                null,
                demandId,
                "SUBLOT-001",
                operationSessionId,
                "00000000-0000-4000-8000-000000000410",
                SlotOperationType.Unload,
                [2, 4],
                0,
                new string('c', 64)),
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4], peer.OutboxCountsAtSend);
        Assert.Equal([0, 1, 1, 2], peer.StationOperationCountsAtSend);
        StationOperationRow[] operations = await context.StationOperations
            .OrderBy(row => row.SlotOperationAttemptId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, operations.Length);
        Assert.Equal(SlotOperationType.Load, operations[0].OperationType);
        Assert.Equal(SlotOperationType.Unload, operations[1].OperationType);
        Assert.All(operations, operation => Assert.Equal("SUBLOT-001", operation.SublotId));
        Assert.Equal(4, peer.Lines.Count);
        using JsonDocument sublotEnvelope = JsonDocument.Parse(peer.Lines[0]);
        using JsonDocument slotEnvelope = JsonDocument.Parse(peer.Lines[1]);
        using JsonDocument safetyEnvelope = JsonDocument.Parse(peer.Lines[2]);
        using JsonDocument unloadEnvelope = JsonDocument.Parse(peer.Lines[3]);
        Assert.Null(sublotEnvelope.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(
            ["SCANNER", "KEYBOARD"],
            sublotEnvelope.RootElement.GetProperty("payload").GetProperty("entryMethods")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.True(sublotEnvelope.RootElement.GetProperty("payload")
            .GetProperty("expiresOnRevisionChange").GetBoolean());
        Assert.Equal(sublotMessageId, slotEnvelope.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("LOAD", slotEnvelope.RootElement.GetProperty("payload").GetProperty("operationType").GetString());
        Assert.Equal(2, slotEnvelope.RootElement.GetProperty("payload").GetProperty("expectedBasketCount").GetInt32());
        Assert.Equal("OCCUPIED", slotEnvelope.RootElement.GetProperty("payload")
            .GetProperty("expectedFinalPhysicalState").GetString());
        Assert.Equal(17, safetyEnvelope.RootElement.GetProperty("payload")
            .GetProperty("expectedSafetyStateVersion").GetInt64());
        Assert.Null(unloadEnvelope.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("UNLOAD", unloadEnvelope.RootElement.GetProperty("payload")
            .GetProperty("operationType").GetString());
        Assert.Equal("EMPTY", unloadEnvelope.RootElement.GetProperty("payload")
            .GetProperty("expectedFinalPhysicalState").GetString());
        Assert.Equal(
            ["SublotEntryRequested", "SlotOperationCommand", "PreDepartureSafetyCheck", "SlotOperationCommand"],
            new[] { sublotEnvelope, slotEnvelope, safetyEnvelope, unloadEnvelope }
                .Select(envelope => envelope.RootElement.GetProperty("messageType").GetString()!)
                .ToArray());
        Assert.All(
            new[] { sublotEnvelope, slotEnvelope, safetyEnvelope, unloadEnvelope },
            envelope =>
            {
                Assert.Equal(ProtocolCandidateIdentity.ReleaseVersion,
                    envelope.RootElement.GetProperty("protocolReleaseVersion").GetString());
                Assert.Equal(ProtocolCandidateIdentity.ManifestSha256,
                    envelope.RootElement.GetProperty("protocolReleaseManifestSha256").GetString());
                Assert.Equal(11, envelope.RootElement.GetProperty("sessionGeneration").GetInt64());
            });
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task DurableCommandIsPersistedBeforeByteExactReplayAndStopsAfterMatchingAck()
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
        const string messageId = "00000000-0000-4000-8000-000000000411";
        SublotEntryRequest request = new(
            "00000000-0000-4000-8000-000000000412",
            "00000000-0000-4000-8000-000000000413",
            "PICKUP-01",
            8,
            "SUBLOT-008");

        await publisher.PublishSublotEntryRequestAsync(
            messageId, "AGV-001", 12, request, TestContext.Current.CancellationToken);
        await publisher.PublishSublotEntryRequestAsync(
            messageId, "AGV-001", 12, request, TestContext.Current.CancellationToken);

        ProtocolOutboxRow row = await context.ProtocolOutbox.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1, 1], peer.OutboxCountsAtSend);
        Assert.Equal(2, peer.Lines.Count);
        Assert.Equal(row.PayloadJson + "\n", peer.Lines[0]);
        Assert.Equal(peer.Lines[0], peer.Lines[1]);
        await Assert.ThrowsAsync<ProtocolContentConflictException>(() =>
            publisher.PublishSublotEntryRequestAsync(
                messageId,
                "AGV-001",
                12,
                request with { ExpectedSublot = "SUBLOT-DIFFERENT" },
                TestContext.Current.CancellationToken));

        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context, store, clock, new ConfigurationBuilder().Build());
        OnboardConnectionState state = new() { AgvId = "AGV-001", SessionGeneration = 12 };
        string response = await processor.ProcessAsync(
            Envelope(
                "DurableAck",
                "00000000-0000-4000-8000-000000000414",
                messageId,
                12,
                new
                {
                    acceptedMessageId = messageId,
                    acceptedMessageType = "SublotEntryRequested",
                    acceptedContentSha256 = Sha256(row.PayloadJson),
                    durablyAcceptedAt = "2026-08-26T08:00:00Z"
                }),
            state,
            TestContext.Current.CancellationToken);

        Assert.Empty(response);
        Assert.NotNull(row.AcknowledgedAt);
        await publisher.PublishSublotEntryRequestAsync(
            messageId, "AGV-001", 12, request, TestContext.Current.CancellationToken);
        Assert.Equal(2, peer.Lines.Count);
    }

    [Fact]
    public async Task SlotOperationRejectsInvalidCorrelationAndSlotOrderBeforePersistence()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        OnboardJourneyPublisher publisher = new(
            new WireToGateStore(context),
            new RecordingPeer(context),
            new AdvancingTimeProvider());
        SlotOperationCommand loadWithoutCorrelation = new(
            null,
            "00000000-0000-4000-8000-000000000421",
            "SUBLOT-001",
            "00000000-0000-4000-8000-000000000422",
            "00000000-0000-4000-8000-000000000423",
            SlotOperationType.Load,
            [1, 2],
            0,
            new string('b', 64));
        SlotOperationCommand unsortedUnload = loadWithoutCorrelation with
        {
            OperationType = SlotOperationType.Unload,
            Slots = [2, 1]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.PublishSlotOperationCommandAsync(
                "00000000-0000-4000-8000-000000000424",
                "AGV-001",
                1,
                loadWithoutCorrelation,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.PublishSlotOperationCommandAsync(
                "00000000-0000-4000-8000-000000000425",
                "AGV-001",
                1,
                unsortedUnload,
                TestContext.Current.CancellationToken));

        Assert.Empty(context.ProtocolOutbox);
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
        public List<int> StationOperationCountsAtSend { get; } = [];

        public async Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            OutboxCountsAtSend.Add(await context.ProtocolOutbox.CountAsync(cancellationToken));
            StationOperationCountsAtSend.Add(await context.StationOperations.CountAsync(cancellationToken));
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
