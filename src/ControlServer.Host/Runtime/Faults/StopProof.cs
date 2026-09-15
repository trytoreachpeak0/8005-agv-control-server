using System.Collections.Concurrent;
using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>What the combined evidence said, and which facts were missing when it said no.</summary>
public sealed record StopProofVerdict(bool Proven, IReadOnlyList<string> MissingFacts);

/// <summary>
/// REQ-0247's stop proof: an engaged emergency latch, or else the combined evidence of several
/// consecutive fresh samples, all positively non-moving, all at the same known place, spaced closely
/// enough to have watched the interval between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>An engaged latch is the proof on its own</b> (REQ-0247 as revised by CP-0003, 2026-09-15). A
/// latch read back as <c>CAN_RECOVER</c> or <c>CAN_NOT_RECOVER</c> means the vehicle has stopped,
/// and motion, speed and position are not read at all. The user's ruling was that an emergency stop
/// always stops the vehicle, and the field showed why the samples cannot be asked instead: agv02
/// latched with its order still executing reported <c>MT_RUNNING</c> at speed 0, between stations,
/// for as long as it stood there. The two risks accepted with it — a latch read while the vehicle
/// is offline may be stale, and a vehicle may still be decelerating just after the latch engages —
/// are recorded in CP-0003 and are not to be engineered back in here.
/// </para>
/// <para>
/// <b>Otherwise the requirement is written as a list of things that do not count</b>, and the code
/// is shaped the same way. An order in HELD does not count. A single query does not count. Neither
/// is read by the combined evidence at all, which is the point: it cannot accidentally be satisfied
/// by them.
/// </para>
/// <para>
/// <b>Every missing fact is reported, not just the first.</b> A refusal is read by whoever has to
/// decide what to do about a vehicle that will not confirm it stopped, and "the position is
/// unknown" and "the position is unknown and it is also still moving" call for different actions.
/// </para>
/// <para>
/// Pure and static, so the whole truth table is reachable from a test without a clock, a database
/// or a RIoT. The sampling itself — the part that needs all three — is
/// <see cref="VehicleMotionLedger"/>'s.
/// </para>
/// </remarks>
public static class StopProof
{
    /// <summary>
    /// Fewer samples than the configured count. A single query is REQ-0247's first negative.
    /// </summary>
    /// <remarks>
    /// Reported together with whatever else is wrong with the samples there are, rather than in
    /// place of it. On its own it means only "keep watching"; alongside
    /// <see cref="MotionUnknown"/> it means the watching is not going to converge.
    /// </remarks>
    public const string TooFewSamples = "STOP_PROOF_TOO_FEW_SAMPLES";

    /// <summary>At least one sample reported motion.</summary>
    public const string MotionObserved = "STOP_PROOF_MOTION_OBSERVED";

    /// <summary>At least one sample could not say whether the vehicle was moving.</summary>
    public const string MotionUnknown = "STOP_PROOF_MOTION_UNKNOWN";

    /// <summary>At least one sample had no position. Normal while a vehicle is between stations.</summary>
    public const string PositionUnknown = "STOP_PROOF_POSITION_UNKNOWN";

    /// <summary>The position changed across the window, which is movement by another name.</summary>
    public const string PositionChanged = "STOP_PROOF_POSITION_CHANGED";

    /// <summary>The newest sample is older than the configured maximum, or is in the future.</summary>
    public const string EvidenceStale = "STOP_PROOF_EVIDENCE_STALE";

    /// <summary>Two samples were taken too close together to be two moments.</summary>
    public const string SamplesTooClose = "STOP_PROOF_SAMPLES_TOO_CLOSE";

    /// <summary>Two samples were far enough apart that the interval between them went unwatched.</summary>
    public const string ObservationGap = "STOP_PROOF_OBSERVATION_GAP";

    private static readonly StopProofVerdict LatchEngaged = new(true, []);

    /// <summary>
    /// Whether the vehicle is proven stopped as of <paramref name="now"/>, given the latch read in
    /// this evaluation and the retained <paramref name="samples"/>, oldest first.
    /// </summary>
    /// <remarks>
    /// An unreadable latch is not an engaged one, and falls through to the combined evidence like
    /// an <c>OK</c> one does.
    /// </remarks>
    public static StopProofVerdict Evaluate(
        IReadOnlyList<VehicleMotionSample> samples,
        RiotVehicleEmergencyObservation emergency,
        DateTimeOffset now,
        VehicleFaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(emergency);
        return emergency.IsLatched ? LatchEngaged : Evaluate(samples, now, options);
    }

