using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>What this round's door reading asks of the engine (control-server#789, CP-0017).</summary>
public enum InTransitDoorAction
{
    /// <summary>Nothing to hand on: the doors are proven locked, or the vehicle is not heard from and no fault stands.</summary>
    Nothing,

    /// <summary>A slot reads unknown and its grace has not run out: alarm only, no fault, no command (REQ-0246 as revised).</summary>
    InGrace,

    /// <summary>Hand the vehicle to the fault model this round.</summary>
    HandToFault,
}

/// <summary>One round's answer from <see cref="InTransitDoorReadings.Observe"/>.</summary>
/// <param name="Action">What the engine does this round.</param>
/// <param name="DoorsNotProvenLocked">The doors read this round are not fresh, known and locked.</param>
/// <param name="ReadingRestored">
/// The door fault in effect was raised only for doors that could not be read, the doors have read locked for
/// <see cref="VehicleFaultOptions.DoorReadingRestoredFor"/> without a break, and this process saw the whole of it.
/// </param>
/// <param name="GraceStarted">This round started a grace.</param>
/// <param name="GraceEndedAfter">A grace ended this round on a locked reading, after this long unread.</param>
/// <param name="HandedBecause">Why the vehicle is handed on this round when that is new: one of the <c>Hand*</c> codes.</param>
public sealed record InTransitDoorReading(
    InTransitDoorAction Action,
    bool DoorsNotProvenLocked,
    bool ReadingRestored = false,
    bool GraceStarted = false,
    TimeSpan? GraceEndedAfter = null,
    string? HandedBecause = null);


/// <summary>
/// The door readings of driving vehicles across rounds: the grace for a slot that cannot be read (REQ-0246 as revised by
/// CP-0017), and whether a door fault was raised only for doors that could not be read (REQ-0167 as revised by CP-0017)
/// (control-server#789).
/// </summary>
/// <remarks>
/// <para>
/// <b>An episode</b> starts the first round a live onboard reports a driving vehicle's doors not locked or a slot unknown, and
/// ends the first round its doors read proven locked with no door fault standing. Within it: the grace while nothing has been
/// handed to the fault model yet, and from the hand-off on, whether any reading said a door was not locked. "Any reading"
/// covers the grace too, and the whole fault up to the release (CP-0017 revision two, note 3): a lock that broke and an IO
/// that dropped, in either order, is never taken for a reading that merely came back.
/// </para>
/// <para>
/// <b>Every summary of the episode, not one per round</b> (cs#839 review H1). Onboard sends a summary on every change; a round
/// reads the one in force and, through <see cref="SinceFor"/>, every other one received since the vehicle's last round
/// (<see cref="InTransitDoorVerdict.Between"/>). A door read not locked between two rounds hands a grace on at once and keeps
/// the fault with a person; a slot unread between two locked readings starts the ten seconds again.
/// </para>
/// <para>
/// <b>Before any fault</b> a door read not locked between two rounds counts when the vehicle said it was moving and something
/// is still not right now -- a grace open, or the doors not proven locked this round (cs#839 re-review, case a): followed by an
/// unread slot, it would otherwise open a grace, run out, and be released as "only unread". A blip locked again by the next
/// round with nothing open is not acted on, as since control-server#335: REQ-0246's trigger is not sharpened here. Readings
/// from a vehicle standing still are an authorised load or unload at a station (REQ-0246's source decision, answer 3), and
/// never count before a fault.
/// </para>
/// <para>
/// <b>The grace</b> counts from when this server received the first unknown of the run (CP-0017 section 5, item 6;
/// <see cref="InTransitDoorVerdict.UnknownSince"/>, cs#839 review M3), so neither the round that first saw it nor a restart
/// can lengthen it. It ends on the first locked reading, and hands on at once on a door read not locked, on the grace running
/// out, or when the vehicle has started <see cref="VehicleFaultOptions.DoorUnknownGraceLimit"/> graces within
/// <see cref="VehicleFaultOptions.DoorUnknownGraceWindow"/> -- every grace started counts, whether it recovered or ran out
/// (revision one, note 7). Counted per vehicle, not per order: an IO or a link that drops is the vehicle's, and a new order
/// would not mend it. <see cref="NextDueAfter"/> lets the worker run the round that judges it when it runs out rather than up
/// to a poll interval later (revision one, note 5). A grace no round has looked at for <see cref="UnobservedGraceTimeout"/>
/// -- the order went HANG, the vehicle arrived -- is dropped, and is judged afresh from the inbox the next time.
/// </para>
/// <para>
/// <b>A restart</b> forgets all of it in this process, and each half is rebuilt on the safe side (CP-0017 section 6, item 1;
/// cs#839 review M2). A grace in progress is started again from the first unknown's receipt time, so it is not lengthened. The
/// count is rebuilt from the inbox the first time a vehicle is looked at (<see cref="NeedsGraceHistory"/>,
/// <see cref="InTransitDoorFacts.ReadUnknownRunStartsAsync"/>): every run of unknown summaries in the window counts as a grace
/// given, also one that was handed on at once. A door fault standing at the restart has no history in this process, and is
/// taken to have read a door not locked: no automatic release, a person releases it as before CP-0017.
/// </para>
/// <para>
/// <b>Not on the fault fact.</b> CP-0017 leaves to the implementation how the sub-cause is recorded. Writing it to
/// <c>VehicleFaultStates</c> takes a migration; held here it costs, on a restart, only the release CP-0011 already gave to a
/// person. Rounds run one at a time under the journey mutation gate; the lock is for the worker's read of the due time.
/// </para>
/// </remarks>
public sealed class InTransitDoorReadings(IOptions<VehicleFaultOptions> options)
{
    /// <summary>A door read not locked: never graced.</summary>
    public const string HandReadNotLocked = "DOOR_READ_NOT_LOCKED";

