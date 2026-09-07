using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.RouteGraph;

/// <summary>Why a snapshot is not usable this round.</summary>
public static class RouteGraphStaleReasons
{
    /// <summary>Nothing has been fetched yet. Never means "an empty graph is fine".</summary>
    public const string NeverRefreshed = "ROUTE_GRAPH_NEVER_REFRESHED";

    /// <summary>The design state stood past its TTL backstop.</summary>
    public const string DesignStateExpired = "ROUTE_GRAPH_DESIGN_STATE_EXPIRED";

    /// <summary>The runtime removals stood past their maximum age.</summary>
    public const string RuntimeStateExpired = "ROUTE_GRAPH_RUNTIME_STATE_EXPIRED";

    /// <summary>The Map's edge-group membership changed under the graph.</summary>
    public const string EdgeGroupFingerprintChanged = "ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED";

    /// <summary>Dynamic route cost went from empty to present.</summary>
    public const string DynamicRouteCostAppeared = "ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED";

    /// <summary>A refresh failed and the snapshot cannot be trusted to be current.</summary>
    public const string RefreshFailed = "ROUTE_GRAPH_REFRESH_FAILED";
}

/// <summary>
/// Keeps one Map's route-graph snapshot current, on two independent cycles, and decides when it
/// is stale.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two cycles, not one.</b> The design state — edges and stations — changes when someone edits
/// the Map, so it refreshes on RIoT's own <c>gmtUpdate</c> with a 10-minute TTL backstop. The
/// runtime removals change when RIoT takes an edge or a station out of service, which can happen
/// at any moment, so they refresh every 10 seconds. Reading the design state that often would be
/// 140 KB a shot for nothing; reading the removals that rarely would route vehicles over edges
/// that are gone.
/// </para>
/// <para>
/// <b>Stale means no dispatch, not degraded dispatch.</b> The engine is a hard dependency of the
/// dispatch chain. When it cannot answer, what is lost is not only the cost-ranking layer — that
/// half could fall back to the deterministic tie-break — but reachability, and reachability has no
/// fallback that REQ-0207 permits. So this class fails closed and the admission chain blocks.
/// </para>
/// </remarks>
public sealed class RouteGraphRefresher(
    IRouteGraphSource source,
    IRouteGraphSnapshotStore store,
    IOptions<RouteGraphOptions> options,
    TimeProvider timeProvider,
    ILogger<RouteGraphRefresher> logger)
{
    private static readonly Action<ILogger, int, string, Exception?> LogRefreshFailed =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRefreshFailed)),
            "Route graph refresh failed for map {MapId} during {Phase}.");

    private static readonly Action<ILogger, int, string, Exception?> LogStale =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogStale)),
            "Route graph for map {MapId} is stale: {Reason}.");

    private readonly RouteGraphOptions _options = options.Value;

    /// <summary>
    /// Runs one refresh tick: the runtime cycle always, the design cycle when it is due.
    /// </summary>
    /// <remarks>
    /// Any failure marks the snapshot stale rather than leaving the previous one standing as if it
    /// were current. A graph that was right ten minutes ago is not evidence about now, and the
    /// whole point of the engine is that dispatch stops when the evidence stops.
    /// </remarks>
    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        int mapId = _options.MapId;
        DateTimeOffset now = timeProvider.GetUtcNow();

        RouteGraphSnapshotHeader? header = await store
            .ReadHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsDesignStateDue(header, now))
            {
                await RefreshDesignStateAsync(mapId, now, cancellationToken).ConfigureAwait(false);
            }

            await RefreshRuntimeStateAsync(mapId, now, cancellationToken).ConfigureAwait(false);
            await RefreshEdgeGroupsAsync(mapId, header, now, cancellationToken).ConfigureAwait(false);
            await RefreshDynamicRouteCostAsync(mapId, header, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogRefreshFailed(logger, mapId, "refresh", error);
            await store.MarkStaleAsync(
                mapId, RouteGraphStaleReasons.RefreshFailed, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EvaluateStalenessAsync(mapId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-judges the snapshot against the clock and clears or sets the stale flag.
    /// </summary>
    /// <remarks>
    /// Split out from the refresh so that a caller can ask "is it stale now" without performing
    /// I/O it did not ask for. Age is what changes between refreshes, and age alone can make a
    /// snapshot that was fine a moment ago unusable.
    /// </remarks>
    public async Task EvaluateStalenessAsync(int mapId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RouteGraphSnapshotHeader? header = await store
            .ReadHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);
        string? reason = StaleReason(header, now, _options);

        if (reason is null)
        {
            await store.ClearStaleAsync(mapId, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        LogStale(logger, mapId, reason, null);
        await store.MarkStaleAsync(mapId, reason, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The staleness verdict: null when the snapshot is usable, otherwise the first reason it is not.
    /// </summary>
    /// <remarks>
    /// Static and pure so it can be tested against a header without a store, a clock or a RIoT.
    /// The order matters only for which reason gets reported first; any one of them blocks.
    /// </remarks>
    public static string? StaleReason(
        RouteGraphSnapshotHeader? header,
        DateTimeOffset now,
        RouteGraphOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (header is null || header.DesignRefreshedAt is null || header.DesignRevision <= 0)
        {
            return RouteGraphStaleReasons.NeverRefreshed;
        }

        // A pre-existing stale flag that this evaluation cannot clear stands. The two that
        // survive are the ones no amount of waiting fixes: the world changed under the graph.
        if (header.StaleReason is RouteGraphStaleReasons.EdgeGroupFingerprintChanged or
            RouteGraphStaleReasons.DynamicRouteCostAppeared)
        {
            return header.StaleReason;
        }

        if (now - header.DesignRefreshedAt.Value > options.DesignStateTtl)
        {
            return RouteGraphStaleReasons.DesignStateExpired;
        }

        if (header.RuntimeRefreshedAt is null ||
            now - header.RuntimeRefreshedAt.Value > options.RuntimeStateMaxAge)
        {
            return RouteGraphStaleReasons.RuntimeStateExpired;
        }

        return null;
    }

    private bool IsDesignStateDue(RouteGraphSnapshotHeader? header, DateTimeOffset now) =>
        header is null ||
        header.DesignRefreshedAt is null ||
        header.DesignRevision <= 0 ||
        now - header.DesignRefreshedAt.Value >= _options.DesignStateTtl;

    private async Task RefreshDesignStateAsync(int mapId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<RouteGraphEdgeFact> edges = await source
            .ReadEdgesAsync(mapId, cancellationToken).ConfigureAwait(false);
        // Stations are resolved against these very edges. Reading them against a newer edge table
        // would place a station on a node that the graph being built may not contain.
        IReadOnlyList<RouteGraphStationFact> stations = await source
            .ReadStationsAsync(mapId, edges, cancellationToken).ConfigureAwait(false);

        await store.ReplaceDesignStateAsync(
            mapId, edges, stations, sourceGmtUpdate: null, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshRuntimeStateAsync(int mapId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> removedEdges = await source
            .ReadRemovedEdgeIdsAsync(mapId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<int> removedStations = await source
            .ReadRemovedStationIdsAsync(mapId, cancellationToken).ConfigureAwait(false);

        await store.ReplaceRuntimeStateAsync(
            mapId, removedEdges, removedStations, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshEdgeGroupsAsync(
        int mapId,
        RouteGraphSnapshotHeader? previous,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RouteGraphEdgeGroupFact> groups = await source
            .ReadEdgeGroupsAsync(mapId, cancellationToken).ConfigureAwait(false);
        string fingerprint = RouteGraphEdgeGroupFingerprint.Compute(groups);

        await store.ReplaceEdgeGroupsAsync(
            mapId, groups, fingerprint, now, cancellationToken).ConfigureAwait(false);

        // Only a *change* is a staleness trigger, and only against a fingerprint that was actually
        // taken before. map25 has no edge groups at all, so the first fingerprint is the empty
        // string; treating that as a change would make the engine stale on its very first tick.
        if (previous?.EdgeGroupRefreshedAt is not null &&
            !string.Equals(previous.EdgeGroupFingerprint, fingerprint, StringComparison.Ordinal))
        {
            await store.MarkStaleAsync(
                mapId,
                RouteGraphStaleReasons.EdgeGroupFingerprintChanged,
                now,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshDynamicRouteCostAsync(
        int mapId,
        RouteGraphSnapshotHeader? previous,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool present = await source
            .ReadDynamicRouteCostPresentAsync(cancellationToken).ConfigureAwait(false);

        await store.RecordDynamicRouteCostObservationAsync(
            mapId, present, now, cancellationToken).ConfigureAwait(false);

        // Empty → non-empty only. The graph is built on the assumption that no dynamic cost is in
        // play; the moment there is some, the graph is no longer a complete account of routing.
        // Going the other way is the world returning to what the graph already assumes.
        bool wasAbsent = previous is null ||
            (previous.DynamicRouteCostObservedAt is not null && !previous.DynamicRouteCostPresent);
        if (present && wasAbsent && previous?.DynamicRouteCostObservedAt is not null)
        {
            await store.MarkStaleAsync(
                mapId,
                RouteGraphStaleReasons.DynamicRouteCostAppeared,
                now,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
