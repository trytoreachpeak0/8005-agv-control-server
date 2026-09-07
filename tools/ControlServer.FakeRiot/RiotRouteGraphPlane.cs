using ControlServer.TestDoubles;

namespace ControlServer.FakeRiot;

/// <summary>
/// The four route-graph endpoints the RouteGraphSnapshot engine reads, on top of the station
/// catalog the data plane already serves.
/// </summary>
/// <remarks>
/// <para>
/// Response shapes reproduce Round 43's verbatim bodies from the production RIoT (map25), because
/// the SDK deserializes them by hand and a fake that answered in tidier JSON would let a wrong
/// reader pass. Four quirks are deliberate and must not be cleaned up:
/// </para>
/// <list type="number">
/// <item><c>edges</c> is snake_case, and its compound keys are <c>s_node</c>/<c>e_node</c> — not
/// <c>snode</c>/<c>enode</c> as the vendor's OpenAPI description says.</item>
/// <item><c>stations</c> spells position keys with a literal dot (served by the data plane).</item>
/// <item><c>mapEdgeGroup/all</c> is camelCase — a second style on the same service — and nests its
/// result under group-name keys rather than returning an array.</item>
/// <item>Its timestamps are <c>"2024-06-26 10:57:58"</c>, which is not ISO 8601.</item>
/// </list>
/// <para>
/// <c>removedEdgeDetail</c> is deliberately absent: CP-0001 did not approve it, so a product code
/// path that reached for it should fail here rather than quietly work against the fake.
/// </para>
/// </remarks>
public static class RiotRouteGraphPlane
{
    public static void MapRiotRouteGraphPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeRiotState> engine = app.Services.GetRequiredService<CommandEngine<FakeRiotState>>();

        app.MapGet("/api/imap/v1/mapInfo/edges/{mapId:int}", async (
            int mapId, CancellationToken cancellationToken) =>
        {
            IResult? fault = await RiotDataPlane.ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;

            FakeRiotState state = engine.Snapshot().State;
            if (!state.EdgesByMapId.TryGetValue(mapId, out IReadOnlyList<FakeEdge>? edges))
            {
                return RiotDataPlane.Ok(Array.Empty<object>());
            }

            return RiotDataPlane.Ok(edges.Select(edge => new
            {
                id = edge.Id,
                cost = edge.Cost,
                s_node = edge.StartNode,
                e_node = edge.EndNode,
                sx = edge.StartX,
                sy = edge.StartY,
                ex = edge.EndX,
                ey = edge.EndY,
                s_facing = edge.StartFacing,
                e_facing = edge.EndFacing,
                direction = edge.Direction,
                is_back_edge = edge.IsBackEdge,
                type = edge.Type,
                desc = "",
                // Present on the wire and always empty on map25. Carried so a reader that trips
                // over the key sees it here rather than only in the field.
                user_define_properties = new Dictionary<string, object?>(StringComparer.Ordinal),
            }).ToArray());
        });

        app.MapGet("/api/imap/v1/mapResource/removedEdge/{mapId:int}", async (
            int mapId, CancellationToken cancellationToken) =>
        {
            IResult? fault = await RiotDataPlane.ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;

            FakeRiotState state = engine.Snapshot().State;
            state.RemovedEdgeIdsByMapId.TryGetValue(mapId, out IReadOnlyList<int>? removed);
            // Empty is the normal answer — map25 had nothing removed in Round 43 — and the element
            // shape below has never been seen populated on a real RIoT. It follows the
            // MapRemovedEdge component of the imap description.
            return RiotDataPlane.Ok((removed ?? []).Select(edgeId => new
            {
                id = edgeId,
                mapId,
                edgeId,
                gmtCreate = "2026-09-07 08:00:00",
            }).ToArray());
        });

        app.MapGet("/api/imap/v1/mapResource/removedStation/{mapId:int}", async (
            int mapId, CancellationToken cancellationToken) =>
        {
            IResult? fault = await RiotDataPlane.ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;

            FakeRiotState state = engine.Snapshot().State;
            state.RemovedStationIdsByMapId.TryGetValue(mapId, out IReadOnlyList<int>? removed);
            state.StationsByMapId.TryGetValue(mapId, out IReadOnlyList<FakeStation>? stations);
            return RiotDataPlane.Ok((removed ?? []).Select(stationId => new
            {
                id = stationId,
                mapId,
                stationId,
                stationName = stations?.FirstOrDefault(station => station.Id == stationId)?.Name,
                gmtCreate = "2026-09-07 08:00:00",
            }).ToArray());
        });

        app.MapGet("/api/imap/v1/mapEdgeGroup/all", async (CancellationToken cancellationToken) =>
        {
            IResult? fault = await RiotDataPlane.ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;

            FakeRiotState state = engine.Snapshot().State;
            // Keyed by group name, across every Map — not filtered to one. The consumer filters by
            // mapId, and a fake that pre-filtered would hide the fact that it has to.
            Dictionary<string, object?> byGroupName = new(StringComparer.Ordinal);
            foreach (IGrouping<string, FakeEdgeGroup> group in state.EdgeGroups
                .GroupBy(group => group.GroupName, StringComparer.Ordinal))
            {
                byGroupName[group.Key] = group.Select(member => new
                {
                    id = member.Id,
                    edgeId = member.EdgeId,
                    mapId = member.MapId,
                    mapName = member.MapName,
                    name = member.GroupName,
                    type = member.Type,
                    isDelete = member.IsDelete,
                    gmtCreate = member.GmtCreate,
                    gmtUpdate = member.GmtUpdate,
                }).ToArray();
            }

            return RiotDataPlane.Ok(byGroupName);
        });
    }
}
