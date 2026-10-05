using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Transport;

public sealed partial class OnboardMessageProcessor(
    ControlServerDbContext dbContext,
    WireToGateStore store,
    OnboardRecoveryCoordinator recoveryCoordinator,
    OnboardAlarmProjectionStore alarmStore,
    SlotConfigurationActivationDispatcher activationDispatcher,
    TimeProvider timeProvider,
    IConfiguration configuration,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    ILogger<OnboardMessageProcessor> logger,
    Runtime.Fleet.VehicleRoster? fleet = null,
    Runtime.Charging.ManualStationClearance? stationClearance = null,
    Runtime.Charging.UnableToChargeFieldConfirmations? fieldConfirmations = null)
{
    // The host's one roster (a singleton); a processor built without it, as the tests build it, reads the same options.
    private readonly Runtime.Fleet.VehicleRoster _fleet = fleet ?? new Runtime.Fleet.VehicleRoster(runtimeOptions);

    // The envelope's own settings, not a second copy of them: this instance also hashes the business
    // content the peer hashes, and the two only agree while both use the same serializer settings.
    private static readonly JsonSerializerOptions SerializerOptions = ProtocolEnvelope.SerializerOptions;
    private readonly string _serverInstanceId = Guid.NewGuid().ToString("D");

    public async Task<string> ProcessAsync(
        string line,
        OnboardConnectionState state,
        CancellationToken cancellationToken)
    {
        // Every message starts from the database. OnboardTcpServer opens one scope, and so one
        // DbContext, for as long as a TCP connection lives, while the runtime worker writes the same
        // journeys, operations and demands from a context of its own on every pass. Whatever an earlier
        // message tracked here, a later query would hand back as it stood then: a load correction asked
        // for once while the load was still running was refused inside the one window REQ-0237 allows
        // it in, because the journey and the operation were still the copies that connection first saw
        // (8005-agv-control-server#28, fixed on the MVP line as #40). Clearing here rather than before
        // FlushDeferredOutboundAsync keeps this message's own state for its deferred send. It also stops
        // the tracked set growing for the life of the connection.
        dbContext.ChangeTracker.Clear();
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
                                acceptedProtocolReleaseIdentity = ProtocolEnvelope.ReleaseIdentity(),
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
                            reasonCode = ServerReasonCodes.ProtocolReleaseIdentityMismatch,
                            fieldPath = "payload.protocolReleaseIdentity",
                            displayMessage = error.Message
                        },
                        expectedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                        expectedProtocolReleaseIdentity = ProtocolEnvelope.ReleaseIdentity()
                    });
            }

            using JsonDocument accepted = JsonDocument.Parse(response);
            state.AgvId = agvId;
            state.SessionGeneration = accepted.RootElement.GetProperty("sessionGeneration").GetInt64();
            state.HandshakeCompleted = false;
            state.SafetySnapshotRequestDue = false;
            return response;
        }

        ValidateEnvelopeIdentity(root);
        RequireCurrentSession(root, state, agvId);
        string persistedRequest = messageType == "ExceptionRecoverySessionRequested"
            ? RedactRecoveryAuthenticationProof(line)
            : line;
        // A durable message resent into a later session differs from its first line in sessionGeneration
        // alone: ADR-cross-0030 has a resend keep its messageId, and the onboard rebinds a message whose
        // DurableAck it never got to the new session (8005-agv-control-server#30). One equivalence test
        // decides that for every message type -- GenerationRebindReplayHash, the only one there is.
        // What differs per type is what the equivalent resend is answered with. A RecoveryStateReport is
        // applied again in the new session, and an OperationResult is processed again so the session's
        // pending-result list is reconciled (CV-OPERATION-RESULT-UNKNOWN-RECONCILE); every other durable
        // message is answered from its first acceptance without touching business state a second time.
        bool reprocessedInTheNewSession = messageType is "RecoveryStateReport" or "OperationResult";
        string capturedResponse;
        // control-server#452: a field confirmation reads RIoT before the inbox transaction below takes the write lock, and is
        // judged inside it only if the database still says what it said then. If not, nothing was written: the whole inbox
        // transaction rolls back -- no inbox row, no answer -- and the observation is made again. Twice at most, as the
        // decisions themselves retry; then the same exception they throw, which ends the connection as it always did.
        for (int attempt = 0; ; attempt++)
        {
            FieldConfirmationObservation? observed = await ObserveFieldConfirmationAsync(
                root, state, messageType, messageId, cancellationToken).ConfigureAwait(false);
            try
            {
                capturedResponse = await store.CaptureFirstResponseAsync(
                    messageId,
                    messageType,
                    persistedRequest,
                    contentHash,
                    () => ProcessCurrentSessionMessageAsync(
                        root, state, messageType, messageId, contentHash, observed, cancellationToken),
                    timeProvider.GetUtcNow(),
                    cancellationToken,
                    GenerationRebindReplayHash,
                    messageType == "RecoveryStateReport"
                        ? response => RestoreAcceptedSnapshotVersions(response, state)
                        : null,
                    reprocessedInTheNewSession
                        ? null
                        : firstResponse => RebindDurableAckAsync(
                            firstResponse, messageType, messageId, agvId, contentHash, state, cancellationToken))
                    .ConfigureAwait(false);
                break;
            }
            catch (FieldConfirmationObservationStaleException stale)
            {
                dbContext.ChangeTracker.Clear();
                if (attempt == 2)
                {
                    throw new InvalidOperationException(stale.Message);
                }
            }
            catch (InboundMessageRejectedException rejected)
            {
                // control-server#478: the inbound boundary. A message the server read and will not take is answered with a
                // ProtocolProblem correlated to it, and the connection stays. Until then this reached OnboardTcpServer's
                // catch-all and closed the connection, and the onboard replays an unacknowledged message in every handshake,
                // so one conflicting message ended every reconnect at the same place.
                //
                // Nothing of this message is kept: the inbox transaction did not commit, and what it tracked is cleared here
                // so the next message on this connection does not save it. Nor is anything sent after it: no deferred
                // recovery send, no safety snapshot request -- those belong to a message that was taken.
                //
                // Only this type. ProtocolContentConflictException is also the server's own outbound bookkeeping failing,
                // which is not the vehicle's to be told about (see InboundMessageRejectedException).
                dbContext.ChangeTracker.Clear();
                LogInboundRejected(logger, agvId, messageType, messageId, rejected.ReasonCode, rejected.Message);
                string problem = SerializeEnvelope(
                    "ProtocolProblem",
                    messageId,
                    agvId,
                    state.SessionGeneration,
                    new
                    {
                        rejectedMessageId = messageId,
                        rejectedMessageType = messageType,
                        problem = new
                        {
                            reasonCode = rejected.ReasonCode,
                            fieldPath = (string?)null,
                            displayMessage = rejected.Message
                        },
                        expectedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                        expectedProfileId = ProtocolCandidateIdentity.ProfileId,
                        expectedProtocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256
                    });
                return await AfterRefusedBaselineAsync(problem, messageType, messageId, agvId, state, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        // Never inside the handshake (control-server#202). A reconnecting vehicle resends what the last session left
        // unacknowledged and reads exactly one answer per line until its recovery report is answered, so a command
        // or session snapshot sent after one of these answers would be read in place of the next one, and the
        // vehicle would drop the connection. Nothing is lost by holding them: the recovery report's replay sends every
        // open recovery command and every unacknowledged session snapshot, rebound to the new session -- including
        // the CLOSED snapshot of a session a resent refusal or result closed, which the vehicle needs to clear its
        // own record of it (onboard-hmi#123, #129). Decided here, once, for the deferred send as well; flushing
        // reads no state of its own.
        bool triggersRecoverySend = state.HandshakeCompleted &&
                                    (OnboardRecoveryCoordinator.IsRecoveryRequest(messageType) ||
                                     OnboardRecoveryCoordinator.IsRecoveryResult(messageType) ||
                                     messageType == "OperationResult" ||
                                     messageType == "SlotOperationCommandRejected");
        bool replaysPendingRecovery = messageType == "RecoveryStateReport";
        // 迟到扫码的过时答复（control-server#324）走同一个时机：握手之外、本条应答之后。
        bool answersLateSublot = state.HandshakeCompleted && messageType == "SublotSubmitted";
        if ((triggersRecoverySend || replaysPendingRecovery || answersLateSublot) && state.DeferOutboundUntilResponseWritten)
        {
            state.DeferredRecoveryLine = line;
        }
        else if (answersLateSublot)
        {
            await recoveryCoordinator.SendLateSublotRejectionAsync(root, cancellationToken).ConfigureAwait(false);
        }
        else if (triggersRecoverySend)
        {
            await recoveryCoordinator.SendTriggeredCommandAsync(root, cancellationToken).ConfigureAwait(false);
        }
        else if (replaysPendingRecovery)
        {
            await recoveryCoordinator.ReplayPendingCommandsAsync(
                agvId,
                state.SessionGeneration!.Value,
                cancellationToken).ConfigureAwait(false);
        }
        // A rebuild after a cleared fault with cargo on board waits for the vehicle to show the cargo in its slots (REQ-0362,
        // control-server#318): ask for a snapshot. Recorded only here, where AppendSafetySnapshotRequest is sure to send it --
        // past the handshake, and not on the recovery report that ends it, whose answer the vehicle still reads as the handshake's.
        // A repair release whose record is in waits for readings received after it (control-server#385). The record's own
        // answer asks for them; after a reconnect nothing would, so the first message past the new handshake asks once per
        // connection. Not the recovery report that ends the handshake, for the reason given just below.
        if (state.HandshakeCompleted && messageType is not ("RecoveryStateReport" or "HardwareRecoveryRecordSubmitted") &&
            !state.ReleaseReadingsRequested)
        {
            state.ReleaseReadingsRequested = true;
            if (await recoveryCoordinator.ReleaseAwaitsReadingsAsync(agvId, cancellationToken).ConfigureAwait(false))
            {
                state.SafetySnapshotRequestDue = true;
                state.SafetySnapshotRequestReason = "PRE_MOVEMENT_RECONCILIATION";
            }
        }
        if (messageType == "HardwareRecoveryRecordSubmitted")
        {
            state.ReleaseReadingsRequested = true;
        }
        if (state.HandshakeCompleted && messageType != "RecoveryStateReport" &&
            await OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync(
                dbContext, agvId, state.SessionGeneration!.Value, state.Readiness == SessionReadiness.Ready,
                timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
        {
            state.SafetySnapshotRequestDue = true;
        }
        // control-server#478: while a refused safety message leaves the baseline untrusted, ask again on the vehicle's next
        // messages. The request sent with the refusal is usually not answered: the onboard leaves a snapshot request
        // unanswered while it still holds an unacknowledged SafetyStateChanged -- which the refused one is -- and expects
        // the server to ask again (8005-agv-onboard-hmi WireToGateBusinessService.AnswerSafetyStateSnapshotRequestAsync).
        // Spaced by SafetySnapshotReaskInterval so a heartbeat every two seconds does not become a request every two seconds.
        if (state.SafetyBaselineUntrusted &&
            timeProvider.GetUtcNow() - state.SafetySnapshotRequestedAt >= SafetySnapshotReaskInterval)
        {
            state.SafetySnapshotRequestDue = true;
        }
        return AppendSafetySnapshotRequest(capturedResponse, state);
    }

    /// <summary>
    /// How long an untrusted safety baseline waits before the server asks again for a snapshot (control-server#478).
    /// </summary>
    internal static readonly TimeSpan SafetySnapshotReaskInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Asks the vehicle for a fresh SafetyStateSnapshot when this message made one due (REQ-0358,
    /// control-server#142): an expected-action-overdue alarm newly appeared, or a safety change named a
    /// slot that is overdue. The dashboard shows that slot's lock, light curtain and unlock output, and
    /// only a snapshot carries them -- SafetyStateChanged names the slots that changed, not their state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never inside the handshake. There the vehicle sends capability, safety, alarms and its recovery
    /// report one at a time and reads one answer after each, so a request slipped in between would be read
    /// as the next answer and break the handshake; and the safety snapshot it just sent is fresh anyway.
    /// The request is appended after this message's own answer, outside the first-response capture, so a
    /// resent message never asks twice.
    /// </para>
    /// <para>
    /// Once per message, by design: one physical action that the vehicle reports as several safety changes -- a door
    /// closing on an overdue slot comes as about four SafetyStateChanged within a second -- asks for a snapshot after
    /// each (control-server#167, looked into and kept by the user's decision of 2026-09-19 in control-server#202).
    /// Merging them is not free: <c>L2-EAO-13</c> of control-server#204 relies on the server asking again after a
    /// change that follows an answered request.
    /// </para>
    /// <para>
    /// <c>VERSION_GAP</c> with no requested version: the slot states this server holds are those of the
    /// session's first snapshot, behind the session's current safetyStateVersion, which is the gap the
    /// protocol names. The vehicle answers with the next safetyStateVersion (hmi#109).
    /// </para>
    /// </remarks>
    private string AppendSafetySnapshotRequest(string response, OnboardConnectionState state)
    {
        if (!state.SafetySnapshotRequestDue)
        {
            return response;
        }
        state.SafetySnapshotRequestDue = false;
        string reason = state.SafetySnapshotRequestReason ?? "VERSION_GAP";
        state.SafetySnapshotRequestReason = null;
        if (!state.HandshakeCompleted)
        {
            return response;
        }
        state.SafetySnapshotRequestedAt = timeProvider.GetUtcNow();
        string request = SerializeEnvelope(
            "SafetyStateSnapshotRequested",
            correlationId: null,
            state.AgvId!,
            state.SessionGeneration,
            new
            {
                requestedSafetyStateVersion = (long?)null,
                reason
            });
        return string.IsNullOrWhiteSpace(response) ? request : $"{response}\n{request}";
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
            messageType == "OperationResult" ||
            messageType == "SlotOperationCommandRejected")
        {
            await recoveryCoordinator.SendTriggeredCommandAsync(root, cancellationToken).ConfigureAwait(false);
        }
        else if (messageType == "SublotSubmitted")
        {
            await recoveryCoordinator.SendLateSublotRejectionAsync(root, cancellationToken).ConfigureAwait(false);
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
        FieldConfirmationObservation? observed,
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
                    string reportedFingerprint = RequiredString(
                        payload, SlotConfigurationActivationDelivery.CapabilityFingerprintField);
                    // 协议 v2 在 CapabilitySnapshot 上加了 activeSlotConfigurationFingerprint：车报的
                    // 是它此刻装着哪一版仓位配置。核验照做、不一致照样写治理审计，**但结论不再是拒收**。
                    //
                    // 2026-09-10 改的。原来不一致就回 ProtocolProblem、会话不建立，理由是 fail-closed；
                    // G3 跑出来的后果是一台被动过配置的车永远上不了线——而唯一能把它改回来的手段，
                    // 下发一次激活，要走会话。不一致本身堵死了修复不一致的那条路，人必须到车前。
                    // 证据在 evidence/g3/20260910-fp-is-14-fingerprint-mismatch。
                    //
                    // 现在会话照建，车报的那份指纹存进会话行，由 DecideReadinessAsync 与服务端认定的
                    // 那一版比对：不一致的车拿不到业务就绪、不会被派活，但连接在，激活下得去。
                    // fail-closed 的实质保住了，关掉的只是「连都不让连」那一层。
                    await activationDispatcher.ReconcileReportedFingerprintAsync(
                        agvId,
                        reportedFingerprint,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    await store.ApplyCapabilitySnapshotAsync(
                        agvId, generation, revision, contentHash, reportedFingerprint,
                        cancellationToken).ConfigureAwait(false);
                    state.CapabilityRevision = revision;
                    return SnapshotAck(messageId, agvId, generation, "CAPABILITY", revision, contentHash);
                }
            // 协议 v2 消息 8。RELIABLE 而不是 RESPONSE：REQ-0264 的「不能猜测成功」正是
            // PENDING_RESULT_REPLAY 存在的理由，用 RESPONSE 就没有补报语义，断线即丢。补报的幂等
            // 在 #15 的 RecordResultAsync 里——同一次激活报两次，第二次原样返回已收敛的那一行。
            case "SlotConfigurationActivationResult":
                {
                    await activationDispatcher.RecordResultAsync(
                        SlotConfigurationActivationWire.Result(payload),
                        cancellationToken).ConfigureAwait(false);
                    return DurableAck(messageType, messageId, agvId, generation, contentHash);
                }
            // 协议 v2 消息 9。SNAPSHOT，不是 RELIABLE，也不是事件流：后一份整体取代前一份，所以
            // 断线重连之后不需要知道漏了什么。#16 的 RecordSnapshotAsync 已经处理了「序号回退的
            // 快照忽略掉」，这里不再判一次。
            case "OnboardAlarmSnapshot":
                {
                    long revision = OnboardAlarmSnapshotWire.Revision(payload);
                    HashSet<string> overdueBefore = OverdueIdentities(
                        await alarmStore.ReadExpectedActionOverdueAsync(agvId, cancellationToken).ConfigureAwait(false));
                    await alarmStore.RecordSnapshotAsync(
                        OnboardAlarmSnapshotWire.Read(agvId, payload),
                        generation,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    // A snapshot that was not adopted leaves the stored one as it was, so nothing is new.
                    HashSet<string> overdueAfter = OverdueIdentities(
                        await alarmStore.ReadExpectedActionOverdueAsync(agvId, cancellationToken).ConfigureAwait(false));
                    if (!overdueAfter.IsSubsetOf(overdueBefore))
                    {
                        state.SafetySnapshotRequestDue = true;
                    }
                    return SnapshotAck(messageId, agvId, generation, "ONBOARD_ALARM", revision, contentHash);
                }
            case "SafetyStateSnapshot":
                {
                    long revision = payload.GetProperty("safetyStateVersion").GetInt64();
                    JsonElement safety = payload.GetProperty("safety");
                    bool departureSafe = safety.GetProperty("departureSafe").GetBoolean();
                    // The handshake's snapshot is the session's safety baseline. One after it is the vehicle
                    // answering SafetyStateSnapshotRequested (control-server#142) at the next safetyStateVersion:
                    // a safety change like SafetyStateChanged, carried by the one message that also holds slot
                    // states. It takes the same path -- the revision rules are not relaxed, a revision with
                    // different content or a revision going backwards is still a content conflict -- and, like
                    // SafetyStateChanged, has readiness decided again rather than left at HANDSHAKE_INCOMPLETE,
                    // which ApplySafetySnapshotAsync writes and only the handshake's recovery report clears.
                    //
                    // Mid-session also needs this connection's handshake to be done -- the same test that decides
                    // whether the server may send a request. Inside the handshake the vehicle reads one answer per
                    // message it sends, so a SessionReadiness line added here would be read as the next answer, and a
                    // baseline already on file does not by itself mean the handshake is over.
                    bool midSession = state.HandshakeCompleted &&
                                      await store.HasSafetyBaselineAsync(agvId, generation, cancellationToken)
                                          .ConfigureAwait(false);
                    await store.ApplySafetySnapshotAsync(
                        agvId, generation, revision, departureSafe, contentHash, cancellationToken,
                        SafetyReasonCodes(safety), SafetyUnknownPresent(safety)).ConfigureAwait(false);
                    state.SafetyRevision = revision;
                    // An accepted snapshot is a whole new safety baseline, slot states included: what a refused safety
                    // message left untrusted is trusted again from here (control-server#478).
                    state.SafetyBaselineUntrusted = false;
                    string snapshotAck = SnapshotAck(messageId, agvId, generation, "SAFETY_STATE", revision, contentHash);
                    if (!midSession)
                    {
                        return snapshotAck;
                    }
                    // A repair release waiting for readings takes the first mid-session snapshot as its readings
                    // (control-server#385): proven, the HOLD_RELEASE check is staged with this answer and goes out after it.
                    string? holdReleaseCheck = await recoveryCoordinator.ObserveReleaseReadingsAsync(
                        agvId, generation, payload, cancellationToken).ConfigureAwait(false);
                    SessionReadinessDecision snapshotDecision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    string snapshotAnswer = AnswerWithReadiness(
                        snapshotAck, snapshotDecision, agvId, generation, state, messageType, messageId, announceUnchanged: true);
                    return holdReleaseCheck is null ? snapshotAnswer : $"{snapshotAnswer}\n{holdReleaseCheck}";
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
                    ReportedPendingResult[] pendingResults = payload.GetProperty("pendingResults")
                        .EnumerateArray()
                        .Select(item => new ReportedPendingResult(
                            RequiredString(item, "messageId"), RequiredString(item, "contentSha256")))
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
                    // The recovery report is the last thing the vehicle sends in its handshake; from its answer
                    // on, the vehicle reads the connection in its receive loop and may be asked for things.
                    state.HandshakeCompleted = true;
                    string ack = SerializeEnvelope(
                        "DurableAck", messageId, agvId, generation,
                        new
                        {
                            acceptedMessageId = messageId,
                            acceptedMessageType = messageType,
                            acceptedContentSha256 = contentHash,
                            durablyAcceptedAt = timeProvider.GetUtcNow()
                        });
                    return $"{ack}\n{SessionReadinessLine(decision, agvId, generation, state, messageType, messageId)}";
                }
            case "OperationProgress":
                return DurableAck(messageType, messageId, agvId, generation, contentHash);
            case "PreDepartureSafetyCheckResult":
                {
                    string checkAck = DurableAck(messageType, messageId, agvId, generation, contentHash);
                    // DEPARTURE answers are read by the journey runtime from the inbox (FindSafeDepartureResultAsync, which
                    // since control-server#382 takes DEPARTURE only). An answer correlated to a repair release's check is the
                    // last step of that release (control-server#385), whatever purpose it says: judged by the coordinator,
                    // which spends the release on anything but a SAFE HOLD_RELEASE answer to its own check (review M1). When
                    // it lifts the hold, readiness is decided again and the released business state goes out after the answer.
                    string? releaseActionId = await recoveryCoordinator.ObserveHoldReleaseCheckResultAsync(
                        root, cancellationToken).ConfigureAwait(false);
                    if (releaseActionId is null)
                    {
                        return checkAck;
                    }
                    SessionReadinessDecision releaseDecision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    string? released = await recoveryCoordinator.StageDoorReleaseBusinessStateAsync(
                        agvId, generation, releaseActionId, releaseDecision.Readiness == SessionReadiness.Ready,
                        cancellationToken).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    string releaseAnswer = AnswerWithReadiness(
                        checkAck, releaseDecision, agvId, generation, state, messageType, messageId, announceUnchanged: false);
                    return released is null ? releaseAnswer : $"{releaseAnswer}\n{released}";
                }
            case "SublotSubmitted":
                // 这一站已经结束之后才到的扫码，在这里答过时，否则永远没人回答（control-server#324）；答复在应答之后发。
                // 这一站仍在等录入时什么也不做，留给引擎——见 LateSublotSubmission。
                await LateSublotSubmission.StageRejectionIfStopEndedAsync(
                    dbContext, root, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return DurableAck(messageType, messageId, agvId, generation, contentHash);
            case "SlotOperationCommandRejected":
                // A refused resume command closes its recovery session (control-server#187); every other
                // refusal is only acknowledged. The closing snapshot goes out with the triggered sends below, or, for a
                // refusal resent inside a reconnect handshake, with the recovery report's replay (control-server#202).
                await recoveryCoordinator.ObserveCommandRejectedAsync(root, cancellationToken).ConfigureAwait(false);
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
                        throw new InboundMessageRejectedException(ServerReasonCodes.ContentHashMismatch,
                            "OperationResult resultContentSha256 does not match its business content.");
                    }
                    long? forcedGeneration = await store.FindOperationForcedRecoveryGenerationAsync(
                        attemptId, cancellationToken).ConfigureAwait(false);
                    StationOperationResult stationResult = new(
                        messageId,
                        attemptId,
                        demandId,
                        operationType,
                        overallOutcome,
                        evidence,
                        slotResults.All(item => RequiredString(item, "outcome") == "COMPLETED"),
                        payload.GetProperty("observedAt").GetDateTimeOffset(),
                        resultContentSha256,
                        contentHash,
                        SlotOutcomeReport.FromSlotResults(payload.GetProperty("slotResults")));
                    OperationResultReceipt receipt = await ReceiptAsync(demandId, cancellationToken)
                        .ConfigureAwait(false);
                    OperationResultDisposition disposition;
                    if (forcedGeneration is long knownGeneration)
                    {
                        disposition = await store.ApplyOperationResultAsync(
                            stationResult, agvId, knownGeneration, cancellationToken, receipt).ConfigureAwait(false);
                    }
                    else
                    {
                        // An attempt this server never commanded (control-server#481): acknowledged and kept as evidence,
                        // since the vehicle replays the result until it is acknowledged. The line itself stays in the inbox.
                        disposition = await store.RecordUnknownOperationResultAsync(stationResult, agvId, cancellationToken)
                            .ConfigureAwait(false);
                        LogUnknownOperationResult(
                            logger, agvId, attemptId, demandId, messageId, resultContentSha256, contentHash);
                    }
                    if (disposition is OperationResultDisposition.FailedBeforeStationDeadline
                        or OperationResultDisposition.FailureReasonWithoutTerminalState)
                    {
                        // Not accepted silently (ADR-cross-0058 Verification, decision 5): a failure the server
                        // may only settle after the stop's deadline, or only for a reason with a terminal
                        // state, went to recovery instead, and the operator is told why.
                        LogUnsettleableDeterminateFailure(
                            logger,
                            agvId,
                            demandId,
                            attemptId,
                            disposition == OperationResultDisposition.FailedBeforeStationDeadline
                                ? DeterminateLoadFailure.FailedBeforeStationDeadline
                                : DeterminateLoadFailure.ReasonWithoutTerminalState,
                            receipt.ReceivedAt,
                            receipt.StationDepartureDeadline);
                    }
                    await recoveryCoordinator.ObserveOperationResultAsync(
                        attemptId, disposition, cancellationToken).ConfigureAwait(false);
                    // A result this session's RecoveryStateReport named as pending has now been seen,
                    // whatever the verdict on it. The verdict itself stays with the operation.
                    await store.ReconcileReportedPendingResultAsync(
                        agvId, generation, messageId, cancellationToken).ConfigureAwait(false);
                    await store.SettleReportedAttemptsAsync(agvId, generation, cancellationToken)
                        .ConfigureAwait(false);
                    // Applying a result is the moment the server's own verdict changes: a result it
                    // refuses puts the operation into RecoveryRequired, and readiness has to follow.
                    // It did not until 2026-09-04 -- readiness was recomputed only on
                    // RecoveryStateReport and SafetyStateChanged, neither of which the vehicle sends
                    // afterwards, so the session stayed READY over a load that needed recovery and
                    // the onboard never showed its recovery entry.
                    SessionReadinessDecision resultDecision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    string resultAck = DurableAck(messageType, messageId, agvId, generation, contentHash);
                    // Only when readiness actually changes, so the ordinary path keeps its one-line
                    // answer: a completed unload on a ready session is still just its DurableAck.
                    //
                    // Into RecoveryRequired since 2026-09-04 (the vehicle was never told it needed
                    // recovery). Back to READY since 2026-09-14: the onboard does not send another
                    // RecoveryStateReport after a resume commits, so the READY a settled attempt
                    // produces reached nobody and the vehicle stayed out of work (G3 FP-IS-07
                    // resume-007). This is the widening the earlier note here asked for.
                    return AnswerWithReadiness(
                        resultAck, resultDecision, agvId, generation, state, messageType, messageId, announceUnchanged: false);
                }
            case "HardwareRecoveryRecordSubmitted":
                {
                    string recordResult = await recoveryCoordinator.ProcessRequestAsync(root, contentHash, cancellationToken)
                        .ConfigureAwait(false);
                    // The record is what a forced mechanical recovery's readiness hold waits for
                    // (control-server#137), so readiness is judged again here and announced only on a
                    // change, as after a recovery result. The record resumes nothing by itself.
                    SessionReadinessDecision recordDecision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    return AnswerWithReadiness(
                        recordResult, recordDecision, agvId, generation, state, messageType, messageId, announceUnchanged: false);
                }
            case "ExceptionRecoverySessionRequested":
            case "RecoveryActionSubmitted":
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
                    string recoveryAck = await recoveryCoordinator.ProcessResultAsync(root, contentHash, cancellationToken)
                        .ConfigureAwait(false);
                    // A reconciled recovery settles the operation it was about. Until 2026-09-14 no
                    // recovery result decided readiness, so after a compensation the session still read
                    // OPERATION_RECOVERY_REQUIRED (G3 FP-IS-07 compensate-001). Announced the same way,
                    // and only on a change, as an OperationResult is.
                    await store.SettleReportedAttemptsAsync(agvId, generation, cancellationToken)
                        .ConfigureAwait(false);
                    SessionReadinessDecision recoveryDecision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    return AnswerWithReadiness(
                        recoveryAck, recoveryDecision, agvId, generation, state, messageType, messageId, announceUnchanged: false);
                }
            case "SlotFaultDeclarationResult":
                // REQ-0359, control-server#383. It settles the declaration and its command's outbox line (control-server#384),
                // not the operation: an APPLIED declaration's effect on the
                // operation arrives as the OperationResult the vehicle sends next, settled by the case above. Nothing here
                // throws for a result that matches no pending declaration -- see SlotFaultDeclarationResults.
                await SlotFaultDeclarationResults.RecordAsync(
                    dbContext, store, agvId, messageId, payload, timeProvider.GetUtcNow(), logger, cancellationToken)
                    .ConfigureAwait(false);
                return DurableAck(messageType, messageId, agvId, generation, contentHash);
            case "ManualChargingReturnToServiceRequested":
                {
                    // Not a recovery request despite the administrator context it carries: the
                    // manifest gives it recoveryRole NONE, no exception recovery session is
                    // involved, and it is answered inline rather than through
                    // OnboardRecoveryCoordinator.
                    string requestId = RequiredUuid(payload, "requestId");
                    ManualChargingReturnToServiceDecision decision =
                        await store.DecideManualChargingReturnToServiceAsync(
                            new ManualChargingReturnToServiceRequest(
                                requestId,
                                agvId,
                                generation,
                                messageId,
                                WireContentHash.Sha256(payload.GetRawText()),
                                RequiredString(payload.GetProperty("administrator"), "operatorId"),
                                RequiredString(payload, "administratorRole"),
                                RequiredString(payload, "reason"),
                                NullableDouble(payload, "observedBatteryPercent"),
                                // control-server#404: the server's manual-charging hold is kept per RIoT vehicle key, and the
                                // request names the vehicle by its AGV id. Resolved from the fleet roster here, so an accepted
                                // request lifts the hold in the decision's own save.
                                _fleet.ByAgvId(agvId)?.VehicleKey,
                                timeProvider.GetUtcNow()),
                            cancellationToken).ConfigureAwait(false);
                    return SerializeEnvelope(
                        "ManualChargingReturnToServiceResult", messageId, agvId, generation,
                        new
                        {
                            requestId,
                            outcome = decision.Outcome,
                            problem = decision.ProblemReasonCode is null
                                ? null
                                : new
                                {
                                    reasonCode = decision.ProblemReasonCode,
                                    fieldPath = decision.ProblemFieldPath,
                                    displayMessage = decision.ProblemDisplayMessage
                                },
                            vehicleBusinessStateRevision = decision.VehicleBusinessStateRevision
                        });
                }
            case "ManualStationClearanceConfirmationRequested" when stationClearance is not null:
                {
                    // Batch 9-08 (control-server#406): answered inline like the return to service above. The whole decision is
                    // Runtime.Charging.ManualStationClearance's, shared with the Host entry; the digest is the payload's alone, so
                    // a resubmission under a new messageId is the same request (coordinator's alignment of 09-30, item 2).
                    // control-server#452: judged on what was observed outside the write lock (ProcessAsync), never reading RIoT
                    // here; an observation the database no longer matches rolls the inbox transaction back.
                    ManualStationClearanceRequest request = ManualStationClearanceRequestOf(payload, agvId, generation, messageId);
                    string confirmationRequestId = request.ConfirmationRequestId;
                    ManualStationClearanceConfirmation clearance;
                    try
                    {
                        clearance = await stationClearance.DecideObservedAsync(
                                request,
                                observed?.StationClearance ?? throw Stale(ManualStationClearance.LostEveryRace(confirmationRequestId)),
                                cancellationToken).ConfigureAwait(false)
                            ?? throw Stale(ManualStationClearance.LostEveryRace(confirmationRequestId));
                    }
                    catch (FieldConfirmationContentConflictException conflict)
                    {
                        throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict, conflict.Message);
                    }
                    return SerializeEnvelope(
                        "ManualStationClearanceConfirmationResult", messageId, agvId, generation,
                        new
                        {
                            confirmationRequestId,
                            outcome = clearance.Decision.Outcome,
                            problem = clearance.Decision.ProblemReasonCode is null
                                ? null
                                : new
                                {
                                    reasonCode = clearance.Decision.ProblemReasonCode,
                                    fieldPath = clearance.Decision.ProblemFieldPath,
                                    displayMessage = clearance.Decision.ProblemDisplayMessage
                                },
                            stationReleased = clearance.StationReleased
                        });
                }
            case "UnableToChargeFieldConfirmationRequested" when fieldConfirmations is not null:
                {
                    // Batch 9-12 (control-server#410): answered inline like the clearance above, and decided by
                    // Runtime.Charging.UnableToChargeFieldConfirmations. The digest is the payload's alone, so a resubmission under a
                    // new messageId, sentAt or session generation is the same request. The VehicleBusinessStateSnapshot that follows a
                    // confirmation is the engine's next round's, as after a return to service.
                    // control-server#452: as the clearance above.
                    UnableToChargeFieldConfirmationRequest request = UnableToChargeRequestOf(payload, agvId, generation, messageId);
                    string confirmationRequestId = request.ConfirmationRequestId;
                    UnableToChargeFieldConfirmation confirmation;
                    try
                    {
                        confirmation = await fieldConfirmations.DecideObservedAsync(
                                request,
                                observed?.UnableToCharge
                                    ?? throw Stale(UnableToChargeFieldConfirmations.LostEveryRace(confirmationRequestId)),
                                cancellationToken).ConfigureAwait(false)
                            ?? throw Stale(UnableToChargeFieldConfirmations.LostEveryRace(confirmationRequestId));
                    }
                    catch (FieldConfirmationContentConflictException conflict)
                    {
                        throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict, conflict.Message);
                    }
                    return SerializeEnvelope(
                        "UnableToChargeFieldConfirmationResult", messageId, agvId, generation,
                        new
                        {
                            confirmationRequestId,
                            outcome = confirmation.Decision.Outcome,
                            problem = confirmation.Decision.ProblemReasonCode is null
                                ? null
                                : new
                                {
                                    reasonCode = confirmation.Decision.ProblemReasonCode,
                                    fieldPath = confirmation.Decision.ProblemFieldPath,
                                    displayMessage = confirmation.Decision.ProblemDisplayMessage
                                },
                            chargingPolicyDecision = confirmation.ChargingPolicyDecision
                        });
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
                    if (state.SafetyBaselineUntrusted)
                    {
                        // Taken and acknowledged as always, but a change is not a baseline: it names what changed, not the
                        // state of every slot. The departure verdict stays untrusted until a SafetyStateSnapshot arrives
                        // (control-server#478).
                        await store.DistrustSafetyBaselineAsync(agvId, generation, cancellationToken).ConfigureAwait(false);
                    }
                    SessionReadinessDecision decision = await store.DecideReadinessAsync(
                        agvId, generation, cancellationToken).ConfigureAwait(false);
                    if (await AffectsAnOverdueSlotAsync(agvId, payload, cancellationToken).ConfigureAwait(false))
                    {
                        state.SafetySnapshotRequestDue = true;
                    }
                    string ack = DurableAck(messageType, messageId, agvId, generation, contentHash);
                    return AnswerWithReadiness(ack, decision, agvId, generation, state, messageType, messageId, announceUnchanged: true);
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
                    // The protocol defines ProtocolProblem as how a peer reports that it
                    // rejected one of our messages. Falling through to "unsupported" threw, which
                    // killed the transport and sent the session into a reconnect loop -- and threw
                    // away the only diagnostic saying why the rejection happened. It carries no
                    // obligation, so record it and keep the session; the envelope itself is
                    // already persisted in the inbox by the caller.
                    JsonElement problem = payload.GetProperty("problem");
                    // One refusal carries an obligation after all (control-server#385 review M1): the vehicle refusing a
                    // repair release's HOLD_RELEASE check sends no result, so without this the release would wait forever.
                    await recoveryCoordinator.ObserveReleaseCheckRefusedAsync(
                        agvId, RequiredString(payload, "rejectedMessageId"), RequiredString(problem, "reasonCode"),
                        cancellationToken).ConfigureAwait(false);
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
    /// An answer, followed by a SessionReadiness line when this connection may be told its readiness now: the one
    /// place an answer takes a readiness line after it (control-server#340). <paramref name="state"/>'s readiness is
    /// always brought up to <paramref name="decision"/>; the line goes out when <paramref name="announceUnchanged"/>
    /// or when that readiness changed, and never before the handshake is done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never inside the handshake</b>, for the reason <see cref="AppendSafetySnapshotRequest"/> and the triggered
    /// recovery sends give (control-server#202): until its recovery report is answered, the vehicle reads exactly one
    /// answer for each line it sends. A readiness line after the DurableAck of a message it resent there is read in
    /// place of the answer to its next line -- onboard-hmi#204 saw it after a SafetyStateChanged, read where the
    /// capability snapshot's SnapshotAppliedAck belonged, and the vehicle dropped the connection. Five sites appended
    /// readiness themselves until #340, none of them looking at the handshake; they go through here now, and
    /// <c>OnboardHandshakeReadinessArchitectureTests</c> keeps a sixth from appending one on its own.
    /// </para>
    /// <para>
    /// <b>Nothing held back is lost.</b> The recovery report's answer ends the handshake and always carries readiness,
    /// decided then, so it covers whatever a message resent inside the handshake did to it. That answer is built
    /// where the report is taken, after it sets <c>HandshakeCompleted</c>, and is the one readiness line this class
    /// sends without coming through here.
    /// </para>
    /// <para>
    /// <b>The connection's readiness is still brought up to date inside the handshake.</b> It is only read here, to
    /// tell a change from none, and the recovery report overwrites it and announces unconditionally, so inside the
    /// handshake the choice changes no line on the wire. Keeping it current is the choice that leaves it meaning one
    /// thing -- the latest decision -- rather than two.
    /// </para>
    /// </remarks>
    private string AnswerWithReadiness(
        string answer,
        SessionReadinessDecision decision,
        string agvId,
        long generation,
        OnboardConnectionState state,
        string answeredMessageType,
        string answeredMessageId,
        bool announceUnchanged)
    {
        bool changed = decision.Readiness != state.Readiness;
        state.Readiness = decision.Readiness;
        if (!state.HandshakeCompleted)
        {
            if (changed)
            {
                // A change held back here is announced by the recovery report's answer; logged so that a readiness the
                // vehicle never showed can be told apart from one this server never sent (control-server#380).
                LogSessionReadinessHeldInHandshake(
                    logger, agvId, generation, ReadinessOnTheWire(decision), ReasonCodeOnTheWire(decision),
                    answeredMessageType, answeredMessageId);
            }
            return answer;
        }
        if (!(changed || announceUnchanged))
        {
            return answer;
        }
        return $"{answer}\n{SessionReadinessLine(decision, agvId, generation, state, answeredMessageType, answeredMessageId)}";
    }

    /// <summary>
    /// One SessionReadiness line, built the same way wherever readiness changes. The envelope used to
    /// be written out at each site; two of those copies were putting a reason code on the wire that
    /// the protocol's closed ErrorCode enum does not contain. The last other copy, byte for byte the same
    /// line, went with control-server#340. Its callers are <see cref="AnswerWithReadiness"/> and the
    /// recovery report's answer, and nothing else (<c>OnboardHandshakeReadinessArchitectureTests</c>).
    /// </summary>
    /// <remarks>
    /// Every line it builds is logged with the answer it rides on (control-server#380). The server sends readiness only
    /// on a change, and the vehicle shows its recovery entry only on what it last applied, so when the entry does not
    /// appear this record is what says whether the line left this server at all. The line's own messageId is chosen
    /// here rather than in <see cref="SerializeEnvelope"/> so that the record can name it; the payload is still built
    /// before the id and the send time are read, in the order <see cref="SerializeEnvelope"/> keeps.
    /// </remarks>
    private string SessionReadinessLine(
        SessionReadinessDecision decision,
        string agvId,
        long generation,
        OnboardConnectionState state,
        string answeredMessageType,
        string answeredMessageId)
    {
        string readiness = ReadinessOnTheWire(decision);
        string? reasonCode = ReasonCodeOnTheWire(decision);
        object payload = new
        {
            readiness,
            decidedAt = timeProvider.GetUtcNow(),
            reasonCodes = reasonCode is null ? Array.Empty<string>() : [reasonCode],
            acceptedCapabilityVersion = state.CapabilityRevision ?? 0,
            acceptedSafetyStateVersion = state.SafetyRevision ?? 0,
            vehicleBusinessStateRevision = 1
        };
        string lineMessageId = Guid.NewGuid().ToString("D");
        string line = ProtocolEnvelope.Serialize(
            "SessionReadiness", lineMessageId, correlationId: null, agvId, generation, timeProvider.GetUtcNow(), payload);
        LogSessionReadinessAppended(
            logger, lineMessageId, agvId, generation, readiness, reasonCode, answeredMessageType, answeredMessageId);
        return line;
    }

    private static string ReadinessOnTheWire(SessionReadinessDecision decision) =>
        decision.Readiness == SessionReadiness.Ready ? "READY" : "RECOVERY_REQUIRED";

    private static string? ReasonCodeOnTheWire(SessionReadinessDecision decision) =>
        decision.Readiness == SessionReadiness.Ready
            ? null
            : ProtocolErrorCodes.ToSessionReadinessReasonCode(decision.ReasonCode);

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
    /// control-server#452：人工清桩确认与现场确认充不上，在收件箱事务开写锁之前先观察——读库定下对的是哪一个桩、哪一张单，再读 RIoT。别的消息、
    /// 这台服务端没装这两个判定、或这条线已经进过收件箱（重放、换代重发，答第一次的结果，不再判定）时不观察，答空。确认号已经判过的，判定自己不读 RIoT。
    /// 载荷读不出请求时也答空，由处理那一步照旧抛出同一个错误。什么也不写。
    /// </summary>
    private async Task<FieldConfirmationObservation?> ObserveFieldConfirmationAsync(
        JsonElement root, OnboardConnectionState state, string messageType, string messageId, CancellationToken cancellationToken)
    {
        bool observes = messageType switch
        {
            "ManualStationClearanceConfirmationRequested" => stationClearance is not null,
            "UnableToChargeFieldConfirmationRequested" => fieldConfirmations is not null,
            _ => false,
        };
        if (!observes ||
            await dbContext.ProtocolInbox.AsNoTracking().AnyAsync(row => row.MessageId == messageId, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        JsonElement payload = root.GetProperty("payload");
        string agvId = state.AgvId!;
        long generation = state.SessionGeneration!.Value;
        if (messageType == "ManualStationClearanceConfirmationRequested")
        {
            ManualStationClearanceRequest? clearance = Readable(() => ManualStationClearanceRequestOf(payload, agvId, generation, messageId));
            return clearance is null
                ? null
                : new FieldConfirmationObservation(
                    await stationClearance!.ObserveAsync(clearance, cancellationToken).ConfigureAwait(false), null);
        }
        UnableToChargeFieldConfirmationRequest? unableToCharge =
            Readable(() => UnableToChargeRequestOf(payload, agvId, generation, messageId));
        return unableToCharge is null
            ? null
            : new FieldConfirmationObservation(
                null, await fieldConfirmations!.ObserveAsync(unableToCharge, cancellationToken).ConfigureAwait(false));

        static T? Readable<T>(Func<T> read) where T : class
        {
            try
            {
                return read();
            }
            catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException
                                              or InvalidDataException)
            {
                return null;
            }
        }
    }

    private static FieldConfirmationObservationStaleException Stale(string exhaustedMessage) => new(exhaustedMessage);

    private ManualStationClearanceRequest ManualStationClearanceRequestOf(
        JsonElement payload, string agvId, long generation, string messageId)
    {
        // Batch 9-08 (control-server#406): the digest is the payload's alone, so a resubmission under a new messageId is the same
        // request (coordinator's alignment of 09-30, item 2).
        string confirmationRequestId = RequiredUuid(payload, "confirmationRequestId");
        JsonElement operatorContext = payload.GetProperty("operator");
        return new ManualStationClearanceRequest(
            ManualStationClearanceSources.Onboard,
            agvId,
            _fleet.ByAgvId(agvId)?.VehicleKey,
            confirmationRequestId,
            generation,
            messageId,
            WireContentHash.Sha256(payload.GetRawText()),
            RequiredString(payload, "stationId"),
            NullableString(payload, "publicStationFunction"),
            RequiredString(payload, "clearedCondition"),
            RequiredString(operatorContext, "operatorId"),
            RequiredString(operatorContext, "verificationMethod"),
            operatorContext.GetProperty("verifiedAt").GetDateTimeOffset(),
            payload.GetProperty("observedAt").GetDateTimeOffset());
    }

    private UnableToChargeFieldConfirmationRequest UnableToChargeRequestOf(
        JsonElement payload, string agvId, long generation, string messageId)
    {
        string confirmationRequestId = RequiredUuid(payload, "confirmationRequestId");
        JsonElement operatorContext = payload.GetProperty("operator");
        return new UnableToChargeFieldConfirmationRequest(
            agvId,
            _fleet.ByAgvId(agvId)?.VehicleKey,
            confirmationRequestId,
            generation,
            messageId,
            WireContentHash.Sha256(payload.GetRawText()),
            RequiredString(payload, "chargerStationId"),
            RequiredString(payload, "observedCondition"),
            RequiredString(operatorContext, "operatorId"),
            RequiredString(operatorContext, "verificationMethod"),
            operatorContext.GetProperty("verifiedAt").GetDateTimeOffset(),
            payload.GetProperty("observedAt").GetDateTimeOffset());
    }

    /// <summary>一条现场确认在锁外观察到的，两种之一（control-server#452）。</summary>
    private sealed record FieldConfirmationObservation(
        ManualStationClearance.Observation? StationClearance,
        UnableToChargeFieldConfirmations.Observation? UnableToCharge);

    /// <summary>
    /// Answers a durable message resent into a later session from its first acceptance, or returns null
    /// when that first response was not a DurableAck for this message -- a snapshot ack or a recovery
    /// authorization stays a content conflict, as it did before. Nothing is processed again: the first
    /// processing committed, and what it wrote is bound to the first line's hash, so a second pass would
    /// throw on its own. The ack is rebuilt for this session and names the line just received, which is
    /// what the onboard compares it with; durablyAcceptedAt stays the moment the server took it.
    /// Readiness is recomputed rather than replayed, because the first response's belonged to a session
    /// that is gone -- and announced only when it changed, the way every other site here does it.
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
        SessionReadinessDecision decision = await store.DecideReadinessAsync(
            agvId, generation, cancellationToken).ConfigureAwait(false);
        return AnswerWithReadiness(ack, decision, agvId, generation, state, messageType, messageId, announceUnchanged: false);
    }

    /// <summary>
    /// What a refused message leaves behind when it was one of the session's baselines (control-server#478).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Safety</b> (SafetyStateSnapshot, SafetyStateChanged). Before #478 the refusal ended the connection and the
    /// reconnect's handshake brought a new safety baseline. Kept connected, the server would go on judging departure on the
    /// content that arrived first while the vehicle holds another, so the baseline is no longer trusted: the departure
    /// verdict is cleared (<see cref="WireToGateStore.DistrustSafetyBaselineAsync"/>), readiness is decided again --
    /// RecoveryRequired / DEPARTURE_SAFETY_NOT_READY, the state an unsafe vehicle sits in every day, which holds dispatch and
    /// nothing more -- and a fresh SafetyStateSnapshot is asked for. Only an accepted SafetyStateSnapshot trusts it again
    /// (<see cref="OnboardConnectionState.SafetyBaselineUntrusted"/>). Inside the handshake no request is sent; the
    /// handshake's own snapshot is the new baseline.
    /// </para>
    /// <para>
    /// <b>Capability</b> (CapabilitySnapshot). The vehicle answers no CapabilitySnapshotRequested (8005-agv-onboard-hmi
    /// <c>WireToGateSessionClient</c> lists it among the messages it only logs), so the session has no way to a new capability
    /// baseline but a new handshake. The connection is ended after the ProtocolProblem is written, as every refusal was
    /// before #478. That cannot become the replay loop #478 is about: the onboard sends its CapabilitySnapshot fresh in each
    /// handshake under a new messageId, straight onto the wire rather than through its durable outbox
    /// (<c>SendSnapshotAndRequireAckAsync</c>), so a reconnect does not resend the refused one.
    /// </para>
    /// </remarks>
    private async Task<string> AfterRefusedBaselineAsync(
        string problem,
        string messageType,
        string messageId,
        string agvId,
        OnboardConnectionState state,
        CancellationToken cancellationToken)
    {
        if (messageType == "CapabilitySnapshot")
        {
            state.EndAfterResponse = true;
            return problem;
        }
        if (messageType is not ("SafetyStateSnapshot" or "SafetyStateChanged"))
        {
            return problem;
        }
        long generation = state.SessionGeneration!.Value;
        await store.DistrustSafetyBaselineAsync(agvId, generation, cancellationToken).ConfigureAwait(false);
        state.SafetyBaselineUntrusted = true;
        SessionReadinessDecision decision = await store.DecideReadinessAsync(agvId, generation, cancellationToken)
            .ConfigureAwait(false);
        state.SafetySnapshotRequestDue = true;
        return AppendSafetySnapshotRequest(
            AnswerWithReadiness(problem, decision, agvId, generation, state, messageType, messageId, announceUnchanged: false),
            state);
    }

    /// <summary>
    /// Puts back on this connection the capability and safety versions the first acceptance of a resent
    /// RecoveryStateReport announced, before the report is processed again in the new session.
    /// </summary>
    /// <remarks>
    /// It changes the connection state before the response factory runs, and stays there on purpose: the
    /// SessionReadiness the reprocessed report answers with reads these two versions (<c>acceptedCapabilityVersion</c>,
    /// <c>acceptedSafetyStateVersion</c>). Could that run end in an <see cref="InboundMessageRejectedException"/> after
    /// the state was changed, a refused message would leave it behind on a connection that stays (control-server#478).
    /// It cannot: this runs only for a resend the inbox judged equivalent to an accepted report, and the one refusal
    /// the report's branch can raise -- an active unlock slot outside 1..8 -- depends on the report's own content, which
    /// was accepted once already.
    /// </remarks>
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
        // The id and the clock are read here, not in the choke point: where they happen relative to
        // building the payload is observable on the wire, so it stays with the caller.
        ProtocolEnvelope.Serialize(
            messageType,
            Guid.NewGuid().ToString("D"),
            correlationId,
            agvId,
            sessionGeneration,
            timeProvider.GetUtcNow(),
            payload);

    /// <summary>
    /// What identifies one overdue episode across snapshots: the alarm's own id, which the vehicle keeps for
    /// the life of that episode (hmi#109), else the slot and the moment it went overdue.
    /// </summary>
    private static HashSet<string> OverdueIdentities(IReadOnlyList<OnboardAlarmEntry> overdue) =>
        new(overdue.Select(alarm => alarm.AlarmId
                                    ?? $"{alarm.PhysicalSlotNumber}@{alarm.RaisedAt.ToUnixTimeMilliseconds()}"),
            StringComparer.Ordinal);

    private async Task<bool> AffectsAnOverdueSlotAsync(
        string agvId, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("affectedSlots", out JsonElement affected) ||
            affected.ValueKind != JsonValueKind.Array ||
            affected.GetArrayLength() == 0)
        {
            return false;
        }
        IReadOnlyList<OnboardAlarmEntry> overdue =
            await alarmStore.ReadExpectedActionOverdueAsync(agvId, cancellationToken).ConfigureAwait(false);
        if (overdue.Count == 0)
        {
            return false;
        }
        HashSet<int> overdueSlots = [.. overdue.Select(alarm => alarm.PhysicalSlotNumber!.Value)];
        return affected.EnumerateArray()
            .Any(slot => slot.ValueKind == JsonValueKind.Number && overdueSlots.Contains(slot.GetInt32()));
    }

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

    /// <summary>
    /// When this result is received and the station departure deadline its journey stands under now, read
    /// from the one function the runtime and the worklist take it from.
    /// </summary>
    private async Task<OperationResultReceipt> ReceiptAsync(string demandId, CancellationToken cancellationToken)
    {
        JourneyRuntimeRow? journey = await DemandJourneyLookup.JourneyOf(dbContext, demandId).AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new OperationResultReceipt(
            timeProvider.GetUtcNow(),
            journey is null
                ? null
                : JourneyRuntimeEngine.StationDepartureDeadline(
                    journey, runtimeOptions.Value.StationDepartureWaitTimeout));
    }

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning,
        Message = "Vehicle {AgvId} reported load {SlotOperationAttemptId} of demand {DemandId} FAILED with every " +
                  "slot determinate, but it cannot be settled ({ReasonCode}: received {ReceivedAt}, station " +
                  "deadline {StationDepartureDeadline}); the operation went to recovery.")]
    private static partial void LogUnsettleableDeterminateFailure(
        ILogger logger,
        string agvId,
        string demandId,
        string slotOperationAttemptId,
        string reasonCode,
        DateTimeOffset receivedAt,
        DateTimeOffset? stationDepartureDeadline);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information,
        Message = "SessionReadiness {ReadinessMessageId} to {AgvId} generation {SessionGeneration}: {Readiness} " +
                  "[{ReasonCode}], after the answer to {AnsweredMessageType} {AnsweredMessageId}.")]
    private static partial void LogSessionReadinessAppended(
        ILogger logger,
        string readinessMessageId,
        string agvId,
        long sessionGeneration,
        string readiness,
        string? reasonCode,
        string answeredMessageType,
        string answeredMessageId);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Information,
        Message = "SessionReadiness for {AgvId} generation {SessionGeneration} changed to {Readiness} [{ReasonCode}] " +
                  "on {AnsweredMessageType} {AnsweredMessageId} inside the reconnect handshake; held back for the " +
                  "recovery report's answer.")]
    private static partial void LogSessionReadinessHeldInHandshake(
        ILogger logger,
        string agvId,
        long sessionGeneration,
        string readiness,
        string? reasonCode,
        string answeredMessageType,
        string answeredMessageId);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning,
        Message = "Refused {MessageType} {MessageId} from {AgvId} with ProtocolProblem {ReasonCode}; the connection stays " +
                  "and nothing of it was kept: {Detail}")]
    private static partial void LogInboundRejected(
        ILogger logger,
        string agvId,
        string messageType,
        string messageId,
        string reasonCode,
        string detail);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
        Message = "Vehicle {AgvId} reported a result for slot operation {SlotOperationAttemptId} of demand {DemandId} that " +
                  "this server never commanded (message {MessageId}, resultContentSha256 {ResultContentSha256}, wire " +
                  "content {ContentSha256}); acknowledged and kept as historical evidence, nothing settled.")]
    private static partial void LogUnknownOperationResult(
        ILogger logger,
        string agvId,
        string slotOperationAttemptId,
        string demandId,
        string messageId,
        string resultContentSha256,
        string contentSha256);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Onboard rejected {RejectedMessageType} {RejectedMessageId}: {ReasonCode} at {FieldPath} -- {DisplayMessage}")]
    private static partial void LogOnboardRejection(
        ILogger logger,
        string rejectedMessageType,
        string rejectedMessageId,
        string reasonCode,
        string fieldPath,
        string displayMessage);

    private static string RequiredUuid(JsonElement element, string propertyName)
    {
        string value = RequiredString(element, propertyName);
        return Guid.TryParseExact(value, "D", out _)
            ? value
            : throw new InvalidDataException($"Protocol field '{propertyName}' must be a UUID.");
    }

    /// <summary>
    /// Reads a field the protocol declares as `number | null`, so an explicit null is a value
    /// rather than a violation.
    /// </summary>
    private static double? NullableDouble(JsonElement element, string propertyName)
    {
        JsonElement value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
    }

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
    /// The identity of a durable message the vehicle replays in a later session: everything but the
    /// sessionGeneration it rebinds.
    /// </summary>
    /// <remarks>
    /// OperationResult joined RecoveryStateReport here on 2026-09-13. CV-OPERATION-RESULT-UNKNOWN-RECONCILE
    /// has the vehicle replay a result the server already acknowledged, under the new generation, and
    /// the inbox refused every such replay as a content conflict.
    /// </remarks>
    private static string GenerationRebindReplayHash(string line)
    {
        JsonNode root = JsonNode.Parse(line)
            ?? throw new InvalidDataException("Replayed message JSON cannot be empty.");
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

    /// <summary>
    /// The vehicle has sent its recovery report on this session, so it has left the handshake's
    /// one-question-one-answer exchange and reads the connection in its receive loop.
    /// </summary>
    public bool HandshakeCompleted { get; set; }

    /// <summary>The message being processed made a SafetyStateSnapshotRequested due (control-server#142).</summary>
    public bool SafetySnapshotRequestDue { get; set; }

    /// <summary>
    /// The reason the due request carries; null is <c>VERSION_GAP</c>. A repair release asks with
    /// <c>PRE_MOVEMENT_RECONCILIATION</c> (control-server#385).
    /// </summary>
    public string? SafetySnapshotRequestReason { get; set; }

    /// <summary>
    /// This connection has already looked for, and if needed asked for, the readings a repair release waits for
    /// (control-server#385). A new connection starts false, which is what makes a reconnect ask again.
    /// </summary>
    public bool ReleaseReadingsRequested { get; set; }

    /// <summary>
    /// A safety message was refused on this connection and no SafetyStateSnapshot has been accepted since, so the
    /// session's departure verdict is not trusted (control-server#478). Kept on the connection, not in the database: a
    /// reconnect is a new session generation, whose handshake brings a new safety baseline.
    /// </summary>
    public bool SafetyBaselineUntrusted { get; set; }

    /// <summary>When this connection last sent a SafetyStateSnapshotRequested; spaces the re-asks of control-server#478.</summary>
    public DateTimeOffset SafetySnapshotRequestedAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// End the connection once the answer to the current line is written. Set for a refused CapabilitySnapshot, which only
    /// a new handshake can replace (control-server#478).
    /// </summary>
    public bool EndAfterResponse { get; set; }

    public bool DeferOutboundUntilResponseWritten { get; set; }
    public string? DeferredRecoveryLine { get; set; }
}
