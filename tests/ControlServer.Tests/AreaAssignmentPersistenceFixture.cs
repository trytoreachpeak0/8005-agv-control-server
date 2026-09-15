using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// A migrated in-memory database with the batch 4 persistence ports wired over it. The schema comes from
/// the migrations, not from <c>EnsureCreated</c>, so these tests exercise the tables the migration builds.
/// </summary>
internal sealed class AreaAssignmentPersistenceFixture : IAsyncDisposable
{
    private AreaAssignmentPersistenceFixture(SqliteConnection connection, ControlServerDbContext context)
    {
        Connection = connection;
        Context = context;
        Governance = new GovernanceStore(
            context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        GovernedConfigurationPublisher publisher = new(Governance, Governance);
        SlotAuthority = new SlotConfigurationAuthorityStore(context, publisher);
        AreaAssignments = new AreaAssignmentStore(context, publisher);
        Freezes = new DemandAreaAssignmentFreezeStore(context);
        Blocks = new StructuralDispatchBlockStore(context);
        SlotPositions = new VehicleSlotPositionReader(context);
    }

    private SqliteConnection Connection { get; }
    public ControlServerDbContext Context { get; }
    public GovernanceStore Governance { get; }
    public SlotConfigurationAuthorityStore SlotAuthority { get; }
    public IAreaAssignmentStore AreaAssignments { get; }
    public IDemandAreaAssignmentFreeze Freezes { get; }
    public IStructuralDispatchBlockStore Blocks { get; }
    public IVehicleSlotPositionReader SlotPositions { get; }

    public static async Task<AreaAssignmentPersistenceFixture> CreateAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
        ControlServerDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return new AreaAssignmentPersistenceFixture(connection, context);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