    /// <summary>A slot still unknown when its grace ran out.</summary>
    public const string HandGraceExpired = "DOOR_UNKNOWN_GRACE_EXPIRED";

    /// <summary>A slot unknown on a vehicle that has used up its graces for the window, or with the grace set to zero.</summary>
    public const string HandGraceLimitReached = "DOOR_UNKNOWN_GRACE_LIMIT_REACHED";

    /// <summary>Doors still not proven locked when a door fault this episode handed on is no longer standing.</summary>
    public const string HandAfterFault = "DOOR_STILL_NOT_PROVEN_LOCKED";

    /// <summary>How long a grace in progress may go without a round looking at it before it is dropped (cs#839 review M1).</summary>
    internal static readonly TimeSpan UnobservedGraceTimeout = TimeSpan.FromSeconds(10);

    private readonly VehicleFaultOptions faultOptions = options.Value;
    private readonly Dictionary<string, Vehicle> vehicles = new(StringComparer.Ordinal);

    /// <summary>The configured grace (<see cref="VehicleFaultOptions.DoorUnknownGrace"/>).</summary>
    public TimeSpan Grace => faultOptions.DoorUnknownGrace;

    /// <summary>The configured window the grace limit counts in (<see cref="VehicleFaultOptions.DoorUnknownGraceWindow"/>).</summary>
    public TimeSpan GraceWindow => faultOptions.DoorUnknownGraceWindow;

    /// <summary>
    /// The earliest moment after <paramref name="now"/> a grace in progress runs out, or null when none does (cs#839 review
    /// M1). One already past is a grace whose round has run, or that no round reaches; it must not hide another vehicle's.
    /// </summary>
    public DateTimeOffset? NextDueAfter(DateTimeOffset now)
    {
        lock (vehicles)
        {
            DateTimeOffset? due = null;
            foreach (Vehicle vehicle in vehicles.Values)
            {
                if (vehicle.Episode is { Handed: false, UnknownSince: DateTimeOffset since } &&
                    since + Grace > now &&
                    (due is null || since + Grace < due))
                {
                    due = since + Grace;
                }
            }

            return due;
        }
    }

