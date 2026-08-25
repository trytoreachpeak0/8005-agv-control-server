using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Transport;

public sealed class OnboardMessageProcessor(
    WireToGateStore store,
    TimeProvider timeProvider,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _serverInstanceId = Guid.NewGuid().ToString("D");

    public async Task<string> ProcessAsync(
        string line,
        OnboardConnectionState state,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        string messageType = RequiredString(root, "messageType");
        string messageId = RequiredString(root, "messageId");
        string agvId = RequiredString(root, "agvId");
        string contentHash = CanonicalJson.Sha256(line);

        if (messageType == "SessionHello")
        {
            string response;
            try
            {
                ValidateSessionHello(root);
                response = await store.CaptureFirstResponseAsync(
                    messageId,
                    contentHash,
                    async () =>
                    {
                        long generation = await store.GetNextSessionGenerationAsync(agvId, cancellationToken)
                            .ConfigureAwait(false);
                        await store.BeginSessionRecoveryAsync(
                            new SessionIdentity(
                                agvId,
                                generation,
                                ProtocolCandidateIdentity.RepositoryCommit,
                                ProtocolCandidateIdentity.ManifestSha256,
                                ProtocolCandidateIdentity.ProfileId,
                                ProtocolCandidateIdentity.ProtocolVersion),
                            cancellationToken).ConfigureAwait(false);
                        return SerializeEnvelope(
                            "SessionAccepted",
                            messageId,
                            agvId,
                            generation,
                            new
                            {
                                sessionGeneration = generation,
                                serverInstanceId = _serverInstanceId,
                                serverBuildCommit = configuration["ControlServerBuild:commit"] ?? "WORKTREE_BUILD",
                                acceptedProtocolReleaseIdentity = ProtocolReleaseIdentity(),
                                acceptedAt = timeProvider.GetUtcNow()
                            });
                    },
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProtocolIdentityMismatchException error)
            {
                return SerializeEnvelope(
                    "SessionRejected",
                    messageId,
                    agvId,
                    sessionGeneration: null,
                    new
                    {
                        problem = new
                        {
                            reasonCode = "PROTOCOL_RELEASE_MISMATCH",
                            fieldPath = "payload.protocolReleaseIdentity",
                            displayMessage = error.Message
                        },
                        expectedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                        expectedProtocolReleaseIdentity = ProtocolReleaseIdentity()
                    });
            }

            using JsonDocument accepted = JsonDocument.Parse(response);
            state.AgvId = agvId;
            state.SessionGeneration = accepted.RootElement.GetProperty("sessionGeneration").GetInt64();
            return response;
        }

        RequireCurrentSession(root, state, agvId);
        return await store.CaptureFirstResponseAsync(
            messageId,
            contentHash,
            () => ProcessCurrentSessionMessageAsync(
                root, state, messageType, messageId, contentHash, cancellationToken),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ProcessCurrentSessionMessageAsync(
        JsonElement root,
        OnboardConnectionState state,
        string messageType,
        string messageId,
        string contentHash,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string agvId = state.AgvId!;
        long generation = state.SessionGeneration!.Value;
        switch (messageType)
        {
            case "Heartbeat":
                return SerializeEnvelope(
                    "HeartbeatAck", messageId, agvId, generation,
                    new { receivedHeartbeatMessageId = messageId, serverTime = timeProvider.GetUtcNow() });
            case "CapabilitySnapshot":
            {
                long revision = payload.GetProperty("capabilityVersion").GetInt64();
                await store.ApplyCapabilitySnapshotAsync(
                    agvId, generation, revision, contentHash, cancellationToken).ConfigureAwait(false);
                state.CapabilityRevision = revision;
                return SnapshotAck(messageId, agvId, generation, "CAPABILITY", revision, contentHash);
            }
            case "SafetyStateSnapshot":
            {
                long revision = payload.GetProperty("safetyStateVersion").GetInt64();
                bool departureSafe = payload.GetProperty("safety").GetProperty("departureSafe").GetBoolean();
                await store.ApplySafetySnapshotAsync(
                    agvId, generation, revision, departureSafe, contentHash, cancellationToken).ConfigureAwait(false);
                state.SafetyRevision = revision;
                return SnapshotAck(messageId, agvId, generation, "SAFETY", revision, contentHash);
            }
            case "RecoveryStateReport":
            {
                string reportId = RequiredString(payload, "reportId");
                long forcedGeneration = payload.GetProperty("forcedRecoveryGeneration").GetInt64();
                List<string> pendingAttempts = [];
                if (payload.TryGetProperty("unsettledSlotOperationAttemptId", out JsonElement attempt) &&
                    attempt.ValueKind == JsonValueKind.String)
                {
                    pendingAttempts.Add(attempt.GetString()!);
                }
                string[] pendingResults = payload.GetProperty("pendingResults")
                    .EnumerateArray()
                    .Select(item => RequiredString(item, "messageId"))
                    .ToArray();
                await store.ApplyRecoveryReportAsync(
                    agvId, generation, reportId, forcedGeneration,
                    pendingAttempts, pendingResults, cancellationToken).ConfigureAwait(false);
                SessionReadinessDecision decision = await store.DecideReadinessAsync(
                    agvId, generation, cancellationToken).ConfigureAwait(false);
                string ack = SerializeEnvelope(
                    "DurableAck", messageId, agvId, generation,
                    new
                    {
                        acceptedMessageId = messageId,
                        acceptedMessageType = messageType,
                        acceptedContentSha256 = contentHash,
                        durablyAcceptedAt = timeProvider.GetUtcNow()
                    });
                string readiness = SerializeEnvelope(
                    "SessionReadiness", correlationId: null, agvId, generation,
                    new
                    {
                        readiness = decision.Readiness == SessionReadiness.Ready ? "READY" : "RECOVERY_REQUIRED",
                        decidedAt = timeProvider.GetUtcNow(),
                        reasonCodes = decision.Readiness == SessionReadiness.Ready ? Array.Empty<string>() : [decision.ReasonCode],
                        acceptedCapabilityVersion = state.CapabilityRevision ?? 0,
                        acceptedSafetyStateVersion = state.SafetyRevision ?? 0,
                        vehicleBusinessStateRevision = 1
                    });
                return $"{ack}\n{readiness}";
            }
            default:
                throw new InvalidDataException($"Message type '{messageType}' is not allowed in the recovery handshake.");
        }
    }

    private void ValidateSessionHello(JsonElement root)
    {
        if (root.GetProperty("protocolVersion").GetInt32() != ProtocolCandidateIdentity.ProtocolVersion ||
            RequiredString(root, "profileId") != ProtocolCandidateIdentity.ProfileId ||
            RequiredString(root, "protocolReleaseManifestSha256") != ProtocolCandidateIdentity.ManifestSha256)
        {
            throw new ProtocolIdentityMismatchException("Envelope protocol identity differs from this build.");
        }
        JsonElement payload = root.GetProperty("payload");
        JsonElement identity = payload.GetProperty("protocolReleaseIdentity");
        if (RequiredString(identity, "commit") != ProtocolCandidateIdentity.RepositoryCommit ||
            RequiredString(identity, "manifestSha256") != ProtocolCandidateIdentity.ManifestSha256 ||
            RequiredString(identity, "profileId") != ProtocolCandidateIdentity.ProfileId ||
            identity.GetProperty("protocolVersion").GetInt32() != ProtocolCandidateIdentity.ProtocolVersion)
        {
            throw new ProtocolIdentityMismatchException("ProtocolReleaseIdentity differs from this candidate build.");
        }

        string credentialVariable = configuration[$"{OnboardTransportOptions.SectionName}:CredentialEnvironmentVariable"]
            ?? "CONTROL_SERVER_ONBOARD_CREDENTIAL";
        string? expectedCredential = Environment.GetEnvironmentVariable(credentialVariable);
        string suppliedCredential = RequiredString(payload, "credentialProof");
        if (string.IsNullOrEmpty(expectedCredential) || !FixedTimeEquals(expectedCredential, suppliedCredential))
        {
            throw new ProtocolIdentityMismatchException("Onboard credential proof was not accepted.");
        }
    }

    private static void RequireCurrentSession(JsonElement root, OnboardConnectionState state, string agvId)
    {
        if (state.AgvId is null || state.SessionGeneration is null || state.AgvId != agvId ||
            root.GetProperty("sessionGeneration").GetInt64() != state.SessionGeneration)
        {
            throw new StaleSessionGenerationException("Message does not belong to the current connection session.");
        }
    }

    private string SnapshotAck(
        string messageId,
        string agvId,
        long generation,
        string kind,
        long revision,
        string contentHash) =>
        SerializeEnvelope(
            "SnapshotAppliedAck", messageId, agvId, generation,
            new
            {
                snapshotMessageId = messageId,
                snapshotKind = kind,
                appliedRevision = revision,
                appliedContentSha256 = contentHash
            });

    private string SerializeEnvelope(
        string messageType,
        string? correlationId,
        string agvId,
        long? sessionGeneration,
        object payload) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId,
            agvId,
            sessionGeneration,
            sentAt = timeProvider.GetUtcNow(),
            payload
        }, SerializerOptions);

    private static object ProtocolReleaseIdentity() => new
    {
        repository = "8005-agv-protocol",
        releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        tag = "candidate-72ddde5",
        commit = ProtocolCandidateIdentity.RepositoryCommit,
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
        vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256
    };

    private static string RequiredString(JsonElement element, string propertyName)
    {
        string? value = element.GetProperty(propertyName).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Protocol field '{propertyName}' is required.")
            : value;
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

public sealed class OnboardConnectionState
{
    public string? AgvId { get; set; }
    public long? SessionGeneration { get; set; }
    public long? CapabilityRevision { get; set; }
    public long? SafetyRevision { get; set; }
}
