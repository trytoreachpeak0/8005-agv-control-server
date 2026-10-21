using System.Text.Json;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>What the server can say about a driving vehicle's doors right now (control-server#335, REQ-0246).</summary>
public enum InTransitDoorState
{
    /// <summary>The newest fresh safety summary says every door is locked and every slot is known.</summary>
    ProvenLocked,

    /// <summary>The newest fresh summary says a door is not locked (<c>allTargetSlotsLocked=false</c>, slots known).</summary>
    NotLocked,

    /// <summary>The newest fresh summary says a slot's state is unknown (<c>SLOT_STATE_UNKNOWN</c>): the IO is not answering.</summary>
    SlotStateUnknown,

    /// <summary>No safety summary has been received for this vehicle at all.</summary>
    Unobserved,

    /// <summary>
    /// A summary exists, but the generation it belongs to has not been heard from for <see cref="SessionLiveness.Timeout"/>.
    /// </summary>
    Stale,
}

/// <summary>The verdict, and which message it rests on, for the log.</summary>
/// <param name="ReceivedAt">
/// When this server received the summary the verdict rests on; null when there is none. CP-0017 counts a slot's grace from
/// the moment this server received the unknown, not from the vehicle's own clock (control-server#789).
/// </param>
public sealed record InTransitDoorVerdict(
    InTransitDoorState State,
    long? Generation,
    long? SafetyStateVersion,
    DateTimeOffset? ReceivedAt = null)
{
    /// <summary>REQ-0246's "仓门未能证明安全锁闭": every state but a fresh, known, locked one (issue 70, answer 2).</summary>
    public bool NotProvenLocked => State != InTransitDoorState.ProvenLocked;

    /// <summary>
    /// A live onboard read a door not locked: the one reading CP-0017 never graces and that keeps a door-cause stop with a
    /// person (control-server#789).
    /// </summary>
    public bool ReadNotLocked => State == InTransitDoorState.NotLocked;

    /// <summary>A live onboard reports a slot it cannot read (<c>SLOT_STATE_UNKNOWN</c>): graced for a while (CP-0017).</summary>
    /// <remarks>
    /// <para>
    /// These two are what this server acts on of <see cref="NotProvenLocked"/>, and until control-server#789 they were one
    /// property. <see cref="InTransitDoorState.Stale"/> and <see cref="InTransitDoorState.Unobserved"/> are left out of both
    /// on purpose. Both mean the vehicle has not been heard from, and what the server does about a vehicle it cannot hear
    /// while it drives is the conflict between REQ-0287 (a lost session only blocks new work and alarms) and ADR-cross-0026
    /// (hold the order) that the user put off to batch 9 on 2026-09-20; until then a silent session is named
    /// <c>ONBOARD_SESSION_LOST</c> and nothing is commanded (control-server#234, <c>OnboardSessionLostBlockTests</c>).
    /// </para>
    /// <para>
    /// A summary that names an unknown slot is read as unknown whatever else it says: the summary carries one
    /// <c>allTargetSlotsLocked</c> for all eight doors, and a real onboard sets it false together with
    /// <c>LOCK_NOT_CLOSED</c> whenever a slot cannot be read (measured, control-server#335). Another door open at the same
    /// moment cannot be told apart; CP-0017 section 3.4 accepts that as the protocol's boundary.
    /// </para>
    /// </remarks>
    public bool ReadUnknown => State == InTransitDoorState.SlotStateUnknown;
}

/// <summary>
/// Reads whether a vehicle's doors are proven locked while it drives, from the safety summaries Onboard sends
/// (control-server#335).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not through <see cref="Dispatch.OnboardDispatchFactsReader"/>, and that is the point.</b> That reader answers only for a
/// <c>Ready</c> session, and a real onboard is not Ready for the whole of a leg with this server's order in flight: it reads
/// RIoT's safety interface, sees this server's unfinished order, and reports <c>VEHICLE_NOT_READY</c> or
/// <c>ACTION_NOT_ALLOWED_IN_STATE</c> (control-server#138, #314). Its door column is still reported truthfully -- onboard-hmi's
/// <c>WireToGateSafetyEvaluator</c> computes <c>allTargetSlotsLocked</c> from the eight lock readings alone -- and the server
/// stores every <c>SafetyStateChanged</c> whatever the readiness. Measured on the real rig (run 36387029532,
/// <c>real-onboard-in-transit-door-facts</c>): 53 samples over two legs, none empty.
/// </para>
/// <para>
/// <b>Which summary.</b> The one carrying the session row's own generation and <c>SafetyRevision</c>, the rule
/// <c>OnboardDispatchFactsReader</c> uses and for its reason: the revision orders the vehicle's facts, receive order does not.
/// Old generations' messages are never deleted from the inbox, so a reader that took "the newest" by receive time alone would
/// hand a reconnected vehicle's previous doors in as current. Two rows carrying the same generation and revision cannot both be
/// stored (a second one differs in its line hash and is refused as <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c>); were there two,
/// the later received is taken, the dispatch reader's tie rule, and on one tick the one taken before control-server#597.
/// </para>
/// <para>
/// <b>Between a reconnect and its first snapshot, the previous generation's last summary stands</b> -- until it is stale. The
/// row moves to the new generation with the revision voided (<c>WireToGateStore.BeginSessionRecoveryAsync</c>) about 100 ms
/// before the new snapshot lands (measured, same run). Reading that gap as "unobserved" would hold and stop a healthy vehicle on
/// every reconnect. So the verdict is by age, not by generation: the previous generation's newest summary counts for exactly as
/// long as it would have counted had the link not dropped.
/// </para>
/// <para>
/// <b>Fresh means the fact's own generation was heard from within <see cref="SessionLiveness.Timeout"/></b>, through
/// <see cref="SessionLiveness.HeardFromAsync(ControlServerDbContext, string, long, DateTimeOffset, CancellationToken)"/>, the one
/// rule every "is this vehicle heard from" in this server uses (control-server#234). Not the summary's own age: Onboard sends a
/// summary only when it changes, and on the rig one stood unchanged for 33 s while heartbeats came every 2 s. The same window
/// closes the connection (<c>OnboardTcpServer</c>), so a stale fact and a lost link are one event, not two thresholds that can
/// disagree (Coordinator 8, 2026-09-28).
/// </para>
/// <para>
/// <b><c>unknownPresent</c> is not read.</b> A real onboard sets it while the vehicle's motion is unknown, which is the first
/// second of every leg on this server's order, with the doors locked. A slot whose state is unknown is named by
/// <c>SLOT_STATE_UNKNOWN</c>.
/// </para>
/// </remarks>
public static class InTransitDoorFacts
{
    /// <summary>The safety reason onboard-hmi reports for a slot whose IO reading is missing or stale.</summary>
    public const string SlotStateUnknownReason = "SLOT_STATE_UNKNOWN";