    /// <summary>
    /// The moment the next read of <paramref name="agvId"/>'s doors asks for the summaries received after
    /// (<see cref="InTransitDoorFacts.ReadAsync(ControlServer.Infrastructure.Persistence.ControlServerDbContext, string, DateTimeOffset, DateTimeOffset?, CancellationToken)"/>):
    /// the latest receipt a round has read for the vehicle; null before the first round of this process.
    /// </summary>
    public DateTimeOffset? SinceFor(string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        lock (vehicles)
        {
            return vehicles.TryGetValue(agvId, out Vehicle? vehicle) ? vehicle.ReadUpTo : null;
        }
    }

    /// <summary>Whether this process has not looked at <paramref name="agvId"/> yet, and its grace count is to be rebuilt.</summary>
    public bool NeedsGraceHistory(string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        lock (vehicles)
        {
            return !vehicles.ContainsKey(agvId);
        }
    }

    /// <summary>The graces a process that has just started counts as given to <paramref name="agvId"/> (cs#839 review M2).</summary>
    public void SeedGraceStarts(string agvId, IEnumerable<DateTimeOffset> starts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentNullException.ThrowIfNull(starts);
        lock (vehicles)
        {
            if (!vehicles.ContainsKey(agvId))
            {
                Vehicle vehicle = new();
                vehicle.GraceStarts.AddRange(starts);
                vehicles.Add(agvId, vehicle);
            }
        }
    }

    /// <summary>
    /// Takes this round's reading of a driving vehicle's doors, and whether a door fault stands for it, and says what the
    /// engine does with them.
    /// </summary>
    public InTransitDoorReading Observe(string agvId, InTransitDoorVerdict doors, bool doorFaultInEffect, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentNullException.ThrowIfNull(doors);

        lock (vehicles)
        {
            if (!vehicles.TryGetValue(agvId, out Vehicle? vehicle))
            {
                vehicle = new Vehicle();
                vehicles.Add(agvId, vehicle);
            }

            InTransitDoorReading reading = doorFaultInEffect
                ? WhileFaulted(vehicle, doors, now)
                : BeforeFault(vehicle, doors, now);
            vehicle.LastObservedAt = now;
            vehicle.ReadUpTo = Latest(vehicle.ReadUpTo, doors);
            return reading;
        }
    }

    /// <summary>
    /// Whether the open episode of <paramref name="agvId"/> read only doors that could not be read and this process saw all of
    /// it, and the doors have read locked since -- what the class-three entry asks instead of a repaired lock (CP-0017 section
    /// 6, item 3). False whenever that is not known.
    /// </summary>
    public bool OnlyUnreadAndLockedSince(string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        lock (vehicles)
        {
            return vehicles.TryGetValue(agvId, out Vehicle? vehicle) &&
                vehicle.Episode is { HistoryKnown: true, SawNotLocked: false, LockedSince: not null };
        }
    }