    /// <summary>
    /// Whether <paramref name="samples"/> — oldest first — prove the vehicle is stopped as of
    /// <paramref name="now"/> by the combined evidence alone.
    /// </summary>
    public static StopProofVerdict Evaluate(
        IReadOnlyList<VehicleMotionSample> samples,
        DateTimeOffset now,
        VehicleFaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);

        if (samples.Count == 0)
        {
            return new StopProofVerdict(false, [TooFewSamples]);
        }

        List<VehicleMotionSample> window =
            [.. samples.Skip(Math.Max(0, samples.Count - options.StopProofSampleCount))];
        SortedSet<string> missing = new(StringComparer.Ordinal);

        // A short window is reported alongside everything else that is wrong with it, not instead
        // of it. "Not enough samples yet" and "the vehicle cannot be read at all" call for
        // different actions from whoever is looking at a vehicle that will not confirm it stopped,
        // and reporting only the first would hide the second behind a wait that will never end.
        if (window.Count < options.StopProofSampleCount)
        {
            missing.Add(TooFewSamples);
        }

        foreach (VehicleMotionSample sample in window)
        {
            switch (sample.Reading)
            {
                case VehicleMotionReading.Moving:
                    missing.Add(MotionObserved);
                    break;
                case VehicleMotionReading.Unknown:
                    missing.Add(MotionUnknown);
                    break;
                case VehicleMotionReading.NotMoving:
                default:
                    break;
            }

            if (!sample.HasKnownPosition)
            {
                missing.Add(PositionUnknown);
            }
        }

        for (int index = 1; index < window.Count; index++)
        {
            VehicleMotionSample previous = window[index - 1];
            VehicleMotionSample current = window[index];

            // Only compared when both positions are known; an unknown one is already reported as
            // missing, and calling it a change as well would name a fact nobody observed.
            if (previous.HasKnownPosition && current.HasKnownPosition &&
                !current.IsSamePlaceAs(previous))
            {
                missing.Add(PositionChanged);
            }

            TimeSpan gap = current.ObservedAt - previous.ObservedAt;
            if (gap < options.MinimumSampleInterval)
            {
                missing.Add(SamplesTooClose);
            }
            else if (gap > options.MaximumSampleInterval)
            {
                missing.Add(ObservationGap);
            }
        }

        // A future timestamp is as unusable as an old one: it means two clocks disagree, and an age
        // computed from disagreeing clocks proves nothing about freshness.
        DateTimeOffset newest = window[^1].ObservedAt;
        if (newest > now || now - newest > options.MaximumEvidenceAge)
        {
            missing.Add(EvidenceStale);
        }

        return new StopProofVerdict(missing.Count == 0, [.. missing]);
    }
}

/// <summary>
/// The recent motion samples per vehicle, in memory, so that "several consecutive readings" can be
/// assembled from evaluations that each take one.
/// </summary>
/// <remarks>
/// <para>
/// <b>In memory, and that is a decision rather than a shortcut.</b> Ticket 06 built no table for
/// samples and this batch adds no migration, but persisting them would also be wrong: a stop proof
/// assembled from samples taken before a restart would be a claim about a vehicle nobody was
/// watching across it. Losing the window on restart costs one more sampling cycle and buys the
/// guarantee that every proof was observed by the process that made it. The verdict itself is
/// durable — it goes onto the fault fact with its timestamp.
/// </para>
/// <para>
/// <b>Sampling is driven, never scheduled here.</b> The caller takes one sample per evaluation and
/// records it; there is no timer and no delay loop, for the same reason the emergency supervisor
/// has neither — a component with its own clock is a second scheduler for episodes that already
/// have one.
/// </para>
/// </remarks>
public sealed class VehicleMotionLedger(IOptions<VehicleFaultOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<VehicleMotionSample>> windows =
        new(StringComparer.Ordinal);

    private readonly int capacity = options.Value.StopProofSampleCount;

    /// <summary>Adds one sample and returns the retained window for that vehicle, oldest first.</summary>
    public VehicleMotionSample[] Record(VehicleMotionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        Queue<VehicleMotionSample> window = windows.GetOrAdd(sample.DeviceKey, _ => new Queue<VehicleMotionSample>());
        lock (window)
        {
            window.Enqueue(sample);
            while (window.Count > capacity)
            {
                window.Dequeue();
            }

            return [.. window];
        }
    }

    /// <summary>
    /// Drops everything remembered about a vehicle.
    /// </summary>
    /// <remarks>
    /// Called when a fault episode ends, so the next one starts from an empty window. Carrying
    /// samples across episodes would let observations of one stop stand in for the next.
    /// </remarks>
    public void Forget(string deviceKey) => windows.TryRemove(deviceKey, out _);
}
