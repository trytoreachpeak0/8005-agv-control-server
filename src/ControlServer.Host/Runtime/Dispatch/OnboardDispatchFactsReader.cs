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
        // There is no supportsBatchUnlock to consult: protocol 3.0.0 deleted it from CapabilitySnapshot
        // (8005-agv-program#148, control-server#382). Until then it was deliberately ignored here -- a
        // bare boolean with no semantics that the protocol's own canonical example set false -- and the
        // real question, can the vehicle operate this slot set, is still answered against AvailableSlots
        // when the command is actually sent. See docs/defects/20260829-intake-gates-on-unspecified-onboard-facts.md.
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset capabilityAt = capabilityPayload.GetProperty("observedAt").GetDateTimeOffset();
        DateTimeOffset safetyAt = safetySummaryPayload.GetProperty("observedAt").GetDateTimeOffset();
        if (capabilityAt > now || safetyAt > now)
        {
            return null;
        }
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            agvId, session.SessionGeneration, now - runtimeOptions.MaximumEvidenceAge, cancellationToken).ConfigureAwait(false);
        if (lastInboundAt is null || lastInboundAt > now ||
            now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return null;
        }
        Dictionary<int, JsonElement> safetySlots = safetyPayload.GetProperty("slotStates")
            .EnumerateArray().ToDictionary(item => item.GetProperty("slotNo").GetInt32());
        JsonElement[] capabilitySlots = [.. capabilityPayload.GetProperty("slotStates").EnumerateArray()];
        HashSet<int> unloadedSinceBaseline = capabilitySlots.Any(item => SlotAvailableButForCargo(item, safetySlots))
            ? await SlotsUnloadedSinceAsync(
                    agvId, safetyPayload.GetProperty("observedAt").GetDateTimeOffset(), cancellationToken)
                .ConfigureAwait(false)
            : [];
        int[] available = capabilitySlots
            .Where(item => SlotAvailable(item, safetySlots) ||
                           (SlotAvailableButForCargo(item, safetySlots) &&
                            unloadedSinceBaseline.Contains(item.GetProperty("slotNo").GetInt32())))
            .Select(item => item.GetProperty("slotNo").GetInt32())
            .Order()
            .ToArray();
        // slotStates live only on the snapshot; SafetyStateChanged names the slots it affects but
        // not their new state. Availability therefore stays on the session baseline, which is what
        // the slot reservation ledger is built against -- a load and its unload each "affect" the
        // slots they touch, and treating that as lost availability would strand every slot the
        // first journey used for the rest of the session.
        //
        // The one widening is a slot the baseline refused only for its cargo (control-server#802): a session
        // established with cargo on board left those slots out for its whole life, so once this server unloaded
        // them they stayed lost until the vehicle reconnected -- dispatch refused demands for capacity and the
        // staging stop judged a side full with empty slots on it (agv02, 10-21: the session of 13:54:04 began
        // with 1, 2, 5, 6 and 7 loaded and read only 3, 4 and 8 for the rest of the afternoon). Such a slot joins
        // the baseline on this server's own committed unload of it, see SlotsUnloadedSinceAsync -- never on a
        // later snapshot, which may read it mid-operation (control-server#142). From then on it is a baseline
        // slot like any other: a load into it is taken off by the ledger's assigned slots, once.
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

    private static bool SlotAvailable(JsonElement capability, Dictionary<int, JsonElement> safetySlots) =>
        SlotUsableWith(capability, safetySlots, "EMPTY");

    /// <summary>
    /// A slot the baseline refused for its cargo and nothing else: both snapshots read it occupied, and it is operable,
    /// enabled, locked and reset (control-server#802).
    /// </summary>
    private static bool SlotAvailableButForCargo(JsonElement capability, Dictionary<int, JsonElement> safetySlots) =>
        SlotUsableWith(capability, safetySlots, "OCCUPIED");

    private static bool SlotUsableWith(
        JsonElement capability,
        Dictionary<int, JsonElement> safetySlots,
        string physicalState)
    {
        int slot = capability.GetProperty("slotNo").GetInt32();
        if (!safetySlots.TryGetValue(slot, out JsonElement safety)) return false;
        return RequiredString(capability, "operability") == "OPERABLE" &&
               RequiredString(capability, "administrativeAvailability") == "ENABLED" &&
               RequiredString(capability, "physicalState") == physicalState &&
               RequiredString(capability, "lockState") == "LOCKED" &&
               RequiredString(capability, "unlockOutputState") == "RESET" &&
               RequiredString(safety, "physicalState") == physicalState &&
               RequiredString(safety, "lockState") == "LOCKED" &&
               RequiredString(safety, "unlockOutputState") == "RESET";
    }

    /// <summary>
    /// The slots this server's own unloads emptied on <paramref name="agvId"/> after <paramref name="baselineObservedAt"/>,
    /// the session baseline's own <c>observedAt</c> (control-server#802).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the server's verdict counts.</b> An unload whose operation is <see cref="StationOperationStatus.Committed"/>
    /// passed <c>WireToGateStore.ApplyOperationResultAsync</c>'s check that every target slot came back completed, empty,
    /// locked and reset; its live result (not historical-only, not superseded) carries that evidence per slot, and the
    /// slot must be in it as such. A result the server refused, one it only recorded (an attempt it never commanded, or an
    /// operation already cancelled), and a load prove nothing about a slot being free. An operation still waiting for its
    /// result has no result row at all, which is what keeps a slot mid-unload out.
    /// </para>
    /// <para>
    /// <b>After the baseline, by the vehicle's clock.</b> The result's <c>ObservedAt</c> and the snapshot's
    /// <c>observedAt</c> are both the vehicle's, so they order the two facts without trusting the server's receive time,
    /// which can be stepped (control-server#142). An unload from before the baseline does not count: the baseline is newer
    /// and says the slot has cargo again. That covers a result replayed into a later session too, since the replay keeps
    /// its first row and its first time. Equal times count as before: the baseline wins a tie.
    /// </para>
    /// <para>
    /// Read only for a session whose baseline has such a slot, so the ordinary session, established empty, reads nothing
    /// more than it did.
    /// </para>
    /// </remarks>
    private async Task<HashSet<int>> SlotsUnloadedSinceAsync(
        string agvId,
        DateTimeOffset baselineObservedAt,
        CancellationToken cancellationToken)
    {
        var results = await dbContext.OperationResults.AsNoTracking()
            .Where(result => result.AgvId == agvId &&
                             !result.HistoricalOnly &&
                             result.SupersededByResultId == null)
            .Join(
                dbContext.StationOperations.AsNoTracking()
                    .Where(operation => operation.OperationType == SlotOperationType.Unload &&
                                        operation.Status == StationOperationStatus.Committed),
                result => result.SlotOperationAttemptId,
                operation => operation.SlotOperationAttemptId,
                (result, operation) => new { result.ObservedAt, result.EvidenceJson })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HashSet<int> unloaded = [];
        foreach (var result in results.Where(result => result.ObservedAt > baselineObservedAt))
        {
            foreach (SlotPhysicalEvidence evidence in
                     JsonSerializer.Deserialize<SlotPhysicalEvidence[]>(result.EvidenceJson) ?? [])
            {
                if (evidence is { State: SlotBusinessState.Empty, DoorLocked: true, UnlockOutputReset: true })
                {
                    unloaded.Add(evidence.SlotNumber);
                }
            }
        }
        return unloaded;
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
    /// <remarks>
    /// Read newest first and stopped at the first match (control-server#597): it used to load and parse every safety message
    /// of every vehicle and generation, once per round per vehicle. Which row is taken does not change, ties on one tick included
    /// (<see cref="SafetyMessagesNewestFirst"/>); with no match it still reads them all, as before.
    /// </remarks>
    /// <summary>
    /// When the vehicle observed the safety summary this session carries at <paramref name="safetyRevision"/> (its <c>observedAt</c>),
    /// read the way dispatch reads that summary; null when the message is not on file. The manual journey withdrawal shows it beside
    /// the summary it judges doors on (control-server#771 review S-4).
    /// </summary>
    internal async Task<DateTimeOffset?> SafetySummaryObservedAtAsync(
        string agvId,
        long generation,
        long safetyRevision,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow? row = await LatestSafetySummaryForSessionAsync(agvId, generation, safetyRevision, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(row.RequestJson);
        return document.RootElement.TryGetProperty("payload", out JsonElement payload) &&
               payload.TryGetProperty("observedAt", out JsonElement observedAt) &&
               observedAt.TryGetDateTimeOffset(out DateTimeOffset at)
            ? at
            : null;
    }

    private async Task<ProtocolInboxRow?> LatestSafetySummaryForSessionAsync(
        string agvId,
        long generation,
        long safetyRevision,
        CancellationToken cancellationToken)
    {
        await foreach (ProtocolInboxRow row in SafetyMessagesNewestFirst.Read(dbContext)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            if (RequiredString(root, "agvId") == agvId &&
                root.GetProperty("sessionGeneration").GetInt64() == generation &&
                root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64() == safetyRevision)
            {
                return row;
            }
        }
        return null;
    }

    /// <summary>
    /// Server-observed liveness of one session generation: when any inbound message from it was
    /// last received. Uses the receive time rather than a payload timestamp so a stopped or
    /// misconfigured peer clock cannot make a dead session look alive.
    /// </summary>
    /// <param name="notBefore">
    /// Reads only messages received at or after this. Every liveness decision passes <c>now - MaximumEvidenceAge</c>, which
    /// decides nothing differently: an older message cannot make the session live, and those callers treat "none" and "too
    /// old" alike. <see langword="null"/> reads back as far as the last message of this generation -- only for saying when that
    /// was, as the log does when the session is found silent.
    /// </param>
    /// <remarks>
    /// The inbox keeps every heartbeat a vehicle ever sent and nothing prunes it, so this used to load and parse the whole table
    /// on every call -- once per round per vehicle -- which on three vehicles' three days of heartbeats took seconds
    /// (control-server#563, after the MVP's ef5dad8e). It now reads newest first and stops at the first message of this
    /// generation, which is the latest one. Who sent a message is only in its JSON, so the rows read are the ones newer than
    /// that, every vehicle's, plus the one it stops at.
    /// </remarks>
    public async Task<DateTimeOffset?> LatestInboundAtForSessionAsync(
        string agvId,
        long generation,
        DateTimeOffset? notBefore,
        CancellationToken cancellationToken)
    {
        IQueryable<ProtocolInboxRow> inbound = dbContext.ProtocolInbox.AsNoTracking();
        if (notBefore is DateTimeOffset lowerBound)
        {
            long lowerBoundTicks = lowerBound.UtcTicks;
            inbound = inbound.Where(row => row.ReceivedAtUtcTicks >= lowerBoundTicks);
        }
        await foreach (ProtocolInboxRow row in inbound
                           .OrderByDescending(row => row.ReceivedAtUtcTicks)
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            // This reads every message type, so unlike the per-type readers it meets envelopes
            // that carry no generation yet (or none at all). Those prove nothing about this
            // session's liveness and are skipped rather than throwing.
            if (root.TryGetProperty("agvId", out JsonElement agv) &&
                agv.ValueKind == JsonValueKind.String &&
                agv.GetString() == agvId &&
                root.TryGetProperty("sessionGeneration", out JsonElement sessionGeneration) &&
                sessionGeneration.ValueKind == JsonValueKind.Number &&
                sessionGeneration.GetInt64() == generation)
            {
                return row.ReceivedAt;
            }
        }
        return null;
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
    /// <summary>
    /// 这一代会话里，车载端安全状态中「不能出发」的几条（批次8-19 空闲返回的出发前安全门，control-server#390）：空表即全部仓位锁闭、
    /// 开锁输出复位、当前安全摘要无阻断事实。读不到这一代的安全状态时答 <c>SAFETY_STATE_UNREADABLE</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两处都看。仓位逐个的锁态只在 <c>SafetyStateSnapshot</c> 上（<c>SafetyStateChanged</c> 只说哪几个仓位变了），取这一代最新的那一张：
    /// 八个仓位都要 <c>LOCKED</c>、<c>RESET</c>。它之后的变化由当前那一版摘要说（<see cref="LatestSafetySummaryForSessionAsync"/>，
    /// 与派车读的是同一版）：摘要的 <c>reasonCodes</c> 要为空——<see cref="ReadOnboardFactsAsync"/> 与派车只看五个布尔，不看这一栏。
    /// </para>
    /// <para>
    /// 新鲜度不在这里判：它是会话存活（<see cref="ReadOnboardFactsAsync"/> 的 <c>MaximumEvidenceAge</c>），调用方先要那一份不为空。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> ReadDepartureSafetyGapsAsync(
        string agvId,
        long generation,
        long safetyRevision,
        CancellationToken cancellationToken)
    {
        JsonElement? latestSlots = (await LatestSnapshotPayloadAsync(dbContext, agvId, generation, cancellationToken)
            .ConfigureAwait(false))?.GetProperty("slotStates");
        ProtocolInboxRow? summaryRow = await LatestSafetySummaryForSessionAsync(
            agvId, generation, safetyRevision, cancellationToken).ConfigureAwait(false);
        if (latestSlots is not JsonElement slots || summaryRow is null)
        {
            return ["SAFETY_STATE_UNREADABLE"];
        }

        List<string> gaps =
        [
            .. UnsecuredSlots(slots).Select(slotNo => FormattableString.Invariant($"SLOT_{slotNo}_NOT_LOCKED")),
        ];
        using JsonDocument summary = JsonDocument.Parse(summaryRow.RequestJson);
        gaps.AddRange(summary.RootElement.GetProperty("payload").GetProperty("safety").GetProperty("reasonCodes")
            .EnumerateArray().Select(code => code.GetString() ?? "UNNAMED_SAFETY_REASON"));
        return gaps;
    }

    /// <summary>
    /// Whether the latest <c>SafetyStateSnapshot</c> of this session generation shows a slot the departure gate reads as not
    /// locked (<see cref="ReadDepartureSafetyGapsAsync"/>'s own rule); false when there is none. The transport asks for a fresh
    /// snapshot on a safety change when it does (control-server#865): a change carries no slot states, so a slot locked after the
    /// snapshot stays unsecured to the gate until a snapshot says otherwise.
    /// </summary>
    /// <remarks>
    /// This reads every vehicle's snapshots in the inbox. The transport reads it at most once per connection and generation,
    /// for a connection that has not accepted a snapshot of its own (review S3); each snapshot it accepts sets the answer from
    /// <see cref="HasUnsecuredSlot"/> instead.
    /// </remarks>
    internal static async Task<bool> LatestSnapshotHasUnsecuredSlotAsync(
        ControlServerDbContext dbContext,
        string agvId,
        long generation,
        CancellationToken cancellationToken) =>
        await LatestSnapshotPayloadAsync(dbContext, agvId, generation, cancellationToken).ConfigureAwait(false) is JsonElement payload &&
        HasUnsecuredSlot(payload) == true;

    /// <summary>
    /// Whether a snapshot payload's <c>slotStates</c> show a slot the departure gate reads as not secured; null when the payload
    /// holds no slot states this rule can read (only hand-built test snapshots do: the wire contract requires them).
    /// </summary>
    internal static bool? HasUnsecuredSlot(JsonElement snapshotPayload)
    {
        if (!snapshotPayload.TryGetProperty("slotStates", out JsonElement slotStates) || slotStates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement slot in slotStates.EnumerateArray())
        {
            if (!slot.TryGetProperty("lockState", out JsonElement lockState) || lockState.ValueKind != JsonValueKind.String ||
                !slot.TryGetProperty("unlockOutputState", out JsonElement output) || output.ValueKind != JsonValueKind.String ||
                !slot.TryGetProperty("slotNo", out JsonElement slotNo) || slotNo.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
        }
        return UnsecuredSlots(slotStates).Length > 0;
    }

    /// <summary>The slots the departure gate reads as not secured: lock not <c>LOCKED</c> or unlock output not <c>RESET</c>.</summary>
    private static int[] UnsecuredSlots(JsonElement slotStates) =>
        [
            .. slotStates.EnumerateArray()
                .Where(slot => RequiredString(slot, "lockState") != "LOCKED" || RequiredString(slot, "unlockOutputState") != "RESET")
                .Select(slot => slot.GetProperty("slotNo").GetInt32()),
        ];

    /// <summary>
    /// The payload of this session generation's <c>SafetyStateSnapshot</c> with the highest <c>safetyStateVersion</c>; null
    /// when it has none. Each slot's lock is only on a snapshot (<c>SafetyStateChanged</c> names the slots that changed).
    /// </summary>
    private static async Task<JsonElement?> LatestSnapshotPayloadAsync(
        ControlServerDbContext dbContext,
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        // Review L2: filtered in the database by message type only, with just the JSON column read, and parsed one row at a
        // time. Not by vehicle: the inbox has no vehicle column, and a text match on the id does not work -- the serializer
        // escapes non-ASCII, and the fleet's ids are Chinese (老厂前线新多仓位1 is stored with each character escaped, \u8001 and so on), so such a filter found
        // no row for any real vehicle and read every one as SAFETY_STATE_UNREADABLE: no idle return would ever set off.
        string[] snapshots = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot")
            .Select(row => row.RequestJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? latest = null;
        long latestVersion = long.MinValue;
        foreach (string json in snapshots)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (RequiredString(root, "agvId") != agvId || root.GetProperty("sessionGeneration").GetInt64() != generation)
            {
                continue;
            }
            JsonElement payload = root.GetProperty("payload");
            long version = payload.GetProperty("safetyStateVersion").GetInt64();
            if (version > latestVersion)
            {
                latest = payload.Clone();
                latestVersion = version;
            }
        }
        return latest;
    }

    public Task<SessionRecoveryRow?> CurrentReadySessionAsync(string agvId, CancellationToken cancellationToken) =>
        dbContext.SessionRecoveries.AsNoTracking().SingleOrDefaultAsync(
            row => row.AgvId == agvId && row.Readiness == SessionReadiness.Ready,
            cancellationToken);

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Protocol field '{propertyName}' is required.");
}
