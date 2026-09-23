using ControlServer.Application;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.ForeignOrders;

#pragma warning disable CS9113 // Scaffold: the dependencies are read once the supervision lands (control-server#330).

/// <summary>
/// Finds orders RIoT shows running on this server's vehicles that this server did not create, cancels the proven ones once,
/// and holds the vehicle until RIoT reads them back ended (control-server#330; REQ-0148 and REQ-0164 as revised in
/// requirements baseline v1.5.0).
/// </summary>
public sealed class ForeignRunningOrderSupervisor(
    ControlServerDbContext dbContext,
    IRiotOrderListingFacts orders,
    IRiotOrderCommandGateway commands,
    IRiotOrderCommandAuditStore audit,
    VehicleRoster roster,
    TimeProvider timeProvider,
    ILogger<ForeignRunningOrderSupervisor> logger)
{
    /// <summary>How long after its cancel went out an order still running is handed to a person.</summary>
    public static readonly TimeSpan CancelSettleTime = TimeSpan.FromSeconds(10);

    /// <summary>One round of supervision.</summary>
    public Task SuperviseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
