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
    FakeOnboardOptions options) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = ProtocolEnvelope.SerializerOptions;
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
        await SendLineAsync(Envelope("SessionHello", NewId(), null, null, new
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
        await SendLineAsync(Envelope("CapabilitySnapshot", NewId(), null, generation, new
        {
            capabilityVersion = 1,
            observedAt = DateTimeOffset.UtcNow,
            slotModelVersion = "fake-slot-model-v1",
            activeSlotConfigurationVersion = "fake-slot-config-v1",
            slotStates = SlotStates(),
            supportsBatchUnlock = false,
            onboardJournalFormatVersion = 1
        }), cancellationToken).ConfigureAwait(false);
        await ReadRequiredAsync(reader, "SnapshotAppliedAck", cancellationToken).ConfigureAwait(false);

        await SendLineAsync(Envelope("SafetyStateSnapshot", NewId(), null, generation, new
        {
            safetyStateVersion = state.SafetyStateVersion,
            observedAt = DateTimeOffset.UtcNow,
            safety = SafetyBody(state.Safety),
            slotStates = SlotStates()
        }), cancellationToken).ConfigureAwait(false);
        await ReadRequiredAsync(reader, "SnapshotAppliedAck", cancellationToken).ConfigureAwait(false);

        await SendLineAsync(Envelope("RecoveryStateReport", NewId(), null, generation, new
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
                await SendLineAsync(Envelope("Heartbeat", NewId(), null, state.SessionGeneration, new
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
                // Keyed on the demand, the way a slot operation is keyed on its attempt. A fixed
                // key made the answer cache -- which exists so a replayed request gets the
                // identical reply -- hand the second journey's entry request the first journey's
                // SublotSubmitted, naming a demand the server had already completed. One session
                // could therefore only ever load once, and the second journey sat in AwaitingSublot
                // until the scenario timed out; L2 evidence 20260908-auto-charge-endurance-004.
                await OnRequestAsync(
                    "sublot:" + root.GetProperty("payload").GetProperty("demandId").GetString(),
                    messageType, messageId, root, generation,
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
            case "PreDepartureSafetyCheck":
                // Keyed on the check id, for the same reason the sublot key is keyed on its demand:
                // a fixed key replays the first journey's result to the second journey's check, and
                // the server correctly refuses evidence that names another check. See the note
                // above -- this half surfaced one run later, at AwaitingDepartureSafety.
                await OnRequestAsync(
                    "safety-check:" +
                        root.GetProperty("payload").GetProperty("preDepartureSafetyCheckId").GetString(),
                    messageType, messageId, root, generation,
                    engine.Snapshot().State.Policy.SafetyCheck,
                    (payload, gen) => SafetyCheckResult(payload, gen, safe: true),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "LoadCancellationAuthorization":
                await OnCancellationAuthorizedAsync(root, messageId, generation, cancellationToken)
                    .ConfigureAwait(false);
                return;
            default:
                // HeartbeatAck, DurableAck, SessionReadiness and the recovery commands carry no
                // obligation for this peer. They are already on the wire log.
                return;
        }
    }

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
        Func<JsonElement, long, string> buildAnswer,
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
    public async Task AnswerAsync(string key, string line, CancellationToken cancellationToken)
    {
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

    public string SublotSubmitted(JsonElement requestPayload, long generation) =>
        Envelope("SublotSubmitted", NewId(), null, generation, new
        {
            demandId = requestPayload.GetProperty("demandId").GetString(),
            operationSessionId = requestPayload.GetProperty("operationSessionId").GetString(),
            stationId = requestPayload.GetProperty("stationId").GetString(),
            worklistRevision = requestPayload.GetProperty("worklistRevision").GetInt64(),
            // The request now names every sublot still enterable in this dispatch range. A real
            // operator picks one; this peer takes the first, which is the whole set while a journey
            // carries a single demand.
            sublot = requestPayload.GetProperty("expectedSublots").EnumerateArray().First().GetString(),
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
    public string OperationResult(
        JsonElement commandPayload, long generation, bool completed, bool determinate = false)
    {
        string operationType = commandPayload.GetProperty("operationType").GetString() ?? "LOAD";
        string finalState = operationType == "LOAD" ? "OCCUPIED" : "EMPTY";
        // A failed slot reports EMPTY only when the caller asks for a determinate failure. Under
        // ADR-cross-0058 decision 5 that shape -- known state, locked door, reset output -- settles
        // as StationOperationStatus.Failed and never reaches the recovery handshake, so it is the
        // wrong thing to send when a scenario is exercising recovery. UNKNOWN is what "the vehicle
        // cannot say what happened" looks like on the wire, and that is what still needs a person.
        string failedState = determinate ? "EMPTY" : "UNKNOWN";
        SlotResult[] slotResults = commandPayload.GetProperty("slots").EnumerateArray()
            .Select(slot => new SlotResult(
                slot.GetInt32(),
                completed ? "COMPLETED" : "FAILED",
                completed ? finalState : failedState,
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

    public string SafetyCheckResult(JsonElement checkPayload, long generation, bool safe)
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

    /// <summary>Reports a new safety state, the way the real peer reports every change.</summary>
    /// <summary>
    /// Raises the cancellation an operator raises at a pickup stop that turns out to have nothing
    /// to load. Unlike every other message this peer sends, it answers no request -- so it is
    /// driven from the control plane rather than from the receive pump. The authorization comes
    /// back as a message this peer has no obligation to act on; it lands in the wire log, and the
    /// termination it triggers is read from the server's own tables.
    /// </summary>
    public async Task RequestLoadCancellationAsync(
        string cancellationId,
        string demandId,
        string? slotOperationAttemptId,
        string reason,
        CancellationToken cancellationToken)
    {
        long generation = engine.Snapshot().State.SessionGeneration;
        await SendLineAsync(
            Envelope("LoadCancellationStartRequested", NewId(), null, generation, new
            {
                cancellationId,
                demandId,
                slotOperationAttemptId,
                @operator = new
                {
                    operatorId = "FAKE-ONBOARD-OPERATOR",
                    verificationMethod = "BADGE",
                    verifiedAt = DateTimeOffset.UtcNow
                },
                reason
            }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Proves the slots came back empty after the server authorizes a cancellation raised while a
    /// slot operation was in flight. Nothing here is owed when the cancellation carried no attempt
    /// id: no door was ever opened, so there is no emptiness to prove, and the authorization is the
    /// whole handshake (OnboardRecoveryCoordinator terminates the demand there instead).
    /// </summary>
    private async Task OnCancellationAuthorizedAsync(
        JsonElement root,
        string messageId,
        long generation,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        if (payload.GetProperty("decision").GetString() != "AUTHORIZED") return;
        if (payload.GetProperty("slotOperationAttemptId").ValueKind == JsonValueKind.Null) return;
        // Keyed on the cancellation rather than on the attempt: the server keys its workflow that
        // way, and a replayed authorization must get back the identical result. A second
        // LoadCancellationResult under one workflow carrying a fresh messageId is refused as
        // "already has a different first durable result" and tears the session down.
        await OnRequestAsync(
            "cancellation:" + payload.GetProperty("cancellationId").GetString(),
            "LoadCancellationAuthorization",
            messageId,
            root,
            generation,
            engine.Snapshot().State.Policy.CancellationResult,
            LoadCancellationResult,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the emptiness proof from the authorization's own slot list. Reading the slots back
    /// from the message the server just sent is what makes this survive the server changing its
    /// mind about which slots the cancellation covers: HasExactSafeSlotResult compares the result
    /// against the workflow's stored slots and refuses anything that is not exactly that set.
    /// Unlike an OperationResult this message carries no content hash -- its schema has no field
    /// for one, and the server hashes the whole envelope itself.
    /// </summary>
    public string LoadCancellationResult(JsonElement authorizationPayload, long generation) =>
        Envelope("LoadCancellationResult", NewId(), null, generation, new
        {
            cancellationId = authorizationPayload.GetProperty("cancellationId").GetString(),
            demandId = authorizationPayload.GetProperty("demandId").GetString(),
            slotOperationAttemptId = authorizationPayload.GetProperty("slotOperationAttemptId").GetString(),
            overallOutcome = "ALL_EMPTY",
            slotResults = authorizationPayload.GetProperty("slots").EnumerateArray()
                .Select(slot => new
                {
                    slotNo = slot.GetInt32(),
                    outcome = "COMPLETED",
                    finalPhysicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                })
                .ToArray(),
            observedAt = DateTimeOffset.UtcNow
        });

    public async Task PublishSafetyStateChangedAsync(
        long safetyStateVersion,
        SafetySummary safety,
        int[] affectedSlots,
        CancellationToken cancellationToken)
    {
        long generation = engine.Snapshot().State.SessionGeneration;
        await SendLineAsync(Envelope("SafetyStateChanged", NewId(), null, generation, new
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
        await SendLineAsync(Envelope("SnapshotAppliedAck", NewId(), messageId, generation, new
        {
            snapshotMessageId = messageId,
            snapshotKind = kind,
            appliedRevision = revision,
            appliedContentSha256 = Sha256(root.GetRawText())
        }), cancellationToken).ConfigureAwait(false);
    }

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
            await writer!.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
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

    private string Envelope(string messageType, string messageId, string? correlationId, long? generation, object payload) =>
        ProtocolEnvelope.Serialize(
            messageType, messageId, correlationId, options.AgvId, generation, DateTimeOffset.UtcNow, payload);

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

    private static object[] SlotStates() => Enumerable.Range(1, 8)
        .Select(slotNo => (object)new
        {
            slotNo,
            operability = "OPERABLE",
            administrativeAvailability = "ENABLED",
            physicalState = "EMPTY",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = Array.Empty<string>()
        })
        .ToArray();

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
