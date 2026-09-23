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
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ProtocolProblemIsRecordedWithoutAnsweringOrDroppingTheSession()
    {
        // The protocol defines ProtocolProblem as how the peer reports a rejection. Throwing on it
        // killed the transport, so the peer reconnected in a loop and the complaint that explained
        // the rejection was lost. The expected* fields below deliberately still say v1: they are
        // the peer's own account of what it was built against, and a peer complaining that it
        // expected WIRE_TO_GATE_MVP is exactly what the v2 identity switch makes possible. The
        // server records it and neither answers nor drops the session either way.
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
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
                new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) });

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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
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
                    new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) }),
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
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
                    new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) }),
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
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
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

            // Protocol 2.0.0 gives SublotSubmitted no business deduplication keys, so the same sublot
            // rescanned under a new messageId -- here by keyboard after the scanner -- is a new request
            // the inbox takes and acknowledges, not a business-id content conflict. That its side
            // effect happens once is the runtime's job (ARescanUnderANewMessageIdCommandsNoSecondLoad).
            string rescan = Envelope("SublotSubmitted", "00000000-0000-4000-8000-000000000105", state.SessionGeneration, new
            {
                operationSessionId = "00000000-0000-4000-8000-000000000112",
                stationId = "PICKUP-01",
                worklistRevision = 2,
                sublot = "SUBLOT-001",
                entryMethod = "KEYBOARD",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = "2026-08-25T09:00:05Z"
                }
            });
            using (JsonDocument rescanAck = JsonDocument.Parse(
                await processor.ProcessAsync(rescan, state, TestContext.Current.CancellationToken)))
            {
                Assert.Equal("DurableAck", rescanAck.RootElement.GetProperty("messageType").GetString());
            }
            Assert.Equal(2, await context.ProtocolInbox.CountAsync(
                row => row.MessageType == "SublotSubmitted", TestContext.Current.CancellationToken));

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
    /// 8005-agv-control-server#30 carried to v2 as 8005-agv-program#61 group (1). A durable message whose
    /// DurableAck was lost is resent into the next session exactly as it was first sent but for its
    /// sessionGeneration -- ADR-cross-0030 has a resend keep its messageId. Until this was fixed only
    /// RecoveryStateReport and OperationResult carried the generation-blind replay hash, so an
    /// OperationProgress resent that way was judged a content conflict and the connection was dropped;
    /// the vehicle reconnected, resent and was dropped again, with nothing to break the loop. The answer
    /// is the first acceptance signed for this session: the hash names the line just received, which is
    /// what the onboard compares it with, durablyAcceptedAt stays the moment the server took it, and the
    /// inbox keeps the bytes that were accepted.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AProgressReportResentIntoTheNextSessionIsAcknowledgedFromItsFirstAcceptance()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_PROGRESS_REPLAY_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(token);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            FixedTimeProvider clock = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, clock, configuration);
            OnboardConnectionState firstState = new();
            await ReachReadyAsync(processor, firstState, credential, token);

            const string progressId = "00000000-0000-4000-8000-000000000140";
            string original = Envelope("OperationProgress", progressId, firstState.SessionGeneration, new
            {
                slotOperationAttemptId = "00000000-0000-4000-8000-000000000141",
                phase = "VERIFYING",
                activeUnlockSlots = Array.Empty<int>(),
                completedSlots = FirstTwoSlots,
                observedAt = "2026-08-25T09:00:00Z"
            });
            DateTimeOffset firstAcceptedAt = clock.GetUtcNow();
            string firstAck = await processor.ProcessAsync(original, firstState, token);
            Assert.Equal(WireContentHash(original), AcceptedContentSha256(firstAck));

            // The ack never reached the vehicle. It reconnects and resends the same message under the
            // new generation, the one thing ADR-cross-0030 lets a resend change.
            clock.Advance(TimeSpan.FromMinutes(5));
            OnboardConnectionState secondState = new();
            await ReachReadyAsync(processor, secondState, credential, token);
            long secondGeneration = secondState.SessionGeneration!.Value;
            JsonNode reboundNode = JsonNode.Parse(original)!;
            reboundNode["sessionGeneration"] = secondGeneration;
            string rebound = reboundNode.ToJsonString();

            string replay = await processor.ProcessAsync(rebound, secondState, token);

            using JsonDocument acknowledgement = JsonDocument.Parse(
                replay.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
            Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
            Assert.Equal(secondGeneration, acknowledgement.RootElement.GetProperty("sessionGeneration").GetInt64());
            JsonElement ack = acknowledgement.RootElement.GetProperty("payload");
            Assert.Equal(progressId, ack.GetProperty("acceptedMessageId").GetString());
            Assert.Equal("OperationProgress", ack.GetProperty("acceptedMessageType").GetString());
            Assert.Equal(WireContentHash(rebound), ack.GetProperty("acceptedContentSha256").GetString());
            Assert.Equal(firstAcceptedAt, ack.GetProperty("durablyAcceptedAt").GetDateTimeOffset());

            // The row keeps the bytes that were accepted, not the ones just resent.
            ProtocolInboxRow inbox = await context.ProtocolInbox.SingleAsync(
                row => row.MessageId == progressId, token);
            Assert.Equal(original, inbox.RequestJson);
            Assert.Equal(WireContentHash(original), inbox.ContentHash);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    /// <summary>
    /// The same resend rule for SafetyStateChanged, whose first answer is a DurableAck followed by a
    /// SessionReadiness line. The ack is what the vehicle is waiting for, so that is what is rebuilt for
    /// this session; the readiness that went with the first one spoke for a session that is gone, so it
    /// is decided again from the state as it stands and, like everywhere else here, sent only when it
    /// changed. What a resend still may not do is carry different content: the same messageId with a
    /// different payload is a content conflict, before and after this change.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ASafetyChangeResentIntoTheNextSessionIsAcknowledgedFromItsFirstAcceptance()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_SAFETY_REPLAY_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(token);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
                })
                .Build();
            WireToGateStore store = new(context);
            FixedTimeProvider clock = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, clock, configuration);
            OnboardConnectionState firstState = new();
            await ReachReadyAsync(processor, firstState, credential, token);

            const string changeId = "00000000-0000-4000-8000-000000000150";
            string original = Envelope("SafetyStateChanged", changeId, firstState.SessionGeneration, new
            {
                safetyStateVersion = 2,
                safety = Safety(departureSafe: true),
                observedAt = "2026-08-25T09:00:00Z"
            });
            DateTimeOffset firstAcceptedAt = clock.GetUtcNow();
            string firstResponse = await processor.ProcessAsync(original, firstState, token);
            Assert.Equal("READY", ReadinessOf(firstResponse));

            clock.Advance(TimeSpan.FromMinutes(5));
            OnboardConnectionState secondState = new();
            await ReachReadyAsync(processor, secondState, credential, token);
            long secondGeneration = secondState.SessionGeneration!.Value;
            JsonNode reboundNode = JsonNode.Parse(original)!;
            reboundNode["sessionGeneration"] = secondGeneration;
            string rebound = reboundNode.ToJsonString();

            string replay = await processor.ProcessAsync(rebound, secondState, token);

            using JsonDocument acknowledgement = JsonDocument.Parse(
                replay.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
            Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
            Assert.Equal(secondGeneration, acknowledgement.RootElement.GetProperty("sessionGeneration").GetInt64());
            JsonElement ack = acknowledgement.RootElement.GetProperty("payload");
            Assert.Equal("SafetyStateChanged", ack.GetProperty("acceptedMessageType").GetString());
            Assert.Equal(WireContentHash(rebound), ack.GetProperty("acceptedContentSha256").GetString());
            Assert.Equal(firstAcceptedAt, ack.GetProperty("durablyAcceptedAt").GetDateTimeOffset());
            ProtocolInboxRow inbox = await context.ProtocolInbox.SingleAsync(
                row => row.MessageId == changeId, token);
            Assert.Equal(original, inbox.RequestJson);

            // Rebinding the generation is the only difference a resend may carry.
            JsonNode conflicting = JsonNode.Parse(rebound)!;
            conflicting["payload"]!["safetyStateVersion"] = 3;
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                conflicting.ToJsonString(), secondState, token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    /// <summary>
    /// CV-OPERATION-RESULT-UNKNOWN-RECONCILE: a result the vehicle could not prove is acknowledged,
    /// reported as pending in the next session's RecoveryStateReport, replayed under that session's
    /// generation, acknowledged again -- and reconciled without ever being taken for success.
    /// </summary>
    /// <remarks>
    /// Until 2026-09-13 the replay could not happen at all: the inbox refused the same messageId with
    /// a different wire hash, and the rebound line differs from the original in sessionGeneration
    /// alone. Nothing noticed because the vehicle never replayed an acknowledged result, and its
    /// pendingResults list was always empty -- which also meant the id the report named was never
    /// taken off the session again.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnUnknownResultReportedAsPendingIsReplayedInTheNextSessionAndReconciledWithoutSuccess()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_UNKNOWN_RECONCILE_CREDENTIAL";
        const string credential = "test-credential-not-for-production";
        Environment.SetEnvironmentVariable(credentialVariable, credential);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(token);
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
            await ReachReadyAsync(processor, firstState, credential, token);

            const string attemptId = "00000000-0000-4000-8000-000000000320";
            const string demandId = "00000000-0000-4000-8000-000000000311";
            await store.AcceptWithOrderIntentAsync(
                new AcceptedDemandSnapshot(
                    demandId,
                    "SUBLOT-003|WIRE_TO_GATE",
                    7,
                    "00000000-0000-4000-8000-000000000399",
                    21,
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                new OrderIntent(
                    "00000000-0000-4000-8000-000000000398",
                    demandId,
                    "W2G-D-311-PICKUP-1",
                    "TO_PICKUP",
                    "PICKUP-01",
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                token);
            await store.PrepareSlotOperationAsync(
                new StationOperationPlan(
                    attemptId,
                    demandId,
                    "SUBLOT-003",
                    [1],
                    SlotOperationType.Load,
                    0,
                    new string('c', 64),
                    new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)),
                "00000000-0000-4000-8000-000000000319",
                "load-command-json",
                token);

            object unknownPayload = OperationResultPayload(
                demandId, attemptId, "LOAD", "UNKNOWN", overallOutcome: "UNKNOWN");
            string original = Envelope("OperationResult", attemptId, firstState.SessionGeneration, unknownPayload);
            string firstAck = (await processor.ProcessAsync(original, firstState, token))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.Equal(WireContentHash(original), AcceptedContentSha256(firstAck));
            Assert.Equal(StationOperationStatus.RecoveryRequired, (await context.StationOperations
                .SingleAsync(row => row.SlotOperationAttemptId == attemptId, token)).Status);

            // The vehicle restarts. Its report names the unsettled attempt and the result it already
            // sent for it.
            OnboardConnectionState secondState = new();
            await processor.ProcessAsync(
                Envelope("SessionHello", Guid.NewGuid().ToString("D"), null,
                    new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = credential }),
                secondState,
                token);
            long secondGeneration = secondState.SessionGeneration!.Value;
            await processor.ProcessAsync(
                Envelope("CapabilitySnapshot", Guid.NewGuid().ToString("D"), secondGeneration,
                    new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) }),
                secondState,
                token);
            await processor.ProcessAsync(
                Envelope("SafetyStateSnapshot", Guid.NewGuid().ToString("D"), secondGeneration,
                    new { safetyStateVersion = 1, safety = Safety(departureSafe: true) }),
                secondState,
                token);
            string resultContentSha256;
            using (JsonDocument originalDocument = JsonDocument.Parse(original))
            {
                resultContentSha256 = originalDocument.RootElement
                    .GetProperty("payload").GetProperty("resultContentSha256").GetString()!;
            }
            string report = await processor.ProcessAsync(
                Envelope("RecoveryStateReport", Guid.NewGuid().ToString("D"), secondGeneration,
                    new
                    {
                        reportId = Guid.NewGuid().ToString("D"),
                        unsettledSlotOperationAttemptId = attemptId,
                        provenRecoveryCheckpoint = "SAFE_FINISH_REACHED",
                        activeUnlockSlots = Array.Empty<int>(),
                        forcedRecoveryGeneration = 0,
                        pendingResults = new[]
                        {
                            new
                            {
                                messageType = "OperationResult",
                                messageId = attemptId,
                                businessId = attemptId,
                                contentSha256 = resultContentSha256
                            }
                        }
                    }),
                secondState,
                token);
            Assert.Equal("RECOVERY_REQUIRED", ReadinessOf(report));
            Assert.Contains(attemptId, (await context.SessionRecoveries.SingleAsync(token)).PendingResultIdsJson);

            JsonNode reboundNode = JsonNode.Parse(original)!;
            reboundNode["sessionGeneration"] = secondGeneration;
            string rebound = reboundNode.ToJsonString();
            string replayAck = (await processor.ProcessAsync(rebound, secondState, token))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

            using (JsonDocument acknowledgement = JsonDocument.Parse(replayAck))
            {
                Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
                Assert.Equal(secondGeneration, acknowledgement.RootElement.GetProperty("sessionGeneration").GetInt64());
            }
            Assert.Equal(WireContentHash(rebound), AcceptedContentSha256(replayAck));
            SessionRecoveryRow session = await context.SessionRecoveries.SingleAsync(token);
            Assert.Equal("[]", session.PendingResultIdsJson);
            Assert.Equal(SessionReadiness.RecoveryRequired, session.Readiness);
            Assert.Single(await context.OperationResults.ToListAsync(token));
            Assert.Equal(StationOperationStatus.RecoveryRequired, (await context.StationOperations
                .SingleAsync(row => row.SlotOperationAttemptId == attemptId, token)).Status);
            Assert.NotEqual(DemandExecutionStatus.Succeeded, (await context.AcceptedDemands
                .SingleAsync(row => row.DemandId == demandId, token)).Status);

            // Rebinding the generation is the only difference a replay may carry.
            JsonNode conflicting = JsonNode.Parse(rebound)!;
            conflicting["payload"]!["overallOutcome"] = "COMPLETED";
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                conflicting.ToJsonString(), secondState, token));
            Assert.Single(await context.OperationResults.ToListAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    private static string AcceptedContentSha256(string acknowledgementLine)
    {
        using JsonDocument acknowledgement = JsonDocument.Parse(acknowledgementLine);
        return acknowledgement.RootElement.GetProperty("payload").GetProperty("acceptedContentSha256").GetString()!;
    }

    private static string ReadinessOf(string response)
    {
        using JsonDocument readiness = JsonDocument.Parse(
            response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        return readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString()!;
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
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

        // First line, not the whole response: a result that changes readiness can carry a
        // SessionReadiness line after the ack. This fixture's stored session is not ready
        // (BeginSessionRecoveryAsync leaves no recovery report), and until control-server#340 it
        // got one even though the unload itself succeeded; its connection has not finished a
        // handshake, so since #340 the line is held back. What this test is about is that the
        // demand closed before the ack, which the first line is either way.
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
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
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
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-05")]
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

    /// <summary>
    /// The vehicle sends ManualChargingReturnToServiceRequested and nothing answered it: the type
    /// fell through to the switch's default and threw, which kills the transport. The pair is
    /// frozen since protocol-v0.1.1, carried unchanged into the v2 candidate, and the onboard
    /// client already sends the request.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task ManualChargingReturnToServiceIsAnsweredAndDecidedOncePerRequestId()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_MANUAL_CHARGING_CREDENTIAL";
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
            long generation = state.SessionGeneration!.Value;

            const string requestId = "00000000-0000-4000-8000-0000000000c1";
            const string firstMessageId = "00000000-0000-4000-8000-0000000000c2";
            string first = await processor.ProcessAsync(
                Envelope(
                    "ManualChargingReturnToServiceRequested", firstMessageId, generation,
                    ManualChargingReturnPayload(requestId, "MAINTENANCE_ADMINISTRATOR")),
                state,
                TestContext.Current.CancellationToken);

            using (JsonDocument result = JsonDocument.Parse(first))
            {
                Assert.Equal(
                    "ManualChargingReturnToServiceResult",
                    result.RootElement.GetProperty("messageType").GetString());
                Assert.Equal(firstMessageId, result.RootElement.GetProperty("correlationId").GetString());
                JsonElement payload = result.RootElement.GetProperty("payload");
                Assert.Equal(requestId, payload.GetProperty("requestId").GetString());
                Assert.Equal("RETURNED_TO_ELIGIBILITY_EVALUATION", payload.GetProperty("outcome").GetString());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("problem").ValueKind);
                Assert.Equal(0, payload.GetProperty("vehicleBusinessStateRevision").GetInt64());
            }

            // The protocol's businessDedupKeys for both messages of the pair is requestId, so the
            // same request under a new messageId -- which the transport inbox cannot recognise --
            // has to return the conclusion already reached rather than decide a second time.
            string replay = await processor.ProcessAsync(
                Envelope(
                    "ManualChargingReturnToServiceRequested",
                    "00000000-0000-4000-8000-0000000000c3", generation,
                    ManualChargingReturnPayload(requestId, "MAINTENANCE_ADMINISTRATOR")),
                state,
                TestContext.Current.CancellationToken);

            using (JsonDocument result = JsonDocument.Parse(replay))
            {
                Assert.Equal(
                    "00000000-0000-4000-8000-0000000000c3",
                    result.RootElement.GetProperty("correlationId").GetString());
                JsonElement payload = result.RootElement.GetProperty("payload");
                Assert.Equal("RETURNED_TO_ELIGIBILITY_EVALUATION", payload.GetProperty("outcome").GetString());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("problem").ValueKind);
            }

            ManualChargingReturnToServiceRow stored = await context.ManualChargingReturnToServiceRequests
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(firstMessageId, stored.RequestMessageId);
            Assert.Equal("RETURNED_TO_ELIGIBILITY_EVALUATION", stored.Outcome);
            Assert.Null(stored.ProblemReasonCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    /// <summary>
    /// A session with facts still to reconcile cannot put the vehicle back into eligibility
    /// evaluation, and the reason code saying so has to be one the protocol's closed ErrorCode
    /// enumeration contains.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task ManualChargingReturnToServiceIsRejectedWhileTheSessionStillNeedsRecovery()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_MANUAL_CHARGING_RECOVERY_CREDENTIAL";
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

            // Straight after the handshake the session is RecoveryRequired: no capability
            // snapshot, no safety snapshot, no recovery report.
            await processor.ProcessAsync(
                Envelope(
                    "SessionHello", "00000000-0000-4000-8000-0000000000d0", null,
                    new
                    {
                        protocolReleaseIdentity = ReleaseIdentity(),
                        credentialProof = credential
                    }),
                state,
                TestContext.Current.CancellationToken);
            long generation = state.SessionGeneration!.Value;

            string rejected = await processor.ProcessAsync(
                Envelope(
                    "ManualChargingReturnToServiceRequested",
                    "00000000-0000-4000-8000-0000000000d1", generation,
                    ManualChargingReturnPayload(
                        "00000000-0000-4000-8000-0000000000d2", "SYSTEM_ADMINISTRATOR")),
                state,
                TestContext.Current.CancellationToken);

            using JsonDocument result = JsonDocument.Parse(rejected);
            JsonElement payload = result.RootElement.GetProperty("payload");
            Assert.Equal("REJECTED", payload.GetProperty("outcome").GetString());
            string reasonCode = payload.GetProperty("problem").GetProperty("reasonCode").GetString()!;
            Assert.Equal("SESSION_RECOVERY_REQUIRED", reasonCode);
            Assert.True(ProtocolErrorCodes.Contains(reasonCode));
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    /// <summary>
    /// Neither end validates against the schema at runtime, so an administratorRole outside the
    /// profile's enumeration arrives here as an ordinary string and has to be refused rather than
    /// trusted.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-MANUAL-CHARGING-RETURN")]
    public async Task ManualChargingReturnToServiceRefusesARoleTheProfileDoesNotDefine()
    {
        const string credentialVariable = "CONTROL_SERVER_TEST_MANUAL_CHARGING_ROLE_CREDENTIAL";
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

            string rejected = await processor.ProcessAsync(
                Envelope(
                    "ManualChargingReturnToServiceRequested",
                    "00000000-0000-4000-8000-0000000000e1", state.SessionGeneration!.Value,
                    ManualChargingReturnPayload(
                        "00000000-0000-4000-8000-0000000000e2", "SHIFT_LEAD")),
                state,
                TestContext.Current.CancellationToken);

            using JsonDocument result = JsonDocument.Parse(rejected);
            JsonElement payload = result.RootElement.GetProperty("payload");
            Assert.Equal("REJECTED", payload.GetProperty("outcome").GetString());
            JsonElement problem = payload.GetProperty("problem");
            string reasonCode = problem.GetProperty("reasonCode").GetString()!;
            Assert.Equal("PROTOCOL_SCHEMA_INVALID", reasonCode);
            Assert.True(ProtocolErrorCodes.Contains(reasonCode));
            Assert.Equal("payload.administratorRole", problem.GetProperty("fieldPath").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    private static object ManualChargingReturnPayload(string requestId, string administratorRole) => new
    {
        requestId,
        administrator = new
        {
            operatorId = "OP-4471",
            verificationMethod = "BADGE",
            verifiedAt = "2026-08-25T08:59:00Z"
        },
        administratorRole,
        reason = "手动充电结束，请求恢复业务资格评估",
        observedBatteryPercent = 84.5
    };

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
                new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) }),
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
        int[]? slots = null,
        string overallOutcome = "COMPLETED")
    {
        object[] slotResults = (slots ?? [1])
            .Select(slot => (object)new
            {
                slotNo = slot,
                outcome = overallOutcome,
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
            overallOutcome,
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
        private DateTimeOffset _now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>
        /// Lets a test tell the moment a message was first accepted from the moment it was resent.
        /// A DurableAck rebuilt for a later session keeps the first one.
        /// </summary>
        public void Advance(TimeSpan by) => _now += by;
    }
}
