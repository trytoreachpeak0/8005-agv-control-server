using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>
/// A long-lived synthetic Onboard peer: it performs the five-step recovery handshake
/// (ADR-cross-0029), keeps heartbeating, and answers ControlServer's business requests according
/// to a policy a scenario can change while the session is live.
/// </summary>
/// <remarks>
/// This stands in for the real WPF onboard until the UI Automation driver exists. It is not the
/// real thing and never will be: it has no IO, no journal and no operator. What it does have is
/// the protocol behaviour the server's state machine depends on, which is what an L2 scenario
/// needs in order to be about the server.
/// </remarks>
public sealed class OnboardPeerSession(
    CommandEngine<FakeOnboardState> engine,
    FakeOnboardOptions options,
    SlotStateSeed slotStateSeed) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] FailedSlotReasonCodes = ["ACTION_NOT_ALLOWED_IN_STATE"];

    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentQueue<WireEvent> wire = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> answered =
        new(StringComparer.Ordinal);
    private TcpClient? client;
    private StreamWriter? writer;
    private Task? pump;
    private Task? heartbeat;
    private CancellationTokenSource? lifetime;
    private CancellationToken hostStopping;

    /// <summary>Whether a socket to ControlServer is open, whatever the handshake got to.</summary>
    public bool IsConnected => client is not null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        hostStopping = cancellationToken;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        client = new TcpClient();
        await client.ConnectAsync(options.Host, options.Port, lifetime.Token).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        long generation = await HandshakeAsync(reader, lifetime.Token).ConfigureAwait(false);
        engine.Mutate<object?>(state => (state with
        {
            SessionGeneration = generation,
            Readiness = "READY",
            ReadinessReasonCode = "READY"
        }, null));
        pump = Task.Run(() => PumpAsync(reader, lifetime.Token), CancellationToken.None);
        heartbeat = Task.Run(() => HeartbeatAsync(lifetime.Token), CancellationToken.None);
    }

    /// <summary>
    /// Steps 1 to 5 of ADR-cross-0029, in order, refusing to proceed unless the server answers
    /// each one. A peer that reached READY without the server saying so would hide exactly the
    /// class of defect this harness exists to catch.
    /// </summary>
    private async Task<long> HandshakeAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string credential = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "Credential environment variable '" + options.CredentialEnvironmentVariable + "' is not set.");
        await SendAsync(Envelope("SessionHello", NewId(), null, null, new
        {
            onboardInstanceId = NewId(),
            onboardBuildCommit = "FAKE_ONBOARD_WORKTREE_BUILD",
            supportedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseIdentity = ReleaseIdentity(),
            credentialProof = credential
        }), cancellationToken).ConfigureAwait(false);

        JsonElement accepted = await ReadRequiredAsync(reader, "SessionAccepted", cancellationToken)
            .ConfigureAwait(false);
        long generation = accepted.GetProperty("sessionGeneration").GetInt64();

        FakeOnboardState state = engine.Snapshot().State;
        await SendAsync(Envelope("CapabilitySnapshot", NewId(), null, generation, new
        {
            capabilityVersion = 1,
            observedAt = DateTimeOffset.UtcNow,
            slotModelVersion = "fake-slot-model-v1",
            // 协议 v2 在 CapabilitySnapshot 上要求这一项：车报它此刻装着哪一版仓位配置。这个假车没有 IO
            // 可算摘要，所以它采纳每一次被它接受的激活的目标——激活之后重连，报的就是那一版，服务端比对
            // 得上。「两端算出同一个摘要」是 G3 对真车载端的断言，不是这个假车能证的。
            activeSlotConfigurationVersion = state.ActiveSlotConfigurationVersion,
            activeSlotConfigurationFingerprint = state.ActiveSlotConfigurationFingerprint,
            slotStates = slotStateSeed.Render(),
            supportsBatchUnlock = false,
            onboardJournalFormatVersion = 1
        }), cancellationToken).ConfigureAwait(false);
        await ReadRequiredAsync(reader, "SnapshotAppliedAck", cancellationToken).ConfigureAwait(false);

        await SendAsync(Envelope("SafetyStateSnapshot", NewId(), null, generation, new
        {
            safetyStateVersion = state.SafetyStateVersion,
            observedAt = DateTimeOffset.UtcNow,
            safety = SafetyBody(state.Safety),
            slotStates = slotStateSeed.Render()
        }), cancellationToken).ConfigureAwait(false);
        await ReadRequiredAsync(reader, "SnapshotAppliedAck", cancellationToken).ConfigureAwait(false);

        // 协议 v2 消息 9，与真车载端同一个位置：完整握手里报一份当下的全量告警，空的也报——空快照说的是
        // 「此刻没有告警」，与服务端看板上的「尚未收到该车快照」是两件事。
        await SendAlarmSnapshotAsync(engine.Snapshot().State, generation, cancellationToken).ConfigureAwait(false);
        await ReadRequiredAsync(reader, "SnapshotAppliedAck", cancellationToken).ConfigureAwait(false);

        await SendAsync(Envelope("RecoveryStateReport", NewId(), null, generation, new
        {
            reportId = NewId(),
            observedAt = DateTimeOffset.UtcNow,
            unsettledSlotOperationAttemptId = (string?)null,
            provenRecoveryCheckpoint = "NONE",
            activeUnlockSlots = Array.Empty<int>(),
            forcedRecoveryGeneration = 0,
            pendingResults = Array.Empty<object>(),
            journalContentSha256 = new string('0', 64)
        }), cancellationToken).ConfigureAwait(false);
        await ReadRequiredAsync(reader, "DurableAck", cancellationToken).ConfigureAwait(false);
        JsonElement readiness = await ReadRequiredAsync(reader, "SessionReadiness", cancellationToken)
            .ConfigureAwait(false);
        string? value = readiness.GetProperty("payload").GetProperty("readiness").GetString();
        if (value != "READY")
        {
            throw new InvalidOperationException("ControlServer readiness is '" + value + "'.");
        }
        return generation;
    }

    private async Task PumpAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    engine.Mutate<object?>(state => (state with { Readiness = "DISCONNECTED" }, null));
                    return;
                }
                await HandleAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception error)
        {
            // Anything thrown here stops the peer answering, and an unobserved task exception says
            // nothing: the run just waits out its timeout at whatever stage went unanswered. Record
            // the reason where a scenario reads state, so the failure names itself.
            engine.Mutate<object?>(state => (state with
            {
                Readiness = "FAULTED",
                ReadinessReasonCode = error.GetType().Name + ": " + error.Message
            }, null));
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        // ADR-cross-0027: two seconds, six-second liveness timeout. The server's own liveness check
        // ages the last inbound message from this generation, so stopping this is how a scenario
        // makes a session go quiet without closing the socket.
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FakeOnboardState state = engine.Snapshot().State;
                await SendAsync(Envelope("Heartbeat", NewId(), null, state.SessionGeneration, new
                {
                    observedAt = DateTimeOffset.UtcNow,
                    capabilityVersion = 1L,
                    safetyStateVersion = state.SafetyStateVersion
                }), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // The pump records the disconnection.
        }
    }

    private async Task HandleAsync(string line, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        string messageType = root.GetProperty("messageType").GetString() ?? string.Empty;
        string messageId = root.GetProperty("messageId").GetString() ?? string.Empty;
        Record("in", messageType, messageId);
        long generation = engine.Snapshot().State.SessionGeneration;

        switch (messageType)
        {
            case "VehicleBusinessStateSnapshot":
            case "CurrentStopWorklistSnapshot":
            case "UpcomingStopPlanSnapshot":
            case "ExceptionRecoverySessionSnapshot":
                await AcknowledgeSnapshotAsync(root, messageType, messageId, generation, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "SublotEntryRequested":
                await OnRequestAsync(
                    SublotKey(root.GetProperty("payload")), messageType, messageId, root, generation,
                    engine.Snapshot().State.Policy.Sublot,
                    (payload, gen) => SublotSubmitted(payload, gen),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "SlotOperationCommand":
                {
                    JsonElement payload = root.GetProperty("payload");
                    string operationType = payload.GetProperty("operationType").GetString() ?? "LOAD";
                    AnswerMode mode = operationType == "LOAD"
                        ? engine.Snapshot().State.Policy.LoadResult
                        : engine.Snapshot().State.Policy.UnloadResult;
                    await OnRequestAsync(
                        "operation:" + payload.GetProperty("slotOperationAttemptId").GetString(),
                        messageType, messageId, root, generation, mode,
                        (item, gen) => OperationResult(item, gen, completed: true),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            case "SlotConfigurationActivationCommand":
                await OnActivationCommandAsync(root, messageId, generation, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "PreDepartureSafetyCheck":
                await OnRequestAsync(
                    SafetyCheckKey(root.GetProperty("payload")), messageType, messageId, root, generation,
                    engine.Snapshot().State.Policy.SafetyCheck,
                    (payload, gen) => SafetyCheckResult(payload, gen, safe: true),
                    cancellationToken).ConfigureAwait(false);
                return;
            default:
                // HeartbeatAck, DurableAck, SessionReadiness and the recovery commands carry no
                // obligation for this peer. They are already on the wire log.
                return;
        }
    }

    /// <summary>
    /// The key a sublot entry request is answered and replayed under: its operation session and worklist
    /// revision, which is what the server re-sends unchanged and what changes for the next trip.
    /// </summary>
    /// <remarks>
    /// This was the fixed string <c>sublot</c> until control-server#75's review: the first trip's answer
    /// was then replayed to every later request on the same connection, so the second trip was told the
    /// first trip's sublot and one fake vehicle could never finish two trips. The expected sublots are
    /// fixed by the worklist at that revision (the request expires on a revision change), so they add
    /// nothing to the key.
    /// </remarks>
    public static string SublotKey(JsonElement requestPayload) =>
        "sublot:" + requestPayload.GetProperty("operationSessionId").GetString() + ":"
        + requestPayload.GetProperty("worklistRevision").GetInt64().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The key a pre-departure safety check is answered and replayed under. The server reissues an
    /// expired check under a new <c>preDepartureSafetyCheckId</c>, and each trip has its own, so a
    /// cached answer naming an older check must not be sent back for it.
    /// </summary>
    public static string SafetyCheckKey(JsonElement checkPayload) =>
        "safety-check:" + checkPayload.GetProperty("preDepartureSafetyCheckId").GetString();

    /// <summary>
    /// Records the request, then either answers it now or leaves it open for the scenario. Silent
    /// records it and answers nothing, which is what a station operation timing out looks like from
    /// the server's side.
    /// </summary>
    private async Task OnRequestAsync(
        string key,
        string messageType,
        string messageId,
        JsonElement root,
        long generation,
        AnswerMode mode,
        Func<JsonElement, long, object> buildAnswer,
        CancellationToken cancellationToken)
    {
        // ControlServer replays an unsettled command on every poll until it is answered, so the
        // same request arrives many times. ADR-cross-0006 and ADR-cross-0014 require the peer to
        // return its existing result rather than produce a new one: a second OperationResult under
        // the same slotOperationAttemptId carrying a fresh resultId is a content conflict, and the
        // server tears the session down over it -- which is exactly what happened the first time
        // this ran.
        if (answered.TryGetValue(key, out string? previous))
        {
            await SendLineAsync(previous, cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonElement payload = root.GetProperty("payload");
        PendingRequest request = new(messageType, messageId, key, payload.GetRawText(), DateTimeOffset.UtcNow);
        engine.Mutate<object?>(state =>
        {
            Dictionary<string, PendingRequest> pending = new(state.Pending, StringComparer.Ordinal)
            {
                [key] = request
            };
            return (state with { Pending = pending }, null);
        });
        if (mode != AnswerMode.Auto)
        {
            return;
        }
        await AnswerAsync(key, buildAnswer(payload, generation), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an answer, remembers it verbatim so a replayed request gets the identical message, and
    /// closes the pending request it answers.
    /// </summary>
    public async Task AnswerAsync(string key, object message, CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(message, SerializerOptions);
        answered[key] = line;
        await SendLineAsync(line, cancellationToken).ConfigureAwait(false);
        engine.Mutate<object?>(state =>
        {
            if (!state.Pending.ContainsKey(key))
            {
                return (null, null);
            }
            Dictionary<string, PendingRequest> pending = new(state.Pending, StringComparer.Ordinal);
            pending.Remove(key);
            return (state with { Pending = pending }, null);
        });
    }

    public PendingRequest? Pending(string key) =>
        engine.Snapshot().State.Pending.TryGetValue(key, out PendingRequest? request) ? request : null;

    // Protocol 2.0.0 item 2: the request names the dispatch scope's sublots and no demand, and the
    // submission names only what was scanned -- the server resolves the demand. This peer scans the
    // first sublot it was offered, which with one demand per journey is the only one.
    public object SublotSubmitted(JsonElement requestPayload, long generation) =>
        Envelope("SublotSubmitted", NewId(), null, generation, new
        {
            operationSessionId = requestPayload.GetProperty("operationSessionId").GetString(),
            stationId = requestPayload.GetProperty("stationId").GetString(),
            worklistRevision = requestPayload.GetProperty("worklistRevision").GetInt64(),
            sublot = requestPayload.GetProperty("expectedSublots")[0].GetString(),
            entryMethod = "SCANNER",
            @operator = new
            {
                operatorId = "FAKE-ONBOARD-OPERATOR",
                verificationMethod = "BADGE",
                verifiedAt = DateTimeOffset.UtcNow
            }
        });

    /// <summary>
    /// Builds an OperationResult whose <c>resultContentSha256</c> is computed the way
    /// OnboardMessageProcessor recomputes it. The server rejects the message outright when the two
    /// differ, so this hash is part of the contract rather than a checksum.
    /// </summary>
    public object OperationResult(JsonElement commandPayload, long generation, bool completed)
    {
        string operationType = commandPayload.GetProperty("operationType").GetString() ?? "LOAD";
        string finalState = operationType == "LOAD" ? "OCCUPIED" : "EMPTY";
        SlotResult[] slotResults = commandPayload.GetProperty("slots").EnumerateArray()
            .Select(slot => new SlotResult(
                slot.GetInt32(),
                completed ? "COMPLETED" : "FAILED",
                completed ? finalState : "EMPTY",
                "LOCKED",
                "RESET",
                completed ? [] : FailedSlotReasonCodes))
            .ToArray();
        // One object serialized twice: once to hash, once onto the wire. Building the business
        // content a second time to hash it is how the two silently drift, and the server rejects
        // the message outright when they do.
        ResultContent content = new(
            commandPayload.GetProperty("demandId").GetString()!,
            commandPayload.GetProperty("slotOperationAttemptId").GetString()!,
            operationType,
            completed ? "COMPLETED" : "FAILED",
            slotResults,
            DateTimeOffset.UtcNow,
            "FAKE-ONBOARD-CHECKPOINT");
        string hash = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, SerializerOptions)))
            .ToLowerInvariant();

        return Envelope("OperationResult", NewId(), null, generation, new
        {
            content.DemandId,
            content.SlotOperationAttemptId,
            content.OperationType,
            content.OverallOutcome,
            content.SlotResults,
            content.ObservedAt,
            content.JournalCheckpoint,
            resultContentSha256 = hash
        });
    }

    /// <summary>
    /// The business content of an OperationResult, in the exact property order
    /// OnboardMessageProcessor.ComputeOperationResultContentHash serializes. Order is part of the
    /// hash, so this record's declaration order is a contract, not formatting.
    /// </summary>
    private sealed record ResultContent(
        string DemandId,
        string SlotOperationAttemptId,
        string OperationType,
        string OverallOutcome,
        IReadOnlyList<SlotResult> SlotResults,
        DateTimeOffset ObservedAt,
        string JournalCheckpoint);

    private sealed record SlotResult(
        int SlotNo,
        string Outcome,
        string FinalPhysicalState,
        string LockState,
        string UnlockOutputState,
        IReadOnlyList<string> ReasonCodes);

    public object SafetyCheckResult(JsonElement checkPayload, long generation, bool safe)
    {
        FakeOnboardState state = engine.Snapshot().State;
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        return Envelope(
            "PreDepartureSafetyCheckResult",
            NewId(),
            // The server accepts either the check id or the request's messageId here; the check id
            // is what the real peer sends, so that is what this one sends.
            checkPayload.GetProperty("preDepartureSafetyCheckId").GetString(),
            generation,
            new
            {
                preDepartureSafetyCheckId = checkPayload.GetProperty("preDepartureSafetyCheckId").GetString(),
                outcome = safe ? "SAFE" : "UNSAFE",
                observedAt,
                safetyStateVersion = checkPayload.GetProperty("expectedSafetyStateVersion").GetInt64(),
                // Wide enough that one poll interval cannot expire it, which is the failure the
                // server's AwaitSafeDepartureResultAsync comment records.
                validUntil = observedAt.AddSeconds(30),
                safety = SafetyBody(safe ? state.Safety : state.Safety with { DepartureSafe = false })
            });
    }

    /// <summary>The key a scenario names an open activation by in /answer.</summary>
    public static string ActivationKey(string activationId) => "activation:" + activationId;

    /// <summary>
    /// 协议 v2 消息 7。已经有结论的激活原样再报一次结论，不重新激活；没有结论的按策略挂起或应答。
    /// </summary>
    /// <remarks>
    /// 真车载端的补报就是这个形状：结果与生效配置原子落盘，服务端重连后按 <c>SLOT_CONFIGURATION</c> 这个
    /// 恢复角色重发同一条命令，车认出这个 <c>activationId</c> 已经有结果，原样返回。所以
    /// 「下发 → 断线 → 重连 → 补报」在这里不需要任何专门的补报队列。
    /// </remarks>
    private async Task OnActivationCommandAsync(
        JsonElement root,
        string messageId,
        long generation,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string activationId = payload.GetProperty("activationId").GetString()
            ?? throw new InvalidDataException("SlotConfigurationActivationCommand activationId must be a string.");
        FakeOnboardState state = engine.Mutate(current =>
        {
            FakeOnboardState next = current with
            {
                ActivationCommandMessageIds = [.. current.ActivationCommandMessageIds, messageId]
            };
            return (next, next);
        });

        if (state.ActivationOutcomes.TryGetValue(activationId, out FakeActivationOutcome? settled))
        {
            await SendActivationResultAsync(settled, generation, cancellationToken).ConfigureAwait(false);
            return;
        }

        PendingRequest request = new(
            "SlotConfigurationActivationCommand", messageId, ActivationKey(activationId),
            payload.GetRawText(), DateTimeOffset.UtcNow);
        engine.Mutate<object?>(current => (current with
        {
            Pending = new Dictionary<string, PendingRequest>(current.Pending, StringComparer.Ordinal)
            {
                [request.Key] = request
            }
        }, null));
        if (state.Policy.SlotConfigurationActivation == AnswerMode.Auto)
        {
            await AnswerActivationAsync(request.Key, activated: true, deliver: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 给一条挂着的激活下结论：采纳目标或按指纹不符拒绝，然后报结果——或者先不报。
    /// </summary>
    /// <remarks>
    /// <paramref name="deliver"/> 为假，是「车已经换好配置、结果还没送出去线就断了」：结论落在本机，结果
    /// 等服务端重连后重发命令时才报。这正是 <c>PENDING_RESULT_REPLAY</c> 要兜住的那个窗口。
    /// </remarks>
    public async Task AnswerActivationAsync(
        string key,
        bool activated,
        bool deliver,
        CancellationToken cancellationToken)
    {
        PendingRequest request = Pending(key)
            ?? throw new InvalidOperationException("No activation is open under " + key + ".");
        using JsonDocument document = JsonDocument.Parse(request.PayloadJson);
        JsonElement payload = document.RootElement;
        string activationId = payload.GetProperty("activationId").GetString()!;
        string targetVersion = payload.GetProperty("targetSlotConfigurationVersion").GetString()!;
        string targetFingerprint = payload.GetProperty("targetSlotConfigurationFingerprint").GetString()!;

        FakeActivationOutcome outcome = engine.Mutate(state =>
        {
            FakeActivationOutcome concluded = activated
                ? new FakeActivationOutcome(
                    activationId, "ACTIVATED", null, targetVersion, targetFingerprint, DateTimeOffset.UtcNow)
                : new FakeActivationOutcome(
                    activationId, "REJECTED", "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH",
                    state.ActiveSlotConfigurationVersion, state.ActiveSlotConfigurationFingerprint,
                    DateTimeOffset.UtcNow);
            Dictionary<string, PendingRequest> pending = new(state.Pending, StringComparer.Ordinal);
            pending.Remove(key);
            return (state with
            {
                Pending = pending,
                ActivationOutcomes = new Dictionary<string, FakeActivationOutcome>(
                    state.ActivationOutcomes, StringComparer.Ordinal)
                {
                    [activationId] = concluded
                },
                ActiveSlotConfigurationVersion = concluded.ActiveSlotConfigurationVersion,
                ActiveSlotConfigurationFingerprint = concluded.ActiveSlotConfigurationFingerprint
            }, concluded);
        });

        if (deliver)
        {
            await SendActivationResultAsync(outcome, engine.Snapshot().State.SessionGeneration, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 协议 v2 消息 8。每次发都是新的 messageId 与当前会话代，内容是那一份结论——与真车载端补报时一致。
    /// </summary>
    private async Task SendActivationResultAsync(
        FakeActivationOutcome outcome,
        long generation,
        CancellationToken cancellationToken)
    {
        await SendAsync(Envelope("SlotConfigurationActivationResult", NewId(), null, generation, new
        {
            activationId = outcome.ActivationId,
            outcome = outcome.Outcome,
            problem = outcome.ReasonCode is null
                ? null
                : (object)new
                {
                    reasonCode = outcome.ReasonCode,
                    fieldPath = "payload.targetSlotConfigurationFingerprint",
                    displayMessage = (string?)null
                },
            activeSlotConfigurationVersion = outcome.ActiveSlotConfigurationVersion,
            activeSlotConfigurationFingerprint = outcome.ActiveSlotConfigurationFingerprint,
            verifiedAt = outcome.VerifiedAt
        }), cancellationToken).ConfigureAwait(false);
        engine.Mutate<object?>(state => (state with { ActivationResultsSent = state.ActivationResultsSent + 1 }, null));
    }

    /// <summary>协议 v2 消息 9：把当下的全量告警报上去。</summary>
    public Task PublishAlarmsAsync(CancellationToken cancellationToken)
    {
        FakeOnboardState state = engine.Snapshot().State;
        return SendAlarmSnapshotAsync(state, state.SessionGeneration, cancellationToken);
    }

    private Task SendAlarmSnapshotAsync(FakeOnboardState state, long generation, CancellationToken cancellationToken) =>
        SendAsync(Envelope("OnboardAlarmSnapshot", NewId(), null, generation, new
        {
            alarmSnapshotRevision = state.AlarmSnapshotRevision,
            observedAt = DateTimeOffset.UtcNow,
            alarms = state.Alarms.Select(alarm => new
            {
                alarmId = alarm.AlarmId,
                code = alarm.Code,
                severity = alarm.Severity,
                raisedAt = alarm.RaisedAt,
                subjectType = alarm.SubjectType,
                subjectId = alarm.SubjectId,
                displayMessage = alarm.DisplayMessage
            }).ToArray()
        }), cancellationToken);

    /// <summary>
    /// 把这条会话断掉，不自己重连——场景用它造出「车掉线」，读完服务端在掉线期间的状态再调
    /// <see cref="ReconnectAsync"/>。
    /// </summary>
    public async Task DisconnectAsync()
    {
        CancellationTokenSource? current = lifetime;
        if (current is null)
        {
            return;
        }
        await current.CancelAsync().ConfigureAwait(false);
        // Closing the socket is what the server observes; cancelling first keeps the pump from
        // reading that close as a fault.
        client?.Dispose();
        foreach (Task? task in new[] { pump, heartbeat })
        {
            if (task is null) continue;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Expected while tearing a live session down.
            }
        }
        if (writer is not null)
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The socket is already gone; there is nothing left to flush into.
            }
        }
        writer = null;
        client = null;
        pump = null;
        heartbeat = null;
        lifetime = null;
        current.Dispose();
        engine.Mutate<object?>(state => (state with
        {
            Readiness = "DISCONNECTED",
            ReadinessReasonCode = "DISCONNECTED_BY_SCENARIO"
        }, null));
    }

    /// <summary>新开一条连接、走一遍完整握手。会话代由服务端给，必然比上一代大。</summary>
    public Task ReconnectAsync() => StartAsync(hostStopping);

    /// <summary>Reports a new safety state, the way the real peer reports every change.</summary>
    public async Task PublishSafetyStateChangedAsync(
        long safetyStateVersion,
        SafetySummary safety,
        int[] affectedSlots,
        CancellationToken cancellationToken)
    {
        long generation = engine.Snapshot().State.SessionGeneration;
        await SendAsync(Envelope("SafetyStateChanged", NewId(), null, generation, new
        {
            safetyStateVersion,
            observedAt = DateTimeOffset.UtcNow,
            safety = SafetyBody(safety),
            affectedSlots
        }), cancellationToken).ConfigureAwait(false);
    }

    private async Task AcknowledgeSnapshotAsync(
        JsonElement root,
        string messageType,
        string messageId,
        long generation,
        CancellationToken cancellationToken)
    {
        // Each snapshot names its revision differently, and the server compares appliedRevision
        // against the one it stored under that exact name. Guessing at a generic "revision" threw,
        // which killed the read pump silently and left the journey waiting forever at the stage
        // whose snapshot went unacknowledged.
        (string kind, string revisionField) = messageType switch
        {
            "VehicleBusinessStateSnapshot" => ("VEHICLE_BUSINESS_STATE", "vehicleBusinessStateRevision"),
            "CurrentStopWorklistSnapshot" => ("CURRENT_STOP_WORKLIST", "worklistRevision"),
            "UpcomingStopPlanSnapshot" => ("UPCOMING_STOP_PLAN", "planRevision"),
            _ => ("EXCEPTION_RECOVERY_SESSION", "recoverySessionRevision")
        };
        JsonElement payload = root.GetProperty("payload");
        long revision = payload.GetProperty(revisionField).GetInt64();
        await SendAsync(Envelope("SnapshotAppliedAck", NewId(), messageId, generation, new
        {
            snapshotMessageId = messageId,
            snapshotKind = kind,
            appliedRevision = revision,
            appliedContentSha256 = Sha256(root.GetRawText())
        }), cancellationToken).ConfigureAwait(false);
    }

    private Task SendAsync(object message, CancellationToken cancellationToken) =>
        SendLineAsync(JsonSerializer.Serialize(message, SerializerOptions), cancellationToken);

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        Record(
            "out",
            document.RootElement.GetProperty("messageType").GetString() ?? string.Empty,
            document.RootElement.GetProperty("messageId").GetString() ?? string.Empty);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter current = writer
                ?? throw new IOException("No session to ControlServer is open; the scenario disconnected it.");
            await current.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task<JsonElement> ReadRequiredAsync(
        StreamReader reader,
        string expected,
        CancellationToken cancellationToken)
    {
        string line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("ControlServer closed the connection.");
        JsonDocument document = JsonDocument.Parse(line);
        string? actual = document.RootElement.GetProperty("messageType").GetString();
        Record("in", actual ?? string.Empty, document.RootElement.GetProperty("messageId").GetString() ?? string.Empty);
        if (actual != expected)
        {
            throw new InvalidDataException("Expected '" + expected + "', received '" + actual + "'.");
        }
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The wire log is a diagnostic, not state a scenario asserts a revision against, so it lives
    /// outside the command engine: routing it through there would either move the revision on every
    /// heartbeat or -- the first attempt here -- silently discard the append.
    /// </summary>
    private void Record(string direction, string messageType, string messageId)
    {
        wire.Enqueue(new WireEvent(direction, messageType, messageId, DateTimeOffset.UtcNow));
        while (wire.Count > 500 && wire.TryDequeue(out _))
        {
            // Bounded: a long run must not turn the log into the reason it ran out of memory.
        }
    }

    public IReadOnlyList<WireEvent> Wire() => wire.ToArray();

    private object Envelope(string messageType, string messageId, string? correlationId, long? generation, object payload) => new
    {
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        messageType,
        messageId,
        correlationId,
        agvId = options.AgvId,
        sessionGeneration = generation,
        sentAt = DateTimeOffset.UtcNow,
        payload
    };

    private static object SafetyBody(SafetySummary safety) => new
    {
        departureSafe = safety.DepartureSafe,
        vehicleStopped = safety.VehicleStopped,
        allTargetSlotsLocked = safety.AllTargetSlotsLocked,
        allUnlockOutputsReset = safety.AllUnlockOutputsReset,
        unknownPresent = safety.UnknownPresent,
        reasonCodes = safety.ReasonCodes
    };

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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string NewId() => Guid.NewGuid().ToString("D");

    public async ValueTask DisposeAsync()
    {
        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        foreach (Task? task in new[] { pump, heartbeat })
        {
            if (task is null) continue;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
        if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
        client?.Dispose();
        lifetime?.Dispose();
        writeGate.Dispose();
    }
}

public sealed class FakeOnboardOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 58005;
    public string AgvId { get; set; } = "AGV-FAKE-001";
    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";

    public string Describe() =>
        Host + ":" + Port.ToString(CultureInfo.InvariantCulture) + " as " + AgvId;
}
