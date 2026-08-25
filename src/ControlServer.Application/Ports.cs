using ControlServer.Domain;

namespace ControlServer.Application;

public interface IMesIngestCatalog
{
    Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken);
}

public interface IRiotMovementGateway
{
    Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken);

    Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken);
}

public interface IOnboardPeer
{
    Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken);
}

public interface IDemandAcceptanceStore
{
    Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        CancellationToken cancellationToken);
}

public interface IMovementIntentStore
{
    Task<StoredMovementIntent?> GetByUpperIdAsync(string upperId, CancellationToken cancellationToken);

    Task MarkResultUnknownAsync(string upperId, CancellationToken cancellationToken);

    Task ConfirmAsync(string upperId, string orderId, CancellationToken cancellationToken);
}

public enum RiotOrderObservationKind
{
    NotFound,
    Active,
    Terminal,
    Unknown
}

public sealed record RiotOrderObservation(
    string UpperId,
    RiotOrderObservationKind Kind,
    string? OrderId);

public sealed record StoredMovementIntent(OrderIntent Intent, string Status, string? OrderId);
