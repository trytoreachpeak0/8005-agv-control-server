using System.Collections.Concurrent;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// The latest battery reading of each vehicle, as this process last read it, for the dashboard's alarm on a loaded vehicle below
/// its mandatory charge line (control-server#866). In memory only, held by <see cref="ChargingAllocationBoard"/> (a host singleton).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fed only by reads that already happen.</b> The dispatch round reads every vehicle it admits, the ones under way included,
/// before it looks at anything else (<c>DispatchRoundRunner.TryAdmitToRoundAsync</c>), and the waiting journey watch reads every
/// waiting vehicle. Both write what they read here; nothing reads RIoT for this. That is what lets the alarm cover a loaded vehicle
/// that is driving (11-08: agv01 was at 39% from 23:16 and reached its stop at 23:30) without a request more per round.
/// </para>
/// <para>
/// <b>A failed read keeps the last good one</b> and says since when nothing could be read (review M3): a vehicle about to lose
/// power is exactly when RIoT stops answering for it, and an alarm that disappeared then would be worse than none.
/// </para>
/// <para>
/// <b>Since when it has been below a line.</b> Kept as the first time each percentage was read in the current run of readings that
/// has not gone up: a reading at <c>p</c> forgets every entry below <c>p</c> (the battery is back up there) and keeps the earlier,
/// higher ones. The first time below a line <c>L</c> is then the earliest entry under <c>L</c>. A process that started below the
/// line knows only its own first reading; the dashboard says so rather than guess.
/// </para>
/// </remarks>
public sealed class VehicleBatteryReadings
{
    private readonly ConcurrentDictionary<string, Readings> _byAgvId = new(StringComparer.Ordinal);

    /// <summary>Records what a read of <paramref name="agvId"/>'s battery gave at <paramref name="readAt"/>; null when it gave nothing.</summary>
    public void Record(string agvId, int? batteryPercent, DateTimeOffset readAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        Readings readings = _byAgvId.GetOrAdd(agvId, _ => new Readings());
        lock (readings)
        {
            if (batteryPercent is not int percent)
            {
                if (readings.LastPercent is not null && readings.UnreadSince is null)
                {
                    readings.UnreadSince = readAt;
                }
                return;
            }

            if (readings.LastReadAt is { } last && readAt < last)
            {
                return;
            }
            readings.LastPercent = percent;
            readings.LastReadAt = readAt;
            readings.UnreadSince = null;
            foreach (int lower in readings.FirstReadAt.Keys.Where(key => key < percent).ToArray())
            {
                readings.FirstReadAt.Remove(lower);
            }
            readings.FirstReadAt.TryAdd(percent, readAt);
        }
    }

    /// <summary>The last good reading of <paramref name="agvId"/>, or null when this process has never read one.</summary>
    public VehicleBatteryReading? Latest(string agvId)
    {
        if (!_byAgvId.TryGetValue(agvId, out Readings? readings))
        {
            return null;
        }
        lock (readings)
        {
            return readings is { LastPercent: int percent, LastReadAt: { } at }
                ? new VehicleBatteryReading(percent, at, readings.UnreadSince)
                : null;
        }
    }

    /// <summary>
    /// Since when <paramref name="agvId"/> has read below <paramref name="linePercent"/> without a reading at or above it in between,
    /// or null when its last reading is not below it or nothing was read.
    /// </summary>
    public DateTimeOffset? BelowSince(string agvId, int linePercent)
    {
        if (!_byAgvId.TryGetValue(agvId, out Readings? readings))
        {
            return null;
        }
        lock (readings)
        {
            if (readings.LastPercent is not int last || last >= linePercent)
            {
                return null;
            }
            return readings.FirstReadAt.Where(entry => entry.Key < linePercent).Min(entry => entry.Value);
        }
    }

    private sealed class Readings
    {
        public int? LastPercent { get; set; }

        public DateTimeOffset? LastReadAt { get; set; }

        public DateTimeOffset? UnreadSince { get; set; }

        public Dictionary<int, DateTimeOffset> FirstReadAt { get; } = [];
    }
}

/// <summary>A vehicle's last good battery reading and, when later reads gave nothing, since when.</summary>
public sealed record VehicleBatteryReading(int Percent, DateTimeOffset ReadAt, DateTimeOffset? UnreadSince);