    private InTransitDoorReading BeforeFault(Vehicle vehicle, InTransitDoorVerdict doors, DateTimeOffset now)
    {
        Episode? episode = vehicle.Episode;

        // A door read not locked while moving, since the last round or just before the run of unknown readings now in force,
        // is read -- whether or not a grace is open, and before an unwatched grace is dropped below (cs#839 review H1,
        // re-review cases a and b): never graced, and the fault it raises is one that read a door not locked. A blip that was
        // locked again by now, with nothing else open, is left as it always was (see the remarks on the residual).
        if ((ReadNotLockedWhileMovingBetween(doors) && (episode is not null || doors.State != InTransitDoorState.ProvenLocked)) ||
            (doors.State == InTransitDoorState.SlotStateUnknown && doors.UnknownRunAfterNotLocked))
        {
            episode ??= vehicle.Episode = new Episode(historyKnown: true);
            episode.SawNotLocked = true;
            return Hand(episode, HandReadNotLocked);
        }

        if (episode is { Handed: false } && vehicle.LastObservedAt is DateTimeOffset last && now - last > UnobservedGraceTimeout)
        {
            // A grace no round has looked at for a while (cs#839 review M1): judged afresh from the inbox.
            episode = vehicle.Episode = null;
        }

        switch (doors.State)
        {
            case InTransitDoorState.ProvenLocked:
                vehicle.Episode = null;
                return new InTransitDoorReading(
                    InTransitDoorAction.Nothing,
                    DoorsNotProvenLocked: false,
                    // How long the doors went unread: up to the locked summary's receipt, not to the round that read it.
                    GraceEndedAfter: episode is { Handed: false, UnknownSince: DateTimeOffset unreadSince }
                        ? (doors.ReceivedAt ?? now) - unreadSince
                        : null);

            case InTransitDoorState.NotLocked:
                episode ??= vehicle.Episode = new Episode(historyKnown: true);
                episode.SawNotLocked = true;
                return Hand(episode, HandReadNotLocked);

            case InTransitDoorState.SlotStateUnknown:
                if (episode is { Handed: true })
                {
                    // The fault this episode was handed to is no longer standing -- cleared by a person, or its evidence
                    // overwritten -- and the doors have not read locked since: no new grace.
                    return Hand(episode, HandAfterFault);
                }

                return Unknown(vehicle, episode, doors, now);

            default:
                // Not heard from: nothing is commanded (control-server#234). A grace in progress keeps its clock, and a vehicle
                // heard again still unknown past it is handed on at once.
                return new InTransitDoorReading(InTransitDoorAction.Nothing, DoorsNotProvenLocked: true);
        }
    }

    /// <summary>
    /// A slot unknown now, with no fault standing: every run of unknown readings since the last round that a locked reading
    /// started afresh is a grace begun (cs#839 re-review, M1), the last of them the one in force, counted from its first
    /// receipt (M3).
    /// </summary>
    private InTransitDoorReading Unknown(Vehicle vehicle, Episode? episode, InTransitDoorVerdict doors, DateTimeOffset now)
    {
        List<InTransitDoorSample> readings =
        [
            .. (doors.Between ?? []).Where(sample =>
                sample.State is InTransitDoorState.SlotStateUnknown or InTransitDoorState.ProvenLocked),
            new InTransitDoorSample(InTransitDoorState.SlotStateUnknown, doors.ReceivedAt ?? now),
        ];
        int lastLocked = readings.FindLastIndex(sample => sample.State == InTransitDoorState.ProvenLocked);
        bool inRun = episode is { Handed: false };
        bool started = false;
        for (int index = 0; index < readings.Count; index++)
        {
            InTransitDoorSample sample = readings[index];
            if (sample.State == InTransitDoorState.ProvenLocked)
            {
                inRun = false;
                continue;
            }

            if (inRun)
            {
                continue;
            }

            // The run in force begins at its first receipt, which the verdict carries when it lies before this round's window.
            DateTimeOffset at = index > lastLocked && doors.UnknownSince is DateTimeOffset first && first < sample.ReceivedAt
                ? first
                : sample.ReceivedAt;
            if (at > now)
            {
                at = now;
            }

            episode ??= vehicle.Episode = new Episode(historyKnown: true);
            episode.UnknownSince = at;
            inRun = true;
            vehicle.GraceStarts.RemoveAll(start => start <= now - faultOptions.DoorUnknownGraceWindow);
            if (Grace <= TimeSpan.Zero || vehicle.GraceStarts.Count >= faultOptions.DoorUnknownGraceLimit)
            {
                return Hand(episode, HandGraceLimitReached);
            }

            vehicle.GraceStarts.Add(at);
            started = true;
        }

        episode!.UnknownSince ??= now;
        return now - episode.UnknownSince.Value < Grace
            ? new InTransitDoorReading(InTransitDoorAction.InGrace, DoorsNotProvenLocked: true, GraceStarted: started)
            : Hand(episode, HandGraceExpired);
    }

