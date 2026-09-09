using ControlServer.Domain;

namespace ControlServer.Application;

public interface IMesIngestCatalog
{
    Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken);

    Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken);
}

public interface ISublotBoxCountReader
{
    Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken);
}

public interface IPackageCapacityStore
{
    Task<int?> ResolveAndTrackAsync(
        string package,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}
