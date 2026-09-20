using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

public sealed record PolicyCommand : CommandEnvelope
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnswerMode? Sublot { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnswerMode? LoadResult { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnswerMode? UnloadResult { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnswerMode? SafetyCheck { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnswerMode? SlotConfigurationActivation { get; init; }
}

/// <summary>Drops the session to ControlServer, or opens a new one with a full handshake.</summary>
public sealed record ConnectionCommand : CommandEnvelope
{
    public bool Connected { get; init; }
}

/// <summary>
/// Stops the peer sending anything while leaving its socket open, or lets it speak again
/// (control-server#234).
/// </summary>
/// <remarks>
/// This is the hung-onboard shape, and it is not <see cref="ConnectionCommand"/>: nothing is disconnected,
/// the TCP connection stays up, and every byte this peer would have written is dropped instead. Speaking
/// again does not by itself re-open a session — if the server has already closed the connection on its
/// liveness timeout, the scenario reconnects through <see cref="ConnectionCommand"/> afterwards.
/// </remarks>
public sealed record SilenceCommand : CommandEnvelope
{
    public bool Silent { get; init; }
}

/// <summary>One alarm as a scenario states it; the peer assigns alarmId and raisedAt.</summary>
public sealed record AlarmInput
{
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = "WARNING";
    public string SubjectType { get; init; } = "VEHICLE";
    public string? SubjectId { get; init; }
    public string? DisplayMessage { get; init; }
}

/// <summary>Replaces the whole alarm set and publishes it as the next OnboardAlarmSnapshot.</summary>
public sealed record AlarmsCommand : CommandEnvelope
{
    public IReadOnlyList<AlarmInput>? Alarms { get; init; }
}

/// <summary>
/// Reports a new abstract safety state. <c>safetyStateVersion</c> defaults to the next one, which
/// is what a real peer does; a scenario can pin it to exercise a revision the server refuses.
/// </summary>
public sealed record SafetyCommand : CommandEnvelope
{
    public long? SafetyStateVersion { get; init; }
    public bool? DepartureSafe { get; init; }
    public bool? VehicleStopped { get; init; }
    public bool? AllTargetSlotsLocked { get; init; }
    public bool? AllUnlockOutputsReset { get; init; }
    public bool? UnknownPresent { get; init; }
    public string[]? ReasonCodes { get; init; }
    public int[]? AffectedSlots { get; init; }
}

/// <summary>Answers one request the peer is holding open.</summary>
public sealed record AnswerCommand : CommandEnvelope
{
    /// <summary>Completed for a working operation, or a safe pre-departure answer.</summary>
    public bool Completed { get; init; } = true;

    /// <summary>
    /// Activations only. False concludes the activation on the peer but holds its result back, which is
    /// a vehicle that switched configuration and lost the link before the result got out.
    /// </summary>
    public bool Deliver { get; init; } = true;
}

public static class ControlPlane
{
    public static void MapControlPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();
        FakeOnboardOptions peerOptions = app.Services.GetRequiredService<FakeOnboardOptions>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/openapi.json", ControlPlaneConventions.OpenApiDocument);

        control.MapGet("/health", () =>
        {
            FakeOnboardState state = engine.Snapshot().State;
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                status = state.Readiness == "READY" ? "live" : "not-ready",
                state.Readiness,
                state.SessionGeneration
            }));
        });

        control.MapGet("/snapshot", () =>
        {
            FakeOnboardState state = engine.Snapshot().State;
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                // Which vehicle this process is. With several peers running at once, a
                // snapshot that did not say would let one stand in for another unnoticed.
                agvId = peerOptions.AgvId,
                state.SessionGeneration,
                state.Readiness,
                state.ReadinessReasonCode,
                state.Silent,
                state.SafetyStateVersion,
                state.Safety,
                state.Policy,
                state.ActiveSlotConfigurationVersion,
                state.ActiveSlotConfigurationFingerprint,
                activationOutcomes = state.ActivationOutcomes.Values,
                state.ActivationCommandMessageIds,
                state.ActivationResultsSent,
                state.AlarmSnapshotRevision,
                state.Alarms,
                pending = state.Pending.Values
                    .OrderBy(request => request.ReceivedAt)
                    .Select(request => new
                    {
                        request.Key,
                        request.MessageType,
                        request.MessageId,
                        request.ReceivedAt
                    }),
                wire = holder.Peer?.Wire().TakeLast(200) ?? []
            }));
        });

        control.MapPut("/policy", (PolicyCommand command) =>
            ControlPlaneConventions.Handle(engine, "policy", command, state =>
            {
                FakeOnboardPolicy policy = state.Policy with
                {
                    Sublot = command.Sublot ?? state.Policy.Sublot,
                    LoadResult = command.LoadResult ?? state.Policy.LoadResult,
                    UnloadResult = command.UnloadResult ?? state.Policy.UnloadResult,
                    SafetyCheck = command.SafetyCheck ?? state.Policy.SafetyCheck,
                    SlotConfigurationActivation =
                        command.SlotConfigurationActivation ?? state.Policy.SlotConfigurationActivation
                };
                return policy == state.Policy ? null : state with { Policy = policy };
            }));

        control.MapPut("/safety", async (SafetyCommand command, CancellationToken cancellationToken) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            SafetySummary? published = null;
            long publishedVersion = 0;
            IResult result = ControlPlaneConventions.Handle(engine, "safety", command, state =>
            {
                SafetySummary safety = state.Safety with
                {
                    DepartureSafe = command.DepartureSafe ?? state.Safety.DepartureSafe,
                    VehicleStopped = command.VehicleStopped ?? state.Safety.VehicleStopped,
                    AllTargetSlotsLocked = command.AllTargetSlotsLocked ?? state.Safety.AllTargetSlotsLocked,
                    AllUnlockOutputsReset = command.AllUnlockOutputsReset ?? state.Safety.AllUnlockOutputsReset,
                    UnknownPresent = command.UnknownPresent ?? state.Safety.UnknownPresent,
                    ReasonCodes = command.ReasonCodes ?? state.Safety.ReasonCodes
                };
                long version = command.SafetyStateVersion ?? state.SafetyStateVersion + 1;
                if (safety == state.Safety && version == state.SafetyStateVersion)
                {
                    return null;
                }
                published = safety;
                publishedVersion = version;
                return state with { Safety = safety, SafetyStateVersion = version };
            });
            if (published is not null)
            {
                // Sent after the state moved, so a scenario that reads the snapshot the moment this
                // returns sees the version it just published rather than the one before it.
                await peer.PublishSafetyStateChangedAsync(
                    publishedVersion, published, command.AffectedSlots ?? [], cancellationToken)
                    .ConfigureAwait(false);
            }
            return result;
        });

        control.MapPut("/answer/{key}", async (string key, AnswerCommand command, CancellationToken cancellationToken) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            PendingRequest? request = peer.Pending(key);
            if (request is null)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotFound, command.CommandId);
            }
            if (request.MessageType == "SlotConfigurationActivationCommand")
            {
                // Not through AnswerAsync's verbatim cache: a result replayed after a reconnect has to
                // carry the new session generation, so it is rebuilt from the peer's recorded outcome.
                await peer.AnswerActivationAsync(key, command.Completed, command.Deliver, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(ControlPlaneConventions.Envelope(engine, new
                {
                    command.CommandId,
                    answered = key,
                    request.MessageType,
                    delivered = command.Deliver
                }));
            }
            using JsonDocument payload = JsonDocument.Parse(request.PayloadJson);
            long generation = engine.Snapshot().State.SessionGeneration;
            string answer = request.MessageType switch
            {
                "SublotEntryRequested" => peer.SublotSubmitted(payload.RootElement, generation),
                "SlotOperationCommand" => peer.OperationResult(payload.RootElement, generation, command.Completed),
                "PreDepartureSafetyCheck" => peer.SafetyCheckResult(payload.RootElement, generation, command.Completed),
                _ => throw new InvalidOperationException("Unanswerable request type: " + request.MessageType)
            };
            await peer.AnswerAsync(key, answer, cancellationToken).ConfigureAwait(false);
            // Answering is an outbound message, not a state edit a scenario races on, so it is not
            // routed through the command engine and does not move the revision.
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                command.CommandId,
                answered = key,
                request.MessageType
            }));
        });
    }
}

