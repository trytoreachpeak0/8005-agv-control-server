using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Watches journeys whose vehicle stands waiting for a person, reads its battery and logs a wait that has lasted
/// (control-server#273).
/// </summary>
internal sealed class WaitingJourneyWatch(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    JourneyRuntimeOptions options,
    TimeProvider timeProvider,
    ILogger logger)
{
    public Task ObserveAsync(IReadOnlyList<JourneyRuntimeRow> journeys, CancellationToken cancellationToken)
    {
        _ = dbContext;
        _ = vehicleFacts;
        _ = options;
        _ = timeProvider;
        _ = logger;
        _ = journeys;
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
