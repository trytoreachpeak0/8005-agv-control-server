using System.Globalization;
using System.Linq.Expressions;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Watches journeys whose vehicle stands waiting for a person: reads its battery from RIoT every round, records the
/// reading on the journey, and logs a wait that has lasted past <see cref="JourneyRuntimeOptions.WaitingJourneyWarningAfter"/>,
/// again every <see cref="JourneyRuntimeOptions.WaitingJourneyWarningRepeat"/> while it lasts (control-server#273).
/// </summary>
/// <remarks>
/// <para>
/// <b>It reports and nothing else.</b> v2 has no automatic charging before batch 9, and REQ-0169 lets a falling battery
/// raise the alarm and its urgency, never cancel, reassign to charging or rebuild an order. So this class writes four
/// columns of its own and a log line; it never writes a stage, a reason code, an order or an outbound message. Under the
/// dispatch minimum the line is an error; under the rescue line it says a person has to move the vehicle to a charger.
/// Whether a vehicle should instead go and charge on its own is program#134, a batch 9 question.
/// </para>
/// <para>
/// <b>It can never hold a journey back.</b> The engine calls it after every journey has had its advance, and every
/// failure in here -- RIoT throwing, the database refusing the write -- is caught per journey: the battery reads
/// "unknown", or the round goes on without the record. Only a shutdown cancellation escapes.
/// </para>
/// <para>
/// <b>Its writes go around the engine's change tracker.</b> The context is shared with the round, and a tracked save here
/// would also write whatever the advance left pending -- including values an advance deliberately put back without
/// saving. So the four columns are written with one <c>ExecuteUpdate</c>, and the tracked row is brought level without
/// being marked modified, which is what the next round's reading of <see cref="JourneyRuntimeRow.WaitingWarnedAt"/> relies
/// on while the same context lives.
/// </para>
/// <para>
/// A reading is recorded when the percentage changes, when a line is logged (so the line and the dashboard say the same
/// thing), and otherwise at most once every <see cref="RecordRefresh"/>: the dashboard shows when the reading was taken, and
/// a row rewritten every two seconds for a number that has not moved buys nothing.
/// </para>
/// </remarks>
internal sealed class WaitingJourneyWatch(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    JourneyRuntimeOptions options,
    TimeProvider timeProvider,
    ILogger logger)
{
    /// <summary>How stale an unchanged reading may grow before it is recorded again.</summary>
    internal static readonly TimeSpan RecordRefresh = TimeSpan.FromMinutes(1);

    private static readonly Action<ILogger, string, string, string, long, string, string, Exception?> LogWaitingWarning =
        LoggerMessage.Define<string, string, string, long, string, string>(
            LogLevel.Warning,
            new EventId(2163, nameof(LogWaitingWarning)),
            "Journey {JourneyId} on vehicle {AgvId} has waited for a person in {Where} for {WaitedMinutes} min; " +
            "battery {Battery}. {Advice}");

    private static readonly Action<ILogger, string, string, string, long, string, string, Exception?> LogWaitingError =
        LoggerMessage.Define<string, string, string, long, string, string>(
            LogLevel.Error,
            new EventId(2164, nameof(LogWaitingError)),
            "Journey {JourneyId} on vehicle {AgvId} has waited for a person in {Where} for {WaitedMinutes} min; " +
            "battery {Battery}. {Advice}");

    private static readonly Action<ILogger, string, Exception?> LogWatchFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2165, nameof(LogWatchFailed)),
            "The waiting journey watch could not record journey {JourneyId} this round; the journey is not affected and the " +
            "next round tries again.");

    private const string NothingMovesIt = "Nothing on this server will move it (REQ-0169).";

    public async Task ObserveAsync(IReadOnlyList<JourneyRuntimeRow> journeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journeys);
        foreach (JourneyRuntimeRow runtime in journeys)
        {
            if (!JourneyWaitClassification.IsWaiting(runtime))
            {
                continue;
            }
            try
            {
                await ObserveOneAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogWatchFailed(logger, runtime.JourneyId, error);
            }
        }
    }

    private async Task ObserveOneAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        int? battery = await ReadBatteryAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        // Null only for a row neither the migration nor a save could stamp; its last write is the nearest later bound.
        DateTimeOffset since = runtime.StageSince ?? runtime.UpdatedAt;
        TimeSpan waited = now - since;
        bool due = waited >= options.WaitingJourneyWarningAfter &&
            (runtime.WaitingWarnedAt is not { } warnedAt ||
             warnedAt < since ||
             now - warnedAt >= options.WaitingJourneyWarningRepeat);

        if (due)
        {
            Log(runtime, waited, battery, now);
        }

        bool record = due ||
            runtime.WaitingBatteryObservedAt is not { } observedAt ||
            runtime.WaitingBatteryPercent != battery ||
            now - observedAt >= RecordRefresh;
        if (!record)
        {
            return;
        }

        DateTimeOffset? warned = due ? now : runtime.WaitingWarnedAt;
        await dbContext.JourneyRuntimes
            .Where(row => row.JourneyId == runtime.JourneyId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.WaitingBatteryPercent, battery)
                    .SetProperty(row => row.WaitingBatteryObservedAt, now)
                    .SetProperty(row => row.WaitingWarnedAt, warned),
                cancellationToken)
            .ConfigureAwait(false);
        EntityEntry<JourneyRuntimeRow> entry = dbContext.Entry(runtime);
        if (entry.State != EntityState.Detached)
        {
            Level(entry, row => row.WaitingBatteryPercent, battery);
            Level(entry, row => row.WaitingBatteryObservedAt, now);
            Level(entry, row => row.WaitingWarnedAt, warned);
        }
    }

    /// <summary>
    /// The battery percentage RIoT reports for the vehicle, or null when it reports none or cannot be asked. The product
    /// gateway already answers an SDK failure with an observation carrying no percentage; anything else it throws is the
    /// same "unknown" here, never a reason to stop.
    /// </summary>
    private async Task<int?> ReadBatteryAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        try
        {
            RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(vehicleKey, cancellationToken)
                .ConfigureAwait(false);
            return vehicle.BatteryPercent;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void Log(JourneyRuntimeRow runtime, TimeSpan waited, int? battery, DateTimeOffset readAt)
    {
        WaitingBatteryLevel level = JourneyWaitClassification.BatteryLevel(battery, options);
        string where = runtime.BlockReasonCode is { } reason
            ? $"{runtime.Stage} (reason {reason})"
            : runtime.Stage.ToString();
        string batteryText = battery is { } percent
            ? string.Create(CultureInfo.InvariantCulture, $"{percent}% as read at {readAt:O}")
            : string.Create(CultureInfo.InvariantCulture, $"unknown (no reading from RIoT at {readAt:O})");
        string advice = level switch
        {
            WaitingBatteryLevel.Unknown => "The battery could not be read; look at the vehicle. " + NothingMovesIt,
            WaitingBatteryLevel.BelowDispatchMinimum => string.Create(
                CultureInfo.InvariantCulture,
                $"The battery is below the dispatch minimum of {options.MinimumBatteryPercent}%. {NothingMovesIt}"),
            WaitingBatteryLevel.BelowRescueLine => string.Create(
                CultureInfo.InvariantCulture,
                $"The battery is below the rescue line of {options.WaitingJourneyRescueBatteryPercent}%: a person has to " +
                $"move the vehicle to a charger (需要人工挪车充电). {NothingMovesIt}"),
            _ => NothingMovesIt
        };
        long minutes = (long)waited.TotalMinutes;
        if (level is WaitingBatteryLevel.BelowDispatchMinimum or WaitingBatteryLevel.BelowRescueLine)
        {
            LogWaitingError(logger, runtime.JourneyId, runtime.AgvId, where, minutes, batteryText, advice, null);
        }
        else
        {
            LogWaitingWarning(logger, runtime.JourneyId, runtime.AgvId, where, minutes, batteryText, advice, null);
        }
    }

    private static void Level<T>(
        EntityEntry<JourneyRuntimeRow> entry,
        Expression<Func<JourneyRuntimeRow, T>> property,
        T value)
    {
        PropertyEntry<JourneyRuntimeRow, T> column = entry.Property(property);
        column.CurrentValue = value;
        column.OriginalValue = value;
        column.IsModified = false;
    }
}
