using System.Globalization;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Fleet;

/// <summary>
/// Refuses to start a multi-vehicle server whose waiting point registration cannot give every vehicle a waiting point
/// of its own (specification 5.4, REQ-0289; control-server#388).
/// </summary>
/// <remarks>
/// <para>
/// The reason is the interlock in 5.4: with fewer waiting points than vehicles, a vehicle with nowhere to go holds the
/// point another one needs. "Enough" is judged as a matching, not a total (<see cref="WaitingPointCoverageCalculator"/>):
/// every vehicle must be able to take a distinct enabled point on this server's Map that its whitelist admits.
/// </para>
/// <para>
/// Only points on <c>JourneyRuntime:mapId</c> count. A deployment still configured for Map 25 while the points are
/// registered on Map 26 is refused, and the message says where the points are. Points that are a task type's fixed
/// station on this Map do not count either: the import refuses them, but the binding may have arrived after the import.
/// It runs after <see cref="TaskTypeStations.TaskTypeStationStartup"/> for that reason.
/// </para>
/// <para>
/// A single-vehicle deployment is not checked, nor is a server whose journey runtime is off. A version imported while
/// the server runs is not re-checked -- the specification refuses to <i>start</i> with such a configuration; the next
/// start does. The database is migrated before this runs, so a refused first start leaves a database the FieldOps
/// import can write to.
/// </para>
/// </remarks>
public static class WaitingPointStartupCheck
{
    public const string ReasonCode = "WAITING_POINTS_FEWER_THAN_VEHICLES";

