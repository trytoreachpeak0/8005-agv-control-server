using System.Globalization;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.FieldOps;

/// <summary>
/// 等待点登记的 FieldOps 动词（control-server#388，批次8-17）：按图整表导入与只读查看（REQ-0289、REQ-0297，规格 5.4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>服务端停着也能导，而且首次部署必须这样导。</b>投运车辆数大于 1、登记的启用等待点少于车辆数时服务端拒绝启动；那时它不读 RIoT、
/// 不确认目录，所以本动词不要求带来的目录就是服务端最近确认的那一份，只把两者是否一致报出来（<c>catalogMatchesServerConfirmation</c>）。
/// 本工具不读设置文件，服务端所跑的图（<c>--map</c>）与投运名册（<c>--fleet</c>）由运维照部署配置写出来；服务端启动时按自己的配置再判一次覆盖。
/// </para>
/// <para>
/// 导入写的是服务端正在用的同一个库；服务端不重启，下一次承诺按新版本判（批次8-18）。已有的预占与占用不动。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string ImportWaitingPointsCommand = "import-waiting-points";

    private const string ReadWaitingPointsCommand = "read-waiting-points";

    private static async Task<int> ImportWaitingPointsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        const string usage = " needs --input <waiting-points.csv> --catalog <stations.json> --map <id> --fleet <VehicleKey;VehicleKey...>";
        if (!options.TryGetValue("input", out string? inputPath) || !options.TryGetValue("catalog", out string? catalogPath))
        {
            return Usage(ImportWaitingPointsCommand + usage);
        }
        if (!TryReadMapAndFleet(options, out int mapId, out string[] fleet, out string? problem))
        {
            return Usage(problem ?? ImportWaitingPointsCommand + usage);
        }
        if (!File.Exists(inputPath))
        {
            return Usage($"input file not found: {inputPath}");
        }
        if (!TryReadCatalog(catalogPath, now, out RiotMapStationCatalogSnapshot? catalog, out problem))
        {
            return Usage(problem!);
        }
        bool dryRun = options.ContainsKey("dry-run");

        WaitingPointImportService importer = WaitingPointImporter(context, governance);
        WaitingPointImportRequest request = new(await File.ReadAllTextAsync(inputPath), mapId, fleet, catalog!);
        WaitingPointImportResult result;
        try
        {
            if (dryRun)
            {
                // A preview writes nothing and takes no write lock, for the reason given at import-dispatch-zone-parameters.
                result = await importer.ImportAsync(request, dryRun: true, now, CancellationToken.None);
            }
            else
            {
                // One write transaction around reading the current version, comparing and writing: a second import of the
                // same table waits here, then reads what the first wrote and reports UNCHANGED. The registry joins it.
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
                result = await importer.ImportAsync(request, dryRun: false, now, CancellationToken.None);
                if (result.Outcome == WaitingPointImportOutcome.Accepted)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception conflict) when (IsVersionNumberAlreadyTaken(conflict))
        {
            return Emit(
                new
                {
                    command = ImportWaitingPointsCommand,
                    outcome = "CONFLICT",
                    dryRun,
                    input = Path.GetFullPath(inputPath),
                    detail = "Another import committed first and this one was rolled back whole. Read the current version and "
                        + "run the import again if it is still wanted.",
                    cause = conflict.GetBaseException().Message
                },
                1);
        }

        return Emit(
            new
            {
                command = ImportWaitingPointsCommand,
                outcome = result.Outcome switch
                {
                    WaitingPointImportOutcome.Accepted => "OK",
                    WaitingPointImportOutcome.Unchanged => "UNCHANGED",
                    _ => "REJECTED"
                },
                dryRun,
                input = Path.GetFullPath(inputPath),
                catalog = Path.GetFullPath(catalogPath),
                mapId,
                fleet,
                catalogMatchesServerConfirmation = result.CatalogMatchesServerConfirmation,
                entryCount = result.EntryCount,
                previousVersion = result.PreviousVersion,
                version = result.Version?.Version,
                contentSha256 = result.Version?.ContentSha256,
                snapshotId = result.Version?.SnapshotId,
                loadedAt = result.Version?.LoadedAt,
                errorCount = result.Errors.Count,
                errors = result.Errors.Select(error => new
                {
                    line = error.Line,
                    reasonCode = error.ReasonCode,
                    stationId = error.StationId,
                    detail = error.Detail
                }),
                changes = result.Changes.Select(change => new
                {
                    mapId = change.MapId,
                    stationId = change.StationId,
                    kind = ChangeKind(change),
                    before = Point(change.Before),
                    after = Point(change.After)
                }),
                retainedReferences = result.RetainedReferences.Select(reference => new
                {
                    mapId = reference.Exclusivity.MapId,
                    stationId = reference.Exclusivity.StationId,
                    state = reference.Exclusivity.State,
                    vehicleKey = reference.Exclusivity.VehicleKey,
                    journeyId = reference.Exclusivity.JourneyId,
                    stateSince = reference.Exclusivity.StateSince,
                    waitingPointVersion = reference.Exclusivity.WaitingPointVersion,
                    newVersionReason = reference.NewVersionReason,
                    retention = reference.Retention
                }),
                coverage = Coverage(result.Coverage)
            },
            result.Outcome == WaitingPointImportOutcome.Rejected ? 1 : 0);
    }

    /// <summary>
    /// 当前（或指定）那一版登记。给了 <c>--map</c> 与 <c>--fleet</c> 时附上覆盖：这支车队在这张图上够不够。<b>只读</b>，库以只读模式开。
    /// 一版都没有也是 <c>OK</c>，那就是一个等待点也没登记。
    /// </summary>
    private static async Task<int> ReadWaitingPointsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options)
    {
        long? version = null;
        if (options.TryGetValue("version", out string? versionText))
        {
            if (!long.TryParse(versionText, CultureInfo.InvariantCulture, out long parsed))
            {
                return Usage($"--version must be a whole number, not '{versionText}'");
            }
            version = parsed;
        }
        bool wantsCoverage = options.ContainsKey("map") || options.ContainsKey("fleet");
        int mapId = 0;
        string[] fleet = [];
        if (wantsCoverage && !TryReadMapAndFleet(options, out mapId, out fleet, out string? problem))
        {
            return Usage(problem ?? $"{ReadWaitingPointsCommand} takes --map and --fleet together");
        }

        // A point bound as a task type's fixed station after it was registered does not count, exactly as at startup.
        IReadOnlySet<int> fixedStations = wantsCoverage
            ? await new WaitingPointImportFacts(
                    new TaskTypeStationBindingStore(context, new GovernedConfigurationPublisher(governance, governance)),
                    new CatalogAvailabilityStore(context))
                .ReadFixedTaskStationIdsAsync(mapId, CancellationToken.None)
            : new HashSet<int>();
        // The read path publishes nothing; the publisher is only there to build the registry.
        WaitingPointRegistry registry = new(context, new GovernedConfigurationPublisher(governance, governance));
        WaitingPointRegistrationVersion? registration = version is null
            ? await registry.ReadCurrentAsync(CancellationToken.None)
            : await registry.ReadVersionAsync(version.Value, CancellationToken.None);
        if (version is not null && registration is null)
        {
            return Emit(
                new { command = ReadWaitingPointsCommand, outcome = "NOT_FOUND", requestedVersion = version, detail = "That version does not exist." },
                1);
        }
        return Emit(
            new
            {
                command = ReadWaitingPointsCommand,
                outcome = "OK",
                version = registration?.Version,
                contentSha256 = registration?.ContentSha256,
                snapshotId = registration?.SnapshotId,
                loadedAt = registration?.LoadedAt,
                source = registration?.Source,
                points = (registration?.Points ?? []).Select(Point),
                coverage = wantsCoverage
                    ? Coverage(WaitingPointCoverageCalculator.Evaluate(registration?.Points ?? [], mapId, fleet, fixedStations))
                    : null
            },
            0);
    }

    private static WaitingPointImportService WaitingPointImporter(ControlServerDbContext context, GovernanceStore governance)
    {
        GovernedConfigurationPublisher publisher = new(governance, governance);
        return new WaitingPointImportService(
            new WaitingPointRegistry(context, publisher),
            new StationExclusivityStore(context),
            new WaitingPointImportFacts(new TaskTypeStationBindingStore(context, publisher), new CatalogAvailabilityStore(context)));
    }

    private static bool TryReadMapAndFleet(
        Dictionary<string, string> options,
        out int mapId,
        out string[] fleet,
        out string? problem)
    {
        mapId = 0;
        fleet = [];
        problem = null;
        if (!options.TryGetValue("map", out string? mapText) || !options.TryGetValue("fleet", out string? fleetText))
        {
            return false;
        }
        if (!int.TryParse(mapText, NumberStyles.None, CultureInfo.InvariantCulture, out mapId) || mapId <= 0)
        {
            problem = $"--map must be a positive whole number, not '{mapText}'";
            return false;
        }
        fleet = fleetText.Split(';');
        if (fleet.Any(key => key.Length == 0 || !string.Equals(key, key.Trim(), StringComparison.Ordinal))
            || fleet.Distinct(StringComparer.Ordinal).Count() != fleet.Length)
        {
            problem = $"--fleet lists each VehicleKey once, separated by ';', not '{fleetText}'";
            return false;
        }
        return true;
    }

    private static string ChangeKind(WaitingPointChange change) => (change.Before, change.After) switch
    {
        (null, _) => "ADDED",
        (_, null) => "DELETED",
        ({ Enabled: true }, { Enabled: false }) => "DISABLED",
        ({ Enabled: false }, { Enabled: true }) => "ENABLED",
        _ => "CHANGED"
    };

    private static object? Point(WaitingPointEntry? point) =>
        point is null
            ? null
            : new
            {
                mapId = point.MapId,
                stationId = point.StationId,
                stationName = point.StationName,
                enabled = point.Enabled,
                vehicleScope = point.VehicleScope
            };

    private static object? Coverage(WaitingPointCoverage? coverage) =>
        coverage is null
            ? null
            : new
            {
                mapId = coverage.MapId,
                vehicleCount = coverage.VehicleCount,
                enabledOnMap = coverage.EnabledOnMap,
                assignable = coverage.Assignable,
                sufficient = coverage.Sufficient,
                shortfall = coverage.Shortfall,
                unassigned = coverage.Unassigned,
                onOtherMaps = coverage.OnOtherMaps.ToDictionary(
                    pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value),
                warning = coverage.Sufficient
                    ? null
                    : "A server running this fleet on this map will refuse to start (WAITING_POINTS_FEWER_THAN_VEHICLES) until more waiting points are enabled."
            };
}
