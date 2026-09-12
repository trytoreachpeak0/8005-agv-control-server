using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

public sealed class OnboardMessageProcessorTests
{
    private static readonly int[] FirstTwoSlots = [1, 2];

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task ProtocolProblemIsRecordedWithoutAnsweringOrDroppingTheSession()
    {
        // protocol-v0.1.1 defines ProtocolProblem as how the peer reports a rejection. Throwing
        // on it killed the transport, so the peer reconnected in a loop and the complaint that
        // explained the rejection was lost.
        const string credentialVariable = "CONTROL_SERVER_TEST_ONBOARD_PROBLEM_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
            OnboardConnectionState state = new();

            await processor.ProcessAsync(
                Envelope(
                    "SessionHello",
                    "00000000-0000-4000-8000-000000000090",
                    null,
                    new
                    {
                        protocolReleaseIdentity = ReleaseIdentity(),
                        credentialProof = credential
                    }),
                state,
                TestContext.Current.CancellationToken);

            string response = await processor.ProcessAsync(
                Envelope(
                    "ProtocolProblem",
                    "00000000-0000-4000-8000-000000000091",
                    state.SessionGeneration,
                    new
                    {
                        rejectedMessageId = "00000000-0000-4000-8000-000000000092",
                        rejectedMessageType = (string?)null,
                        problem = new
                        {
                            reasonCode = "W2G-PROTOCOL-0001",
                            fieldPath = (string?)null,
                            displayMessage = (string?)null
                        },
                        expectedProtocolVersion = 1,
                        expectedProfileId = "WIRE_TO_GATE_MVP",
                        expectedProtocolReleaseManifestSha256 = new string('a', 64)
                    }),
                state,
                TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, response);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task SnapshotAppliedAcksUseExactWireContentHashAndProtocolKinds()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_ONBOARD_WIRE_HASH_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
            OnboardConnectionState state = new();

            await processor.ProcessAsync(
                Envelope(
                    "SessionHello",
                    "00000000-0000-4000-8000-000000000010",
                    null,
                    new
                    {
                        protocolReleaseIdentity = ReleaseIdentity(),
                        credentialProof = credential
                    }),
                state,
                TestContext.Current.CancellationToken);
            string snapshot = Envelope(
                "CapabilitySnapshot",
                "00000000-0000-4000-8000-000000000011",
                state.SessionGeneration,
                new { capabilityVersion = 1 });

            string capabilityResponse = await processor.ProcessAsync(
                snapshot, state, TestContext.Current.CancellationToken);

            using JsonDocument capabilityAcknowledgement = JsonDocument.Parse(capabilityResponse);
            JsonElement capabilityPayload = capabilityAcknowledgement.RootElement.GetProperty("payload");
            string expectedHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)))
                .ToLowerInvariant();
            Assert.Equal(expectedHash, capabilityPayload.GetProperty("appliedContentSha256").GetString());
            Assert.Equal("CAPABILITY", capabilityPayload.GetProperty("snapshotKind").GetString());
            Assert.Equal(
                "00000000-0000-4000-8000-000000000011",
                capabilityPayload.GetProperty("snapshotMessageId").GetString());

            string safetySnapshot = Envelope(
                "SafetyStateSnapshot",
                "00000000-0000-4000-8000-000000000012",
                state.SessionGeneration,
                new { safetyStateVersion = 1, safety = new { departureSafe = false } });
            string safetyResponse = await processor.ProcessAsync(
                safetySnapshot, state, TestContext.Current.CancellationToken);

            using JsonDocument safetyAcknowledgement = JsonDocument.Parse(safetyResponse);
            JsonElement safetyPayload = safetyAcknowledgement.RootElement.GetProperty("payload");
            string expectedSafetyHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(safetySnapshot)))
                .ToLowerInvariant();
            Assert.Equal(expectedSafetyHash, safetyPayload.GetProperty("appliedContentSha256").GetString());
            Assert.Equal("SAFETY_STATE", safetyPayload.GetProperty("snapshotKind").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task CandidateHandshakeReachesReadyAndReplaysSessionResponse()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_ONBOARD_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable,
                    ["ControlServerBuild:commit"] = "TEST_BUILD"
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
            OnboardConnectionState state = new();

            string hello = Envelope(
                "SessionHello", "00000000-0000-4000-8000-000000000001", null,
                new
                {
                    protocolReleaseIdentity = ReleaseIdentity(),
                    credentialProof = credential
                });
            string accepted = await processor.ProcessAsync(
                hello, state, TestContext.Current.CancellationToken);
            string replay = await processor.ProcessAsync(
                hello, new OnboardConnectionState(), TestContext.Current.CancellationToken);
            Assert.Equal(accepted, replay);
            ProtocolInboxRow storedHello = await context.ProtocolInbox.SingleAsync(
                row => row.MessageId == "00000000-0000-4000-8000-000000000001",
                TestContext.Current.CancellationToken);
            Assert.Equal("SessionHello", storedHello.MessageType);
            Assert.DoesNotContain(credential, storedHello.RequestJson, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", storedHello.RequestJson, StringComparison.Ordinal);

            long generation = state.SessionGeneration!.Value;
            await processor.ProcessAsync(
                Envelope("CapabilitySnapshot", "00000000-0000-4000-8000-000000000002", generation,
                    new { capabilityVersion = 1 }),
                state, TestContext.Current.CancellationToken);
            await processor.ProcessAsync(
                Envelope("SafetyStateSnapshot", "00000000-0000-4000-8000-000000000003", generation,
                    new { safetyStateVersion = 1, safety = new { departureSafe = true } }),
                state, TestContext.Current.CancellationToken);
            string recovery = await processor.ProcessAsync(
                Envelope("RecoveryStateReport", "00000000-0000-4000-8000-000000000004", generation,
                    new
                    {
                        reportId = "00000000-0000-4000-8000-000000000005",
                        unsettledSlotOperationAttemptId = (string?)null,
                        provenRecoveryCheckpoint = (string?)null,
                        activeUnlockSlots = Array.Empty<int>(),
                        forcedRecoveryGeneration = 0,
                        pendingResults = Array.Empty<object>()
                    }),
                state, TestContext.Current.CancellationToken);

            string readinessLine = recovery.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
            using JsonDocument readiness = JsonDocument.Parse(readinessLine);
            Assert.Equal("READY", readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString());
            Assert.Equal(SessionReadiness.Ready,
                (await context.SessionRecoveries.SingleAsync(TestContext.Current.CancellationToken)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task RecoveryStateReportAckDropRebindsAcrossSessionWithoutContentConflict()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_RECOVERY_REBIND_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);

            OnboardConnectionState firstState = new();
            await processor.ProcessAsync(
                Envelope(
                    "SessionHello",
                    "00000000-0000-4000-8000-000000000021",
                    null,
                    new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = credential }),
                firstState,
                TestContext.Current.CancellationToken);
            long firstGeneration = firstState.SessionGeneration!.Value;
            await processor.ProcessAsync(
                Envelope(
                    "CapabilitySnapshot",
                    "00000000-0000-4000-8000-000000000025",
                    firstGeneration,
                    new { capabilityVersion = 1 }),
                firstState,
                TestContext.Current.CancellationToken);
            await processor.ProcessAsync(
                Envelope(
                    "SafetyStateSnapshot",
                    "00000000-0000-4000-8000-000000000026",
                    firstGeneration,
                    new { safetyStateVersion = 1, safety = new { departureSafe = false } }),
                firstState,
                TestContext.Current.CancellationToken);
            string firstReport = Envelope(
                "RecoveryStateReport",
                "00000000-0000-4000-8000-000000000022",
                firstGeneration,
                new
                {
                    reportId = "00000000-0000-4000-8000-000000000023",
                    unsettledSlotOperationAttemptId = (string?)null,
                    provenRecoveryCheckpoint = "NONE",
                    activeUnlockSlots = Array.Empty<int>(),
                    forcedRecoveryGeneration = 0,
                    pendingResults = Array.Empty<object>()
                });
            string firstResponse = await processor.ProcessAsync(
                firstReport,
                firstState,
                TestContext.Current.CancellationToken);

            OnboardConnectionState reboundState = new();
            await processor.ProcessAsync(
                Envelope(
                    "SessionHello",
                    "00000000-0000-4000-8000-000000000024",
                    null,
                    new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = credential }),
                reboundState,
                TestContext.Current.CancellationToken);
            long reboundGeneration = reboundState.SessionGeneration!.Value;
            JsonNode reboundNode = JsonNode.Parse(firstReport)!;
            reboundNode["sessionGeneration"] = reboundGeneration;
            string reboundReport = reboundNode.ToJsonString();
            string reboundResponse = await processor.ProcessAsync(
                reboundReport,
                reboundState,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(firstResponse, reboundResponse);
            string reboundAcknowledgement = reboundResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
            using JsonDocument acknowledgement = JsonDocument.Parse(reboundAcknowledgement);
            Assert.Equal(reboundGeneration, acknowledgement.RootElement.GetProperty("sessionGeneration").GetInt64());
            Assert.Equal(
                WireContentHash(reboundReport),
                acknowledgement.RootElement.GetProperty("payload")
                    .GetProperty("acceptedContentSha256").GetString());
            string reboundReadiness = reboundResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
            using JsonDocument readiness = JsonDocument.Parse(reboundReadiness);
            Assert.Equal(1, readiness.RootElement.GetProperty("payload")
                .GetProperty("acceptedCapabilityVersion").GetInt64());
            Assert.Equal(1, readiness.RootElement.GetProperty("payload")
                .GetProperty("acceptedSafetyStateVersion").GetInt64());
            Assert.Equal(SessionReadiness.RecoveryRequired, (await context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken)).Readiness);

            ProtocolInboxRow stored = await context.ProtocolInbox.SingleAsync(
                row => row.MessageId == "00000000-0000-4000-8000-000000000022",
                TestContext.Current.CancellationToken);
            Assert.Equal(WireContentHash(reboundReport), stored.ContentHash);
            Assert.Equal(reboundReport, stored.RequestJson);
            Assert.Equal(reboundResponse, stored.FirstResponseJson);
            Assert.Equal(reboundGeneration, (await context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken)).SessionGeneration);

            JsonNode conflictingNode = JsonNode.Parse(reboundReport)!;
            conflictingNode["payload"]!["forcedRecoveryGeneration"] = 1;
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                conflictingNode.ToJsonString(),
                reboundState,
                TestContext.Current.CancellationToken));
            Assert.Single(await context.ProtocolInbox.Where(
                row => row.MessageId == "00000000-0000-4000-8000-000000000022")
                .ToArrayAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task OnboardBusinessMessagesAreDurablyAcknowledgedAndOperationResultIsUniquePerAttempt()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_BUSINESS_MESSAGE_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
            OnboardConnectionState state = new();
            await ReachReadyAsync(processor, state, credential, TestContext.Current.CancellationToken);

            const string attemptId = "00000000-0000-4000-8000-000000000120";
            const string demandId = "00000000-0000-4000-8000-000000000111";
            await store.AcceptWithOrderIntentAsync(
                new AcceptedDemandSnapshot(
                    demandId,
                    "SUBLOT-001|WIRE_TO_GATE",
                    7,
                    "00000000-0000-4000-8000-000000000199",
                    21,
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                new OrderIntent(
                    "00000000-0000-4000-8000-000000000198",
                    demandId,
                    "W2G-D-111-PICKUP-1",
                    "TO_PICKUP",
                    "PICKUP-01",
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                TestContext.Current.CancellationToken);
            await store.PrepareSlotOperationAsync(
                new StationOperationPlan(
                    attemptId,
                    demandId,
                    "SUBLOT-001",
                    [1],
                    SlotOperationType.Load,
                    0,
                    new string('b', 64),
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                "00000000-0000-4000-8000-000000000119",
                "load-command-json",
                TestContext.Current.CancellationToken);
            (string MessageType, string MessageId, object Payload)[] messages =
            [
                ("SublotSubmitted", "00000000-0000-4000-8000-000000000101", new
                {
                    demandId,
                    operationSessionId = "00000000-0000-4000-8000-000000000112",
                    stationId = "PICKUP-01",
                    worklistRevision = 2,
                    sublot = "SUBLOT-001",
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = "2026-08-25T09:00:00Z"
                    }
                }),
                ("OperationProgress", "00000000-0000-4000-8000-000000000102", new
                {
                    slotOperationAttemptId = attemptId,
                    phase = "VERIFYING",
                    activeUnlockSlots = Array.Empty<int>(),
                    completedSlots = new[] { 1, 2 },
                    observedAt = "2026-08-25T09:00:00Z"
                }),
                ("PreDepartureSafetyCheckResult", "00000000-0000-4000-8000-000000000103", new
                {
                    preDepartureSafetyCheckId = "00000000-0000-4000-8000-000000000113",
                    outcome = "SAFE",
                    observedAt = "2026-08-25T09:00:00Z",
                    safetyStateVersion = 1,
                    validUntil = "2026-08-25T09:00:02Z",
                    safety = Safety(departureSafe: true)
                }),
                ("SlotOperationCommandRejected", "00000000-0000-4000-8000-000000000104", new
                {
                    slotOperationAttemptId = attemptId,
                    problem = new
                    {
                        reasonCode = "ACTION_NOT_ALLOWED_IN_STATE",
                        fieldPath = (string?)null,
                        displayMessage = (string?)null
                    },
                    observedCapabilityVersion = 1,
                    conflictingContentSha256 = (string?)null
                }),
                ("OperationResult", attemptId, OperationResultPayload(
                    demandId,
                    attemptId,
                    "LOAD",
                    "OCCUPIED"))
            ];

            foreach ((string messageType, string messageId, object payload) in messages)
            {
                string line = Envelope(messageType, messageId, state.SessionGeneration, payload);
                string response = await processor.ProcessAsync(
                    line, state, TestContext.Current.CancellationToken);
                using JsonDocument acknowledgement = JsonDocument.Parse(response);
                JsonElement ackPayload = acknowledgement.RootElement.GetProperty("payload");
                Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
                Assert.Equal(messageId, ackPayload.GetProperty("acceptedMessageId").GetString());
                Assert.Equal(messageType, ackPayload.GetProperty("acceptedMessageType").GetString());
                Assert.Equal(WireContentHash(line), ackPayload.GetProperty("acceptedContentSha256").GetString());

                ProtocolInboxRow inbox = await context.ProtocolInbox.SingleAsync(
                    row => row.MessageId == messageId,
                    TestContext.Current.CancellationToken);
                Assert.Equal(messageType, inbox.MessageType);
                Assert.Equal(line, inbox.RequestJson);
            }

            string operationResult = Envelope(
                messages[^1].MessageType,
                messages[^1].MessageId,
                state.SessionGeneration,
                messages[^1].Payload);
            string replay = await processor.ProcessAsync(
                operationResult, state, TestContext.Current.CancellationToken);
            Assert.Equal(
                (await context.ProtocolInbox.SingleAsync(
                    row => row.MessageId == attemptId,
                    TestContext.Current.CancellationToken)).FirstResponseJson,
                replay);
            Assert.Single(await context.OperationResults.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                StationOperationStatus.Committed,
                (await context.StationOperations.SingleAsync(
                    row => row.SlotOperationAttemptId == attemptId,
                    TestContext.Current.CancellationToken)).Status);

            string conflictingResult = Envelope(
                "OperationResult",
                "00000000-0000-4000-8000-000000000121",
                state.SessionGeneration,
                messages[^1].Payload);
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                conflictingResult, state, TestContext.Current.CancellationToken));
            Assert.Single(await context.OperationResults.ToListAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    /// <summary>
    /// The vehicle's side of a lost DurableAck (8005-agv-control-server#30). The onboard keeps every
    /// durable message in its journal until the ack arrives; on the next connection it replays each
    /// one straight after SessionAccepted -- before any snapshot, see ConnectAndRecoverAsync -- with
    /// the same messageId, the same sentAt and the same payload, rebound to the new session
    /// generation (WireToGateProtocolSerializer.RebindSessionGeneration). ADR-cross-0030 requires
    /// exactly that: a reconnect resend keeps the original messageId.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task DurableMessagesReplayedIntoANewSessionAfterALostAckAreAcknowledgedOnce()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_DURABLE_REPLAY_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
            OnboardConnectionState firstState = new();
            await ReachReadyAsync(processor, firstState, credential, TestContext.Current.CancellationToken);
            long firstGeneration = firstState.SessionGeneration!.Value;

            const string attemptId = "00000000-0000-4000-8000-000000000520";
            const string demandId = "00000000-0000-4000-8000-000000000511";
            await store.AcceptWithOrderIntentAsync(
                new AcceptedDemandSnapshot(
                    demandId,
                    "SUBLOT-005|WIRE_TO_GATE",
                    7,
                    "00000000-0000-4000-8000-000000000599",
                    21,
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                new OrderIntent(
                    "00000000-0000-4000-8000-000000000598",
                    demandId,
                    "W2G-D-511-PICKUP-1",
                    "TO_PICKUP",
                    "PICKUP-01",
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                TestContext.Current.CancellationToken);
            await store.PrepareSlotOperationAsync(
                new StationOperationPlan(
                    attemptId,
                    demandId,
                    "SUBLOT-005",
                    [1],
                    SlotOperationType.Load,
                    0,
                    new string('b', 64),
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                "00000000-0000-4000-8000-000000000519",
                "load-command-json",
                TestContext.Current.CancellationToken);
            (string MessageType, string MessageId, object Payload)[] messages =
            [
                ("SublotSubmitted", "00000000-0000-4000-8000-000000000501", new
                {
                    demandId,
                    operationSessionId = "00000000-0000-4000-8000-000000000512",
                    stationId = "PICKUP-01",
                    worklistRevision = 2,
                    sublot = "SUBLOT-005",
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = "2026-08-25T09:00:00Z"
                    }
                }),
                ("OperationProgress", "00000000-0000-4000-8000-000000000502", new
                {
                    slotOperationAttemptId = attemptId,
                    phase = "VERIFYING",
                    activeUnlockSlots = Array.Empty<int>(),
                    completedSlots = new[] { 1 },
                    observedAt = "2026-08-25T09:00:00Z"
                }),
                ("PreDepartureSafetyCheckResult", "00000000-0000-4000-8000-000000000503", new
                {
                    preDepartureSafetyCheckId = "00000000-0000-4000-8000-000000000513",
                    outcome = "SAFE",
                    observedAt = "2026-08-25T09:00:00Z",
                    safetyStateVersion = 1,
                    validUntil = "2026-08-25T09:00:02Z",
                    safety = Safety(departureSafe: true)
                }),
                ("OperationResult", attemptId, OperationResultPayload(
                    demandId,
                    attemptId,
                    "LOAD",
                    "OCCUPIED"))
            ];

            // First session: every message is durably accepted. The acks are what get lost.
            Dictionary<string, string> firstLines = new(StringComparer.Ordinal);
            foreach ((string messageType, string messageId, object payload) in messages)
            {
                string line = Envelope(messageType, messageId, firstGeneration, payload);
                firstLines[messageId] = line;
                await processor.ProcessAsync(line, firstState, TestContext.Current.CancellationToken);
            }

            // Second session: SessionAccepted, then the journal replay -- no snapshots in between.
            OnboardConnectionState secondState = new();
            await processor.ProcessAsync(
                Envelope(
                    "SessionHello",
                    Guid.NewGuid().ToString("D"),
                    null,
                    new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = credential }),
                secondState,
                TestContext.Current.CancellationToken);
            long secondGeneration = secondState.SessionGeneration!.Value;
            Assert.NotEqual(firstGeneration, secondGeneration);

            foreach ((string messageType, string messageId, object payload) in messages)
            {
                string rebound = Envelope(messageType, messageId, secondGeneration, payload);
                string response = await processor.ProcessAsync(
                    rebound, secondState, TestContext.Current.CancellationToken);

                string ackLine = response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
                using JsonDocument acknowledgement = JsonDocument.Parse(ackLine);
                JsonElement root = acknowledgement.RootElement;
                JsonElement ackPayload = root.GetProperty("payload");
                Assert.Equal("DurableAck", root.GetProperty("messageType").GetString());
                Assert.Equal(secondGeneration, root.GetProperty("sessionGeneration").GetInt64());
                Assert.Equal(messageId, ackPayload.GetProperty("acceptedMessageId").GetString());
                Assert.Equal(messageType, ackPayload.GetProperty("acceptedMessageType").GetString());
                // The onboard compares this with the hash of the line it just sent, not the first one.
                Assert.Equal(WireContentHash(rebound), ackPayload.GetProperty("acceptedContentSha256").GetString());
                // Answered from the first acceptance, not processed again: the row still holds the
                // bytes that were accepted.
                ProtocolInboxRow stored = await context.ProtocolInbox.SingleAsync(
                    row => row.MessageId == messageId,
                    TestContext.Current.CancellationToken);
                Assert.Equal(firstLines[messageId], stored.RequestJson);
            }

            Assert.Single(await context.OperationResults.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                StationOperationStatus.Committed,
                (await context.StationOperations.SingleAsync(
                    row => row.SlotOperationAttemptId == attemptId,
                    TestContext.Current.CancellationToken)).Status);

            // Only the session generation may differ. The same messageId with other content is still
            // a conflict, in the new session as in the old one.
            string conflicting = Envelope(
                "OperationResult",
                attemptId,
                secondGeneration,
                OperationResultPayload(demandId, attemptId, "LOAD", "EMPTY"));
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                conflicting, secondState, TestContext.Current.CancellationToken));
            Assert.Single(await context.OperationResults.ToListAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task CompletedUnloadResultAtomicallyClosesDemandBeforeDurableAck()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);
        const string demandId = "00000000-0000-4000-8000-000000000301";
        const string attemptId = "00000000-0000-4000-8000-000000000302";
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot(
                demandId,
                "SUBLOT-301|WIRE_TO_GATE",
                9,
                "00000000-0000-4000-8000-000000000303",
                24,
                now),
            new OrderIntent(
                "00000000-0000-4000-8000-000000000304",
                demandId,
                "W2G-D-301-PICKUP-1",
                "TO_PICKUP",
                "PICKUP-01",
                now),
            TestContext.Current.CancellationToken);
        await store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                attemptId,
                demandId,
                "SUBLOT-301",
                [1, 2],
                SlotOperationType.Unload,
                0,
                new string('c', 64),
                now),
            "00000000-0000-4000-8000-000000000305",
            "unload-command-json",
            TestContext.Current.CancellationToken);
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context, store, new FixedTimeProvider(), new ConfigurationBuilder().Build());
        OnboardConnectionState state = new()
        {
            AgvId = "AGV-001",
            SessionGeneration = 1,
            Readiness = SessionReadiness.Ready
        };
        await store.BeginSessionRecoveryAsync(
            new SessionIdentity(
                "AGV-001",
                1,
                ProtocolCandidateIdentity.RepositoryCommit,
                ProtocolCandidateIdentity.ManifestSha256,
                ProtocolCandidateIdentity.ProfileId,
                ProtocolCandidateIdentity.ProtocolVersion),
            TestContext.Current.CancellationToken);
        string line = Envelope(
            "OperationResult",
            attemptId,
            1,
            OperationResultPayload(demandId, attemptId, "UNLOAD", "EMPTY", [1, 2]));

        string response = await processor.ProcessAsync(
            line,
            state,
            TestContext.Current.CancellationToken);

        // First line, not the whole response: a result the server refuses now carries a
        // SessionReadiness line after the ack. This fixture's stored session is not ready
        // (BeginSessionRecoveryAsync leaves no recovery report), so it gets one even though
        // the unload itself succeeded. What this test is about is that the demand closed
        // before the ack, which the first line is.
        using JsonDocument acknowledgement = JsonDocument.Parse(
            response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(WireContentHash(line), acknowledgement.RootElement.GetProperty("payload")
            .GetProperty("acceptedContentSha256").GetString());
        Assert.Equal(1, await context.UnloadBatches.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await context.StopClosures.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            DemandExecutionStatus.Succeeded,
            (await context.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            StationOperationStatus.Committed,
            (await context.StationOperations.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, await context.ProtocolInbox.CountAsync(
            row => row.MessageId == attemptId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task UnsafeSafetyStateChangeIsAcknowledgedThenFailClosesSessionReadiness()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_SAFETY_CHANGE_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
            OnboardConnectionState state = new();
            await ReachReadyAsync(processor, state, credential, TestContext.Current.CancellationToken);
            string line = Envelope(
                "SafetyStateChanged",
                "00000000-0000-4000-8000-000000000201",
                state.SessionGeneration,
                new
                {
                    safetyStateVersion = 2,
                    observedAt = "2026-08-25T09:00:00Z",
                    safety = Safety(departureSafe: false),
                    affectedSlots = FirstTwoSlots
                });

            string response = await processor.ProcessAsync(
                line, state, TestContext.Current.CancellationToken);

            string[] responseLines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, responseLines.Length);
            using JsonDocument acknowledgement = JsonDocument.Parse(responseLines[0]);
            Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
            Assert.Equal(
                WireContentHash(line),
                acknowledgement.RootElement.GetProperty("payload").GetProperty("acceptedContentSha256").GetString());
            using JsonDocument readiness = JsonDocument.Parse(responseLines[1]);
            Assert.Equal("SessionReadiness", readiness.RootElement.GetProperty("messageType").GetString());
            Assert.Equal(
                "RECOVERY_REQUIRED",
                readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString());
            // On the wire this must be a protocol ErrorCode. It used to be the server's own
            // "DEPARTURE_SAFETY_NOT_READY", which is not one of the 43 the schema allows.
            Assert.Equal(
                "DEPARTURE_UNSAFE",
                Assert.Single(readiness.RootElement.GetProperty("payload").GetProperty("reasonCodes")
                    .EnumerateArray()).GetString());
            // The richer internal reason survives where the diagnosis is actually read.
            Assert.Equal(
                "DEPARTURE_SAFETY_NOT_READY",
                (await context.SessionRecoveries.SingleAsync(TestContext.Current.CancellationToken))
                    .ReasonCode);

            SessionRecoveryRow stored = await context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(2, stored.SafetyRevision);
            Assert.False(stored.DepartureSafe);
            Assert.Equal(SessionReadiness.RecoveryRequired, stored.Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task SafeSafetyStateChangeAcknowledgesAndPublishesReadyTransition()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_SAFE_CHANGE_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(), configuration);
            OnboardConnectionState state = new();
            await ReachReadyAsync(processor, state, credential, TestContext.Current.CancellationToken);
            await processor.ProcessAsync(
                Envelope(
                    "SafetyStateChanged",
                    "00000000-0000-4000-8000-000000000202",
                    state.SessionGeneration,
                    new
                    {
                        safetyStateVersion = 2,
                        observedAt = "2026-08-25T09:00:00Z",
                        safety = Safety(departureSafe: false),
                        affectedSlots = FirstTwoSlots
                    }),
                state,
                TestContext.Current.CancellationToken);
            Assert.Equal(SessionReadiness.RecoveryRequired, state.Readiness);
            string safeLine = Envelope(
                "SafetyStateChanged",
                "00000000-0000-4000-8000-000000000203",
                state.SessionGeneration,
                new
                {
                    safetyStateVersion = 3,
                    observedAt = "2026-08-25T09:00:01Z",
                    safety = Safety(departureSafe: true),
                    affectedSlots = FirstTwoSlots
                });

            string response = await processor.ProcessAsync(
                safeLine, state, TestContext.Current.CancellationToken);

            string[] responseLines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, responseLines.Length);
            using JsonDocument acknowledgement = JsonDocument.Parse(responseLines[0]);
            Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
            Assert.Equal(
                WireContentHash(safeLine),
                acknowledgement.RootElement.GetProperty("payload").GetProperty("acceptedContentSha256").GetString());
            using JsonDocument readiness = JsonDocument.Parse(responseLines[1]);
            Assert.Equal("SessionReadiness", readiness.RootElement.GetProperty("messageType").GetString());
            JsonElement readinessPayload = readiness.RootElement.GetProperty("payload");
            Assert.Equal("READY", readinessPayload.GetProperty("readiness").GetString());
            Assert.Equal(3, readinessPayload.GetProperty("acceptedSafetyStateVersion").GetInt64());
            Assert.Empty(readinessPayload.GetProperty("reasonCodes").EnumerateArray());

            SessionRecoveryRow stored = await context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(3, stored.SafetyRevision);
            Assert.True(stored.DepartureSafe);
            Assert.Equal(SessionReadiness.Ready, stored.Readiness);
            Assert.Equal(SessionReadiness.Ready, state.Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    private static async Task ReachReadyAsync(
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string credential,
        CancellationToken cancellationToken)
    {
        await processor.ProcessAsync(
            Envelope(
                "SessionHello",
                Guid.NewGuid().ToString("D"),
                null,
                new
                {
                    protocolReleaseIdentity = ReleaseIdentity(),
                    credentialProof = credential
                }),
            state,
            cancellationToken);
        long generation = state.SessionGeneration!.Value;
        await processor.ProcessAsync(
            Envelope("CapabilitySnapshot", Guid.NewGuid().ToString("D"), generation,
                new { capabilityVersion = 1 }),
            state,
            cancellationToken);
        await processor.ProcessAsync(
            Envelope("SafetyStateSnapshot", Guid.NewGuid().ToString("D"), generation,
                new { safetyStateVersion = 1, safety = Safety(departureSafe: true) }),
            state,
            cancellationToken);
        await processor.ProcessAsync(
            Envelope("RecoveryStateReport", Guid.NewGuid().ToString("D"), generation,
                new
                {
                    reportId = Guid.NewGuid().ToString("D"),
                    unsettledSlotOperationAttemptId = (string?)null,
                    provenRecoveryCheckpoint = (string?)null,
                    activeUnlockSlots = Array.Empty<int>(),
                    forcedRecoveryGeneration = 0,
                    pendingResults = Array.Empty<object>()
                }),
            state,
            cancellationToken);
    }

    private static object Safety(bool departureSafe) => new
    {
        departureSafe,
        vehicleStopped = departureSafe,
        allTargetSlotsLocked = true,
        allUnlockOutputsReset = true,
        unknownPresent = !departureSafe,
        reasonCodes = departureSafe ? Array.Empty<string>() : new[] { "VEHICLE_MOTION_UNKNOWN" }
    };

    private static object OperationResultPayload(
        string demandId,
        string attemptId,
        string operationType,
        string finalPhysicalState,
        int[]? slots = null)
    {
        object[] slotResults = (slots ?? [1])
            .Select(slot => (object)new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState,
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            })
            .ToArray();
        // The peer hashes CLR values before they reach the wire, so its observedAt is written by the
        // DateTimeOffset converter and keeps the '+' of its offset verbatim, while the wire carries
        // that same character escaped. A timestamp in UTC has no '+' at all and hides the
        // difference completely, which is how the mismatch reached the field: keep a real offset.
        var withoutHash = new
        {
            demandId,
            slotOperationAttemptId = attemptId,
            operationType,
            overallOutcome = "COMPLETED",
            slotResults,
            observedAt = new DateTimeOffset(2026, 8, 25, 17, 0, 0, TimeSpan.FromHours(8)),
            journalCheckpoint = "RESULT_RECORDED"
        };
        return new
        {
            withoutHash.demandId,
            withoutHash.slotOperationAttemptId,
            withoutHash.operationType,
            withoutHash.overallOutcome,
            withoutHash.slotResults,
            withoutHash.observedAt,
            withoutHash.journalCheckpoint,
            resultContentSha256 = Convert.ToHexString(
                    SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withoutHash, PeerSerializerOptions)))
                .ToLowerInvariant()
        };
    }

    /// <summary>Web defaults, the options both ends use.</summary>
    private static readonly JsonSerializerOptions PeerSerializerOptions = new(JsonSerializerDefaults.Web);

    private static string WireContentHash(string line) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant();

    /// <summary>
    /// Builds a line the way the peer does: the payload is materialised as a JsonElement first, so
    /// its strings are written through the encoder exactly as they arrive on the real wire. Passing
    /// the payload object straight through would let a converter write characters the encoder
    /// escapes, and the test would then exercise bytes no peer ever sends.
    /// </summary>
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
            agvId = "AGV-001",
            sessionGeneration = generation,
            sentAt = "2026-08-25T09:00:00Z",
            payload = JsonSerializer.SerializeToElement(payload, PeerSerializerOptions)
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
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
    }
}
