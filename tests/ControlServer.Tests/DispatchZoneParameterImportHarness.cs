using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 每区派车参数导入（control-server#216）的测试库：一个迁移过的 SQLite 文件，服务端以目标配置起过一次的样子——调度策略里有
/// <see cref="ZoneA"/>、<see cref="ZoneB"/>、<see cref="ZoneC"/> 三个分区。文件而不是内存库，因为并发导入要两条连接，FieldOps 要另一个进程。
/// </summary>
internal sealed class DispatchZoneParameterImportHarness : IAsyncDisposable
{
    internal const string ZoneA = "MAP-25-WIRE_TO_GATE";

    internal const string ZoneB = "MAP-26-WIRE_TO_GATE";

    internal const string ZoneC = "MAP-26-STAGING_TO_WIRE";

    internal const string Header =
        "dispatch_zone,en_route_addition_max_path_cost_increase_mm,starvation_threshold_seconds";

    internal static readonly DateTimeOffset Seeded = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    internal static readonly DateTimeOffset Imported = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory;

    private DispatchZoneParameterImportHarness(string directory)
    {
        _directory = directory;
        DatabasePath = Path.Combine(directory, "controlserver.db");
    }

    public string DatabasePath { get; }

    public string Directory => _directory;

    public static async Task<DispatchZoneParameterImportHarness> CreateAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-zone-parameters-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        DispatchZoneParameterImportHarness harness = new(directory);
        await using ControlServerDbContext context = harness.Open();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        foreach (string zone in (string[])[ZoneA, ZoneB, ZoneC])
        {
            context.DispatchZoneVehicles.Add(new DispatchZoneVehicleRow
            {
                Zone = zone,
                AgvId = "AGV-TEST",
                ConfigurationVersion = "test",
                UpdatedAt = Seeded
            });
        }
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return harness;
    }

    public ControlServerDbContext Open(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false))
            .AddInterceptors(interceptors)
            .Options);

    public static DispatchZoneParameterStore Store(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
        return new DispatchZoneParameterStore(context, new GovernedConfigurationPublisher(governance, governance));
    }

    public static DispatchZoneParameterImportService Service(ControlServerDbContext context) =>
        new(Store(context), new DispatchZoneParameterImportFacts(context));

    /// <summary>一次导入，在自己的上下文里，像 FieldOps 每跑一次是一个新进程那样。</summary>
    public async Task<DispatchZoneParameterImportResult> ImportAsync(
        string csv, bool dryRun = false, DateTimeOffset? at = null)
    {
        await using ControlServerDbContext context = Open();
        return await Service(context).ImportAsync(csv, dryRun, at ?? Imported, TestContext.Current.CancellationToken);
    }

    public async Task<DispatchZoneParameterTableVersion?> ReadCurrentAsync()
    {
        await using ControlServerDbContext context = Open();
        return await Store(context).ReadCurrentAsync(TestContext.Current.CancellationToken);
    }

    public async Task<DispatchZoneParameterTableVersion?> ReadVersionAsync(long version)
    {
        await using ControlServerDbContext context = Open();
        return await Store(context).ReadVersionAsync(version, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 这张表在库里留下的全部痕迹：版本行、分区行、这类对象的治理快照、这类对象的导入审计。四者要么一起有，要么一起没有。
    /// </summary>
    public async Task<(long Versions, long Rows, long Snapshots, long Audits)> FootprintAsync()
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: true));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return (
            await ScalarAsync(connection, "SELECT COUNT(*) FROM DispatchZoneParameterVersions"),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM DispatchZoneParameters"),
            await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM GovernedConfigurationSnapshots WHERE ObjectKind = '{GovernedObjectKind.DispatchZoneParameters}'"),
            await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{DispatchZoneParameterGovernance.VersionImportedAction}'"));
    }

    /// <summary>每个版本各自的快照与审计：版本号 → (快照 SnapshotId, 审计条数, 审计所指 SnapshotId)。</summary>
    public async Task<IReadOnlyList<string>> GovernanceTrailAsync()
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: true));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.Version,
                   (SELECT COUNT(*) FROM GovernedConfigurationSnapshots s
                     WHERE s.ObjectKind = '{GovernedObjectKind.DispatchZoneParameters}' AND s.Version = v.Version
                       AND s.SnapshotId = v.SnapshotId AND s.ContentSha256 = v.ContentSha256),
                   (SELECT COUNT(*) FROM BusinessAuditRecords a
                     WHERE a.Action = '{DispatchZoneParameterGovernance.VersionImportedAction}' AND a.Version = v.Version
                       AND a.ObjectId = '{DispatchZoneParameterGovernance.ObjectId}' AND a.SnapshotId = v.SnapshotId)
            FROM DispatchZoneParameterVersions v ORDER BY v.Version
            """;
        List<string> trail = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            trail.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"v{reader.GetInt64(0)} snapshot={reader.GetInt64(1)} audit={reader.GetInt64(2)}"));
        }
        return trail;
    }

    /// <summary>一个版本里各分区的取值，写成「分区 增量 阈值」，未配置写 <c>-</c>。</summary>
    public static string[] Describe(DispatchZoneParameterTableVersion? table) =>
        table is null
            ? []
            : [.. table.Zones.Values
                .OrderBy(zone => zone.DispatchZone, StringComparer.Ordinal)
                .Select(zone => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{zone.DispatchZone} {zone.EnRouteAdditionMaxPathCostIncrease?.ToString(CultureInfo.InvariantCulture) ?? "-"} {zone.StarvationThresholdSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-"}"))];

    public static string Csv(params string[] rows) => string.Join('\n', [Header, .. rows]) + "\n";

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        // The pool keeps the file open on behalf of the process; without this the directory cannot go.
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Left in the temp directory; not worth a red test.
        }
        return ValueTask.CompletedTask;
    }
}