    private static readonly Action<ILogger, string, string, Exception?> Refused =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(9401, "WaitingPointStartupRefused"),
            "Waiting point registration refused: {ReasonCode} {Detail}");

    private static readonly Action<ILogger, int, string, Exception?> ExcludedFixedStations =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(9402, "WaitingPointIsFixedStation"),
            "Enabled waiting points on Map {MapId} that are a task type's fixed station do not count: {Stations}");

    /// <summary>
    /// The refusal detail when <paramref name="coverage"/> falls short, or null when it does not. It gives the way out that
    /// needs no database edit: how many points are missing, which vehicles have none, the FieldOps command with its
    /// arguments filled in as far as the server knows them, and that a single-vehicle deployment is not checked.
    /// </summary>
    /// <param name="databasePath">The server's SQLite file, for the command; null writes a placeholder.</param>
    /// <param name="fixedStationOrigins">Where each excluded fixed station comes from, so the refusal names the binding set version.</param>
    public static string? Judge(
        WaitingPointCoverage coverage,
        long? registrationVersion,
        IReadOnlyList<string> fleet,
        string? databasePath,
        IReadOnlyList<FixedTaskStationOrigin>? fixedStationOrigins = null)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(fleet);
        if (coverage.Sufficient)
        {
            return null;
        }

        string elsewhere = coverage.OnOtherMaps.Count == 0
            ? string.Empty
            : " Registered on other Maps, which this server does not run: "
              + string.Join(", ", coverage.OnOtherMaps.Select(pair => string.Create(
                  CultureInfo.InvariantCulture, $"Map {pair.Key} x{pair.Value}")))
              + ".";
        string excluded = coverage.Excluded.Count == 0
            ? string.Empty
            : " Not counted because they are a task type's fixed station: " + DescribeExcluded(coverage, fixedStationOrigins ?? [])
              + (fixedStationOrigins?.Any(origin => !origin.Active && coverage.Excluded.Contains(origin.StationId)) == true
                  ? ". A binding set version that was written but never activated counts too: roll it back to the active version "
                    + "(rollback-task-type-stations --version <active version>) or activate it. Both need the catalog a running "
                    + "server has confirmed, so start this server with one vehicle in JourneyRuntime:Fleet first, then restore the fleet."
                  : ".");
        string version = registrationVersion is long number
            ? FormattableString.Invariant($"version {number}")
            : "(none imported)";
        string database = databasePath ?? "<controlserver.db>";
        string summary = FormattableString.Invariant(
                $"JourneyRuntime:Fleet has {coverage.VehicleCount} vehicles, but waiting point registration {version} gives only ")
            + FormattableString.Invariant(
                $"{coverage.Assignable} of them a waiting point of their own on Map {coverage.MapId} ({coverage.EnabledOnMap} enabled there).")
            + FormattableString.Invariant($" Short by {coverage.Shortfall}; left without a point: {string.Join(", ", coverage.Unassigned)}.")
            + elsewhere + excluded
            + " No database edit is needed.";
        // Points registered on another Map mean the likelier mistake is this server's Map, not the registration: a ready
        // command for this Map would, followed as written, register waiting points on a Map nobody measured them on (on
        // site, the MVP's Map 25 with agv01 on it). So no ready command then -- the Map comes first.
        string register = coverage.OnOtherMaps.Count > 0
            ? FormattableString.Invariant(
                  $" First check JourneyRuntime:mapId (this server runs Map {coverage.MapId}): if the vehicles run on the Map the points are registered on, correct it and start again. ")
              + "Only if they really run on this Map, register waiting points on it with ControlServer.FieldOps.exe import-waiting-points "
              + "--database <controlserver.db> --input <waiting-points.csv> --catalog <stations.json> --map <the Map the vehicles run on> "
              + "--fleet <VehicleKey;VehicleKey> (add --dry-run to preview)."
            : FormattableString.Invariant(
                  $" Either register or enable at least {coverage.Shortfall} more waiting point(s) on Map {coverage.MapId} ")
              + "that these vehicles' whitelists admit, with the server stopped, then start it again: "
              + FormattableString.Invariant(
                  $"ControlServer.FieldOps.exe import-waiting-points --database \"{database}\" --input <waiting-points.csv> ")
              + FormattableString.Invariant(
                  $"--catalog <stations-{coverage.MapId}.json> --map {coverage.MapId} --fleet \"{string.Join(";", fleet)}\" ")
              + "(add --dry-run to preview; read-waiting-points shows the current registration).";
        string removeVehicles = FormattableString.Invariant(
            $" Or take vehicles out of JourneyRuntime:Fleet until the registration covers the rest ({coverage.Assignable} can be covered now; one vehicle is not checked).");
        return summary + register + removeVehicles
            + " See docs/field/batch-8-waiting-point-registration.md."
            + " A single-vehicle deployment (JourneyRuntime:Fleet empty or one vehicle) is not subject to this check.";
    }

    public static async Task EnsureAsync(
        JourneyRuntimeOptions options,
        IWaitingPointRegistry registry,
        ITaskTypeStationBindingStore bindings,
        ILogger logger,
        string? databasePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(logger);
        // An empty Fleet is the single-vehicle deployment (VehicleRoster); a Fleet of one is one vehicle too.
        if (!options.Enabled || options.Fleet.Length <= 1)
        {
            return;
        }

        WaitingPointRegistrationVersion? registration = await registry.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        // The one derivation the import, the read verb and the eligibility predicate use too, so a start, a preview and a
        // commitment never count different points.
        IReadOnlyList<FixedTaskStationOrigin> fixedOrigins =
            await WaitingPointFixedTaskStations.ReadAsync(bindings, options.MapId, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<int> fixedStations = WaitingPointFixedTaskStations.StationIds(fixedOrigins);
        string[] fleet = [.. options.Fleet.Select(vehicle => vehicle.VehicleKey)];
        WaitingPointCoverage coverage = WaitingPointCoverageCalculator.Evaluate(
            registration?.Points ?? [], options.MapId, fleet, fixedStations);
        if (coverage.Excluded.Count > 0)
        {
            ExcludedFixedStations(logger, options.MapId, DescribeExcluded(coverage, fixedOrigins), null);
        }

        string? detail = Judge(coverage, registration?.Version, fleet, databasePath, fixedOrigins);
        if (detail is null)
        {
            return;
        }
        Refused(logger, ReasonCode, detail, null);
        throw new InvalidOperationException($"{ReasonCode}: {detail}");
    }

    /// <summary>The host's entry point: resolves the options and the stores from a fresh scope.</summary>
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        await EnsureAsync(
                provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value,
                provider.GetRequiredService<IWaitingPointRegistry>(),
                provider.GetRequiredService<ITaskTypeStationBindingStore>(),
                provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(WaitingPointStartupCheck).FullName!)
                    ?? NullLogger.Instance,
                DatabasePath(provider.GetRequiredService<ControlServerDbContext>()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string DescribeExcluded(WaitingPointCoverage coverage, IReadOnlyList<FixedTaskStationOrigin> origins) =>
        string.Join(", ", coverage.Excluded.Select(station => WaitingPointFixedTaskStations.Describe(station, origins)));

    // The SQLite file the server actually opened (the configured one, defaults and %ProgramData% expanded), so the command
    // in the refusal can be pasted as it stands.
    private static string? DatabasePath(ControlServerDbContext context)
    {
        string dataSource = context.Database.GetDbConnection().DataSource;
        return string.IsNullOrWhiteSpace(dataSource) ? null : Path.GetFullPath(dataSource);
    }
}
