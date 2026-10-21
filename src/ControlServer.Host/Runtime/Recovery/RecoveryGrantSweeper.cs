using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>control-server#797 stub: only enough to compile the tests; sweeps nothing yet.</summary>
public sealed class RecoveryGrantSweeper(
    IServiceScopeFactory scopes,
    RecoveryGrantRegistry registry,
    IOptions<RecoveryGrantOptions> options,
    TimeProvider timeProvider,
    ILogger<RecoveryGrantSweeper> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = scopes; _ = registry; _ = options; _ = timeProvider; _ = logger; _ = stoppingToken;
        return Task.CompletedTask;
    }

    public Task SweepOnceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
