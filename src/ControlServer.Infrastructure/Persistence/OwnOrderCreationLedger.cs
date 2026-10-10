using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// <see cref="IOwnOrderCreationLedger"/> over this server's <c>OrderIntents</c> (control-server#573).
/// </summary>
public sealed class OwnOrderCreationLedger(ControlServerDbContext dbContext, TimeProvider timeProvider) : IOwnOrderCreationLedger
{
    public Task<bool> MayHaveCreatedSinceAsync(string vehicleKey, DateTimeOffset since, CancellationToken cancellationToken)
    {
        _ = dbContext;
        _ = timeProvider;
        _ = vehicleKey;
        _ = since;
        _ = cancellationToken;
        throw new NotImplementedException();
    }
}
