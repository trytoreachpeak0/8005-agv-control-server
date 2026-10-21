using Microsoft.Extensions.Options;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>control-server#797 stub: only enough to compile the tests; never refuses yet.</summary>
public sealed class RecoveryGrantGate(
    RecoveryGrantRegistry registry,
    IOptions<RecoveryGrantOptions> options,
    ControlServerDbContext dbContext)
{
    public bool Required => options.Value.Required;

    public Task<string?> RefusalAsync(string agvId, string action, CancellationToken cancellationToken)
    {
        _ = agvId; _ = action; _ = cancellationToken; _ = registry; _ = dbContext;
        return Task.FromResult<string?>(null);
    }
}
