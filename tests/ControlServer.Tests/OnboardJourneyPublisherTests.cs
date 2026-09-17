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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
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
            "TRANSPORT",
            false,
            "SUFFICIENT",
            "NOT_CHARGING",
            LoadingPhaseProjection.Loading,
            []);

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
        // Read back rather than asserted on `row`: OnboardMessageProcessor clears this context's tracking
        // at the top of every message (8005-agv-control-server#28/#40), so the instance loaded before the
        // call is no longer the one the acknowledgement wrote.
        Assert.Equal(clock.LastReturned, (await context.ProtocolOutbox.SingleAsync(
            item => item.MessageId == messageId, TestContext.Current.CancellationToken)).AcknowledgedAt);

        await publisher.PublishVehicleBusinessStateAsync(
            messageId,
            "AGV-001",
            7,
            projection,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, peer.Lines.Count);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
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
            new VehicleBusinessProjection(3, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", LoadingPhaseProjection.Loading, []),
            TestContext.Current.CancellationToken);
        await publisher.PublishCurrentStopWorklistAsync(
            "00000000-0000-4000-8000-000000000324",
            "AGV-001",
            9,
            new CurrentStopWorklistProjection(
                "PICKUP-01",
                5,
                operationSessionId,
                null,
                [new CurrentStopWorklistItem(
                    demandId, "SUBLOT-001|WIRE_TO_GATE", "SUBLOT-001", "WIRE_TO_GATE", "PICKUP", 2)]),
            TestContext.Current.CancellationToken);
        await publisher.PublishUpcomingStopPlanAsync(
            "00000000-0000-4000-8000-000000000325",
            "AGV-001",
            9,
            new UpcomingStopPlanProjection(
                7,
                [new UpcomingMovementLeg(
                    "00000000-0000-4000-8000-000000000326",
                    "TO_PICKUP",
                    "BUSINESS",
                    demandId,
                    null,
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
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
    public async Task UnchangedSnapshotRepublishesIntoAnAdvancingSessionGeneration()
    {
        // The snapshot keeps one deterministic messageId per journey stage, so after a reconnect
        // the same state is published again under a newer generation. It used to carry a fresh
        // observedAt, which made the payload differ every time and be refused as a semantic
        // conflict -- an arrived journey then looped between reconnects forever. AdvancingTime-
        // Provider moves on every read, so this fails if the timestamp leaks back into the payload.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);
        RecordingPeer peer = new(context);
        OnboardJourneyPublisher publisher = new(store, peer, new AdvancingTimeProvider());
        const string messageId = "00000000-0000-4000-8000-000000000331";
        VehicleBusinessProjection projection = new(2, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", LoadingPhaseProjection.Loading, []);

        await publisher.PublishVehicleBusinessStateAsync(
            messageId, "AGV-001", 1, projection, TestContext.Current.CancellationToken);
        await publisher.PublishVehicleBusinessStateAsync(
            messageId, "AGV-001", 2, projection, TestContext.Current.CancellationToken);
        await publisher.PublishVehicleBusinessStateAsync(
            messageId, "AGV-001", 3, projection, TestContext.Current.CancellationToken);

        ProtocolOutboxRow row = await context.ProtocolOutbox.SingleAsync(
            TestContext.Current.CancellationToken);
        using JsonDocument stored = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(3, stored.RootElement.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(
            stored.RootElement.GetProperty("sentAt").GetDateTimeOffset(),
            stored.RootElement.GetProperty("payload").GetProperty("observedAt").GetDateTimeOffset());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
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
        VehicleBusinessProjection original = new(1, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", LoadingPhaseProjection.Loading, []);
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
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-04")]
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
            new SublotEntryRequest(operationSessionId, "PICKUP-01", 5, ["SUBLOT-001"]),
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
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
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
            "00000000-0000-4000-8000-000000000413",
            "PICKUP-01",
            8,
            ["SUBLOT-008"]);

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
                request with { ExpectedSublots = ["SUBLOT-DIFFERENT"] },
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
        // Read back rather than asserted on `row`: OnboardMessageProcessor clears this context's tracking
        // at the top of every message (8005-agv-control-server#28/#40), so the instance loaded before the
        // call is no longer the one the acknowledgement wrote.
        Assert.NotNull((await context.ProtocolOutbox.SingleAsync(
            item => item.MessageId == messageId, TestContext.Current.CancellationToken)).AcknowledgedAt);
        await publisher.PublishSublotEntryRequestAsync(
            messageId, "AGV-001", 12, request, TestContext.Current.CancellationToken);
        Assert.Equal(2, peer.Lines.Count);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-04")]
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

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task SnapshotWireIsReproducibleByThePeerThatAcknowledgesIt()
    {
        // The acknowledgement requires the peer to reproduce our wire byte for byte, so the
        // hash has to be computed the way the peer computes it -- by parsing our line into the
        // contract envelope, whose payload stays a JsonElement, and re-serialising that. Hashing
        // our own stored bytes only proves we agree with ourselves. observedAt is a
        // DateTimeOffset, and a converter writes the '+' in its offset verbatim while a
        // JsonElement writes it through the encoder, so the two forms used to differ and the
        // first acknowledgement after arrival tore the connection down.
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
        const string messageId = "00000000-0000-4000-8000-000000000341";

        await publisher.PublishVehicleBusinessStateAsync(
            messageId,
            "AGV-001",
            1,
            new VehicleBusinessProjection(6, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", LoadingPhaseProjection.Loading, []),
            TestContext.Current.CancellationToken);

        ProtocolOutboxRow row = await context.ProtocolOutbox.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(Sha256(row.PayloadJson), PeerContentSha256(row.PayloadJson));

        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context, store, clock, new ConfigurationBuilder().Build());
        OnboardConnectionState state = new() { AgvId = "AGV-001", SessionGeneration = 1 };
        string response = await processor.ProcessAsync(
            Envelope(
                "SnapshotAppliedAck",
                "00000000-0000-4000-8000-000000000342",
                messageId,
                1,
                new
                {
                    snapshotMessageId = messageId,
                    snapshotKind = "VEHICLE_BUSINESS_STATE",
                    appliedRevision = 6,
                    appliedContentSha256 = PeerContentSha256(row.PayloadJson)
                }),
            state,
            TestContext.Current.CancellationToken);

        Assert.Empty(response);
        // Read back rather than asserted on `row`: OnboardMessageProcessor clears this context's tracking
        // at the top of every message (8005-agv-control-server#28/#40), so the instance loaded before the
        // call is no longer the one the acknowledgement wrote.
        Assert.NotNull((await context.ProtocolOutbox.SingleAsync(
            item => item.MessageId == messageId, TestContext.Current.CancellationToken)).AcknowledgedAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
    public async Task ReplayIntoANewGenerationLeavesTheWireThePublisherWouldWriteAgain()
    {
        // ReplayPendingForSessionAsync rebinds a pending envelope through JsonNode, which writes
        // every payload string through the encoder. When that landed on different bytes than the
        // publisher produces, the next republish at the same generation was no longer an
        // early-returning no-op: it reached the generation check and was refused, once per runtime
        // iteration, for as long as the journey stayed at the stage.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);
        RecordingPeer peer = new(context);
        OnboardJourneyPublisher publisher = new(store, peer, new AdvancingTimeProvider());
        const string messageId = "00000000-0000-4000-8000-000000000351";
        VehicleBusinessProjection projection = new(6, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", LoadingPhaseProjection.Loading, []);

        await publisher.PublishVehicleBusinessStateAsync(
            messageId, "AGV-001", 1, projection, TestContext.Current.CancellationToken);
        await publisher.ReplayPendingForSessionAsync(
            "AGV-001", 2, new HashSet<string> { messageId }, TestContext.Current.CancellationToken);
        ProtocolOutboxRow row = await context.ProtocolOutbox.SingleAsync(
            TestContext.Current.CancellationToken);
        string replayed = row.PayloadJson;

        await publisher.PublishVehicleBusinessStateAsync(
            messageId, "AGV-001", 2, projection, TestContext.Current.CancellationToken);

        Assert.Equal(replayed, row.PayloadJson);
        Assert.Equal(Sha256(replayed), PeerContentSha256(replayed));
    }

    /// <summary>
    /// The replayed line reaches the observation point, with the bytes the peer is handed.
    /// </summary>
    /// <remarks>
    /// Every other outbound line passes through <c>ProtocolEnvelope.Serialize</c>, which hands itself
    /// to the observer on the way out. This one does not: it rewrites the stored envelope through
    /// <c>JsonNode</c> instead of building it from fields, so it has to hand itself over by hand. Left
    /// out, the schema gate would be blind to every replayed envelope -- the lines a session resume
    /// exists to send.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
    public async Task ReplayIntoANewGenerationHandsTheLineToTheObservationPoint()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        OnboardJourneyPublisher publisher = new(
            new WireToGateStore(context), new RecordingPeer(context), new AdvancingTimeProvider());
        const string messageId = "00000000-0000-4000-8000-000000000352";
        VehicleBusinessProjection projection = new(7, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", LoadingPhaseProjection.Loading, []);
        await publisher.PublishVehicleBusinessStateAsync(
            messageId, "AGV-001", 1, projection, TestContext.Current.CancellationToken);

        // The assembly fixture is watching too; this test observes without taking the gate's eyes off
        // the rest of the run.
        Action<string, string>? previous = ProtocolEnvelope.OutboundObserver;
        List<(string Type, string Line)> observed = [];
        ProtocolEnvelope.OutboundObserver = (type, line) =>
        {
            observed.Add((type, line));
            previous?.Invoke(type, line);
        };
        try
        {
            await publisher.ReplayPendingForSessionAsync(
                "AGV-001", 2, new HashSet<string> { messageId }, TestContext.Current.CancellationToken);
        }
        finally
        {
            ProtocolEnvelope.OutboundObserver = previous;
        }

        (string type, string line) = Assert.Single(observed);
        Assert.Equal("VehicleBusinessStateSnapshot", type);
        ProtocolOutboxRow row = await context.ProtocolOutbox.SingleAsync(
            TestContext.Current.CancellationToken);
        // The rebound bytes, not the ones the first publish wrote: the generation moved to 2, which is
        // the whole point of the rewrite this hook was added around.
        Assert.Equal(row.PayloadJson, line);
        Assert.Equal(2, JsonDocument.Parse(line).RootElement.GetProperty("sessionGeneration").GetInt64());
    }

    /// <summary>
    /// Protocol 2.0.0 items 5 and 6 for a vehicle with no transport journey: <c>loadingPhase</c> is
    /// present and null, and <c>chargingCycleState</c> is still reported.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AVehicleWithoutATransportJourneyReportsANullLoadingPhase()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        RecordingPeer peer = new(context);
        OnboardJourneyPublisher publisher = new(new WireToGateStore(context), peer, new AdvancingTimeProvider());

        await publisher.PublishVehicleBusinessStateAsync(
            "00000000-0000-4000-8000-000000000361",
            "AGV-001",
            1,
            new VehicleBusinessProjection(1, "READY", null, false, "SUFFICIENT", "NOT_CHARGING", null, []),
            TestContext.Current.CancellationToken);

        using JsonDocument envelope = JsonDocument.Parse(peer.Lines.Single());
        JsonElement payload = envelope.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("activePurpose").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("loadingPhase").ValueKind);
        Assert.Equal("NOT_CHARGING", payload.GetProperty("chargingCycleState").GetString());
    }

    /// <summary>
    /// The schema accepts a null loading phase whatever the purpose, so the publisher is where "a
    /// journey never reports null, and nothing else reports a phase" is held. Both mistakes are
    /// refused before anything is persisted or sent.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ThePublisherRefusesAJourneyWithoutALoadingPhaseAndALoadingPhaseWithoutAJourney()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        RecordingPeer peer = new(context);
        OnboardJourneyPublisher publisher = new(new WireToGateStore(context), peer, new AdvancingTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishVehicleBusinessStateAsync(
            "00000000-0000-4000-8000-000000000362",
            "AGV-001",
            1,
            new VehicleBusinessProjection(1, "READY", "TRANSPORT", false, "SUFFICIENT", "NOT_CHARGING", null, []),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishVehicleBusinessStateAsync(
            "00000000-0000-4000-8000-000000000363",
            "AGV-001",
            1,
            new VehicleBusinessProjection(
                1, "READY", null, false, "SUFFICIENT", "NOT_CHARGING", LoadingPhaseProjection.Loading, []),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishVehicleBusinessStateAsync(
            "00000000-0000-4000-8000-000000000364",
            "AGV-001",
            1,
            new VehicleBusinessProjection(
                1, "READY", "TRANSPORT", false, "SUFFICIENT", "IDLE", LoadingPhaseProjection.Loading, []),
            TestContext.Current.CancellationToken));

        Assert.Empty(peer.Lines);
        Assert.Empty(await context.ProtocolOutbox.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <c>expectedSublots</c> within the schema's bounds: one to eight, no repeats.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-2)]
    public async Task ASublotEntryRequestOutsideTheSchemasBoundsIsRefused(int count)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        RecordingPeer peer = new(context);
        OnboardJourneyPublisher publisher = new(new WireToGateStore(context), peer, new AdvancingTimeProvider());
        // -2 stands for two entries that repeat one sublot.
        string[] sublots = count < 0
            ? ["SUBLOT-001", "SUBLOT-001"]
            : [.. Enumerable.Range(1, count).Select(index => $"SUBLOT-{index:D3}")];

        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishSublotEntryRequestAsync(
            "00000000-0000-4000-8000-000000000365",
            "AGV-001",
            1,
            new SublotEntryRequest("00000000-0000-4000-8000-000000000366", "PICKUP-01", 1, sublots),
            TestContext.Current.CancellationToken));
        Assert.Empty(peer.Lines);
    }

    /// <summary>
    /// Hashes a wire line the way the peer does: parse it into the contract envelope, whose
    /// payload stays a JsonElement, and re-serialise that.
    /// </summary>
    private static string PeerContentSha256(string wireLine) =>
        Sha256(JsonSerializer.Serialize(
            JsonSerializer.Deserialize<PeerEnvelope>(wireLine, PeerSerializerOptions)!,
            PeerSerializerOptions));

    private static readonly JsonSerializerOptions PeerSerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed record PeerEnvelope(
        int ProtocolVersion,
        string ProfileId,
        string ProtocolReleaseVersion,
        string ProtocolReleaseManifestSha256,
        string MessageType,
        string MessageId,
        string? CorrelationId,
        string AgvId,
        long? SessionGeneration,
        DateTimeOffset SentAt,
        JsonElement Payload);

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
