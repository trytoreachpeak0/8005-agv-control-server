using System.Buffers.Binary;
using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.CreateGate;

/// <summary>Why the Map/Station catalog is not usable, at the catalog level (REQ-0308).</summary>
/// <remarks>
/// These are catalog-level reasons: they are about the snapshot, not about one demand's stations.
/// The task-level counterparts live in <see cref="CreateGateReasons"/> and are produced only when
/// a snapshot that is itself fresh proves a specific station missing.
/// </remarks>
public static class CatalogAvailabilityReasons
{
    /// <summary>
    /// The two REQ-0302 values were never approved. This is specification 8.6's hard block: no
    /// Map/Station-dependent business runs at all, and no amount of waiting changes it.
    /// </summary>
    public const string ParametersNotApproved = "CATALOG_PARAMETERS_NOT_APPROVED";

    /// <summary>Nothing has ever been confirmed. Never read as "an empty catalog is fine".</summary>
    public const string NeverConfirmed = "CATALOG_NEVER_CONFIRMED";

    /// <summary>Time since the last complete confirmation exceeded the approved maximum.</summary>
    public const string FreshnessExceeded = "CATALOG_FRESHNESS_EXCEEDED";

    /// <summary>
    /// The running build or environment is not the one the catalog was confirmed against.
    /// Blocks regardless of freshness — REQ-0303 gives it the same business gate as no snapshot.
    /// </summary>
    public const string BuildIncompatible = "CATALOG_BUILD_INCOMPATIBLE";
}

/// <summary>
/// The catalog's verdict for one round: usable or not, and on what evidence.
/// </summary>
/// <param name="BlockReason">Null when usable; otherwise the one reason it is not.</param>
/// <param name="CatalogRevision">
/// The revision the endpoints of any demand accepted this round are frozen against.
/// </param>
/// <param name="DegradedReason">
/// Set when the catalog is being used past a failed refresh but still inside the approved window.
/// REQ-0302 allows exactly that and requires it to be marked explicitly — never described as
/// having obtained a current catalog.
/// </param>
public sealed record CatalogAvailability(
    string? BlockReason,
    long CatalogRevision,
    string? DegradedReason)
{
    public bool IsUsable => BlockReason is null;

    public static CatalogAvailability Blocked(string reason) => new(reason, 0, null);
}

