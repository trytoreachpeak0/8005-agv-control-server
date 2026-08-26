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
