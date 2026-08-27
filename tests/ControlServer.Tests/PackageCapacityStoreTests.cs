using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed class PackageCapacityStoreTests
{
    private const string SeedSource = "客户提供花篮容量对照表（2026-07-16迁移）";

    [Fact]
    public async Task SeedContainsTwentySevenExactRulesAndApprovedTollPrefixWithSourceAttribution()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        PackageCapacityRuleRow[] rules = await fixture.Context.PackageCapacityRules
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(28, rules.Length);
        Assert.Equal(27, rules.Count(rule => rule.MatchType == "exact"));
        PackageCapacityRuleRow prefix = Assert.Single(rules, rule => rule.MatchType == "prefix");
        Assert.Equal("TOLL-", prefix.Pattern);
        Assert.Equal(3, prefix.MaxBoxesPerBasket);
        Assert.All(rules, rule => Assert.Equal(SeedSource, rule.Source));

        PackageCapacityStore store = new(fixture.Context);
        Assert.Equal(4, await store.ResolveAndTrackAsync(
            "PDFN5×6-8L(12R)", fixture.Now, TestContext.Current.CancellationToken));
        Assert.Equal(3, await store.ResolveAndTrackAsync(
            "TOLL-CUSTOM", fixture.Now, TestContext.Current.CancellationToken));
        Assert.Null(await store.ResolveAndTrackAsync(
            "toll-CUSTOM", fixture.Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownPackageIsDeduplicatedByExactPackageAndUpdatesOnlyItsTrackingFields()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        PackageCapacityStore store = new(fixture.Context);
        DateTimeOffset later = fixture.Now.AddMinutes(5);

        Assert.Null(await store.ResolveAndTrackAsync(
            "UNKNOWN-PACKAGE", fixture.Now, TestContext.Current.CancellationToken));
        MissingPackageRow firstObservation = await fixture.Context.MissingPackages.AsNoTracking().SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("PENDING", firstObservation.Status);
        Assert.Null(await store.ResolveAndTrackAsync(
            "UNKNOWN-PACKAGE", later, TestContext.Current.CancellationToken));

        MissingPackageRow row = await fixture.Context.MissingPackages.AsNoTracking().SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN-PACKAGE", row.Package);
        Assert.Equal(fixture.Now, row.FirstSeenAt);
        Assert.Equal(later, row.LastSeenAt);
        Assert.Equal("PENDING", row.Status);
        Assert.Equal(
            ["FirstSeenAt", "LastSeenAt", "Package", "Status"],
            typeof(MissingPackageRow).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SuppliedCapacityMarksExistingMissingPackageSuppliedAndPreservesHistory()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        PackageCapacityStore store = new(fixture.Context);
        DateTimeOffset suppliedAt = fixture.Now.AddDays(1);
        Assert.Null(await store.ResolveAndTrackAsync(
            "CUSTOM-PACKAGE", fixture.Now, TestContext.Current.CancellationToken));

        PackageCapacityImportResult import = await new PackageCapacityImportService(fixture.Context).ImportAsync(
            [new PackageCapacityImportRule("CUSTOM-PACKAGE", "exact", 5, "客户补充数据")],
            2,
            suppliedAt,
            TestContext.Current.CancellationToken);
        int? capacity = await store.ResolveAndTrackAsync(
            "CUSTOM-PACKAGE", suppliedAt, TestContext.Current.CancellationToken);

        Assert.Equal(new PackageCapacityImportResult(1, 0, 0), import);
        Assert.Equal(5, capacity);
        MissingPackageRow missing = await fixture.Context.MissingPackages.AsNoTracking().SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Now, missing.FirstSeenAt);
        Assert.Equal(suppliedAt, missing.LastSeenAt);
        Assert.Equal("SUPPLIED", missing.Status);
    }

    [Fact]
    public async Task ImportIsIdempotentButConflictingCapacityRequiresHigherVersionAndPreservesOldRule()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        PackageCapacityImportService importer = new(fixture.Context);
        PackageCapacityImportRule unchanged = new("TO-126", "exact", 8, SeedSource);

        PackageCapacityImportResult idempotent = await importer.ImportAsync(
            [unchanged], 1, fixture.Now, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(
            [unchanged with { MaxBoxesPerBasket = 9 }],
            1,
            fixture.Now,
            TestContext.Current.CancellationToken));
        PackageCapacityImportResult updated = await importer.ImportAsync(
            [unchanged with { MaxBoxesPerBasket = 9, Source = "客户补充数据" }],
            2,
            fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(new PackageCapacityImportResult(0, 1, 0), idempotent);
        Assert.Equal(new PackageCapacityImportResult(1, 0, 1), updated);
        PackageCapacityRuleRow[] history = await fixture.Context.PackageCapacityRules.AsNoTracking()
            .Where(rule => rule.Pattern == "TO-126" && rule.MatchType == "exact")
            .OrderBy(rule => rule.Version)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, history.Length);
        Assert.Equal(8, history[0].MaxBoxesPerBasket);
        Assert.Equal(fixture.Now, history[0].SupersededAt);
        Assert.Equal(9, history[1].MaxBoxesPerBasket);
        Assert.Null(history[1].SupersededAt);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public DateTimeOffset Now { get; } = new(2026, 8, 27, 2, 0, 0, TimeSpan.Zero);

        public static async Task<StoreFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new StoreFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