    private InTransitDoorReading WhileFaulted(Vehicle vehicle, InTransitDoorVerdict doors, DateTimeOffset now)
    {
        // A fault standing with no episode in this process was raised before a restart: its history is unknown, and is taken
        // to have read a door not locked.
        Episode episode = vehicle.Episode ??= new Episode(historyKnown: false);
        episode.Handed = true;

        // What arrived since the last round first (cs#839 review H1): a door read not locked there is read, and anything but
        // locked there breaks the run of locked readings, though the summary in force reads locked again.
        foreach (InTransitDoorSample sample in doors.Between ?? [])
        {
            if (sample.State == InTransitDoorState.NotLocked)
            {
                episode.SawNotLocked = true;
            }

            if (sample.State != InTransitDoorState.ProvenLocked)
            {
                episode.LockedSince = null;
            }
        }

        switch (doors.State)
        {
            case InTransitDoorState.ProvenLocked:
                episode.LockedSince ??= now;
                break;
            case InTransitDoorState.NotLocked:
                episode.SawNotLocked = true;
                episode.LockedSince = null;
                break;
            default:
                // Unknown, unobserved or stale is "读不到" (CP-0017, 读法): it breaks the run of locked readings and nothing else.
                episode.LockedSince = null;
                break;
        }

        bool restored = episode is { HistoryKnown: true, SawNotLocked: false, LockedSince: DateTimeOffset lockedSince } &&
            now - lockedSince >= faultOptions.DoorReadingRestoredFor;
        return new InTransitDoorReading(
            InTransitDoorAction.HandToFault,
            DoorsNotProvenLocked: doors.State != InTransitDoorState.ProvenLocked,
            ReadingRestored: restored);
    }

    private static bool ReadNotLockedWhileMovingBetween(InTransitDoorVerdict doors) =>
        doors.Between?.Any(sample => sample.State == InTransitDoorState.NotLocked && sample.Moving) == true;

    private static DateTimeOffset? Latest(DateTimeOffset? readUpTo, InTransitDoorVerdict doors)
    {
        DateTimeOffset? latest = readUpTo;
        foreach (DateTimeOffset received in (doors.Between ?? []).Select(sample => sample.ReceivedAt)
                     .Append(doors.ReceivedAt ?? DateTimeOffset.MinValue))
        {
            if (latest is null || received > latest)
            {
                latest = received;
            }
        }

        return latest == DateTimeOffset.MinValue ? null : latest;
    }

    private static InTransitDoorReading Hand(Episode episode, string because)
    {
        bool first = !episode.Handed;
        episode.Handed = true;
        return new InTransitDoorReading(
            InTransitDoorAction.HandToFault, DoorsNotProvenLocked: true, HandedBecause: first ? because : null);
    }

    private sealed class Vehicle
    {
        public List<DateTimeOffset> GraceStarts { get; } = [];

        public Episode? Episode { get; set; }

        /// <summary>The last round that looked at this vehicle's doors.</summary>
        public DateTimeOffset? LastObservedAt { get; set; }

        /// <summary>
        /// The latest receipt time of a summary a round has read for this vehicle: the next round reads every one after it.
        /// Kept per vehicle, not per episode (cs#839 re-review, case a): the readings before an episode opens are the ones that
        /// decide what it is.
        /// </summary>
        public DateTimeOffset? ReadUpTo { get; set; }
    }

    private sealed class Episode(bool historyKnown)
    {
        /// <summary>This process saw the episode from its first reading; false for a fault standing at a restart.</summary>
        public bool HistoryKnown { get; } = historyKnown;

        /// <summary>When this server received the unknown the grace counts from; null when the episode began otherwise.</summary>
        public DateTimeOffset? UnknownSince { get; set; }

        /// <summary>Handed to the fault model: from here on no grace, and every reading counts towards the sub-cause.</summary>
        public bool Handed { get; set; }

        /// <summary>A door read not locked at any point of the episode.</summary>
        public bool SawNotLocked { get; set; }

        /// <summary>The first round of the current unbroken run of locked readings under the fault; null when there is none.</summary>
        public DateTimeOffset? LockedSince { get; set; }
    }
}
