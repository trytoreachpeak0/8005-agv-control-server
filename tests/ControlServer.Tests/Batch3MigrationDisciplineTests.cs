using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// Batch 3 creates every table it needs in one migration, and no later ticket in the batch adds a
/// second one.
/// </summary>
/// <remarks>
/// <c>ControlServerDbContextModelSnapshot.cs</c> is a single file EF rewrites in full, so two
/// tickets each adding a migration is a hard conflict -- and resolving such a conflict wrongly drops
/// tables silently rather than failing. This test is the guard: it names the migration batch 3 is
/// allowed to have, and it fails the moment a second one appears.
/// </remarks>
public sealed class Batch3MigrationDisciplineTests
{
    private const string Batch3Migration = "20260909124757_Batch3GovernanceAndSlotConfiguration";

    private static readonly string[] Batch3Tables =
    [
        "ActiveSlotConfigurations",
        "AdministratorAuditRecords",
        "AgvArchives",
        "AgvLifecycles",
        "AgvRestorationAttempts",
        "BusinessAuditRecords",
        "ConfigurationConsumerBindings",
        "GovernedConfigurationSnapshots",
        "OnboardAlarmSnapshots",
        "SlotConfigurationActivations",
        "SlotConfigurationReadiness",
        "SlotConfigurationVerifications",
        "SlotIoBindings",
        "SlotModelSlots",
        "SlotModelVersions",
        "SlotTemplates",
    ];

    [Fact]
    public async Task Batch3AddsExactlyOneMigrationAndItIsTheLastOne()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        Assert.Equal(Batch3Migration, migrations[^1]);
        Assert.Single(migrations, name => name.Contains("Batch3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOneMigrationCreatesEverySlotConfigurationGovernanceAndDashboardTableBatch3Needs()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        HashSet<string> tables = await ReadTableNamesAsync(fixture.Context);
        foreach (string table in Batch3Tables)
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public async Task EveryBatch3TableIsReachableWithoutADbSetPropertyOnTheSharedContextFile()
    {
        // The point of the batch 3 preparation: a new table is a new configuration file, and the
        // shared ControlServerDbContext.cs is not edited to add one. Set<T>() is how they are read.
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.Set<SlotTemplateRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<OnboardAlarmSnapshotRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<ConfigurationConsumerBindingRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ConfiguredAuditRetentionBelowTheHundredAndEightyDayFloorIsRefusedAtStartup()
    {
        IConfiguration tooShort = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GovernanceModule.RetentionDaysKey] = "90"
            })
            .Build();
        InvalidDataException failure =
            Assert.Throws<InvalidDataException>(() => GovernanceModule.ResolveRetentionPolicy(tooShort));
        Assert.Contains("REQ-0271", failure.Message, StringComparison.Ordinal);

        IConfiguration longer = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GovernanceModule.RetentionDaysKey] = "365"
            })
            .Build();
        Assert.Equal(365, GovernanceModule.ResolveRetentionPolicy(longer).RetainFor.TotalDays);

        IConfiguration empty = new ConfigurationBuilder().Build();
        Assert.Equal(
            AuditRetentionPolicy.Default.RetainFor,
            GovernanceModule.ResolveRetentionPolicy(empty).RetainFor);
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(ControlServerDbContext context)
    {
        HashSet<string> tables = new(StringComparer.Ordinal);
        await using SqliteCommand command = (SqliteCommand)context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private sealed class MigrationFixture : IAsyncDisposable
    {
        private MigrationFixture(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }

        public static async Task<MigrationFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            return new MigrationFixture(connection, new ControlServerDbContext(options));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
