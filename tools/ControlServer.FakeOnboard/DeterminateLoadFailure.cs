using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>Answers a held load command with a determinate failure.</summary>
public sealed record DeterminateLoadFailureCommand : CommandEnvelope
{
    /// <summary>The failed slot's reason code; <c>OPERATOR_TIMEOUT</c> when omitted.</summary>
    public string? ReasonCode { get; init; }
}

/// <summary>
/// The answer ADR-cross-0058 decision 5 describes, for control-server#81's scenario: a load that ran out its
/// station deadline with every door shut. The first target slot is <c>FAILED</c> under a reason code, the rest
/// <c>NOT_STARTED</c>, and every slot <c>EMPTY</c>, <c>LOCKED</c> and <c>RESET</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not what the v2 onboard sends.</b> After its deadline the v2 onboard reopens a closed, empty slot rather
/// than failing it (8005-agv-program#55). The server keeps settling this answer defensively because the protocol
/// lets another onboard version send it, and this peer is how a scenario reaches that path at all. It is sent
/// when the scenario says so; this peer has no clock and no deadline of its own.
/// </para>
/// <para>
/// Answered through <see cref="OnboardPeerSession.AnswerAsync"/>, so a replayed command gets this same line back,
/// like every other answer. The <c>/answer</c> shape (<c>completed: false</c>) is left as it is: the existing
/// recovery scenarios depend on it.
/// </para>
/// </remarks>
public static class ControlPlaneDeterminateLoadFailure
{
    // The envelope's own settings: this content hash has to agree with the server's, byte for byte.
    private static readonly JsonSerializerOptions SerializerOptions = ProtocolEnvelope.SerializerOptions;

    public static void MapControlPlaneDeterminateLoadFailure(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();
        FakeOnboardOptions peerOptions = app.Services.GetRequiredService<FakeOnboardOptions>();

        app.MapGroup("/control/v1").MapPut(
            "/determinate-load-failures/{key}",
            async (string key, DeterminateLoadFailureCommand command, CancellationToken cancellationToken) =>
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
                if (request.MessageType != "SlotOperationCommand" ||
                    payload.RootElement.GetProperty("operationType").GetString() != "LOAD")
                {
                    return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
                }

                string reasonCode = command.ReasonCode ?? ServerReasonCodes.OperatorTimeout;
                string answer = Result(
                    payload.RootElement,
                    peerOptions.AgvId,
                    engine.Snapshot().State.SessionGeneration,
                    reasonCode);
                await peer.AnswerAsync(key, answer, cancellationToken).ConfigureAwait(false);
                return Results.Json(ControlPlaneConventions.Envelope(engine, new
                {
                    command.CommandId,
                    answered = key,
                    request.MessageType,
                    reasonCode
                }));
            });
    }

    private static string Result(JsonElement commandPayload, string agvId, long generation, string reasonCode)
    {
        object[] slotResults =
        [
            .. commandPayload.GetProperty("slots").EnumerateArray().Select((slot, index) => (object)new
            {
                slotNo = slot.GetInt32(),
                outcome = index == 0 ? "FAILED" : "NOT_STARTED",
                finalPhysicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = index == 0 ? new[] { reasonCode } : []
            })
        ];
        // Declaration order is the hash contract: OnboardMessageProcessor.ComputeOperationResultContentHash
        // serializes the business content in exactly this order, and refuses the message when they differ.
        var content = new
        {
            demandId = commandPayload.GetProperty("demandId").GetString(),
            slotOperationAttemptId = commandPayload.GetProperty("slotOperationAttemptId").GetString(),
            operationType = "LOAD",
            overallOutcome = "FAILED",
            slotResults,
            observedAt = DateTimeOffset.UtcNow,
            journalCheckpoint = "FAKE-ONBOARD-DETERMINATE-FAILURE"
        };
        string hash = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, SerializerOptions))).ToLowerInvariant();

        return ProtocolEnvelope.Serialize(
            "OperationResult",
            Guid.NewGuid().ToString("D"),
            correlationId: null,
            agvId,
            generation,
            DateTimeOffset.UtcNow,
            new
            {
                content.demandId,
                content.slotOperationAttemptId,
                content.operationType,
                content.overallOutcome,
                content.slotResults,
                content.observedAt,
                content.journalCheckpoint,
                resultContentSha256 = hash
            });
    }
}
