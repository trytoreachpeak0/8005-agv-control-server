using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Transport;

public sealed partial class OnboardMessageProcessor(
    WireToGateStore store,
    OnboardRecoveryCoordinator recoveryCoordinator,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<OnboardMessageProcessor> logger)
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
        string contentHash = WireContentHash.Sha256(line);

        if (messageType == "SessionHello")
        {
            string response;
            try
            {
                ValidateSessionHello(root);
                response = await store.CaptureFirstResponseAsync(
                    messageId,
                    messageType,
                    RedactSessionCredential(line),
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
                        state.Readiness = SessionReadiness.RecoveryRequired;
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

        ValidateEnvelopeIdentity(root);
        RequireCurrentSession(root, state, agvId);
        string persistedRequest = messageType == "ExceptionRecoverySessionRequested"
            ? RedactRecoveryAuthenticationProof(line)
            : line;
        string capturedResponse = await store.CaptureFirstResponseAsync(
            messageId,
            messageType,
            persistedRequest,
            contentHash,
            () => ProcessCurrentSessionMessageAsync(
                root, state, messageType, messageId, contentHash, cancellationToken),
            timeProvider.GetUtcNow(),
            cancellationToken,
            messageType == "RecoveryStateReport" ? RecoveryReplayIdentityHash : null,
            messageType == "RecoveryStateReport"
                ? response => RestoreAcceptedSnapshotVersions(response, state)
                : null).ConfigureAwait(false);
        bool hasDeferredRecoveryOutbound = OnboardRecoveryCoordinator.IsRecoveryRequest(messageType) ||
                                           OnboardRecoveryCoordinator.IsRecoveryResult(messageType) ||
                                           messageType == "OperationResult" ||
                                           messageType == "RecoveryStateReport";
        if (hasDeferredRecoveryOutbound && state.DeferOutboundUntilResponseWritten)
        {
            state.DeferredRecoveryLine = line;
        }
        else if (OnboardRecoveryCoordinator.IsRecoveryRequest(messageType) ||
                 OnboardRecoveryCoordinator.IsRecoveryResult(messageType) ||
                 messageType == "OperationResult")
        {
            await recoveryCoordinator.SendTriggeredCommandAsync(root, cancellationToken).ConfigureAwait(false);
        }
        else if (messageType == "RecoveryStateReport")
        {
            await recoveryCoordinator.ReplayPendingCommandsAsync(
                agvId,
                state.SessionGeneration!.Value,
                cancellationToken).ConfigureAwait(false);
        }
        return capturedResponse;
    }

    public async Task FlushDeferredOutboundAsync(
        OnboardConnectionState state,
        CancellationToken cancellationToken)
    {
        string? line = state.DeferredRecoveryLine;
        state.DeferredRecoveryLine = null;
        if (line is null) return;
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        string messageType = RequiredString(root, "messageType");
        if (OnboardRecoveryCoordinator.IsRecoveryRequest(messageType) ||
            OnboardRecoveryCoordinator.IsRecoveryResult(messageType) ||
            messageType == "OperationResult")
        {
            await recoveryCoordinator.SendTriggeredCommandAsync(root, cancellationToken).ConfigureAwait(false);
        }
        else if (messageType == "RecoveryStateReport")
        {
            await recoveryCoordinator.ReplayPendingCommandsAsync(
                RequiredString(root, "agvId"),
                root.GetProperty("sessionGeneration").GetInt64(),
                cancellationToken).ConfigureAwait(false);
        }
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
                    return SnapshotAck(messageId, agvId, generation, "SAFETY_STATE", revision, contentHash);
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
                    string? unsettledAttemptId = payload.GetProperty("unsettledSlotOperationAttemptId").ValueKind == JsonValueKind.Null
                        ? null
                        : RequiredString(payload, "unsettledSlotOperationAttemptId");
                    string? checkpoint = payload.GetProperty("provenRecoveryCheckpoint").ValueKind == JsonValueKind.Null
                        ? null
                        : RequiredString(payload, "provenRecoveryCheckpoint");
                    int[] activeUnlockSlots = payload.GetProperty("activeUnlockSlots")
                        .EnumerateArray().Select(item => item.GetInt32()).ToArray();
                    await store.ApplyRecoveryReportAsync(
                        agvId, generation, reportId, forcedGeneration,
                        unsettledAttemptId, checkpoint, activeUnlockSlots,
                        pendingAttempts, pendingResults, cancellationToken).ConfigureAwait(false);
                    SessionReadinessDecision decision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    state.Readiness = decision.Readiness;
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
            case "OperationProgress":
            case "PreDepartureSafetyCheckResult":
            case "SlotOperationCommandRejected":
            case "SublotSubmitted":
                return DurableAck(messageType, messageId, agvId, generation, contentHash);
            case "OperationResult":
                {
                    string attemptId = RequiredString(payload, "slotOperationAttemptId");
                    string demandId = RequiredString(payload, "demandId");
                    SlotOperationType operationType = RequiredString(payload, "operationType") switch
                    {
                        "LOAD" => SlotOperationType.Load,
                        "UNLOAD" => SlotOperationType.Unload,
                        _ => throw new InvalidDataException("OperationResult operationType is not supported.")
                    };
                    string overallOutcome = RequiredString(payload, "overallOutcome");
                    JsonElement[] slotResults = payload.GetProperty("slotResults").EnumerateArray().ToArray();
                    SlotPhysicalEvidence[] evidence = slotResults
                        .Select(item => new SlotPhysicalEvidence(
                            item.GetProperty("slotNo").GetInt32(),
                            RequiredString(item, "finalPhysicalState") switch
                            {
                                "EMPTY" => SlotBusinessState.Empty,
                                "OCCUPIED" => SlotBusinessState.Occupied,
                                "UNKNOWN" => SlotBusinessState.Unknown,
                                _ => throw new InvalidDataException(
                                    "OperationResult finalPhysicalState is not supported.")
                            },
                            RequiredString(item, "lockState") == "LOCKED",
                            RequiredString(item, "unlockOutputState") == "RESET"))
                        .ToArray();
                    string resultContentSha256 = RequiredString(payload, "resultContentSha256");
                    string computedResultHash = ComputeOperationResultContentHash(payload);
                    if (!string.Equals(resultContentSha256, computedResultHash, StringComparison.Ordinal))
                    {
                        throw new ProtocolContentConflictException(
                            "OperationResult resultContentSha256 does not match its business content.");
                    }
                    long forcedGeneration = await store.GetOperationForcedRecoveryGenerationAsync(
                        attemptId, cancellationToken).ConfigureAwait(false);
                    OperationResultDisposition disposition = await store.ApplyOperationResultAsync(
                        new StationOperationResult(
                            messageId,
                            attemptId,
                            demandId,
                            operationType,
                            overallOutcome,
                            evidence,
                            slotResults.All(item => RequiredString(item, "outcome") == "COMPLETED"),
                            payload.GetProperty("observedAt").GetDateTimeOffset(),
                            resultContentSha256,
                            contentHash),
                        agvId,
                        forcedGeneration,
                        cancellationToken).ConfigureAwait(false);
                    await recoveryCoordinator.ObserveOperationResultAsync(
                        attemptId, disposition, cancellationToken).ConfigureAwait(false);
                    return DurableAck(messageType, messageId, agvId, generation, contentHash);
                }
            case "ExceptionRecoverySessionRequested":
            case "RecoveryActionSubmitted":
            case "HardwareRecoveryRecordSubmitted":
            case "LoadCancellationStartRequested":
            case "LoadCompensationRequested":
            case "LoadCorrectionRequested":
                return await recoveryCoordinator.ProcessRequestAsync(root, contentHash, cancellationToken)
                    .ConfigureAwait(false);
            case "FaultCargoRecoveryResult":
            case "ForcedMechanicalRecoveryResult":
            case "LoadCancellationResult":
            case "LoadCompensationResult":
            case "LoadCorrectionResult":
                return await recoveryCoordinator.ProcessResultAsync(root, contentHash, cancellationToken)
                    .ConfigureAwait(false);
            case "SafetyStateChanged":
                {
                    long revision = payload.GetProperty("safetyStateVersion").GetInt64();
                    bool departureSafe = payload.GetProperty("safety").GetProperty("departureSafe").GetBoolean();
                    await store.ApplySafetySnapshotAsync(
                        agvId, generation, revision, departureSafe, contentHash, cancellationToken)
                        .ConfigureAwait(false);
                    state.SafetyRevision = revision;
                    SessionReadinessDecision decision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    state.Readiness = decision.Readiness;
                    string ack = DurableAck(messageType, messageId, agvId, generation, contentHash);
                    string readiness = SerializeReadiness(agvId, generation, state, decision);
                    return $"{ack}\n{readiness}";
                }
            case "SnapshotAppliedAck":
                {
                    string snapshotMessageId = RequiredString(payload, "snapshotMessageId");
                    if (RequiredString(root, "correlationId") != snapshotMessageId)
                    {
                        throw new InvalidDataException("SnapshotAppliedAck correlationId must identify the snapshot.");
                    }
                    string snapshotMessageType = RequiredString(payload, "snapshotKind") switch
                    {
                        "VEHICLE_BUSINESS_STATE" => "VehicleBusinessStateSnapshot",
                        "CURRENT_STOP_WORKLIST" => "CurrentStopWorklistSnapshot",
                        "UPCOMING_STOP_PLAN" => "UpcomingStopPlanSnapshot",
                        "EXCEPTION_RECOVERY_SESSION" => "ExceptionRecoverySessionSnapshot",
                        _ => throw new InvalidDataException("SnapshotAppliedAck snapshotKind is not supported.")
                    };
                    await store.AcknowledgeOutboundEnvelopeAsync(
                        snapshotMessageId,
                        snapshotMessageType,
                        RequiredString(payload, "appliedContentSha256"),
                        payload.GetProperty("appliedRevision").GetInt64(),
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    return string.Empty;
                }
            case "DurableAck":
                {
                    string acceptedMessageId = RequiredString(payload, "acceptedMessageId");
                    if (RequiredString(root, "correlationId") != acceptedMessageId)
                    {
                        throw new InvalidDataException("DurableAck correlationId must identify the accepted message.");
                    }
                    await store.AcknowledgeOutboundEnvelopeAsync(
                        acceptedMessageId,
                        RequiredString(payload, "acceptedMessageType"),
                        RequiredString(payload, "acceptedContentSha256"),
                        appliedRevision: null,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    return string.Empty;
                }
            case "ProtocolProblem":
                {
                    // protocol-v0.1.1 defines ProtocolProblem as how a peer reports that it
                    // rejected one of our messages. Falling through to "unsupported" threw, which
                    // killed the transport and sent the session into a reconnect loop -- and threw
                    // away the only diagnostic saying why the rejection happened. It carries no
                    // obligation, so record it and keep the session; the envelope itself is
                    // already persisted in the inbox by the caller.
                    JsonElement problem = payload.GetProperty("problem");
                    LogOnboardRejection(
                        logger,
                        NullableString(payload, "rejectedMessageType") ?? "(unstated)",
                        RequiredString(payload, "rejectedMessageId"),
                        RequiredString(problem, "reasonCode"),
                        NullableString(problem, "fieldPath") ?? "(none)",
                        NullableString(problem, "displayMessage") ?? "(none)");
                    return string.Empty;
                }
            default:
                throw new InvalidDataException($"Message type '{messageType}' is not supported by ControlServer.");
        }
    }

    private string DurableAck(
        string acceptedMessageType,
        string acceptedMessageId,
        string agvId,
        long generation,
        string contentHash) =>
        SerializeEnvelope(
            "DurableAck",
            acceptedMessageId,
            agvId,
            generation,
            new
            {
                acceptedMessageId,
                acceptedMessageType,
                acceptedContentSha256 = contentHash,
                durablyAcceptedAt = timeProvider.GetUtcNow()
            });

    private string SerializeReadiness(
        string agvId,
        long generation,
        OnboardConnectionState state,
        SessionReadinessDecision decision) =>
        SerializeEnvelope(
            "SessionReadiness",
            correlationId: null,
            agvId,
            generation,
            new
            {
                readiness = decision.Readiness == SessionReadiness.Ready ? "READY" : "RECOVERY_REQUIRED",
                decidedAt = timeProvider.GetUtcNow(),
                reasonCodes = decision.Readiness == SessionReadiness.Ready
                    ? Array.Empty<string>()
                    : [decision.ReasonCode],
                acceptedCapabilityVersion = state.CapabilityRevision ?? 0,
                acceptedSafetyStateVersion = state.SafetyRevision ?? 0,
                vehicleBusinessStateRevision = 1
            });

    private static void RestoreAcceptedSnapshotVersions(
        string firstResponse,
        OnboardConnectionState state)
    {
        string? readinessLine = firstResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("messageType").GetString() == "SessionReadiness";
            });
        if (readinessLine is null)
        {
            return;
        }

        using JsonDocument readiness = JsonDocument.Parse(readinessLine);
        JsonElement payload = readiness.RootElement.GetProperty("payload");
        long capabilityRevision = payload.GetProperty("acceptedCapabilityVersion").GetInt64();
        long safetyRevision = payload.GetProperty("acceptedSafetyStateVersion").GetInt64();
        state.CapabilityRevision = capabilityRevision > 0 ? capabilityRevision : null;
        state.SafetyRevision = safetyRevision > 0 ? safetyRevision : null;
    }

    private void ValidateSessionHello(JsonElement root)
    {
        ValidateEnvelopeIdentity(root);
        JsonElement payload = root.GetProperty("payload");
        JsonElement identity = payload.GetProperty("protocolReleaseIdentity");
        if (RequiredString(identity, "commit") != ProtocolCandidateIdentity.RepositoryCommit ||
            RequiredString(identity, "manifestSha256") != ProtocolCandidateIdentity.ManifestSha256 ||
            RequiredString(identity, "profileId") != ProtocolCandidateIdentity.ProfileId ||
            identity.GetProperty("protocolVersion").GetInt32() != ProtocolCandidateIdentity.ProtocolVersion)
        {
            throw new ProtocolIdentityMismatchException("ProtocolReleaseIdentity differs from this release build.");
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

    private static void ValidateEnvelopeIdentity(JsonElement root)
    {
        if (root.GetProperty("protocolVersion").GetInt32() != ProtocolCandidateIdentity.ProtocolVersion ||
            RequiredString(root, "profileId") != ProtocolCandidateIdentity.ProfileId ||
            RequiredString(root, "protocolReleaseManifestSha256") != ProtocolCandidateIdentity.ManifestSha256 ||
            RequiredString(root, "protocolReleaseVersion") != ProtocolCandidateIdentity.ReleaseVersion)
        {
            throw new ProtocolIdentityMismatchException("Envelope protocol identity differs from this build.");
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
        tag = ProtocolCandidateIdentity.Tag,
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

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Onboard rejected {RejectedMessageType} {RejectedMessageId}: {ReasonCode} at {FieldPath} -- {DisplayMessage}")]
    private static partial void LogOnboardRejection(
        ILogger logger,
        string rejectedMessageType,
        string rejectedMessageId,
        string reasonCode,
        string fieldPath,
        string displayMessage);

    /// <summary>
    /// Reads a field the protocol declares as `string | null`, so an explicit null is a value
    /// rather than a violation.
    /// </summary>
    private static string? NullableString(JsonElement element, string propertyName)
    {
        JsonElement value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static string ComputeOperationResultContentHash(JsonElement payload)
    {
        byte[] businessContent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            demandId = payload.GetProperty("demandId"),
            slotOperationAttemptId = payload.GetProperty("slotOperationAttemptId"),
            operationType = payload.GetProperty("operationType"),
            overallOutcome = payload.GetProperty("overallOutcome"),
            slotResults = payload.GetProperty("slotResults"),
            observedAt = payload.GetProperty("observedAt"),
            journalCheckpoint = payload.GetProperty("journalCheckpoint")
        }, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(businessContent)).ToLowerInvariant();
    }

    private static string RecoveryReplayIdentityHash(string line)
    {
        JsonNode root = JsonNode.Parse(line)
            ?? throw new InvalidDataException("RecoveryStateReport JSON cannot be empty.");
        root["sessionGeneration"] = 0;
        return WireContentHash.Sha256(root.ToJsonString(SerializerOptions));
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string RedactSessionCredential(string line)
    {
        JsonNode root = JsonNode.Parse(line)
            ?? throw new InvalidDataException("SessionHello JSON cannot be empty.");
        JsonObject payload = root["payload"]?.AsObject()
            ?? throw new InvalidDataException("SessionHello payload is required.");
        payload["credentialProof"] = "[REDACTED]";
        return root.ToJsonString(SerializerOptions);
    }

    private static string RedactRecoveryAuthenticationProof(string line)
    {
        JsonNode root = JsonNode.Parse(line)
            ?? throw new InvalidDataException("ExceptionRecoverySessionRequested JSON cannot be empty.");
        JsonObject payload = root["payload"]?.AsObject()
            ?? throw new InvalidDataException("ExceptionRecoverySessionRequested payload is required.");
        payload["authenticationProof"] = "[REDACTED]";
        return root.ToJsonString(SerializerOptions);
    }
}

public sealed class OnboardConnectionState
{
    public string? AgvId { get; set; }
    public long? SessionGeneration { get; set; }
    public long? CapabilityRevision { get; set; }
    public long? SafetyRevision { get; set; }
    public SessionReadiness Readiness { get; set; } = SessionReadiness.RecoveryRequired;
    public bool DeferOutboundUntilResponseWritten { get; set; }
    public string? DeferredRecoveryLine { get; set; }
}
