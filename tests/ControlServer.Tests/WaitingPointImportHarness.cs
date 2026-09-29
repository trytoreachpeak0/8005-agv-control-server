using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 等待点登记导入（control-server#388）的测试库：一个迁移过的 SQLite 文件，外加一份 26 号图的站点目录——站 214／215／216 是等待点，
/// 210／211／212 是关卡与充电，12 是机台站，305 是派工待送取货站。文件而不是内存库，因为 FieldOps 要另一个进程、并发要两条连接。
/// </summary>
internal sealed class WaitingPointImportHarness : IAsyncDisposable
{
    internal const int Map = 26;

    internal const string VehicleA = "VK-A";

    internal const string VehicleB = "VK-B";

    internal const string Header = "map_id,station_id,station_name,enabled,vehicle_scope";

    internal static readonly string[] Fleet = [VehicleA, VehicleB];

    internal static readonly DateTimeOffset Imported = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    internal static readonly RiotMapStation[] Stations =
    [
        new(12, "N1-3_N1-7"),
        new(210, "关卡"),
        new(211, "充电点1"),
        new(212, "充电准备点1"),
        new(214, "等待点1"),
        new(215, "等待点2"),
        new(216, "等待点3"),
        new(305, "派工待送取货"),
    ];

    private WaitingPointImportHarness(string directory)
    {
        Directory = directory;
        DatabasePath = Path.Combine(directory, "controlserver.db");
        CatalogPath = Path.Combine(directory, "catalog-26.json");
    }

    public string Directory { get; }

    public string DatabasePath { get; }

    public string CatalogPath { get; }

    public static RiotMapStationCatalogSnapshot Catalog(int mapId = Map) =>
        TaskTypeStationCatalogEvidence.Supplied(mapId, Stations, Imported);

    public static async Task<WaitingPointImportHarness> CreateAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-waiting-points-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        WaitingPointImportHarness harness = new(directory);
        await using (ControlServerDbContext context = harness.Open())
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        File.WriteAllText(
            harness.CatalogPath,
            JsonSerializer.Serialize(new
            {
                mapId = Map,
                stations = Stations.Select(station => new { stationId = station.StationId, stationName = station.StationName })
            }),
            new UTF8Encoding(false));
        return harness;
    }

    public static string Csv(params string[] rows) => string.Join("\n", [Header, .. rows]) + "\n";

    public string WriteCsv(string name, string csv)
    {
        string path = Path.Combine(Directory, name);
        File.WriteAllText(path, csv, new UTF8Encoding(false));
        return path;
    }

    public ControlServerDbContext Open(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false))
            .AddInterceptors(interceptors)
            .Options);

    public static GovernedConfigurationPublisher Publisher(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
        return new GovernedConfigurationPublisher(governance, governance);
    }

    public static WaitingPointImportService Service(ControlServerDbContext context)
    {
        GovernedConfigurationPublisher publisher = Publisher(context);
        return new WaitingPointImportService(
            new WaitingPointRegistry(context, publisher),
            new StationExclusivityStore(context),
            new WaitingPointImportFacts(new TaskTypeStationBindingStore(context, publisher), new CatalogAvailabilityStore(context)));
    }

    /// <summary>一次导入，在自己的上下文与写事务里，像 FieldOps 每跑一次是一个新进程那样。</summary>
    public async Task<WaitingPointImportResult> ImportAsync(
        string csv,
        bool dryRun = false,
        IReadOnlyList<string>? fleet = null,
        RiotMapStationCatalogSnapshot? catalog = null,
        int mapId = Map)
    {
        await using ControlServerDbContext context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        WaitingPointImportResult result = await Service(context).ImportAsync(
            new WaitingPointImportRequest(csv, mapId, fleet ?? Fleet, catalog ?? Catalog()),
            dryRun,
            Imported,
            TestContext.Current.CancellationToken);
        if (!dryRun && result.Outcome == WaitingPointImportOutcome.Accepted)
        {
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        return result;
    }

    public async Task<WaitingPointRegistrationVersion?> ReadCurrentAsync()
    {
        await using ControlServerDbContext context = Open();
        return await new WaitingPointRegistry(context, Publisher(context)).ReadCurrentAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>站点独占：<paramref name="vehicleKey"/> 的旅程以当前登记版本预占 <paramref name="stationId"/>。</summary>
    public async Task ReserveAsync(int stationId, string vehicleKey, string journeyId, long version)
    {
        await using ControlServerDbContext context = Open();
        StationExclusivityAcquisitionOutcome outcome = await new StationExclusivityStore(context).TryAcquireAsync(
            new StationExclusivityRequest(Map, stationId, StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Reserved, version),
            vehicleKey,
            journeyId,
            Imported,
            TestContext.Current.CancellationToken);
        Assert.Equal(StationExclusivityAcquisitionOutcome.Acquired, outcome);
    }

    public async Task<StationExclusivity?> ReadExclusivityAsync(int stationId)
    {
        await using ControlServerDbContext context = Open();
        return await new StationExclusivityStore(context).ReadAsync(Map, stationId, TestContext.Current.CancellationToken);
    }

    /// <summary>该图一版生效的任务类型绑定：派工待送取货站 305 是 <c>STAGING_TO_WIRE</c> 的固定站。</summary>
    public async Task BindStagingStationAsync()
    {
        await using ControlServerDbContext context = Open();
        GovernedConfigurationPublisher publisher = Publisher(context);
        TaskTypeStationRuleStore rules = new(context, publisher);
        TaskTypeStationBindingStore bindings = new(context, publisher);
        TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> rule = await rules.WriteVersionAsync(
            TaskTypeStationTestData.SixRules, TaskTypeStationTestData.Source, Imported, TestContext.Current.CancellationToken);
        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> set = await bindings.WriteVersionAsync(
            Map,
            rule.Version.Version,
            [TransportTaskTypes.StagingToWire],
            [TaskTypeStationTestData.StagingBinding],
            null,
            TaskTypeStationTestData.Source,
            Imported,
            TestContext.Current.CancellationToken);
        await bindings.SetActiveAsync(Map, set.Version.Version, Imported, TestContext.Current.CancellationToken);
    }

    /// <summary>登记在库里留下的全部痕迹：版本行、等待点行、白名单行、治理快照、导入审计。要么一起有，要么一起没有。</summary>
    public async Task<(long Versions, long Points, long Scopes, long Snapshots, long Audits)> FootprintAsync()
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: true));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return (
            await ScalarAsync(connection, "SELECT COUNT(*) FROM WaitingPointVersions"),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM WaitingPoints"),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM WaitingPointVehicleScopes"),
            await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM GovernedConfigurationSnapshots WHERE ObjectKind = '{GovernedObjectKind.WaitingPointRegistration}'"),
            await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{WaitingPointGovernance.VersionImportedAction}'"));
    }

    internal static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return ValueTask.CompletedTask;
    }
}
