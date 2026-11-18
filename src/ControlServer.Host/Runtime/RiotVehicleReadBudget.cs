using ControlServer.Application;

namespace ControlServer.Host.Runtime;

/// <summary>
/// A RIoT vehicle read bounded by <see cref="JourneyRuntimeOptions.RiotVehicleReadBudget"/> (control-server#799).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The journey runtime's round advances one vehicle after another, and every vehicle's emergency stop watch and
/// REQ-0248 re-trigger run at its end (<c>JourneyRuntimeEngine.WatchOpenStopsAsync</c>). The gateway's own timeout is thirty
/// seconds, so on a RIoT that stopped answering every read the round made held all of that back one gateway timeout. The
/// reads that already had a budget were bounded the same way: <c>WaitingJourneyWatch.ReadBatteryAsync</c> and the position
/// check before a slot command (control-server#783).
/// </para>
/// <para>
/// <b>Past the budget the answer is null</b>, and every call site maps null to the branch it already takes for a read that
/// failed there: nothing proven, nothing released, nothing sent. Which branch that is at each call site is listed in the pull
/// request of control-server#799. The read is also given up on when the stand-in or the SDK does not honour the cancellation:
/// the round stops waiting for it at the budget either way.
/// </para>
/// <para>
/// The host's shutdown is not a timeout: it propagates. Any other failure of the read propagates unchanged, to the catch the
/// call site already had for it.
/// </para>
/// </remarks>
internal static class RiotVehicleReadBudget
{
    /// <summary>
    /// The vehicle as RIoT reads it within <paramref name="budget"/>, or null when the budget ran out first.
    /// </summary>
    public static Task<RiotVehicleObservation?> ReadVehicleWithinAsync(
        this IRiotVehicleFacts vehicleFacts,
        string vehicleKey,
        TimeSpan budget,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleFacts);
        return WithinAsync(token => vehicleFacts.ReadVehicleAsync(vehicleKey, token), budget, timeProvider, cancellationToken);
    }

    /// <summary>
    /// RIoT's vehicle safety reading within <paramref name="budget"/>, or null when the budget ran out first. Only where it is
    /// read together with the vehicle, in one try (<c>ProvenStoppedVehicleAsync</c>, <c>JudgeEndedIdleReturnOrderAsync</c>).
    /// </summary>
    public static Task<RiotVehicleSafetyObservation?> ReadVehicleSafetyWithinAsync(
        this IRiotVehicleSafetyFacts vehicleSafety,
        string vehicleKey,
        TimeSpan budget,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleSafety);
        return WithinAsync(
            token => vehicleSafety.ReadVehicleSafetyAsync(vehicleKey, token), budget, timeProvider, cancellationToken);
    }

    /// <summary>
    /// What the product gateway answers for a vehicle it could not read (<c>HttpRiotMovementGateway.UnknownVehicle</c>): not
    /// connected, not enabled, <c>UNKNOWN</c>, no map, no station, no battery, no speed. For a call site whose only "not read"
    /// branch is that shape; <c>DemandReleaseService</c> recognises it by exactly these fields.
    /// </summary>
    public static RiotVehicleObservation Unread(string vehicleKey, DateTimeOffset at) => new(
        vehicleKey,
        Connected: false,
        Enabled: false,
        ProcState: "UNKNOWN",
        CurrentMap: string.Empty,
        CurrentStationId: null,
        BatteryPercent: null,
        BatteryState: null,
        Speed: null,
        ObservedAt: at,
        LockStatus: null,
        OrderTaskId: null);

    private static async Task<T?> WithinAsync<T>(
        Func<CancellationToken, Task<T>> read,
        TimeSpan budget,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        using CancellationTokenSource expiry = new(budget, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        Task<T> reading = read(linked.Token);
        try
        {
            // WaitAsync: the round stops waiting at the budget even if the read itself does not honour its token.
            return await reading.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A read left running is not waited for, and whatever it ends in is observed here rather than left unobserved.
            _ = reading.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return null;
        }
    }
}
