using ControlServer.Application;

namespace ControlServer.Host.Runtime;

/// <summary>
/// How the battery of a waiting vehicle reads against the two lines (control-server#273): the vehicle's mandatory charge
/// entry threshold, under which it takes no new work, and the rescue line, under which a person has to move it to a charger.
/// Nothing on this server moves it: REQ-0169 lets a falling battery raise the alarm and nothing else.
/// </summary>
/// <remarks>
/// The first line was <c>JourneyRuntime:MinimumBatteryPercent</c> until batch 9-05 (control-server#403); it is now the
/// <c>MandatoryChargeEntryThreshold</c> of the charging policy version this vehicle would be judged under for new work
/// (<see cref="IChargingPolicyResolver.ResolveForNewDecisionAsync"/>). Without one -- no approved version covers the vehicle,
/// or it cannot be read -- the level is <see cref="WaitingBatteryLevel.Unknown"/>, which the watch still logs: the watch never
/// goes quiet for want of a policy.
/// </remarks>
public static class WaitingJourneyBattery
{
    /// <param name="mandatoryChargeEntryPercent">The vehicle's current entry threshold; null when it has no policy.</param>
    public static WaitingBatteryLevel Level(int? batteryPercent, int? mandatoryChargeEntryPercent, JourneyRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return batteryPercent switch
        {
            null => WaitingBatteryLevel.Unknown,
            // The rescue line needs no policy, and it is the more urgent of the two.
            { } percent when percent < options.WaitingJourneyRescueBatteryPercent => WaitingBatteryLevel.BelowRescueLine,
            _ when mandatoryChargeEntryPercent is null => WaitingBatteryLevel.Unknown,
            { } percent when percent < mandatoryChargeEntryPercent => WaitingBatteryLevel.BelowDispatchMinimum,
            _ => WaitingBatteryLevel.Sufficient
        };
    }

    /// <summary>The vehicle's current entry threshold, or null when no approved policy covers it or it cannot be read.</summary>
    public static async Task<int?> MandatoryChargeEntryPercentAsync(
        IChargingPolicyResolver chargingPolicy, string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chargingPolicy);
        if (string.IsNullOrWhiteSpace(vehicleKey))
        {
            return null;
        }
        VehicleChargingPolicyDecision decision =
            await chargingPolicy.ResolveForNewDecisionAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        return decision.Effective?.Policy.Content.MandatoryChargeEntryThresholdPercent;
    }
}

public enum WaitingBatteryLevel
{
    Unknown,
    Sufficient,
    BelowDispatchMinimum,
    BelowRescueLine
}