/// <summary>
/// The two v2 surfaces a batch-3 scenario drives: dropping and re-opening the session, and the alarm
/// set message 9 reports. Kept apart from <see cref="ControlPlane.MapControlPlane"/> only for length.
/// </summary>
public static class ControlPlaneV2
{
    private static readonly string[] Severities = ["INFO", "WARNING", "CRITICAL"];

    public static void MapControlPlaneV2(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapPut("/connection", async (ConnectionCommand command) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null || string.IsNullOrWhiteSpace(command.CommandId))
            {
                return ControlPlaneConventions.Refused(
                    engine, peer is null ? ReasonCodes.NotAllowedInState : ReasonCodes.InvalidArgument, command.CommandId);
            }
            bool changed = command.Connected != peer.IsConnected;
            if (changed)
            {
                try
                {
                    if (command.Connected)
                    {
                        await peer.ReconnectAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await peer.DisconnectAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Said here rather than as a 500: a reconnect the server refused is a finding, and
                    // the scenario needs the reason in its evidence, not a stack trace in a log.
                    return Results.Json(
                        ControlPlaneConventions.Envelope(engine, new
                        {
                            command.CommandId,
                            reasonCode = "CONNECTION_CHANGE_FAILED",
                            detail = error.GetType().Name + ": " + error.Message
                        }),
                        statusCode: StatusCodes.Status409Conflict);
                }
            }
            FakeOnboardState state = engine.Snapshot().State;
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                command.CommandId,
                connected = peer.IsConnected,
                changed,
                state.SessionGeneration,
                state.Readiness
            }));
        });

        control.MapPut("/silence", (SilenceCommand command) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null || string.IsNullOrWhiteSpace(command.CommandId))
            {
                return ControlPlaneConventions.Refused(
                    engine,
                    peer is null ? ReasonCodes.NotAllowedInState : ReasonCodes.InvalidArgument,
                    command.CommandId);
            }
            return ControlPlaneConventions.Handle(engine, "silence", command, state =>
                state.Silent == command.Silent ? null : state with { Silent = command.Silent });
        });

        control.MapPut("/alarms", async (AlarmsCommand command, CancellationToken cancellationToken) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            IReadOnlyList<AlarmInput> inputs = command.Alarms ?? [];
            if (inputs.Any(input => string.IsNullOrWhiteSpace(input.Code) ||
                                    string.IsNullOrWhiteSpace(input.SubjectType) ||
                                    !Severities.Contains(input.Severity, StringComparer.Ordinal)))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }
            DateTimeOffset raisedAt = DateTimeOffset.UtcNow;
            FakeAlarm[] alarms =
            [
                .. inputs.Select(input => new FakeAlarm
                {
                    AlarmId = Guid.NewGuid().ToString("D"),
                    Code = input.Code,
                    Severity = input.Severity,
                    RaisedAt = raisedAt,
                    SubjectType = input.SubjectType,
                    SubjectId = input.SubjectId,
                    DisplayMessage = input.DisplayMessage
                })
            ];
            bool published = false;
            IResult result = ControlPlaneConventions.Handle(engine, "alarms", command, state =>
            {
                published = true;
                return state with { Alarms = alarms, AlarmSnapshotRevision = state.AlarmSnapshotRevision + 1 };
            });
            // A disconnected peer keeps the new set and reports it in its next handshake, which is
            // what a vehicle does with alarms raised while the link was down.
            if (published && peer.IsConnected)
            {
                await peer.PublishAlarmsAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        });
    }
}

/// <summary>
/// Holds the live peer so the control plane can reach it. The peer connects after the web host
/// starts, so this cannot be a constructor dependency.
/// </summary>
public sealed class OnboardPeerHolder
{
    public OnboardPeerSession? Peer { get; set; }
}
