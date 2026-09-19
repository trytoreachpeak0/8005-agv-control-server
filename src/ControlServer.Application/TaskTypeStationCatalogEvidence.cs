namespace ControlServer.Application;

/// <summary>Stub for control-server#161's test commit; the implementation lands in the next commit.</summary>
public static class TaskTypeStationCatalogEvidence
{
    public static string Fingerprint(int mapId, IEnumerable<RiotMapStation> stations) =>
        throw new NotImplementedException();

    public static long RevisionOf(string contentSha256) => throw new NotImplementedException();

    public static RiotMapStationCatalogSnapshot Supplied(
        int mapId,
        IReadOnlyList<RiotMapStation> stations,
        DateTimeOffset observedAt) =>
        throw new NotImplementedException();

    public static IReadOnlyList<TaskTypeStationViolation> JudgeFreshness(
        int mapId,
        RiotMapStationCatalogSnapshot? supplied,
        MapStationCatalogAvailability? state,
        DateTimeOffset now) =>
        throw new NotImplementedException();
}