    public static async Task<InTransitDoorVerdict> ReadAsync(
        ControlServerDbContext dbContext,
        string agvId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return new InTransitDoorVerdict(InTransitDoorState.Unobserved, null, null);
        }

        Summary? fact = null;
        if (session.SafetyRevision is long revision)
        {
            // The session's own summary, read newest first and stopped at (control-server#597): this runs every round of every
            // leg, and used to load and parse every safety message ever received. None matching means the row and the inbox
            // disagree, which proves nothing.
            await foreach (ProtocolInboxRow row in SafetyMessagesNewestFirst.Read(dbContext)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (Parse(row, agvId) is { } summary &&
                    summary.Generation == session.SessionGeneration && summary.Version == revision)
                {
                    fact = summary;
                    break;
                }
            }
        }
        else
        {
            // A handshake whose snapshot has not landed: the previous generation's newest summary, judged by its age below.
            // Still a whole read: "newest" here is by generation and version, which receive order does not bound, and the
            // window lasts about 100 ms per reconnect.
            ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
                .Where(row => row.MessageType == "SafetyStateSnapshot" || row.MessageType == "SafetyStateChanged")
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            Summary[] summaries = [.. rows.Select(row => Parse(row, agvId)).OfType<Summary>()];
            foreach (Summary summary in summaries.Where(summary => summary.Generation < session.SessionGeneration))
            {
                if (Newer(summary, fact))
                {
                    fact = summary;
                }
            }
        }

        if (fact is null)
        {
            return new InTransitDoorVerdict(InTransitDoorState.Unobserved, null, null);
        }

        if (!await SessionLiveness.HeardFromAsync(dbContext, agvId, fact.Generation, now, cancellationToken).ConfigureAwait(false))
        {
            return new InTransitDoorVerdict(InTransitDoorState.Stale, fact.Generation, fact.Version, fact.ReceivedAt);
        }

        InTransitDoorState state = fact.SlotStateUnknown
            ? InTransitDoorState.SlotStateUnknown
            : fact.AllTargetSlotsLocked ? InTransitDoorState.ProvenLocked : InTransitDoorState.NotLocked;
        return new InTransitDoorVerdict(state, fact.Generation, fact.Version, fact.ReceivedAt);
    }

    /// <summary>Ordered by generation, then by the vehicle's own version; never by receive time.</summary>
    private static bool Newer(Summary candidate, Summary? best) =>
        best is null ||
        candidate.Generation > best.Generation ||
        (candidate.Generation == best.Generation && candidate.Version > best.Version);

    private static Summary? Parse(ProtocolInboxRow row, string agvId)
    {
        using JsonDocument document = JsonDocument.Parse(row.RequestJson);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("agvId", out JsonElement agv) || agv.ValueKind != JsonValueKind.String ||
            !string.Equals(agv.GetString(), agvId, StringComparison.Ordinal) ||
            !root.TryGetProperty("sessionGeneration", out JsonElement generation) ||
            generation.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        JsonElement payload = root.GetProperty("payload");
        JsonElement safety = payload.GetProperty("safety");
        bool slotStateUnknown = safety.GetProperty("reasonCodes").EnumerateArray()
            .Any(code => string.Equals(code.GetString(), SlotStateUnknownReason, StringComparison.Ordinal));
        return new Summary(
            generation.GetInt64(),
            payload.GetProperty("safetyStateVersion").GetInt64(),
            safety.GetProperty("allTargetSlotsLocked").GetBoolean(),
            slotStateUnknown,
            row.ReceivedAt);
    }

    private sealed record Summary(
        long Generation, long Version, bool AllTargetSlotsLocked, bool SlotStateUnknown, DateTimeOffset ReceivedAt);
}
