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
}

public static class ControlPlane
{
    public static void MapControlPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();
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
                state.SessionGeneration,
                state.Readiness,
                state.ReadinessReasonCode,
                state.SafetyStateVersion,
                state.Safety,
                state.Policy,
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
                    SafetyCheck = command.SafetyCheck ?? state.Policy.SafetyCheck
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
            using JsonDocument payload = JsonDocument.Parse(request.PayloadJson);
            long generation = engine.Snapshot().State.SessionGeneration;
            object answer = request.MessageType switch
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
/// Holds the live peer so the control plane can reach it. The peer connects after the web host
/// starts, so this cannot be a constructor dependency.
/// </summary>
public sealed class OnboardPeerHolder
{
    public OnboardPeerSession? Peer { get; set; }
}
