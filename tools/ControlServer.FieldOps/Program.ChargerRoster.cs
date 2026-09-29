using System.Globalization;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.FieldOps;

/// <summary>
/// 充电桩名册的 FieldOps 动词（control-server#400，批次9-02）：整份导入与只读查看（REQ-0171、REQ-0288；规格 5.5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>置空与启用是同一个动词。</b>用户 2026-09-29 定：v2 只在授权窗口里自动充电，窗口外名册置空。开窗导入登记 211 的名册文件，关窗导入
/// 零条目的名册文件（<c>docs/field/charger-roster/</c> 两份）——两者走同一条校验、同一次治理发布、同一种审计，没有直接改库的第三条路。
/// 选一个动词而不是「置空」「登记」两个，是因为两个动词就是两条写入路径，第二条迟早会少一道校验；空名册是一个普通的合法版本，
/// 不需要自己的入口。
/// </para>
/// <para>
/// 导入写的是服务端正在用的同一个库；服务端不重启，下一次分配按新版本判（批次9-06）。进行中的充电周期与桩上的预占按原快照继续到结束，
/// 输出把它们列出来：关窗时还有，就是「窗口还不能关」。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string ImportChargerRosterCommand = "import-charger-roster";

    private const string ReadChargerRosterCommand = "charger-roster";

    private static async Task<int> ImportChargerRosterAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        const string usage = " needs --input <charger-roster.json> --catalog <stations.json> --map <id> --fleet <VehicleKey;VehicleKey...>";
        if (!options.TryGetValue("input", out string? inputPath) || !options.TryGetValue("catalog", out string? catalogPath))
        {
            return Usage(ImportChargerRosterCommand + usage);
        }
        if (!TryReadMapAndFleet(options, out int mapId, out string[] fleet, out string? problem))
        {
            return Usage(problem ?? ImportChargerRosterCommand + usage);
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

        ChargerRosterImportService importer = ChargerRosterImporter(context, governance);
        ChargerRosterImportRequest request = new(await File.ReadAllTextAsync(inputPath), mapId, fleet, catalog!);
        ChargerRosterImportResult result;
        try
        {
            if (dryRun)
            {
                // A preview writes nothing and takes no write lock, for the reason given at import-dispatch-zone-parameters.
                result = await importer.ImportAsync(request, dryRun: true, now, CancellationToken.None);
            }
            else
            {
                // One write transaction around reading the current version, comparing and writing -- the audit of an
                // unchanged import included: a second import of the same file waits here, then reads what the first wrote.
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
                result = await importer.ImportAsync(request, dryRun: false, now, CancellationToken.None);
                if (result.Outcome != ChargerRosterImportOutcome.Rejected)
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
                    command = ImportChargerRosterCommand,
                    outcome = "CONFLICT",
                    dryRun,
                    input = Path.GetFullPath(inputPath),
                    detail = "Another import committed first and this one was rolled back whole. Read the current roster and "
                        + "run the import again if it is still wanted.",
                    cause = conflict.GetBaseException().Message
                },
                1);
        }

        return Emit(
            new
            {
                command = ImportChargerRosterCommand,
                outcome = result.Outcome switch
                {
                    ChargerRosterImportOutcome.Accepted => "OK",
                    ChargerRosterImportOutcome.Unchanged => "UNCHANGED",
                    _ => "REJECTED"
                },
                dryRun,
                input = Path.GetFullPath(inputPath),
                catalog = Path.GetFullPath(catalogPath),
                mapId,
                fleet,
                entryCount = result.EntryCount,
                emptyRoster = result.Outcome != ChargerRosterImportOutcome.Rejected && result.EntryCount == 0,
                previousVersion = result.PreviousVersion,
                version = result.Version?.Version,
                contentSha256 = result.Version?.ContentSha256,
                snapshotId = result.Version?.SnapshotId,
                loadedAt = result.Version?.LoadedAt,
                approvedBy = result.Approval?.ApprovedBy,
                approvalBasis = result.Approval?.ApprovalBasis,
                changeNote = result.Approval?.ChangeNote,
                errorCount = result.Errors.Count,
                errors = result.Errors.Select(error => new
                {
                    reasonCode = error.ReasonCode,
                    stationId = error.StationId,
                    detail = error.Detail
                }),
                changes = result.Changes.Select(change => new
                {
                    mapId = change.MapId,
                    stationId = change.StationId,
                    kind = change.Before is null ? "ADDED" : change.After is null ? "REMOVED" : "CHANGED",
                    before = Charger(change.Before),
                    after = Charger(change.After)
                }),
                inProgress = InProgress(result.InProgress),
                windowCanClose = result.WindowCanClose,
                windowNote = result.WindowCanClose switch
                {
                    false => "The roster is empty for new allocations, but charging is still under way: the window cannot close "
                        + "until every cycle listed under inProgress has ended and its charger reservation is gone.",
                    true => "No charging is under way; the window can close.",
                    null => null
                }
            },
            result.Outcome == ChargerRosterImportOutcome.Rejected ? 1 : 0);
    }

    /// <summary>
    /// 当前（或指定）那一版名册，附进行中的充电周期与桩预占。<b>只读</b>，库以只读模式开。一版都没有也是 <c>OK</c>：那与空名册一样，
    /// 没有桩可分配。
    /// </summary>
    private static async Task<int> ReadChargerRosterAsync(
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

        // The read path publishes nothing; the publisher is only there to build the roster.
        ChargerRosterStore roster = new(context, new GovernedConfigurationPublisher(governance, governance));
        ChargerRosterVersion? read = version is null
            ? await roster.ReadCurrentAsync(CancellationToken.None)
            : await roster.ReadVersionAsync(version.Value, CancellationToken.None);
        if (version is not null && read is null)
        {
            return Emit(
                new { command = ReadChargerRosterCommand, outcome = "NOT_FOUND", requestedVersion = version, detail = "That version does not exist." },
                1);
        }
        ChargingGovernanceFacts facts = Facts(context, governance);
        IReadOnlyList<ChargingCycle> cycles = await facts.ListOpenCyclesAsync(CancellationToken.None);
        StationExclusivityStore exclusivity = new(context);
        List<StationExclusivity> reservations = [];
        foreach ((int mapId, int stationId) in (read?.Chargers ?? []).Select(charger => (charger.MapId, charger.StationId))
                     .Concat(cycles.Select(cycle => (cycle.MapId, cycle.StationId))).Distinct())
        {
            if (await exclusivity.ReadAsync(mapId, stationId, CancellationToken.None) is { } held
                && held.StationKind == StationExclusivityKinds.Charger)
            {
                reservations.Add(held);
            }
        }
        return Emit(
            new
            {
                command = ReadChargerRosterCommand,
                outcome = "OK",
                version = read?.Version,
                contentSha256 = read?.ContentSha256,
                snapshotId = read?.SnapshotId,
                loadedAt = read?.LoadedAt,
                source = read?.Source,
                approvedBy = read?.Approval.ApprovedBy,
                approvalBasis = read?.Approval.ApprovalBasis,
                changeNote = read?.Approval.ChangeNote,
                emptyRoster = (read?.Chargers.Count ?? 0) == 0,
                chargers = (read?.Chargers ?? []).Select(Charger),
                inProgress = InProgress(new ChargingWorkInProgress(cycles, reservations))
            },
            0);
    }

    private static ChargerRosterImportService ChargerRosterImporter(ControlServerDbContext context, GovernanceStore governance)
    {
        GovernedConfigurationPublisher publisher = new(governance, governance);
        return new ChargerRosterImportService(
            new ChargerRosterStore(context, publisher),
            new WaitingPointRegistry(context, publisher),
            new StationExclusivityStore(context),
            Facts(context, governance),
            governance);
    }

    private static ChargingGovernanceFacts Facts(ControlServerDbContext context, GovernanceStore governance) =>
        new(context, new TaskTypeStationBindingStore(context, new GovernedConfigurationPublisher(governance, governance)));

    private static object? Charger(ChargerRosterEntry? charger) =>
        charger is null
            ? null
            : new
            {
                mapId = charger.MapId,
                stationId = charger.StationId,
                stationName = charger.StationName,
                entryStationId = charger.EntryStationId,
                exitStationId = charger.ExitStationId,
                vehicleScope = charger.VehicleScope
            };

    private static object InProgress(ChargingWorkInProgress inProgress) =>
        new
        {
            openCycleCount = inProgress.OpenCycles.Count,
            openCycles = inProgress.OpenCycles.Select(Cycle),
            chargerReservations = inProgress.ChargerReservations.Select(held => new
            {
                mapId = held.MapId,
                stationId = held.StationId,
                state = held.State,
                vehicleKey = held.VehicleKey,
                journeyId = held.JourneyId,
                stateSince = held.StateSince,
                chargerRosterVersion = held.ChargerRosterVersion
            }),
            retention = ChargingWorkInProgress.Retention
        };

    private static object Cycle(ChargingCycle cycle) =>
        new
        {
            cycleId = cycle.CycleId,
            vehicleKey = cycle.VehicleKey,
            mapId = cycle.MapId,
            stationId = cycle.StationId,
            wireState = cycle.WireState,
            phase = cycle.Phase,
            allocatedAt = cycle.AllocatedAt,
            chargerRosterVersion = cycle.ChargerRosterVersion,
            chargingPolicyVersion = cycle.ChargingPolicyVersion
        };
}
