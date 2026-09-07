using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class JourneyRuntimeWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<JourneyRuntimeWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogDisabled = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2001, nameof(LogDisabled)),
        "Journey runtime is disabled; MesIngest polling and movement dispatch are fail-closed.");
    private static readonly Action<ILogger, Exception?> LogIterationFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2002, nameof(LogIterationFailed)),
        "Journey runtime iteration failed closed; no stage is inferred from memory.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JourneyRuntimeOptions runtime = options.Value;
        if (!runtime.Enabled)
        {
            LogDisabled(logger, null);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        using PeriodicTimer timer = new(runtime.PollInterval, timeProvider);
        do
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                // The route graph refreshes before dispatch decides, so a round is decided against
                // the freshest snapshot the engine could get. A refresh that fails does not stop
                // the round: it marks the snapshot stale, and the admission chain blocks on that —
                // which is the same outcome, reached through the fail-closed path rather than an
                // exception.
                RouteGraph.RouteGraphOptions routeGraphOptions = scope.ServiceProvider
                    .GetRequiredService<IOptions<RouteGraph.RouteGraphOptions>>().Value;
                if (routeGraphOptions.Enabled)
                {
                    RouteGraph.RouteGraphRefresher refresher = scope.ServiceProvider
                        .GetRequiredService<RouteGraph.RouteGraphRefresher>();
                    await refresher.RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
                }

                JourneyRuntimeEngine engine = scope.ServiceProvider.GetRequiredService<JourneyRuntimeEngine>();
                await engine.ExecuteOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                LogIterationFailed(logger, error);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