/// <summary>
/// Owns the catalog-level half of REQ-0302/0303/0308: what a confirmation is, when freshness has
/// run out, and what the two approved parameters mean.
/// </summary>
/// <remarks>
/// <para>
/// <b>Freshness is measured from the last complete confirmation, never from the last attempt.</b>
/// The store already refuses to move the confirmation timestamp on a failure; this class is the
/// other half of that rule — it reads only the confirmation timestamp, so an attempt that failed
/// cannot extend the window from either side.
/// </para>
/// <para>
/// A refresh that failed while the window is still open does <em>not</em> block. REQ-0302 says so
/// in as many words: the last known good snapshot may keep serving, marked as such, with the
/// pre-create RouteCost check still doing its work. What must never happen is describing that as
/// having obtained a current catalog, which is why the failure is persisted on the row and
/// surfaced as <see cref="CatalogAvailability.DegradedReason"/> rather than being swallowed.
/// </para>
/// </remarks>
public sealed class CatalogAvailabilityAccess(
    ICatalogAvailabilityStore store,
    IOptions<MapStationCatalogOptions> options,
    CatalogAlarmLedger alarms,
    TimeProvider timeProvider,
    ILogger<CatalogAvailabilityAccess> logger)
{
    private static readonly Action<ILogger, int, string, Exception?> LogCatalogState =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(2110, nameof(LogCatalogState)),
            "Map/Station catalog for map {MapId} is not usable: {Reason}.");

    private static readonly Action<ILogger, int, string, Exception?> LogDegraded =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(2111, nameof(LogDegraded)),
            "Map/Station catalog for map {MapId} is serving its last known good snapshot after {Reason}; " +
            "this is not a current catalog.");

    private readonly MapStationCatalogOptions _options = options.Value;

    public bool IsApproved => _options.IsApproved;

    /// <summary>The catalog's verdict as of now.</summary>
    public async Task<CatalogAvailability> ReadAsync(int mapId, CancellationToken cancellationToken)
    {
        if (!_options.IsApproved)
        {
            return LogOnce(mapId, CatalogAvailability.Blocked(CatalogAvailabilityReasons.ParametersNotApproved));
        }

        MapStationCatalogAvailability? state = await store
            .ReadStateAsync(mapId, cancellationToken).ConfigureAwait(false);
        if (state is null || state.LastCompleteConfirmationAt is null)
        {
            return LogOnce(mapId, CatalogAvailability.Blocked(CatalogAvailabilityReasons.NeverConfirmed));
        }

        if (state.State == MapStationCatalogState.BuildIncompatible)
        {
            // REQ-0303 puts a wrong environment, an unapproved build and an incompatible contract
            // behind the same business gate as having no snapshot at all. Freshness is irrelevant
            // here: the snapshot may be seconds old and still be about a different system.
            return LogOnce(mapId, CatalogAvailability.Blocked(CatalogAvailabilityReasons.BuildIncompatible));
        }

        TimeSpan age = timeProvider.GetUtcNow() - state.LastCompleteConfirmationAt.Value;
        if (age > _options.ApprovedMaxUnconfirmed!.Value)
        {
            return LogOnce(mapId, CatalogAvailability.Blocked(CatalogAvailabilityReasons.FreshnessExceeded));
        }

        string? degraded = state.State == MapStationCatalogState.Fresh ? null : state.LastFailureReason;
        if (alarms.ShouldRaise(mapId, degraded) && degraded is not null)
        {
            LogDegraded(logger, mapId, degraded, null);
        }

        return new CatalogAvailability(null, state.CatalogRevision, degraded);
    }

    /// <summary>
    /// Records that the catalog was read whole and adopted. This is the only thing that moves the
    /// freshness window.
    /// </summary>
    public Task RecordConfirmationAsync(
        RiotMapStationCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_options.IsApproved)
        {
            // Nothing to measure a confirmation against. Recording one would leave a row claiming
            // freshness under parameters nobody approved.
            return Task.CompletedTask;
        }

        return store.RecordCompleteConfirmationAsync(
            snapshot.MapId,
            RevisionOf(snapshot.ContentSha256),
            (int)_options.ApprovedSyncPeriod!.Value.TotalSeconds,
            (int)_options.ApprovedMaxUnconfirmed!.Value.TotalSeconds,
            snapshot.ObservedAt,
            cancellationToken);
    }

    /// <summary>Records a catalog-level failure without touching the freshness window.</summary>
    public Task RecordFailureAsync(
        int mapId,
        MapStationCatalogState state,
        string reason,
        CancellationToken cancellationToken) =>
        store.RecordFailureAsync(mapId, state, reason, timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>
    /// The catalog revision a set of stations is frozen against, derived from the snapshot's
    /// content hash.
    /// </summary>
    /// <remarks>
    /// Derived rather than counted, and deliberately so. REQ-0305 freezes "the catalog revision at
    /// the time" so a later refresh can be recognised as a different catalog; a content-derived
    /// number does that exactly — two reads of the same catalog give the same revision, any change
    /// gives another — and it can be recomputed from the evidence afterwards, which a counter
    /// stored in a column nobody kept cannot. It is not monotonic, and nothing here treats it as
    /// ordered.
    /// </remarks>
    public static long RevisionOf(string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);

        Span<byte> first8 = stackalloc byte[8];
        for (int index = 0; index < 8; index++)
        {
            first8[index] = byte.Parse(
                contentSha256.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return BinaryPrimitives.ReadInt64BigEndian(first8) & long.MaxValue;
    }

    private CatalogAvailability LogOnce(int mapId, CatalogAvailability availability)
    {
        if (alarms.ShouldRaise(mapId, availability.BlockReason))
        {
            LogCatalogState(logger, mapId, availability.BlockReason!, null);
        }

        return availability;
    }
}

/// <summary>
/// Remembers which catalog condition has already been announced, per Map.
/// </summary>
/// <remarks>
/// <para>
/// REQ-0308 requires the catalog-level state to be deduplicated by reason and kept updated, and
/// forbids manufacturing one alarm per polling round or per waiting task. The persisted state row
/// satisfies that by construction — it is one row per Map, updated in place. The log does not: the
/// runtime ticks once a second, so the same sentence would be written tens of thousands of times a
/// shift and the one transition worth reading would be unfindable.
/// </para>
/// <para>
/// A singleton, because a condition spans rounds and each round runs in its own scope. It holds a
/// reason string per Map and nothing else; losing it on restart costs one repeated log line.
/// </para>
/// </remarks>
public sealed class CatalogAlarmLedger
{
    private readonly Dictionary<int, string?> _announced = [];
    private readonly object _gate = new();

    /// <summary>True when this condition is a change from the one already announced for this Map.</summary>
    public bool ShouldRaise(int mapId, string? reason)
    {
        lock (_gate)
        {
            if (_announced.TryGetValue(mapId, out string? announced) &&
                string.Equals(announced, reason, StringComparison.Ordinal))
            {
                return false;
            }

            _announced[mapId] = reason;
            return reason is not null;
        }
    }
}
