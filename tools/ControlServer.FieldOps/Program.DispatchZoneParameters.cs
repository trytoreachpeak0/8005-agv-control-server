using System.Globalization;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.FieldOps;

/// <summary>
/// 每区派车参数的 FieldOps 动词（control-server#216，批次7-11）：整表导入与只读查看。
/// </summary>
/// <remarks>
/// <para>
/// 两个参数都按分区批准（REQ-0198 途中追加最大允许增量、REQ-0203 防饥饿阈值），没有全项目默认值。途中追加增量的量纲是计划路径代价增量
/// （毫米），不是时间（规格第 22.2 节第 2 条）；阈值是秒。未配置不是零：途中追加未配置与 <c>0</c> 效果相同（本区禁止），但读出来要分得开，
/// 所以只读动词给每个取值一个状态，不把未配置显示成零。
/// </para>
/// <para>
/// 导入写的是服务端正在用的同一个库，服务端不重启，下一轮派车读到新版本。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string ImportDispatchZoneParametersCommand = "import-dispatch-zone-parameters";

    private const string ReadDispatchZoneParametersCommand = "dispatch-zone-parameters";

    private static async Task<int> ImportDispatchZoneParametersAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("input", out string? inputPath))
        {
            return Usage($"{ImportDispatchZoneParametersCommand} needs --input <zone-parameters.csv>");
        }
        if (!File.Exists(inputPath))
        {
            return Usage($"input file not found: {inputPath}");
        }
        bool dryRun = options.ContainsKey("dry-run");

        DispatchZoneParameterImportService importer = new(
            new DispatchZoneParameterStore(context, new GovernedConfigurationPublisher(governance, governance)),
            new DispatchZoneParameterImportFacts(context));
        string csv = await File.ReadAllTextAsync(inputPath);
        DispatchZoneParameterImportResult result;
        try
        {
            if (dryRun)
            {
                // A preview writes nothing, so it takes no transaction -- and therefore no write lock. This database is
                // deliberately not in WAL mode (ControlServerSqlite), so a write transaction here would queue behind, and hold
                // up, the running server's own writes for as long as the preview took.
                result = await importer.ImportAsync(csv, dryRun: true, now, CancellationToken.None);
            }
            else
            {
                // One write transaction (SQLite BEGIN IMMEDIATE) around reading the current version, comparing and writing: a
                // second import of the same table waits here, then reads the version the first one wrote and reports UNCHANGED
                // instead of writing a version that changes nothing but its number. The store joins this transaction.
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
                result = await importer.ImportAsync(csv, dryRun: false, now, CancellationToken.None);
                if (result.Outcome == DispatchZoneParameterImportOutcome.Accepted)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception conflict) when (IsVersionNumberAlreadyTaken(conflict))
        {
            // Another import took the same version number first; this one rolled back whole. Nothing of it was written.
            // Different content under that number is refused by the snapshot freeze, identical content by the version row.
            return Emit(
                new
                {
                    command = ImportDispatchZoneParametersCommand,
                    outcome = "CONFLICT",
                    dryRun,
                    input = Path.GetFullPath(inputPath),
                    detail = "Another import committed first and this one was rolled back whole. Read the current version and "
                        + "run the import again if it is still wanted.",
                    cause = conflict.GetBaseException().Message
                },
                1);
        }

        string outcome = result.Outcome switch
        {
            DispatchZoneParameterImportOutcome.Accepted => "OK",
            DispatchZoneParameterImportOutcome.Unchanged => "UNCHANGED",
            _ => "REJECTED"
        };
        return Emit(
            new
            {
                command = ImportDispatchZoneParametersCommand,
                outcome,
                dryRun,
                input = Path.GetFullPath(inputPath),
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
                    dispatchZone = error.DispatchZone,
                    column = error.Column,
                    detail = error.Detail
                }),
                changes = result.Changes.Select(change => new
                {
                    dispatchZone = change.DispatchZone,
                    before = Values(change.Before),
                    after = Values(change.After)
                })
            },
            result.Outcome == DispatchZoneParameterImportOutcome.Rejected ? 1 : 0);
    }

    /// <summary>
    /// 这次失败是不是「版本号被另一次导入抢先占了」。
    /// </summary>
    /// <remarks>
    /// 只有两种情形算：同号不同内容由快照冻结抛 <see cref="GovernedSnapshotVersionConflictException"/>，同号同内容由版本行的主键抛
    /// <c>SQLITE_CONSTRAINT</c>（19）。别的 <see cref="DbUpdateException"/>——磁盘满、别处的约束——不算：把它们也报成 CONFLICT 等于叫
    /// 现场「再导一次」，而再导一次不会好。
    /// </remarks>
    internal static bool IsVersionNumberAlreadyTaken(Exception failure) =>
        failure is GovernedSnapshotVersionConflictException ||
        (failure is DbUpdateException && failure.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: SqliteConstraintErrorCode });

    private const int SqliteConstraintErrorCode = 19;

    /// <summary>
    /// 当前（或指定）那一版每区派车参数，外加库内调度策略里每个分区的取值。
    /// </summary>
    /// <remarks>
    /// <b>只读，而且库是以 SQLite 的只读模式开的</b>（见 <see cref="OpensReadOnly"/>）。一版都没有也是 <c>OK</c>：那就是全部未配置，
    /// 与批次 6 的行为相同。
    /// </remarks>
    private static async Task<int> ReadDispatchZoneParametersAsync(
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

        // 读路径不发布，所以这里给的 publisher 只是为了构造 store，它不会被走到。
        DispatchZoneParameterStore store = new(context, new GovernedConfigurationPublisher(governance, governance));
        DispatchZoneParameterTableVersion? table = version is null
            ? await store.ReadCurrentAsync(CancellationToken.None)
            : await store.ReadVersionAsync(version.Value, CancellationToken.None);
        if (version is not null && table is null)
        {
            return Emit(
                new
                {
                    command = ReadDispatchZoneParametersCommand,
                    outcome = "NOT_FOUND",
                    requestedVersion = version,
                    detail = "That version does not exist."
                },
                1);
        }

        IReadOnlySet<string> policyZones =
            await new DispatchZoneParameterImportFacts(context).ReadDispatchZonesAsync(CancellationToken.None);
        IReadOnlyDictionary<string, DispatchZoneParameters> listed =
            table?.Zones ?? new Dictionary<string, DispatchZoneParameters>(StringComparer.Ordinal);
        return Emit(
            new
            {
                command = ReadDispatchZoneParametersCommand,
                outcome = "OK",
                version = table?.Version,
                contentSha256 = table?.ContentSha256,
                snapshotId = table?.SnapshotId,
                loadedAt = table?.LoadedAt,
                source = table?.Source,
                zones = policyZones.Union(listed.Keys, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(zone =>
                    {
                        DispatchZoneParameters? parameters = listed.GetValueOrDefault(zone);
                        long? increase = parameters?.EnRouteAdditionMaxPathCostIncrease;
                        long? threshold = parameters?.StarvationThresholdSeconds;
                        return new
                        {
                            dispatchZone = zone,
                            inTable = parameters is not null,
                            inDispatchPolicy = policyZones.Contains(zone),
                            enRouteAddition = new
                            {
                                state = increase switch { null => "UNCONFIGURED", 0 => "FORBIDDEN", _ => "ALLOWED" },
                                maxPathCostIncreaseMm = increase
                            },
                            starvation = new
                            {
                                state = threshold is null ? "UNCONFIGURED" : "CONFIGURED",
                                thresholdSeconds = threshold
                            }
                        };
                    })
            },
            0);
    }

    private static object? Values(DispatchZoneParameters? zone) =>
        zone is null
            ? null
            : new
            {
                enRouteAdditionMaxPathCostIncreaseMm = zone.EnRouteAdditionMaxPathCostIncrease,
                starvationThresholdSeconds = zone.StarvationThresholdSeconds
            };
}
