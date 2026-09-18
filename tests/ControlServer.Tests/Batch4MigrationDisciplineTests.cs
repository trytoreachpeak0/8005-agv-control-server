using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 批次 4 只有一个迁移，由建表票 control-server#66 独占；批次 4 其余票零迁移。
/// </summary>
/// <remarks>
/// 与 <see cref="Batch3MigrationDisciplineTests"/> 同一个理由：模型快照是 EF 整文件重写的单个文件，两张票各加一个
/// 迁移就是硬冲突，解错了会静默丢表而不是报错。批次 3 那份测试的 <c>MigrationsAfterBatch3</c> 名单也点了本迁移的名，
/// 这里只放批次 4 自己的断言，不去改批次 5 的测试文件（规格 19.7）。
/// </remarks>
public sealed class Batch4MigrationDisciplineTests
{
    private const string MigrationBeforeBatch4 = "20260913131725_StationDepartureWait";
    private const string Batch4Migration = "20260915135043_Batch4AreaAssignmentAndStructuralDispatchBlock";

    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] Batch4Tables =
    [
        "DispatchZoneAreaAssignmentVersions",
        "DispatchZoneAreaAssignments",
        "StructuralDispatchBlocks",
    ];

    [Fact]
    public async Task Batch4AddsExactlyOneMigrationAndItComesStraightAfterTheLastBatch2CloseOutMigration()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string batch4 = Assert.Single(migrations, name => name.Contains("Batch4", StringComparison.Ordinal));
        Assert.Equal(Batch4Migration, batch4);
        Assert.Equal(MigrationBeforeBatch4, migrations[Array.IndexOf(migrations, batch4) - 1]);
    }

    [Fact]
    public async Task MigratingAnEmptyDatabaseCreatesEveryTableBatch4Needs()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        HashSet<string> tables = await ReadTableNamesAsync(fixture.Connection);
        foreach (string table in Batch4Tables)
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public async Task EveryBatch4TableIsReachableWithoutADbSetPropertyOnTheSharedContextFile()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<DispatchZoneAreaAssignmentRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<StructuralDispatchBlockRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheModelSnapshotMatchesTheModel()
    {
        // A migration generated but a snapshot left stale -- or the reverse, after a bad merge -- shows
        // up here, not at the next ticket's `dotnet ef migrations add`.
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task EveryBatch4PortIsRegisteredWithTheGovernanceModuleSoNoLaterTicketEditsIt()
    {
        // control-server#68, #70, #72 and #74 each call one of these ports. Registered here, once, none of
        // the four has to touch GovernanceModule.cs -- the same reason the tables all land in one migration.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        ServiceCollection services = new();
        services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connection));
        services.AddGovernance(new ConfigurationBuilder().Build());
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.IsType<AreaAssignmentStore>(scope.ServiceProvider.GetRequiredService<IAreaAssignmentStore>());
        Assert.IsType<DemandAreaAssignmentFreezeStore>(
            scope.ServiceProvider.GetRequiredService<IDemandAreaAssignmentFreeze>());
        Assert.IsType<StructuralDispatchBlockStore>(
            scope.ServiceProvider.GetRequiredService<IStructuralDispatchBlockStore>());
        Assert.IsType<VehicleSlotPositionReader>(scope.ServiceProvider.GetRequiredService<IVehicleSlotPositionReader>());
    }

    [Fact]
    public async Task SlotRowsThatSayLeftAndRightComeOutAsFrontAndRearWithEveryOtherColumnUnchanged()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.MigrateToAsync(MigrationBeforeBatch4);
        Batch3Vocabulary seeded = await SeedBatch3VocabularyAsync(fixture);
        string[] otherColumnsBefore = await DumpAsync(
            fixture.Connection, "SlotModelSlots", "SlotModelVersionId, PhysicalSlotNumber", "SlotPosition");

        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["FRONT", "FRONT", "FRONT", "FRONT", "REAR", "REAR", "REAR", "REAR"],
            await ReadSlotPositionsAsync(fixture.Connection, seeded.EightSlotModelVersionId));
        // Only the two old words are renamed. Anything else a model says is left exactly as it was.
        Assert.Equal(["MIDDLE"], await ReadSlotPositionsAsync(fixture.Connection, seeded.OtherModelVersionId));
        Assert.Equal(
            otherColumnsBefore,
            await DumpAsync(
                fixture.Connection, "SlotModelSlots", "SlotModelVersionId, PhysicalSlotNumber", "SlotPosition"));
    }

    [Fact]
    public async Task FrozenSnapshotsAndAuditRecordsAreByteForByteTheSameAcrossTheRename()
    {
        // REQ-0271: snapshots and audit are not rewritable. The rename is the same physical fact under a
        // new term, so history keeps the words it was published with -- LEFT and RIGHT stay in there.
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.MigrateToAsync(MigrationBeforeBatch4);
        await SeedBatch3VocabularyAsync(fixture);
        string[] snapshotsBefore = await DumpAsync(fixture.Connection, "GovernedConfigurationSnapshots", "SnapshotId");
        string[] businessBefore = await DumpAsync(fixture.Connection, "BusinessAuditRecords", "AuditRecordId");
        string[] administratorBefore =
            await DumpAsync(fixture.Connection, "AdministratorAuditRecords", "AuditRecordId");
        Assert.Contains(snapshotsBefore, row => row.Contains("LEFT", StringComparison.Ordinal));
        Assert.NotEmpty(businessBefore);
        Assert.Contains(administratorBefore, row => row.Contains("RIGHT", StringComparison.Ordinal));

        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(snapshotsBefore, await DumpAsync(fixture.Connection, "GovernedConfigurationSnapshots", "SnapshotId"));
        Assert.Equal(businessBefore, await DumpAsync(fixture.Connection, "BusinessAuditRecords", "AuditRecordId"));
        Assert.Equal(
            administratorBefore,
            await DumpAsync(fixture.Connection, "AdministratorAuditRecords", "AuditRecordId"));
    }

    private sealed record Batch3Vocabulary(string EightSlotModelVersionId, string OtherModelVersionId);

    /// <summary>
    /// A database as batch 3 left it: the eight-slot model published with LEFT and RIGHT, its snapshot and
    /// audit, an administrator audit that quotes the old words, and one unrelated model.
    /// </summary>
    private static async Task<Batch3Vocabulary> SeedBatch3VocabularyAsync(MigrationFixture fixture)
    {
        GovernanceStore governance = new(
            fixture.Context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        SlotConfigurationAuthorityStore store = new(
            fixture.Context, new GovernedConfigurationPublisher(governance, governance));

        SlotTemplateRow template = await store.PublishTemplateVersionAsync(
            ApprovedSlotHardwareFacts.TemplateKey,
            new SlotTemplateSpecification(600, 400, 300, ["PDFN5", "TOLL"]),
            Now,
            TestContext.Current.CancellationToken);
        SlotModelVersionRow eightSlot = await store.PublishModelVersionAsync(
            ApprovedSlotHardwareFacts.ModelKey,
            [
                .. Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount).Select(number =>
                    new SlotModelSlotSpecification(
                        number, number <= 4 ? "LEFT" : "RIGHT", template.TemplateKey, template.Version))
            ],
            Now,
            TestContext.Current.CancellationToken);
        SlotModelVersionRow other = await store.PublishModelVersionAsync(
            "unrelated-one-slot",
            [new SlotModelSlotSpecification(1, "MIDDLE", template.TemplateKey, template.Version)],
            Now,
            TestContext.Current.CancellationToken);
        await governance.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                "SLOT_MODEL_REVIEWED",
                GovernedObjectKind.SlotModelVersion,
                ApprovedSlotHardwareFacts.ModelKey,
                1,
                GovernanceActionOutcome.Succeeded,
                """{"slot1":"LEFT","slot5":"RIGHT"}"""),
            Now,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        return new Batch3Vocabulary(eightSlot.SlotModelVersionId, other.SlotModelVersionId);
    }

    private static async Task<string[]> ReadSlotPositionsAsync(SqliteConnection connection, string slotModelVersionId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SlotPosition FROM SlotModelSlots WHERE SlotModelVersionId = $id ORDER BY PhysicalSlotNumber";
        command.Parameters.AddWithValue("$id", slotModelVersionId);
        List<string> positions = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            positions.Add(reader.GetString(0));
        }
        return [.. positions];
    }

    /// <summary>
    /// Every row of a table as SQLite's own literal for each column (<c>quote()</c>), so two dumps are equal
    /// only when every stored value is the same bytes of the same storage class.
    /// </summary>
    private static async Task<string[]> DumpAsync(
        SqliteConnection connection,
        string table,
        string orderBy,
        params string[] exceptColumns)
    {
        List<string> columns = [];
        await using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info('{table}')";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                string name = reader.GetString(1);
                if (!exceptColumns.Contains(name, StringComparer.Ordinal))
                {
                    columns.Add(name);
                }
            }
        }

        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} "
            + $"FROM \"{table}\" ORDER BY {orderBy}";
        List<string> rows = [];
        await using SqliteDataReader rowsReader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rowsReader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(rowsReader.GetString(0));
        }
        return [.. rows];
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        HashSet<string> tables = new(StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
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

        public SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }

        public static async Task<MigrationFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            return new MigrationFixture(connection, new ControlServerDbContext(options));
        }

        public Task MigrateToAsync(string migration) =>
            Context.GetService<IMigrator>().MigrateAsync(migration, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
