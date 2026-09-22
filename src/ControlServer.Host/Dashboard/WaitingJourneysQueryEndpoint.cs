using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>车队视图「等人中的旅程」的数据面（control-server#273）。</summary>
internal sealed class WaitingJourneysQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly JourneyRuntimeOptions _options;
    private readonly TimeProvider _clock;

    public WaitingJourneysQueryEndpoint()
        : this(new JourneyRuntimeOptions(), TimeProvider.System)
    {
    }

    [ActivatorUtilitiesConstructor]
    public WaitingJourneysQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value, TimeProvider.System)
    {
    }

    internal WaitingJourneysQueryEndpoint(JourneyRuntimeOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "waiting-journeys";

    public Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _ = _options;
        _ = _clock;
        _ = cancellationToken;
        return Task.FromResult<object>(new { journeys = Array.Empty<object>() });
    }
}
