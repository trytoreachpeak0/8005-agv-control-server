using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// A migrated in-memory database with the batch 6 persistence ports wired over it. The schema comes from the
/// migrations, not from <c>EnsureCreated</c>, so these tests exercise the tables the migration builds.
/// </summary>
internal sealed class TaskTypeStationPersistenceFixture : IAsyncDisposable
{
    private TaskTypeStationPersistenceFixture(SqliteConnection connection, ControlServerDbContext context)
    {
        Connection = connection;
        Context = context;
        Governance = new GovernanceStore(
            context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        GovernedConfigurationPublisher publisher = new(Governance, Governance);
        Rules = new TaskTypeStationRuleStore(context, publisher);
        Bindings = new TaskTypeStationBindingStore(context, publisher);
        Holds = new TaskTypeStationHoldStore(context);
        CatalogChanges = new TaskTypeStationCatalogChangeStore(context);
        Freezes = new DemandTaskTypeStationFreezeStore(context);
    }

    public SqliteConnection Connection { get; }
    public ControlServerDbContext Context { get; }
    public GovernanceStore Governance { get; }
    public ITaskTypeStationRuleStore Rules { get; }
    public ITaskTypeStationBindingStore Bindings { get; }
    public ITaskTypeStationHoldStore Holds { get; }
    public ITaskTypeStationCatalogChangeStore CatalogChanges { get; }
    public IDemandTaskTypeStationFreeze Freezes { get; }

    public static async Task<TaskTypeStationPersistenceFixture> CreateAsync(params IInterceptor[] interceptors)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (ControlServerDbContext migrator = new(
                         new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options))
        {
            await migrator.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptors)
            .Options;
        return new TaskTypeStationPersistenceFixture(connection, new ControlServerDbContext(options));
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

/// <summary>The rules and bindings the factory preset ships, as test data.</summary>
internal static class TaskTypeStationTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

    public const string Source = "preset:test";

    public static readonly TaskTypeStationRule[] SixRules =
    [
        new(TransportTaskTypes.DieToWireStaging, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.DieToOven, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.WireToGate, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.WireToOptical, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.StagingToWire, TaskTypeFixedEnd.Origin),
        new(TransportTaskTypes.WireToNitrogen, TaskTypeFixedEnd.Destination),
    ];

    public static readonly TaskTypeStationBinding GateBinding =
        new(TransportTaskTypes.WireToGate, 210, "关卡", "MAP-25-WIRE_TO_GATE-20260827");

    public static readonly TaskTypeStationBinding StagingBinding =
        new(TransportTaskTypes.StagingToWire, 305, "派工待送取货", "SITE-CHECK-STAGING");
}
