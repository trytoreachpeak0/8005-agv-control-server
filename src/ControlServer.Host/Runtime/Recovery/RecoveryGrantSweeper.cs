using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>
/// Ends the exception recovery sessions their grant no longer covers (control-server#797, REQ-0279 as CP-0022 revised it): at
/// start -- every session left open across a restart, whose grant the restart ended -- whenever a grant is given, renewed or
/// revoked, the moment the next one runs out, and every <see cref="Backstop"/> regardless. The decision is
/// <see cref="OnboardRecoveryCoordinator.EndSessionsOutsideGrantsAsync"/>'s; this times it and sends what it queued.
/// </summary>
/// <remarks>
/// Does nothing while <see cref="RecoveryGrantOptions.Required"/> is off. A round that fails is logged and the next one tries
/// again: nothing here may stop the host.
/// </remarks>
public sealed class RecoveryGrantSweeper(
    IServiceScopeFactory scopes,
    RecoveryGrantRegistry registry,
    IOptions<RecoveryGrantOptions> options,
    TimeProvider timeProvider,
    ILogger<RecoveryGrantSweeper> logger) : BackgroundService
{
    /// <summary>The longest the sweep waits without a grant changing or running out.</summary>
    public static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private static readonly Action<ILogger, Exception?> LogRoundFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(9796, nameof(LogRoundFailed)),
            "A recovery grant sweep failed (control-server#797); the next one tries again.");

    private static readonly Action<ILogger, Exception?> LogSweepRunning =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(9795, nameof(LogSweepRunning)),
            "Recovery grants are required: the recovery grant sweep is running (control-server#797).");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Required)
        {
            return;
        }
        LogSweepRunning(logger, null);
        while (!stoppingToken.IsCancellationRequested)
        {
            Task changed = registry.WaitForChangeAsync(stoppingToken);
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogRoundFailed(logger, error);
            }
            TimeSpan wait = Backstop;
            if (registry.NextExpiry() is DateTimeOffset next)
            {
                TimeSpan untilNext = next - timeProvider.GetUtcNow();
                wait = untilNext < wait ? (untilNext > TimeSpan.Zero ? untilNext : TimeSpan.Zero) : wait;
            }
            try
            {
                await Task.WhenAny(changed, Task.Delay(wait, timeProvider, stoppingToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One round: ends what is outside its grant, then sends the vehicles their snapshots.</summary>
    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ControlServerDbContext dbContext = scope.ServiceProvider.GetRequiredService<ControlServerDbContext>();
        OnboardRecoveryCoordinator coordinator = scope.ServiceProvider.GetRequiredService<OnboardRecoveryCoordinator>();
        IReadOnlyList<string> told;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            told = await coordinator.EndSessionsOutsideGrantsAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (string agvId in told.Distinct(StringComparer.Ordinal))
        {
            await coordinator.TrySendPendingSessionSnapshotsAsync(agvId, cancellationToken).ConfigureAwait(false);
        }
    }
}
