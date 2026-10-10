using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Host.Transport;

/// <summary>
/// Warns at startup when the slot fault declaration entry point is switched off while declarations are still waiting for
/// a vehicle's answer (review of control-server#383, S1).
/// </summary>
/// <remarks>
/// A warning, not a refusal to start: nothing is wrong with the store, and with the switch off those commands are not
/// replayed (<see cref="SlotFaultDeclarationResults.PendingCommandMessageIdsAsync"/>), so no vehicle is affected. What the
/// warning says is that those declarations will stay unanswered until the switch is turned on again against an onboard
/// that knows the command -- which a person has to know, because the dashboard shows them as waiting.
/// </remarks>
public static class SlotFaultDeclarationStartupCheck
{
    private static readonly Action<ILogger, int, string, Exception?> LogPendingWhileOff =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(9504, "SlotFaultDeclarationsPendingWhileSwitchedOff"),
            "SlotFaultDeclaration is switched off, and {Count} declaration(s) still wait for the vehicle's answer ({Vehicles}). "
            + "Their commands are not replayed while it is off.");

    /// <summary>The pending declarations per vehicle it warned about; empty when it did not warn.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> WarnAsync(
        ControlServerDbContext dbContext,
        bool enabled,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);
        if (enabled)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
        string[] vehicles = await dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking()
            .Where(row => row.State == SlotFaultDeclarationStates.Pending)
            .Select(row => row.AgvId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, int> perVehicle = vehicles
            .GroupBy(agvId => agvId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (perVehicle.Count > 0)
        {
            LogPendingWhileOff(
                logger,
                vehicles.Length,
                string.Join(", ", perVehicle.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}: {pair.Value}")),
                null);
        }
        return perVehicle;
    }

    /// <summary>The host's entry point: resolves the database, the switch and a logger.</summary>
    public static async Task WarnAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        await WarnAsync(
                provider.GetRequiredService<ControlServerDbContext>(),
                SlotFaultDeclarationOptions.IsEnabled(provider.GetRequiredService<IConfiguration>()),
                provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(SlotFaultDeclarationStartupCheck).FullName!)
                    ?? NullLogger.Instance,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
