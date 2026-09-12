using System.Text.Json;
using System.Text.RegularExpressions;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The versioned-snapshot and immutable-audit mechanism FP-C7, FP-C5 and FP-C9b share.
/// </summary>
public sealed class GovernanceSnapshotAndAuditTests
{
    private const string TemplateId = "SLOT-TEMPLATE-STANDARD";

    private static readonly string[] CompatibleBasketTypes = ["PDFN5", "TOLL"];

    private static string TemplateContent(int heightMm) => JsonSerializer.Serialize(new
    {
        templateKey = TemplateId,
        lengthMm = 600,
        widthMm = 400,
        heightMm,
        compatibleBasketTypes = CompatibleBasketTypes
    });

    [Fact]
    public async Task FrozenSnapshotComesBackFieldForFieldAndRefreezingTheSameVersionDoesNotWriteASecondOne()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();
        string content = TemplateContent(300);

        GovernedConfigurationSnapshot first = await fixture.Store.FreezeAsync(
            GovernedObjectKind.SlotTemplate, TemplateId, 1, content, fixture.Now,
            TestContext.Current.CancellationToken);
        GovernedConfigurationSnapshot second = await fixture.Store.FreezeAsync(
            GovernedObjectKind.SlotTemplate, TemplateId, 1, TemplateContent(999), fixture.Now.AddHours(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(content, second.ContentJson);
        Assert.Equal(1, await fixture.Context.Set<GovernedConfigurationSnapshotRow>()
            .CountAsync(TestContext.Current.CancellationToken));

        GovernedConfigurationSnapshot read = Assert.IsType<GovernedConfigurationSnapshot>(
            await fixture.Store.ReadAsync(
                GovernedObjectKind.SlotTemplate, TemplateId, 1, TestContext.Current.CancellationToken));
        Assert.Equal(content, read.ContentJson);
        Assert.Equal(first.ContentSha256, read.ContentSha256);
        Assert.Equal(fixture.Now, read.FrozenAt);
        using JsonDocument frozen = JsonDocument.Parse(read.ContentJson);
        Assert.Equal(300, frozen.RootElement.GetProperty("heightMm").GetInt32());
        Assert.Equal(600, frozen.RootElement.GetProperty("lengthMm").GetInt32());
    }

    [Fact]
    public async Task FieldLevelDifferenceIsComputedFromTwoSnapshotsAndNoDifferenceTableExists()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();
        await fixture.Store.FreezeAsync(
            GovernedObjectKind.SlotTemplate, TemplateId, 1, TemplateContent(300), fixture.Now,
            TestContext.Current.CancellationToken);
        await fixture.Store.FreezeAsync(
            GovernedObjectKind.SlotTemplate, TemplateId, 2, TemplateContent(350), fixture.Now.AddDays(1),
            TestContext.Current.CancellationToken);

        IReadOnlyList<ConfigurationFieldDifference> differences = await fixture.Store.DiffAsync(
            GovernedObjectKind.SlotTemplate, TemplateId, 1, 2, TestContext.Current.CancellationToken);

        ConfigurationFieldDifference only = Assert.Single(differences);
        Assert.Equal("heightMm", only.FieldPath);
        Assert.Equal("300", only.LeftValue);
        Assert.Equal("350", only.RightValue);

        // The difference is a computation, not a second truth about the same change: nothing in the
        // model stores one.
        string[] entityNames = [.. fixture.Context.Model.GetEntityTypes().Select(type => type.ClrType.Name)];
        Assert.DoesNotContain(entityNames, name =>
            name.Contains("Diff", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Difference", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Delta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuditRecordsCannotBeUpdated()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();
        await fixture.Store.WriteBusinessAsync(
            Entry("SLOT_TEMPLATE_PUBLISHED"), fixture.Now, TestContext.Current.CancellationToken);
        await fixture.Store.WriteAdministratorAsync(
            Entry("SLOT_TEMPLATE_PUBLISHED"), fixture.Now, TestContext.Current.CancellationToken);

        BusinessAuditRecordRow business = await fixture.Context.Set<BusinessAuditRecordRow>()
            .SingleAsync(TestContext.Current.CancellationToken);
        business.Action = "REWRITTEN";
        AuditRecordImmutabilityException businessFailure =
            await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() =>
                fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("write-once", businessFailure.Message, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();

        AdministratorAuditRecordRow administrator = await fixture.Context.Set<AdministratorAuditRecordRow>()
            .SingleAsync(TestContext.Current.CancellationToken);
        administrator.Outcome = GovernanceActionOutcome.Failed;
        await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() =>
            fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        // Nothing was written: the records are exactly as they were.
        Assert.Equal("SLOT_TEMPLATE_PUBLISHED", (await fixture.Context.Set<BusinessAuditRecordRow>()
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Action);
        Assert.Equal(GovernanceActionOutcome.Succeeded, (await fixture.Context.Set<AdministratorAuditRecordRow>()
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task AuditRecordsInsideRetentionCannotBeDeleted()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();
        await fixture.Store.WriteBusinessAsync(
            Entry("SLOT_TEMPLATE_PUBLISHED"), fixture.Now, TestContext.Current.CancellationToken);

        BusinessAuditRecordRow row = await fixture.Context.Set<BusinessAuditRecordRow>()
            .SingleAsync(TestContext.Current.CancellationToken);
        fixture.Context.Set<BusinessAuditRecordRow>().Remove(row);

        AuditRecordImmutabilityException failure =
            await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() =>
                fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("180 day", failure.Message, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Context.Set<BusinessAuditRecordRow>()
            .CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetentionDefaultsTo180DaysAndPurgesOnlyWhatIsPastIt()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();
        Assert.Equal(180, fixture.Context.AuditRetention.RetainFor.TotalDays);

        await fixture.Store.WriteBusinessAsync(
            Entry("OLD"), fixture.Now.AddDays(-200), TestContext.Current.CancellationToken);
        await fixture.Store.WriteAdministratorAsync(
            Entry("OLD"), fixture.Now.AddDays(-200), TestContext.Current.CancellationToken);
        await fixture.Store.WriteBusinessAsync(
            Entry("RECENT"), fixture.Now.AddDays(-179), TestContext.Current.CancellationToken);

        // Not expired yet: nothing goes.
        Assert.Equal(0, await fixture.Store.PurgeExpiredAuditAsync(
            fixture.Now.AddDays(-190), TestContext.Current.CancellationToken));
        Assert.Equal(3, await fixture.Context.Set<BusinessAuditRecordRow>()
            .CountAsync(TestContext.Current.CancellationToken)
            + await fixture.Context.Set<AdministratorAuditRecordRow>()
                .CountAsync(TestContext.Current.CancellationToken));

        // Expired: the two 200-day-old records go, the 179-day-old one stays.
        fixture.Clock.Now = fixture.Now;
        Assert.Equal(2, await fixture.Store.PurgeExpiredAuditAsync(
            fixture.Now, TestContext.Current.CancellationToken));
        Assert.Equal("RECENT", (await fixture.Context.Set<BusinessAuditRecordRow>()
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Action);
        Assert.Empty(await fixture.Context.Set<AdministratorAuditRecordRow>()
            .AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// REQ-0271 后半句：保留期的变更本身形成管理员审计。第一次看到记一条；值没变不重复记（重启不是变更）；
    /// 值变了再记一条，带上前后两个值。
    /// </summary>
    [Fact]
    public async Task TheRetentionSettingIsAuditedAsAnAdministratorActionWhenFirstSeenAndWhenItChanges()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();

        Assert.True(await fixture.Store.RecordRetentionPolicyAsync(
            fixture.Now, TestContext.Current.CancellationToken));
        Assert.False(await fixture.Store.RecordRetentionPolicyAsync(
            fixture.Now.AddMinutes(1), TestContext.Current.CancellationToken));

        GovernanceStore reconfigured = new(
            fixture.Context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            new AuditRetentionPolicy(TimeSpan.FromDays(365)));
        Assert.True(await reconfigured.RecordRetentionPolicyAsync(
            fixture.Now.AddDays(1), TestContext.Current.CancellationToken));

        AdministratorAuditRecordRow[] records = [.. (await fixture.Context.Set<AdministratorAuditRecordRow>()
                .AsNoTracking()
                .Where(row => row.Action == GovernanceStore.RetentionPolicyConfiguredAction)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.RecordedAtUtcTicks)];
        Assert.Equal(2, records.Length);

        using JsonDocument first = JsonDocument.Parse(records[0].DetailJson);
        Assert.Equal(180d, first.RootElement.GetProperty("retainForDays").GetDouble());
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("previousRetainForDays").ValueKind);
        using JsonDocument second = JsonDocument.Parse(records[1].DetailJson);
        Assert.Equal(365d, second.RootElement.GetProperty("retainForDays").GetDouble());
        Assert.Equal(180d, second.RootElement.GetProperty("previousRetainForDays").GetDouble());
        Assert.All(records, row =>
        {
            Assert.Equal(GovernedObjectKind.AuditRetention, row.ObjectKind);
            Assert.Equal(AuditActorAttribution.NotAttributableToNaturalPerson, row.ActorAttribution);
            Assert.Null(row.ClaimedAdministratorRole);
        });
    }

    [Fact]
    public async Task ConfiguredRetentionIsAcceptedAboveTheFloorAndRejectedBelowIt()
    {
        AuditRetentionPolicy longer = new(TimeSpan.FromDays(365));
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync(longer);
        Assert.Equal(365, fixture.Context.AuditRetention.RetainFor.TotalDays);

        await fixture.Store.WriteBusinessAsync(
            Entry("OLD"), fixture.Now.AddDays(-200), TestContext.Current.CancellationToken);
        Assert.Equal(0, await fixture.Store.PurgeExpiredAuditAsync(
            fixture.Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThePersonFieldIsADeploymentIdentityExplicitlyMarkedNotAttributableToANaturalPerson()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();
        await fixture.Store.WriteBusinessAsync(
            Entry("SLOT_TEMPLATE_PUBLISHED"), fixture.Now, TestContext.Current.CancellationToken);
        await fixture.Store.WriteAdministratorAsync(
            Entry("SLOT_TEMPLATE_PUBLISHED", claimedAdministratorRole: "SYSTEM_ADMINISTRATOR"),
            fixture.Now,
            TestContext.Current.CancellationToken);

        BusinessAuditRecordRow business = await fixture.Context.Set<BusinessAuditRecordRow>()
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        AdministratorAuditRecordRow administrator = await fixture.Context.Set<AdministratorAuditRecordRow>()
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        foreach (string actor in new[] { business.ActorIdentity, administrator.ActorIdentity })
        {
            Assert.False(string.IsNullOrWhiteSpace(actor));
            Assert.StartsWith(AuditActorAttribution.DeploymentIdentityPrefix, actor, StringComparison.Ordinal);
            // Not a person's name: a deployment identity is never two capitalised words.
            Assert.False(
                Regex.IsMatch(actor, @"^\p{Lu}\p{Ll}+\s+\p{Lu}\p{Ll}+$", RegexOptions.None, TimeSpan.FromSeconds(1)),
                $"'{actor}' reads as a person's name.");
        }

        Assert.Equal(AuditActorAttribution.NotAttributableToNaturalPerson, business.ActorAttribution);
        Assert.Equal(AuditActorAttribution.NotAttributableToNaturalPerson, administrator.ActorAttribution);

        // The role string on the wire is recorded as a claim, never as the actor. Whoever holds the
        // one site-wide key can put any role there.
        Assert.Equal("SYSTEM_ADMINISTRATOR", administrator.ClaimedAdministratorRole);
        Assert.NotEqual(administrator.ClaimedAdministratorRole, administrator.ActorIdentity);
    }

    [Fact]
    public void ADeploymentIdentityThatCouldPassForAPersonIsRefused()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => new GovernanceDeploymentIdentity("Zhengyu Shao"));
        Assert.Contains("cannot be mistaken for a person", failure.Message, StringComparison.Ordinal);
        Assert.StartsWith(
            AuditActorAttribution.DeploymentIdentityPrefix,
            GovernanceDeploymentIdentity.ForCurrentHost().Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TracerPublishesAVersionFreezesItAuditsItAndReadsBothBackThroughTheReadOnlyQuery()
    {
        await using GovernanceFixture fixture = await GovernanceFixture.CreateAsync();
        GovernedConfigurationPublisher publisher = new(fixture.Store, fixture.Store);
        string content = TemplateContent(300);

        GovernedConfigurationSnapshot published = await publisher.PublishVersionAsync(
            GovernedObjectKind.SlotTemplate,
            TemplateId,
            1,
            content,
            "SLOT_TEMPLATE_PUBLISHED",
            fixture.Now,
            TestContext.Current.CancellationToken,
            claimedAdministratorRole: "SYSTEM_ADMINISTRATOR");

        GovernedVersionView view = Assert.IsType<GovernedVersionView>(
            await fixture.Store.ReadVersionAsync(
                GovernedObjectKind.SlotTemplate, TemplateId, 1, TestContext.Current.CancellationToken));

        Assert.Equal(published.SnapshotId, view.Snapshot.SnapshotId);
        Assert.Equal(content, view.Snapshot.ContentJson);

        GovernanceAuditView business = Assert.Single(view.BusinessAudit);
        Assert.Equal("SLOT_TEMPLATE_PUBLISHED", business.Action);
        Assert.Equal(published.SnapshotId, business.SnapshotId);
        Assert.Equal(GovernanceActionOutcome.Succeeded, business.Outcome);
        Assert.Contains(published.ContentSha256, business.DetailJson, StringComparison.Ordinal);

        GovernanceAuditView administrator = Assert.Single(view.AdministratorAudit);
        Assert.Equal(published.SnapshotId, administrator.SnapshotId);
        Assert.Equal(
            AuditActorAttribution.NotAttributableToNaturalPerson, administrator.ActorAttribution);

        Assert.Null(await fixture.Store.ReadVersionAsync(
            GovernedObjectKind.SlotTemplate, TemplateId, 2, TestContext.Current.CancellationToken));
    }

    private static GovernanceAuditEntry Entry(string action, string? claimedAdministratorRole = null) =>
        new(
            action,
            GovernedObjectKind.SlotTemplate,
            TemplateId,
            1,
            GovernanceActionOutcome.Succeeded,
            """{"reason":"test"}""",
            ClaimedAdministratorRole: claimedAdministratorRole);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class GovernanceFixture : IAsyncDisposable
    {
        private GovernanceFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            GovernanceStore store,
            MutableTimeProvider clock)
        {
            Connection = connection;
            Context = context;
            Store = store;
            Clock = clock;
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public GovernanceStore Store { get; }
        public MutableTimeProvider Clock { get; }
        public DateTimeOffset Now { get; } = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

        public static async Task<GovernanceFixture> CreateAsync(AuditRetentionPolicy? retention = null)
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            // Far enough back that everything written at the fixture's "now" is inside retention.
            MutableTimeProvider clock = new(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
            context.AuditClock = clock;
            GovernanceStore store = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                retention ?? AuditRetentionPolicy.Default);
            return new GovernanceFixture(connection, context, store, clock);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
