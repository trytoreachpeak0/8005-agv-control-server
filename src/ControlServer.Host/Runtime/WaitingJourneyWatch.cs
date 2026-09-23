using System.Globalization;
using System.Linq.Expressions;
using ControlServer.Application;
using ControlServer.Domain;
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
/// raise the alarm and its urgency, never cancel, reassign to charging or rebuild an order. So this class writes three
/// columns of its own and a log line; it never writes a stage, a reason code, an order or an outbound message. Under the
/// dispatch minimum the line is an error; under the rescue line it says a person has to move the vehicle to a charger.
/// Whether a vehicle should instead go and charge on its own is program#134, a batch 9 question.
/// </para>
/// <para>
/// <b>It can never hold a journey back, and nothing that goes wrong in a round stops it.</b> The engine runs it at the end
/// of every round, whichever way the round ended -- the Map catalog could not be read, or an advance threw: when the
/// engine is stuck is exactly when a stopped vehicle needs someone told. Every failure in here -- RIoT throwing or not
/// answering within its budget, the database refusing the write -- is caught per journey: the battery reads "unknown",
/// or the round goes on without the record. Only a shutdown cancellation escapes.
/// </para>
/// <para>
/// <b>Its writes go around the engine's change tracker.</b> The context is shared with the round, and a tracked save here
/// would also write whatever the advance left pending -- including values an advance deliberately put back without
/// saving. So the three columns are written with one <c>ExecuteUpdate</c>, and the tracked row is brought level without
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

    // The line stays in English, like every other line this server logs: it goes to a console that a service wrapper
    // redirects, and the first L2 run of this watch showed a Chinese phrase in it arriving as code page 936 mojibake. The
    // dashboard card says it in Chinese ("需要人工挪车充电：在车上用单机方式挪车、充电，不要在 RIoT 里给这辆车下单"), where the page is served as UTF-8.

    /// <summary>
    /// Observes every journey that is waiting now, read fresh from the database. Never throws but for a shutdown
    /// cancellation: a failure to read the journeys is logged once and the round goes on, a failure on one journey is
    /// logged and the next one is still observed.
    /// </summary>
    public async Task ObserveAsync(CancellationToken cancellationToken)
    {
        JourneyRuntimeRow[] journeys;
        try
        {
            // Untracked: the round's own tracked rows may hold what an advance changed and did not save (it threw half
            // way, which is one of the times this must still run). The database says what is waiting.
            journeys = await dbContext.JourneyRuntimes.AsNoTracking()
                .Where(row => row.Stage != JourneyRuntimeStage.Completed)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogWatchFailed(logger, "(all)", error);
            return;
        }

        foreach (JourneyRuntimeRow runtime in journeys)
        {
            if (!JourneyWaitClassification.IsWaiting(runtime.Stage, runtime.BlockReasonCode))
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
        // Null only for a row no save has reconciled since the column arrived; its last write is the nearest later bound.
        DateTimeOffset? waitingSince = runtime.WaitingSince;
        TimeSpan waited = now - (waitingSince ?? runtime.UpdatedAt);
        // No "warned before this wait began" case: a journey that stops waiting loses its warning time with its start
        // (JourneyRuntimeRow.ReconcileWait), so a new wait always begins unwarned, whatever the two settings are.
        bool due = waited >= options.WaitingJourneyWarningAfter &&
            (runtime.WaitingWarnedAt is not { } warnedAt || now - warnedAt >= options.WaitingJourneyWarningRepeat);

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
        // Only the wait that was read: a journey that departed, or began another wait, between the read and this write
        // has another start (or none), and a warning time written onto it would belong to a wait it is no longer in.
        int written = await dbContext.JourneyRuntimes
            .Where(row => row.JourneyId == runtime.JourneyId && row.WaitingSince == waitingSince)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.WaitingBatteryPercent, battery)
                    .SetProperty(row => row.WaitingBatteryObservedAt, now)
                    .SetProperty(row => row.WaitingWarnedAt, warned),
                cancellationToken)
            .ConfigureAwait(false);
        JourneyRuntimeRow? tracked = dbContext.JourneyRuntimes.Local
            .FirstOrDefault(row => string.Equals(row.JourneyId, runtime.JourneyId, StringComparison.Ordinal));
        if (written == 1 && tracked is not null)
        {
            EntityEntry<JourneyRuntimeRow> entry = dbContext.Entry(tracked);
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
    /// <remarks>
    /// Within <see cref="JourneyRuntimeOptions.WaitingJourneyBatteryReadBudget"/>, the way the dispatch round bounds its
    /// per-vehicle reads (<c>DispatchRoundRunner.TryAdmitToRoundAsync</c>): the gateway's own timeout is thirty seconds, and
    /// N waiting vehicles on a RIoT that has stopped answering would otherwise hold the next round back N times that. A
    /// read past the budget is "unknown", like any other read that gave no percentage.
    /// </remarks>
    private async Task<int?> ReadBatteryAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        using CancellationTokenSource expiry = new(options.WaitingJourneyBatteryReadBudget, timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(vehicleKey, linked.Token)
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
        WaitingBatteryLevel level = WaitingJourneyBattery.Level(battery, options);
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
                $"move the vehicle to a charger. {NothingMovesIt}"),
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
