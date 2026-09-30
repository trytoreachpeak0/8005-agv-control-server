using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>How this peer answers the next <c>SlotFaultDeclarationCommand</c>.</summary>
public sealed record SlotFaultDeclarationPolicyCommand : CommandEnvelope
{
    /// <summary>
    /// <c>AUTO</c> (the default): <c>APPLIED</c> while the declared attempt's command is still held open on this peer,
    /// <c>NOT_APPLICABLE</c> once it was answered. <c>NOT_APPLICABLE</c>: refuse whatever the state, which is the operator
    /// closing the door the moment the administrator declared, before the result went out. <c>HOLD</c>: record the
    /// command and answer nothing.
    /// </summary>
    public string? Answer { get; init; }
}

/// <summary>What this peer knows about one declaration it received.</summary>
public sealed record FakeSlotFaultDeclaration
{
    public string DeclarationId { get; init; } = string.Empty;
    public string SlotOperationAttemptId { get; init; } = string.Empty;
    public int SlotNo { get; init; }
    public string CommandMessageId { get; init; } = string.Empty;
    public int TimesReceived { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public string? Outcome { get; init; }
    public string? ResultMessageId { get; init; }
    public string? OperationResultMessageId { get; init; }
}

/// <summary>
/// The onboard half of REQ-0359 as far as a server scenario needs it (control-server#383): take a
/// <c>SlotFaultDeclarationCommand</c> and answer it the way the v3 onboard does (onboard-hmi#215).
/// </summary>
/// <remarks>
/// <para>
/// <b>APPLIED is two messages.</b> The declaration result first, then the <c>OperationResult</c> for the held command:
/// the slots before the declared one <c>COMPLETED</c>, the declared one <c>UNKNOWN</c> under <c>SLOT_FAULT_DECLARED</c>,
/// the ones after it <c>NOT_STARTED</c> (CP-0005 section 1, item two, note 4). The operation result goes out through
/// <see cref="OnboardPeerSession.AnswerAsync"/>, so a replayed load command is answered with that same line.
/// </para>
/// <para>
/// <b>Business deduplication on <c>declarationId</c>.</b> The server replays an unanswered command on every reconnect; a
/// declaration already answered gets its first result line back, never a second decision.
/// </para>
/// <para>
/// Which slot counts as "before" is the command's own slot order. This peer has no IO and does not know which doors it
/// opened; the order is what a real onboard walks, one slot at a time (REQ-0357).
/// </para>
/// </remarks>
public sealed class FakeSlotFaultDeclarations
{
    private static readonly JsonSerializerOptions SerializerOptions = ProtocolEnvelope.SerializerOptions;

    private readonly ConcurrentDictionary<string, FakeSlotFaultDeclaration> declarations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> resultLines = new(StringComparer.Ordinal);

    public string Answer { get; set; } = "AUTO";

    public IReadOnlyList<FakeSlotFaultDeclaration> Snapshot() =>
        [.. declarations.Values.OrderBy(item => item.ReceivedAt)];

