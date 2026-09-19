using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Reads one vehicle's Onboard-side facts off its current session: what a dispatch decision admits a vehicle on, and
/// what the advance side trusts an arrival on.
/// </summary>
/// <remarks>
/// Both sides read these through this one class (control-server#209): it used to be private to
/// <see cref="JourneyRuntimeEngine"/>, and when the dispatch round moved out it would otherwise have had to be copied,
/// with two copies of a safety read free to drift apart. Everything here is a read; nothing is tracked.
/// </remarks>
public sealed class OnboardDispatchFactsReader(
    ControlServerDbContext dbContext,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider)
{
    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    /// <summary>
    /// One vehicle's Onboard-side facts, read against that vehicle's own session.
    /// </summary>
    /// <remarks>
    /// The <c>agvId</c> is a parameter rather than the configured one because every read below is
    /// session-scoped: the session row, the two snapshots and the last inbound message all belong
    /// to one vehicle's session generation. Reading them for the configured vehicle while deciding
    /// for another would admit a vehicle on a different vehicle's safety evidence.
    /// </remarks>
    public async Task<OnboardDispatchFacts?> ReadOnboardFactsAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await CurrentReadySessionAsync(agvId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }
        if (session.SafetyRevision is not long safetyRevision)
        {
            return null;
        }
        ProtocolInboxRow? capability = await LatestInboxForSessionAsync(
            "CapabilitySnapshot", agvId, session.SessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        // The session's baseline SafetyStateSnapshot, not its latest: slot availability is the session baseline
        // (see below), and since control-server#142 a session can carry later snapshots -- the vehicle's answer
        // when the dashboard asks for an overdue slot's readings, which reads a slot mid-operation as occupied
        // or unlocked. Their safety summary still counts, through LatestSafetySummaryForSessionAsync.
        ProtocolInboxRow? safetyRow = await BaselineSafetySnapshotForSessionAsync(
            agvId, session.SessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        // The snapshot is sent once per session; every later change arrives as SafetyStateChanged
        // (ADR-cross-0033), which carries the same safety summary and no slotStates. Reading the
        // summary from the snapshot alone froze it at whatever was true when the session was
        // established: a session opened while the vehicle was moving reported vehicleStopped false
        // for its whole life, so the vehicle could stop at the pickup station and neither be
        // admitted nor have its arrival trusted -- while SessionRecoveries, which SafetyStateChanged
        // does update, correctly showed Ready. The reverse is worse: a session opened at rest went
        // on reporting the vehicle stopped after Onboard said it had moved.
        ProtocolInboxRow? safetySummaryRow = await LatestSafetySummaryForSessionAsync(
            agvId, session.SessionGeneration, safetyRevision, cancellationToken)
            .ConfigureAwait(false);
        if (capability is null || safetyRow is null || safetySummaryRow is null)
        {
            return null;
        }
        using JsonDocument capabilityDocument = JsonDocument.Parse(capability.RequestJson);
        using JsonDocument safetyDocument = JsonDocument.Parse(safetyRow.RequestJson);
        using JsonDocument safetySummaryDocument = JsonDocument.Parse(safetySummaryRow.RequestJson);
        JsonElement capabilityPayload = capabilityDocument.RootElement.GetProperty("payload");
        JsonElement safetyPayload = safetyDocument.RootElement.GetProperty("payload");
        JsonElement safetySummaryPayload = safetySummaryDocument.RootElement.GetProperty("payload");
        // The two snapshots are session-scoped facts, not polled evidence: the protocol states no
        // cadence for them and Onboard sends each once per session, so ageing them out would cap
        // every session's admission window at MaximumEvidenceAge. What must still be bounded is
        // session *liveness* -- a dead peer leaves a Ready row and its last snapshots behind, and
        // CurrentReadySessionAsync has no liveness component of its own. So the age limit applies
        // to the last thing we heard from this session generation (Heartbeat arrives periodically,
        // and any inbound message counts, which also covers the window before the first one).
        // Content stays current through the session: everything read here is scoped to this exact
        // SessionGeneration, a new generation supersedes it, and the safety summary is taken from
        // the message carrying the session's current safetyStateVersion rather than from the
        // session-start snapshot. MaximumEvidenceAge still separately governs the RIoT vehicle
        // observation in ValidateDynamicFacts, which genuinely is polled.
        //
        // supportsBatchUnlock is deliberately not consulted: the protocol declares it with no
        // semantics -- a bare boolean in CapabilitySnapshot, unchanged from protocol-v0.1.1 through
        // the v2 candidate -- and its own canonical example sets it false, while the real question,
        // can the vehicle operate this slot set, is answered against AvailableSlots when the command
        // is actually sent. See docs/defects/20260829-intake-gates-on-unspecified-onboard-facts.md.
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset capabilityAt = capabilityPayload.GetProperty("observedAt").GetDateTimeOffset();
        DateTimeOffset safetyAt = safetySummaryPayload.GetProperty("observedAt").GetDateTimeOffset();
        if (capabilityAt > now || safetyAt > now)
        {
            return null;
        }
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            agvId, session.SessionGeneration, cancellationToken).ConfigureAwait(false);
        if (lastInboundAt is null || lastInboundAt > now ||
            now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return null;
        }
        Dictionary<int, JsonElement> safetySlots = safetyPayload.GetProperty("slotStates")
            .EnumerateArray().ToDictionary(item => item.GetProperty("slotNo").GetInt32());
        int[] available = capabilityPayload.GetProperty("slotStates").EnumerateArray()
            .Where(item => SlotAvailable(item, safetySlots))
            .Select(item => item.GetProperty("slotNo").GetInt32())
            .Order()
            .ToArray();
        // slotStates live only on the snapshot; SafetyStateChanged names the slots it affects but
        // not their new state. Availability therefore stays on the session baseline, which is what
        // the slot reservation ledger is built against -- a load and its unload each "affect" the
        // slots they touch, and treating that as lost availability would strand every slot the
        // first journey used for the rest of the session.
        JsonElement safety = safetySummaryPayload.GetProperty("safety");
        return new OnboardDispatchFacts(
            session.SessionGeneration,
            available,
            safety.GetProperty("departureSafe").GetBoolean(),
            safety.GetProperty("vehicleStopped").GetBoolean(),
            safety.GetProperty("allTargetSlotsLocked").GetBoolean(),
            safety.GetProperty("allUnlockOutputsReset").GetBoolean(),
            safety.GetProperty("unknownPresent").GetBoolean());
    }

    private static bool SlotAvailable(JsonElement capability, Dictionary<int, JsonElement> safetySlots)
    {
        int slot = capability.GetProperty("slotNo").GetInt32();
        if (!safetySlots.TryGetValue(slot, out JsonElement safety)) return false;
        return RequiredString(capability, "operability") == "OPERABLE" &&
               RequiredString(capability, "administrativeAvailability") == "ENABLED" &&
               RequiredString(capability, "physicalState") == "EMPTY" &&
               RequiredString(capability, "lockState") == "LOCKED" &&
               RequiredString(capability, "unlockOutputState") == "RESET" &&
               RequiredString(safety, "physicalState") == "EMPTY" &&
               RequiredString(safety, "lockState") == "LOCKED" &&
               RequiredString(safety, "unlockOutputState") == "RESET";
    }

    private async Task<ProtocolInboxRow?> LatestInboxForSessionAsync(
        string messageType,
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == messageType)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderByDescending(row => row.ReceivedAt).ToArray();
        return rows.FirstOrDefault(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            return RequiredString(root, "agvId") == agvId &&
                   root.GetProperty("sessionGeneration").GetInt64() == generation;
        });
    }

    /// <summary>
    /// The session's baseline SafetyStateSnapshot: the one with this generation's lowest
    /// <c>safetyStateVersion</c>, which is the handshake's. Ordered by version rather than receive time
    /// for the reason LatestSafetySummaryForSessionAsync is: Onboard allocates the version under one lock,
    /// so it orders the vehicle's safety facts, while the server's receive clock can be stepped back
    /// between the handshake snapshot and a later answer to SafetyStateSnapshotRequested (control-server#142).
    /// </summary>
    private async Task<ProtocolInboxRow?> BaselineSafetySnapshotForSessionAsync(
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        ProtocolInboxRow? baseline = null;
        long baselineVersion = long.MaxValue;
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            if (RequiredString(root, "agvId") != agvId ||
                root.GetProperty("sessionGeneration").GetInt64() != generation)
            {
                continue;
            }
            long version = root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();
            if (version < baselineVersion)
            {
                baseline = row;
                baselineVersion = version;
            }
        }
        return baseline;
    }

    /// <summary>
    /// The abstract safety summary Onboard currently stands behind, taken from whichever message
    /// carries the session's own <c>safetyStateVersion</c>. SafetyStateSnapshot and
    /// SafetyStateChanged both carry that summary and both advance the revision on the session row
    /// in the same transaction that stores the envelope, so matching on the revision picks the
    /// newest one without trusting either peer clock or receive order. No match means the session
    /// row and the inbox disagree, which proves nothing and fails closed.
    /// </summary>
    private async Task<ProtocolInboxRow?> LatestSafetySummaryForSessionAsync(
        string agvId,
        long generation,
        long safetyRevision,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot" || row.MessageType == "SafetyStateChanged")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderByDescending(row => row.ReceivedAt).ToArray();
        return rows.FirstOrDefault(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            return RequiredString(root, "agvId") == agvId &&
                   root.GetProperty("sessionGeneration").GetInt64() == generation &&
                   root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64() == safetyRevision;
        });
    }

    /// <summary>
    /// Server-observed liveness of one session generation: when any inbound message from it was
    /// last received. Uses the receive time rather than a payload timestamp so a stopped or
    /// misconfigured peer clock cannot make a dead session look alive.
    /// </summary>
    public async Task<DateTimeOffset?> LatestInboundAtForSessionAsync(
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? latest = null;
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            // This scans every inbound message, so unlike the per-type readers it meets envelopes
            // that carry no generation yet (or none at all). Those prove nothing about this
            // session's liveness and are skipped rather than throwing.
            if (!root.TryGetProperty("agvId", out JsonElement agv) ||
                agv.ValueKind != JsonValueKind.String ||
                agv.GetString() != agvId ||
                !root.TryGetProperty("sessionGeneration", out JsonElement sessionGeneration) ||
                sessionGeneration.ValueKind != JsonValueKind.Number ||
                sessionGeneration.GetInt64() != generation)
            {
                continue;
            }
            if (latest is null || row.ReceivedAt > latest.Value)
            {
                latest = row.ReceivedAt;
            }
        }
        return latest;
    }

    /// <summary>
    /// The vehicle's session row as the database holds it right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>AsNoTracking is the point of this method, not a performance note.</b> The engine only
    /// ever reads this row -- Onboard's transport owns every write to it, on its own scope and its
    /// own <see cref="ControlServerDbContext"/>. A tracking query returns the instance the change
    /// tracker already holds and leaves its values alone, so the first read in an iteration pinned
    /// the row for the whole iteration: a SafetyStateChanged that landed while the iteration was
    /// running was invisible to every later read of it, however long the iteration then ran
    /// against RIoT.
    /// </para>
    /// <para>
    /// That is not merely stale, it is unsafe, because
    /// <see cref="ReadOnboardFactsAsync"/> pins the safety summary to
    /// <c>SafetyRevision</c>: a stale revision does not fail to match, it matches the *previous*
    /// message, which is still in the inbox and still says whatever was true before. The arrival
    /// check then read a vehicle Onboard had already reported moving as stopped, and trusted an
    /// arrival that had not happened. See
    /// docs/defects/20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md.
    /// </para>
    /// </remarks>
    public Task<SessionRecoveryRow?> CurrentReadySessionAsync(string agvId, CancellationToken cancellationToken) =>
        dbContext.SessionRecoveries.AsNoTracking().SingleOrDefaultAsync(
            row => row.AgvId == agvId && row.Readiness == SessionReadiness.Ready,
            cancellationToken);

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Protocol field '{propertyName}' is required.");
}
