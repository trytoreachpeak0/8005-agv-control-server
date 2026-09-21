using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图的持货等单数据面（批次7-12，control-server#217）。桩：只够测试编译，什么都不列。
/// </summary>
internal sealed class CargoHoldingQueryEndpoint : IDashboardQueryEndpoint
{
    public CargoHoldingQueryEndpoint()
        : this(new JourneyRuntimeOptions().CargoHoldingTimeout, TimeProvider.System)
    {
    }

    public CargoHoldingQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value.CargoHoldingTimeout, TimeProvider.System)
    {
    }

    internal CargoHoldingQueryEndpoint(TimeSpan cargoHoldingTimeout, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _ = cargoHoldingTimeout;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "cargo-holding";

    public Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return Task.FromResult<object>(new { journeys = Array.Empty<object>() });
    }
}
