using ControlServer.Domain;

namespace ControlServer.Application;

public interface IMesIngestCatalog
{
    Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken);
}

public interface IRiotMovementGateway
{
    Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken);
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

public sealed record RiotOrderObservation(string UpperId, string Outcome, string? OrderId);
