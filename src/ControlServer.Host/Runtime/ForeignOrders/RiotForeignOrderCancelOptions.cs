namespace ControlServer.Host.Runtime.ForeignOrders;

/// <summary>
/// Operational gate for cancelling foreign orders running on this server's vehicles (control-server#330), separate from
/// <c>JourneyRuntime</c> and from <see cref="RiotCreateDispatchOptions"/>. Closed unless the deployed configuration opens it,
/// so a missing section fails closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a gate of its own.</b> Enabling the journey runtime is not an authorization to write to RIoT, and neither is the create
/// gate: creating this server's own orders and cancelling someone else's are two different authorizations. A cancel stops a
/// vehicle that someone else set moving -- a person moving it by hand in RIoT, an experiment, RIoT's own charging order -- so a
/// deployment that is only meant to watch (the parallel v2 instance, whose definition keeps the create gate closed) must not
/// cancel anything either (independent review M1 of control-server#330).
/// </para>
/// <para>
/// <b>Closed still keeps REQ-0164's 0/1 gate.</b> The supervision goes on recognising: a proven foreign order is recorded,
/// alarmed and holds its vehicle exactly as an unprovable one does (<c>ForeignRiotOrderStates.HeldCancelNotAuthorized</c>);
/// only the cancel is not sent. That is REQ-0164's rule before its revision added the cancel: a deployment whose gate is closed
/// stays on it until someone authorizes the cancel for that deployment.
/// </para>
/// </remarks>
public sealed class RiotForeignOrderCancelOptions
{
    public const string SectionName = "RiotForeignOrderCancel";

    public bool Enabled { get; set; }
}

public static class RiotForeignOrderCancelServiceCollectionExtensions
{
    public static IServiceCollection AddRiotForeignOrderCancelGate(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RiotForeignOrderCancelOptions>()
            .Bind(configuration.GetSection(RiotForeignOrderCancelOptions.SectionName))
            .ValidateOnStart();
        return services;
    }
}
