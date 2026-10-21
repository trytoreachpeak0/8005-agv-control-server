using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>Asks for the remaining-baskets skip of a held load command.</summary>
public sealed record RemainingBasketsSkipCommand : CommandEnvelope
{
    /// <summary>The target slots the operator loaded, at least one and not all of them.</summary>
    public int[] LoadedSlots { get; init; } = [];
}

/// <summary>One skip this peer asked for, and what the server answered.</summary>
public sealed record FakeRemainingBasketsSkip
{
    public required string SkipRequestId { get; init; }
    public required string Key { get; init; }
    public required string DemandId { get; init; }
    public required string SlotOperationAttemptId { get; init; }
    public required int[] TargetSlots { get; init; }
    public required int[] LoadedSlots { get; init; }
    public required string RequestMessageId { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public string? Outcome { get; init; }
    public int[] AuthorizedSlots { get; init; } = [];
    public string? ProblemReasonCode { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public bool ResultReported { get; init; }
}

/// <summary>
/// 「剩下的不装」（REQ-0367，CP-0021，control-server#830）的车载端那一半，够一个服务端场景用：对一条还压着的装货命令，先报一份逐仓快照（装进的仓有货、
/// 其余空着，都锁闭复位），再发 <c>RemainingBasketsSkipRequested</c>；服务端答 <c>AUTHORIZED</c> 就按 <c>COMPLETED_REMAINING_SKIPPED</c> 答那条装货命令，
/// 答 <c>REJECTED</c> 就只记下、命令照旧压着。两种答复都回 <c>DurableAck</c>（RELIABLE）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么先报快照。</b>服务端只按车报来的逐仓读数判，而真车关门那一下发的 <c>SafetyStateChanged</c> 不带状态；这个假车没有 IO，也从不在会话中途报
/// 快照，服务端手上只有握手那一份（全空）。先报一份当下的快照，服务端读到的就是这次装货此刻的样子——真车载端在服务端要快照时报的也正是这一份。
/// </para>
/// <para>
/// 不是真车载端：没有日志簿、没有按键，按下与重提都由场景驱动。授权之后才结束装货、只报授权的仓为 <c>COMPLETED</c>、其余带
/// <c>REMAINING_BASKETS_SKIPPED</c>，与 wire-notes 减篮一节写给车载端的规矩一致。
/// </para>
/// </remarks>
public sealed class FakeRemainingBasketsSkips
{
    private static readonly JsonSerializerOptions SerializerOptions = ProtocolEnvelope.SerializerOptions;

    private readonly ConcurrentDictionary<string, FakeRemainingBasketsSkip> skips = new(StringComparer.Ordinal);

    public IReadOnlyList<FakeRemainingBasketsSkip> Snapshot() => [.. skips.Values.OrderBy(item => item.RequestedAt)];

    public async Task<FakeRemainingBasketsSkip> RequestAsync(
        OnboardPeerSession peer,
        CommandEngine<FakeOnboardState> engine,
        SlotStateSeed seed,
        string key,
        JsonElement commandPayload,
        int[] loadedSlots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(seed);
        int[] targets = [.. commandPayload.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32()).Order()];
        int[] loaded = [.. loadedSlots.Distinct().Order()];

        long version = engine.Mutate(state =>
        {
            long next = state.SafetyStateVersion + 1;
            return (state with { SafetyStateVersion = next }, next);
        });
        JsonArray slotStates = JsonSerializer.SerializeToNode(seed.Render(), SerializerOptions)!.AsArray();
        foreach (JsonNode? slot in slotStates)
        {
            int slotNo = slot!["slotNo"]!.GetValue<int>();
            if (targets.Contains(slotNo))
            {
                slot["physicalState"] = loaded.Contains(slotNo) ? "OCCUPIED" : "EMPTY";
                slot["lockState"] = "LOCKED";
                slot["unlockOutputState"] = "RESET";
            }
        }
        FakeOnboardState state = engine.Snapshot().State;
        await peer.SendEnvelopeAsync("SafetyStateSnapshot", Guid.NewGuid().ToString("D"), new
        {
            safetyStateVersion = version,
            observedAt = DateTimeOffset.UtcNow,
            safety = new
            {
                departureSafe = state.Safety.DepartureSafe,
                vehicleStopped = state.Safety.VehicleStopped,
                allTargetSlotsLocked = true,
                allUnlockOutputsReset = true,
                unknownPresent = false,
                reasonCodes = Array.Empty<string>()
            },
            slotStates
        }, cancellationToken).ConfigureAwait(false);

        string skipRequestId = Guid.NewGuid().ToString("D");
        string messageId = Guid.NewGuid().ToString("D");
        FakeRemainingBasketsSkip record = new()
        {
            SkipRequestId = skipRequestId,
            Key = key,
            DemandId = commandPayload.GetProperty("demandId").GetString()!,
            SlotOperationAttemptId = commandPayload.GetProperty("slotOperationAttemptId").GetString()!,
            TargetSlots = targets,
            LoadedSlots = loaded,
            RequestMessageId = messageId,
            RequestedAt = DateTimeOffset.UtcNow
        };
        // Recorded before it is sent: the answer can arrive before the send returns.
        skips[skipRequestId] = record;
        await peer.SendEnvelopeAsync("RemainingBasketsSkipRequested", messageId, new
        {
            skipRequestId,
            demandId = record.DemandId,
            operationSessionId = commandPayload.GetProperty("operationSessionId").GetString(),
            slotOperationAttemptId = record.SlotOperationAttemptId,
            loadedSlots = loaded,
            @operator = new
            {
                operatorId = "FAKE-ONBOARD-OPERATOR",
                verificationMethod = "BADGE",
                verifiedAt = DateTimeOffset.UtcNow
            }
        }, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// The server's answer: recorded, and on <c>AUTHORIZED</c> the held load command is answered with the skipped result. The
    /// <c>DurableAck</c> is the caller's, sent for every answer.
    /// </summary>
    public async Task ObserveResultAsync(
        OnboardPeerSession peer,
        JsonElement result,
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        JsonElement payload = result.GetProperty("payload");
        string skipRequestId = payload.GetProperty("skipRequestId").GetString() ?? string.Empty;
        if (!skips.TryGetValue(skipRequestId, out FakeRemainingBasketsSkip? record))
        {
            return;
        }
        string outcome = payload.GetProperty("outcome").GetString() ?? string.Empty;
        JsonElement problem = payload.GetProperty("problem");
        record = record with
        {
            Outcome = outcome,
            AuthorizedSlots = [.. payload.GetProperty("loadedSlots").EnumerateArray().Select(slot => slot.GetInt32())],
            ProblemReasonCode = problem.ValueKind == JsonValueKind.Object ? problem.GetProperty("reasonCode").GetString() : null,
            DecidedAt = DateTimeOffset.UtcNow
        };
        skips[skipRequestId] = record;
        if (outcome != "AUTHORIZED" || record.ResultReported)
        {
            return;
        }
        skips[skipRequestId] = record with { ResultReported = true };
        await peer.AnswerAsync(record.Key, SkippedResult(record, agvId, generation), cancellationToken).ConfigureAwait(false);
    }

    private static string SkippedResult(FakeRemainingBasketsSkip record, string agvId, long generation)
    {
        object[] slotResults =
        [
            .. record.TargetSlots.Select(slot => (object)new
            {
                slotNo = slot,
                outcome = record.AuthorizedSlots.Contains(slot) ? "COMPLETED" : "NOT_STARTED",
                finalPhysicalState = record.AuthorizedSlots.Contains(slot) ? "OCCUPIED" : "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = record.AuthorizedSlots.Contains(slot) ? Array.Empty<string>() : [ServerReasonCodes.RemainingBasketsSkipped]
            })
        ];
        // Declaration order is the hash contract (OnboardMessageProcessor.ComputeOperationResultContentHash).
        var content = new
        {
            demandId = record.DemandId,
            slotOperationAttemptId = record.SlotOperationAttemptId,
            operationType = "LOAD",
            overallOutcome = "COMPLETED_REMAINING_SKIPPED",
            slotResults,
            observedAt = DateTimeOffset.UtcNow,
            journalCheckpoint = "FAKE-ONBOARD-REMAINING-BASKETS-SKIPPED"
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

public static class ControlPlaneRemainingBasketsSkip
{
    public static void MapControlPlaneRemainingBasketsSkip(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();
        FakeRemainingBasketsSkips skips = app.Services.GetRequiredService<FakeRemainingBasketsSkips>();
        SlotStateSeed seed = app.Services.GetRequiredService<SlotStateSeed>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/remaining-baskets-skips", () =>
            Results.Json(ControlPlaneConventions.Envelope(engine, new { skips = skips.Snapshot() })));

        control.MapPut("/remaining-baskets-skips/{key}", async (
            string key, RemainingBasketsSkipCommand command, CancellationToken cancellationToken) =>
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
            int[] targets = [.. payload.RootElement.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32())];
            if (request.MessageType != "SlotOperationCommand" ||
                payload.RootElement.GetProperty("operationType").GetString() != "LOAD" ||
                command.LoadedSlots.Length == 0 || command.LoadedSlots.Length >= targets.Length ||
                !command.LoadedSlots.All(targets.Contains))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }
            FakeRemainingBasketsSkip skip = await skips.RequestAsync(
                peer, engine, seed, key, payload.RootElement, command.LoadedSlots, cancellationToken).ConfigureAwait(false);
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                command.CommandId,
                skip.SkipRequestId,
                skip.LoadedSlots
            }));
        });
    }
}
