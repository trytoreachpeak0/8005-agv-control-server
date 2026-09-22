namespace ControlServer.Host.Runtime;

/// <summary>
/// How the battery of a waiting vehicle reads against the two lines (control-server#273): the dispatch minimum, under
/// which the vehicle takes no new work, and the rescue line, under which a person has to move it to a charger. Nothing on
/// this server moves it: REQ-0169 lets a falling battery raise the alarm and nothing else.
/// </summary>
public static class WaitingJourneyBattery
{
    public static WaitingBatteryLevel Level(int? batteryPercent, JourneyRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return batteryPercent switch
        {
            null => WaitingBatteryLevel.Unknown,
            { } percent when percent < options.WaitingJourneyRescueBatteryPercent => WaitingBatteryLevel.BelowRescueLine,
            { } percent when percent < options.MinimumBatteryPercent => WaitingBatteryLevel.BelowDispatchMinimum,
            _ => WaitingBatteryLevel.Sufficient
        };
    }
}

public enum WaitingBatteryLevel
{
    Unknown,
    Sufficient,
    BelowDispatchMinimum,
    BelowRescueLine
}
