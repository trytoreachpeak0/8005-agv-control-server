namespace ControlServer.Domain;

/// <summary>Where a station resolved onto the graph, and how well.</summary>
/// <param name="Node">The node the station occupies.</param>
/// <param name="ResidualMm">
/// Distance from the station's own coordinates to that node's. A quality signal, not a tolerance:
/// nothing is rejected on it here. Round 43 measured a maximum of 4 mm across map25's 206
/// stations, so a residual in the metres would mean the edge this station names is not the edge
/// it sits on — worth seeing in evidence.
/// </param>
public sealed record RouteGraphPlacement(int Node, double ResidualMm);

/// <summary>
/// Places a station on one of the two endpoints of the edge it names.
/// </summary>
/// <remarks>
/// <para>
/// RIoT gives a station an <c>edge_id</c> and a position, not a node. Round 43 measured how the
/// two relate on map25: every one of the 206 stations sits at one end of its edge — projection
/// parameter t was 0 for 96 of them and 1 for the other 110, never in between — and the
/// perpendicular distance to the edge was a median of 0.0 mm and a maximum of 4.0 mm. So the rule
/// is simply: whichever endpoint is nearer.
/// </para>
/// <para>
/// <b><c>station_offset</c> cannot be used for this.</b> It is zero on all 206, which is why the
/// rule is geometric rather than reading the field that looks like it was meant for it.
/// </para>
/// </remarks>
public static class RouteGraphStationPlacement
{
    public static RouteGraphPlacement Resolve(
        double stationX,
        double stationY,
        int startNode,
        int startX,
        int startY,
        int endNode,
        int endX,
        int endY)
    {
        double toStart = Distance(stationX, stationY, startX, startY);
        double toEnd = Distance(stationX, stationY, endX, endY);

        // Ties go to the start node. A tie means the station is equidistant from both ends, which
        // on a real Map means the edge has zero length; either answer is as good, and picking
        // deterministically keeps the snapshot reproducible.
        return toStart <= toEnd
            ? new RouteGraphPlacement(startNode, toStart)
            : new RouteGraphPlacement(endNode, toEnd);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