    public async Task ObserveCommandAsync(
        OnboardPeerSession peer,
        JsonElement command,
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        string commandMessageId = command.GetProperty("messageId").GetString() ?? string.Empty;
        JsonElement payload = command.GetProperty("payload");
        string declarationId = payload.GetProperty("declarationId").GetString() ?? string.Empty;
        string attemptId = payload.GetProperty("slotOperationAttemptId").GetString() ?? string.Empty;
        int slotNo = payload.GetProperty("slotNo").GetInt32();

        FakeSlotFaultDeclaration record = declarations.AddOrUpdate(
            declarationId,
            _ => new FakeSlotFaultDeclaration
            {
                DeclarationId = declarationId,
                SlotOperationAttemptId = attemptId,
                SlotNo = slotNo,
                CommandMessageId = commandMessageId,
                TimesReceived = 1,
                ReceivedAt = DateTimeOffset.UtcNow
            },
            (_, existing) => existing with { TimesReceived = existing.TimesReceived + 1 });
        if (resultLines.TryGetValue(declarationId, out string? answered))
        {
            await peer.ResendLineAsync(answered, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (Answer == "HOLD")
        {
            return;
        }

        string key = "operation:" + attemptId;
        PendingRequest? held = peer.Pending(key);
        bool applies = Answer != "NOT_APPLICABLE" && held is not null && held.MessageType == "SlotOperationCommand";
        string outcome = applies ? "APPLIED" : "NOT_APPLICABLE";
        string resultMessageId = Guid.NewGuid().ToString("D");
        string resultLine = ProtocolEnvelope.Serialize(
            "SlotFaultDeclarationResult",
            resultMessageId,
            correlationId: null,
            agvId,
            generation,
            DateTimeOffset.UtcNow,
            new
            {
                declarationId,
                slotOperationAttemptId = attemptId,
                outcome,
                problem = applies
                    ? null
                    : new
                    {
                        reasonCode = "ACTION_NOT_ALLOWED_IN_STATE",
                        fieldPath = "payload.slotOperationAttemptId",
                        displayMessage = "The declared slot is no longer waiting for the operator."
                    }
            });
        resultLines[declarationId] = resultLine;
        declarations[declarationId] = record with { Outcome = outcome, ResultMessageId = resultMessageId };
        await peer.ResendLineAsync(resultLine, cancellationToken).ConfigureAwait(false);
        if (!applies)
        {
            return;
        }

        using JsonDocument heldCommand = JsonDocument.Parse(held!.PayloadJson);
        string operationResult = DeclaredOperationResult(heldCommand.RootElement, slotNo, agvId, generation, out string id);
        declarations[declarationId] = declarations[declarationId] with { OperationResultMessageId = id };
        await peer.AnswerAsync(key, operationResult, cancellationToken).ConfigureAwait(false);
    }

    private static string DeclaredOperationResult(
        JsonElement commandPayload, int declaredSlot, string agvId, long generation, out string messageId)
    {
        string operationType = commandPayload.GetProperty("operationType").GetString() ?? "LOAD";
        int[] slots = [.. commandPayload.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32())];
        int declaredIndex = Array.IndexOf(slots, declaredSlot);
        object[] slotResults =
        [
            .. slots.Select((slot, index) => (object)(index < declaredIndex
                ? new
                {
                    slotNo = slot,
                    outcome = "COMPLETED",
                    finalPhysicalState = operationType == "LOAD" ? "OCCUPIED" : "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }
                : index == declaredIndex
                    ? new
                    {
                        slotNo = slot,
                        outcome = "UNKNOWN",
                        finalPhysicalState = "UNKNOWN",
                        lockState = "UNLOCKED",
                        unlockOutputState = "RESET",
                        reasonCodes = new[] { ServerReasonCodes.SlotFaultDeclared }
                    }
                    : new
                    {
                        slotNo = slot,
                        outcome = "NOT_STARTED",
                        finalPhysicalState = operationType == "LOAD" ? "EMPTY" : "OCCUPIED",
                        lockState = "LOCKED",
                        unlockOutputState = "RESET",
                        reasonCodes = Array.Empty<string>()
                    }))
        ];
        // Declaration order is the hash contract, as in DeterminateLoadFailure.cs.
        var content = new
        {
            demandId = commandPayload.GetProperty("demandId").GetString(),
            slotOperationAttemptId = commandPayload.GetProperty("slotOperationAttemptId").GetString(),
            operationType,
            overallOutcome = "UNKNOWN",
            slotResults,
            observedAt = DateTimeOffset.UtcNow,
            journalCheckpoint = "FAKE-ONBOARD-SLOT-FAULT-DECLARED"
        };
        string hash = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, SerializerOptions))).ToLowerInvariant();
        messageId = Guid.NewGuid().ToString("D");
        return ProtocolEnvelope.Serialize(
            "OperationResult",
            messageId,
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

/// <summary>The control plane for <see cref="FakeSlotFaultDeclarations"/>.</summary>
public static class ControlPlaneSlotFaultDeclaration
{
    private static readonly string[] Answers = ["AUTO", "NOT_APPLICABLE", "HOLD"];

    public static void MapControlPlaneSlotFaultDeclaration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        FakeSlotFaultDeclarations declarations = app.Services.GetRequiredService<FakeSlotFaultDeclarations>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/slot-fault-declarations", () =>
            Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                answer = declarations.Answer,
                declarations = declarations.Snapshot()
            })));

        control.MapPut("/slot-fault-declaration-policy", (SlotFaultDeclarationPolicyCommand command) =>
        {
            if (command.Answer is null || !Answers.Contains(command.Answer, StringComparer.Ordinal))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }
            declarations.Answer = command.Answer;
            return Results.Json(ControlPlaneConventions.Envelope(engine, new { command.CommandId, answer = command.Answer }));
        });
    }
}
