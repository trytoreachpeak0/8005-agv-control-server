using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

public sealed class OnboardMessageProcessorTests
{
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
            OnboardMessageProcessor processor = new(
                new WireToGateStore(context), new FixedTimeProvider(), configuration);
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
            OnboardMessageProcessor processor = new(
                new WireToGateStore(context), new FixedTimeProvider(), configuration);
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
            payload
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
