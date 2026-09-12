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
    private static readonly JsonSerializerOptions SerializerOptions = ProtocolEnvelope.SerializerOptions;
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
        // The same message resent into a later session differs from its first line only in
        // sessionGeneration: ADR-cross-0030 has a resend keep its messageId, and the onboard rebinds a
        // pending durable message to the new session (8005-agv-control-server#30). A RecoveryStateReport
        // is applied again in the new session; anything else is answered from its first acceptance.
        bool recoveryReport = messageType == "RecoveryStateReport";
        string capturedResponse = await store.CaptureFirstResponseAsync(
            messageId,
            messageType,
            persistedRequest,
            contentHash,
            () => ProcessCurrentSessionMessageAsync(
                root, state, messageType, messageId, contentHash, cancellationToken),
            timeProvider.GetUtcNow(),
            cancellationToken,
            ReplayIdentityHash,
            recoveryReport
                ? response => RestoreAcceptedSnapshotVersions(response, state)
                : null,
            recoveryReport
                ? null
                : firstResponse => RebindDurableAckAsync(
                    firstResponse, messageType, messageId, agvId, contentHash, state, cancellationToken))
            .ConfigureAwait(false);
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
                    JsonElement safety = payload.GetProperty("safety");
                    bool departureSafe = safety.GetProperty("departureSafe").GetBoolean();
                    await store.ApplySafetySnapshotAsync(
                        agvId, generation, revision, departureSafe, contentHash, cancellationToken,
                        SafetyReasonCodes(safety), SafetyUnknownPresent(safety)).ConfigureAwait(false);
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
                    return $"{ack}\n{SessionReadinessLine(decision, agvId, generation, state)}";
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
                    // Applying a result is the moment the server's own verdict changes: a result it
                    // refuses puts the operation into RecoveryRequired, and readiness has to follow.
                    // It did not until 2026-09-04 -- readiness was recomputed only on
                    // RecoveryStateReport and SafetyStateChanged, neither of which the vehicle sends
                    // afterwards, so the session stayed READY over a load that needed recovery and
                    // the onboard never showed its recovery entry.
                    //
                    // It is also the moment an attempt the handshake reported as outstanding gets its
                    // conclusion, which can move the session the other way (8005-agv-program#46).
                    string resultAck = DurableAck(messageType, messageId, agvId, generation, contentHash);
                    return await WithReadinessChangeAsync(resultAck, agvId, generation, state, cancellationToken)
                        .ConfigureAwait(false);
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
                {
                    // Reconciling a recovery settles the operation that held the session out of
                    // Ready. Nothing else recomputed readiness afterwards: the vehicle sends its
                    // RecoveryStateReport only at the handshake, so a vehicle whose load had just
                    // been compensated stayed RECOVERY_REQUIRED until it was restarted
                    // (8005-agv-program#46).
                    string recoveryAck = await recoveryCoordinator.ProcessResultAsync(
                        root, contentHash, cancellationToken).ConfigureAwait(false);
                    return await WithReadinessChangeAsync(recoveryAck, agvId, generation, state, cancellationToken)
                        .ConfigureAwait(false);
                }
            case "SafetyStateChanged":
                {
                    long revision = payload.GetProperty("safetyStateVersion").GetInt64();
                    JsonElement safety = payload.GetProperty("safety");
                    bool departureSafe = safety.GetProperty("departureSafe").GetBoolean();
                    await store.ApplySafetySnapshotAsync(
                        agvId, generation, revision, departureSafe, contentHash, cancellationToken,
                        SafetyReasonCodes(safety), SafetyUnknownPresent(safety))
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

    /// <summary>
    /// Recomputes readiness after a result and appends a SessionReadiness line only when it changed.
    /// The ordinary path keeps its one-line answer -- a completed load on a Ready session is Ready
    /// before and after -- and each result that does move the session says so, in either direction.
    /// </summary>
    private async Task<string> WithReadinessChangeAsync(
        string ack,
        string agvId,
        long generation,
        OnboardConnectionState state,
        CancellationToken cancellationToken)
    {
        SessionReadinessDecision decision = await store.DecideReadinessAsync(
            agvId, generation, cancellationToken).ConfigureAwait(false);
        if (decision.Readiness == state.Readiness)
        {
            return ack;
        }
        state.Readiness = decision.Readiness;
        return $"{ack}\n{SessionReadinessLine(decision, agvId, generation, state)}";
    }

    /// <summary>
    /// One SessionReadiness line, built the same way wherever readiness changes. The envelope used to
    /// be written out at each site; two of those copies were putting a reason code on the wire that
    /// the protocol's closed ErrorCode enum does not contain.
    /// </summary>
    private string SessionReadinessLine(
        SessionReadinessDecision decision,
        string agvId,
        long generation,
        OnboardConnectionState state) =>
        SerializeEnvelope(
            "SessionReadiness", correlationId: null, agvId, generation,
            new
            {
                readiness = decision.Readiness == SessionReadiness.Ready ? "READY" : "RECOVERY_REQUIRED",
                decidedAt = timeProvider.GetUtcNow(),
                reasonCodes = decision.Readiness == SessionReadiness.Ready
                    ? Array.Empty<string>()
                    : [ProtocolErrorCodes.ToSessionReadinessReasonCode(decision.ReasonCode)],
                acceptedCapabilityVersion = state.CapabilityRevision ?? 0,
                acceptedSafetyStateVersion = state.SafetyRevision ?? 0,
                vehicleBusinessStateRevision = 1
            });

    private string DurableAck(
        string acceptedMessageType,
        string acceptedMessageId,
        string agvId,
        long generation,
        string contentHash,
        DateTimeOffset? durablyAcceptedAt = null) =>
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
                durablyAcceptedAt = durablyAcceptedAt ?? timeProvider.GetUtcNow()
            });

    /// <summary>
    /// Answers a durable message resent into a later session from its first acceptance, or returns null
    /// when that first response was not a DurableAck for it -- a snapshot or a recovery request stays a
    /// conflict. Nothing is processed again: the first processing committed, and what it wrote is bound
    /// to the first line's hash (ApplyOperationResultAsync, RecoveryResultEvidence), so a second pass
    /// would throw on its own. The ack is rebuilt for this session and names the line just received,
    /// which is what the onboard compares it with; durablyAcceptedAt stays the moment it was accepted.
    /// Readiness is recomputed rather than replayed, because the first response's belonged to the old
    /// session.
    /// </summary>
    private async Task<string?> RebindDurableAckAsync(
        string firstResponse,
        string messageType,
        string messageId,
        string agvId,
        string contentHash,
        OnboardConnectionState state,
        CancellationToken cancellationToken)
    {
        string? firstLine = firstResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstLine is null)
        {
            return null;
        }
        using JsonDocument first = JsonDocument.Parse(firstLine);
        JsonElement firstRoot = first.RootElement;
        if (RequiredString(firstRoot, "messageType") != "DurableAck")
        {
            return null;
        }
        JsonElement firstAck = firstRoot.GetProperty("payload");
        if (RequiredString(firstAck, "acceptedMessageId") != messageId)
        {
            return null;
        }

        long generation = state.SessionGeneration!.Value;
        string ack = DurableAck(
            messageType,
            messageId,
            agvId,
            generation,
            contentHash,
            firstAck.GetProperty("durablyAcceptedAt").GetDateTimeOffset());
        return await WithReadinessChangeAsync(ack, agvId, generation, state, cancellationToken)
            .ConfigureAwait(false);
    }

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
                    : [ProtocolErrorCodes.ToSessionReadinessReasonCode(decision.ReasonCode)],
                acceptedCapabilityVersion = state.CapabilityRevision ?? 0,
                acceptedSafetyStateVersion = state.SafetyRevision ?? 0,
                // NOTE: this third copy differs from SessionReadinessLine only in indentation; it is
                // left alone because it is on the handshake path and takes its revisions from a
                // different source. Three copies is how the illegal reasonCodes survived in two of
                // them for as long as they did.
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
        ProtocolEnvelope.Serialize(
            messageType,
            Guid.NewGuid().ToString("D"),
            correlationId,
            agvId,
            sessionGeneration,
            timeProvider.GetUtcNow(),
            payload);

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

    /// <summary>
    /// Why the peer says the vehicle is unsafe to depart. Both this and unknownPresent used to be
    /// dropped, which left the session unable to tell unsafety caused by a slot operation this
    /// server itself commanded from unsafety that must fail the session closed.
    /// </summary>
    private static string[] SafetyReasonCodes(JsonElement safety) =>
        safety.TryGetProperty("reasonCodes", out JsonElement codes) && codes.ValueKind == JsonValueKind.Array
            ? codes.EnumerateArray()
                .Where(code => code.ValueKind == JsonValueKind.String)
                .Select(code => code.GetString()!)
                .ToArray()
            : [];

    private static bool? SafetyUnknownPresent(JsonElement safety) =>
        safety.TryGetProperty("unknownPresent", out JsonElement unknown) &&
        unknown.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? unknown.GetBoolean()
            : null;

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

    /// <summary>
    /// Recomputes the peer's resultContentSha256 over the same business content it hashed.
    /// </summary>
    /// <remarks>
    /// The peer hashes CLR values before they reach the wire, so its observedAt is written by the
    /// DateTimeOffset converter and keeps the '+' of its offset verbatim. Copying the received
    /// JsonElement into the anonymous object instead re-wrote that string through the encoder,
    /// which escapes '+', so the two hashes could never agree and every OperationResult was refused
    /// as a content conflict -- the connection died on the first completed load, then again every
    /// two seconds as the peer replayed it. Decoding each field back to a CLR value reproduces what
    /// the peer hashed; it also stops any other escapable character inside slotResults from
    /// reintroducing the same class of drift.
    /// </remarks>
    private static string ComputeOperationResultContentHash(JsonElement payload)
    {
        byte[] businessContent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            demandId = RequiredString(payload, "demandId"),
            slotOperationAttemptId = RequiredString(payload, "slotOperationAttemptId"),
            operationType = RequiredString(payload, "operationType"),
            overallOutcome = RequiredString(payload, "overallOutcome"),
            slotResults = payload.GetProperty("slotResults").EnumerateArray().Select(slot => new
            {
                slotNo = slot.GetProperty("slotNo").GetInt32(),
                outcome = RequiredString(slot, "outcome"),
                finalPhysicalState = RequiredString(slot, "finalPhysicalState"),
                lockState = RequiredString(slot, "lockState"),
                unlockOutputState = RequiredString(slot, "unlockOutputState"),
                reasonCodes = slot.GetProperty("reasonCodes").EnumerateArray()
                    .Select(code => code.GetString()!).ToArray()
            }).ToArray(),
            observedAt = payload.GetProperty("observedAt").GetDateTimeOffset(),
            journalCheckpoint = RequiredString(payload, "journalCheckpoint")
        }, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(businessContent)).ToLowerInvariant();
    }

    /// <summary>
    /// A line's identity with its session generation taken out, the one envelope field a resend into a
    /// later session changes. Everything else, sentAt included, still has to match.
    /// </summary>
    private static string ReplayIdentityHash(string line)
    {
        JsonNode root = JsonNode.Parse(line)
            ?? throw new InvalidDataException("Protocol line JSON cannot be empty.");
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
