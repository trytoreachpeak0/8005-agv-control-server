using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 回滚是受控的新激活（REQ-0346），四类敏感生效动作都先给影响预览（REQ-0339 已实施的那一半），
/// 覆盖治理明文排除（REQ-0326）。
/// </summary>
public sealed class RollbackAndImpactPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARollbackIsANewActivationAndTheVersionItSelectedIsNotTouched()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync("AGV-01", model, pulseResetMilliseconds: 500);
        await fixture.PublishBindingsAsync("AGV-01", model, pulseResetMilliseconds: 800);

        string objectId = $"AGV-01:{model}";
        GovernedConfigurationSnapshot before = await fixture.RequireSnapshotAsync(objectId, 1);

        RollbackOutcome outcome = await fixture.Store.RollbackAsync(
            "AGV-01", model, toVersion: 1, Now.AddHours(1), TestContext.Current.CancellationToken);

        // 产出的是一条新的激活记录与一版新的版本号，不是把 v2 改回 v1。
        Assert.Equal(3, outcome.NewVersion);
        Assert.Equal(1, outcome.RolledBackToVersion);
        SlotConfigurationActivationRow activation = await fixture.Context.Set<SlotConfigurationActivationRow>()
            .AsNoTracking()
            .SingleAsync(row => row.ActivationId == outcome.ActivationId, TestContext.Current.CancellationToken);
        Assert.Equal("ROLLBACK", activation.Kind);
        Assert.Equal(1, activation.RolledBackToVersion);

        // 被回滚到的那一版逐字段不变——内容、哈希、冻结时刻、快照 id 全都还是原来的。
        GovernedConfigurationSnapshot after = await fixture.RequireSnapshotAsync(objectId, 1);
        Assert.Equal(before, after);

        // v2 也没有被「撤销」：三版并存，历史是加出来的不是改出来的。
        long[] versions = await fixture.Context.Set<GovernedConfigurationSnapshotRow>().AsNoTracking()
            .Where(row => row.ObjectId == objectId)
            .Select(row => row.Version)
            .OrderBy(version => version)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L, 3L], versions);

        // 新版内容与旧版逐字节相同，并且带着自己的完整快照与审计。
        GovernedConfigurationSnapshot rolled = await fixture.RequireSnapshotAsync(objectId, 3);
        Assert.Equal(before.ContentJson, rolled.ContentJson);
        Assert.Equal(before.ContentSha256, rolled.ContentSha256);
        BusinessAuditRecordRow[] audit = await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == "SENSITIVE_ACTIVATION_ROLLED_BACK")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Single(audit);
        Assert.Equal(rolled.SnapshotId, audit[0].SnapshotId);
    }

    [Fact]
    public async Task ABindingPublishedAfterARollbackTakesTheNextVersionInsteadOfTheRollbacks()
    {
        // 回滚与绑定发布冻结在同一条版本线上：v1、v2 是两次发布，回滚到 v1 得到 v3。之后再发布一次绑定，
        // 它若只按绑定行数版本号会拿到 3，冻结时撞上回滚那一版，拿回的是一份装着回滚内容的快照。
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync("AGV-01", model, pulseResetMilliseconds: 500);
        await fixture.PublishBindingsAsync("AGV-01", model, pulseResetMilliseconds: 800);
        RollbackOutcome rollback = await fixture.Store.RollbackAsync(
            "AGV-01", model, toVersion: 1, Now.AddHours(1), TestContext.Current.CancellationToken);

        IReadOnlyList<SlotIoBindingRow> republished = await fixture.Authority.PublishIoBindingsAsync(
            "AGV-01",
            model,
            [.. ApprovedSlotHardwareFacts.IoBindings.Select(binding => binding with { PulseResetMilliseconds = 900 })],
            Now.AddHours(2),
            TestContext.Current.CancellationToken);

        long version = Assert.Single(republished.Select(row => row.Version).Distinct());
        Assert.Equal(4, version);
        string snapshotId = Assert.Single(republished.Select(row => row.SnapshotId).Distinct())!;
        Assert.NotEqual(rollback.SnapshotId, snapshotId);
        GovernedConfigurationSnapshot frozen = await fixture.RequireSnapshotAsync($"AGV-01:{model}", version);
        Assert.Equal(snapshotId, frozen.SnapshotId);
        Assert.All(
            JsonSerializer.Deserialize<SlotIoBindingSpecification[]>(frozen.ContentJson)!,
            binding => Assert.Equal(900, binding.PulseResetMilliseconds));
    }

    [Fact]
    public async Task ARollbackReachesOnlyTheConsumersWhoseFreezePointIsLaterThanIt()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync("AGV-01", model, pulseResetMilliseconds: 500);
        string objectId = $"AGV-01:{model}";
        GovernedConfigurationSnapshot v1 = await fixture.RequireSnapshotAsync(objectId, 1);

        // 一个消费者在回滚之前就固化了，另一个在回滚之后才固化。
        DateTimeOffset rollbackAt = Now.AddHours(5);
        await fixture.Store.RecordConsumerFreezeAsync(
            "DEMAND", "D-EARLY", GovernedObjectKind.ActiveSlotConfiguration, objectId,
            1, rollbackAt.AddHours(-1), v1.SnapshotId, TestContext.Current.CancellationToken);
        await fixture.Store.RecordConsumerFreezeAsync(
            "DEMAND", "D-LATE", GovernedObjectKind.ActiveSlotConfiguration, objectId,
            1, rollbackAt.AddHours(1), v1.SnapshotId, TestContext.Current.CancellationToken);

        RollbackOutcome outcome = await fixture.Store.RollbackAsync(
            "AGV-01", model, toVersion: 1, rollbackAt, TestContext.Current.CancellationToken);

        Assert.Equal(["D-LATE"], outcome.Impact.Impacted.Select(consumer => consumer.ConsumerId));
        Assert.Equal(["D-EARLY"], outcome.Impact.Unaffected.Select(consumer => consumer.ConsumerId));
        Assert.False(outcome.Impact.HasNoImpactedObject);
    }

    [Fact]
    public async Task AllFourSensitiveActionsPreviewTheirImpactWithTheSameCalculationThatDoesIt()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync("AGV-01", model, pulseResetMilliseconds: 500);
        string objectId = $"AGV-01:{model}";
        GovernedConfigurationSnapshot v1 = await fixture.RequireSnapshotAsync(objectId, 1);

        DateTimeOffset effectiveFrom = Now.AddHours(5);
        await fixture.Store.RecordConsumerFreezeAsync(
            "DEMAND", "D-EARLY", GovernedObjectKind.ActiveSlotConfiguration, objectId,
            1, effectiveFrom.AddHours(-1), v1.SnapshotId, TestContext.Current.CancellationToken);
        await fixture.Store.RecordConsumerFreezeAsync(
            "DEMAND", "D-LATE", GovernedObjectKind.ActiveSlotConfiguration, objectId,
            1, effectiveFrom.AddHours(1), v1.SnapshotId, TestContext.Current.CancellationToken);

        foreach (SensitiveActivationAction action in Enum.GetValues<SensitiveActivationAction>())
        {
            SensitiveActivationRequest request = new(
                action, GovernedObjectKind.ActiveSlotConfiguration, objectId, 1, effectiveFrom);

            ActivationImpactPreview shown = await fixture.Store.PreviewAsync(
                request, TestContext.Current.CancellationToken);
            ActivationImpactPreview applied = await fixture.Store.ApplyAsync(
                request, TestContext.Current.CancellationToken);

            // 给人看的那一份与执行时用的那一份是同一份：同一个入参、同一个方法、逐字段相等。
            Assert.Equal(shown, applied);
            Assert.Equal(["D-LATE"], applied.Impacted.Select(consumer => consumer.ConsumerId));
            Assert.Equal("影响 1 个对象", applied.Statement);
        }

        // 四类动作各留下一条审计，四个动作码互不相同。
        string[] actions = await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action.StartsWith("SENSITIVE_ACTIVATION_"))
            .Select(row => row.Action)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, actions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AnEmptyPreviewStillSaysThereIsNoImpactedObjectRatherThanSayingNothing()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync("AGV-01", model, pulseResetMilliseconds: 500);
        string objectId = $"AGV-01:{model}";

        ActivationImpactPreview preview = await fixture.Store.PreviewAsync(
            new SensitiveActivationRequest(
                SensitiveActivationAction.RemoveActiveBinding,
                GovernedObjectKind.ActiveSlotConfiguration,
                objectId,
                1,
                Now.AddHours(5)),
            TestContext.Current.CancellationToken);

        Assert.True(preview.HasNoImpactedObject);
        Assert.Empty(preview.Impacted);

        // 空不等于没有：预览仍然明说「无影响对象」，而不是静默跳过这一步。
        Assert.Equal("无影响对象", preview.Statement);

        ActivationImpactPreview applied = await fixture.Store.ApplyAsync(
            preview.Request, TestContext.Current.CancellationToken);
        BusinessAuditRecordRow audit = await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .SingleAsync(row => row.Action == "SENSITIVE_ACTIVATION_BINDING_REMOVED",
                TestContext.Current.CancellationToken);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal("无影响对象", detail.RootElement.GetProperty("impactStatement").GetString());
        Assert.True(applied.HasNoImpactedObject);
    }

    [Fact]
    public void NoSharedSecretPosesAsTheFreshSecondFactorAnywhereOnTheActivationPath()
    {
        // REQ-0339 的另一半本期不做，而「不做」的落法是**空着**，不是找个东西顶上。本期唯一的
        // 「认证」是对一个全场共用环境变量做定时安全比较：持有密钥者可以自称任何角色，拿它当新鲜
        // 二次认证只是把一句不成立的话写进审计。这条测试守的就是没有人这么顶过。
        string root = FindRepositoryRoot();
        foreach (string file in new[]
        {
            Path.Combine(root, "src", "ControlServer.Infrastructure", "Persistence", "GovernedActivationStore.cs"),
            Path.Combine(root, "src", "ControlServer.Domain", "ActivationImpactModels.cs")
        })
        {
            string source = File.ReadAllText(file);
            foreach (string forbidden in new[]
            {
                "GetEnvironmentVariable", "FixedTimeEquals", "administratorRole", "sharedSecret", "password"
            })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 整个 src/ 里也没有任何一个「二次认证」形状的标识符——那一半是空的，不是别处实现了。
        foreach (string file in SourceFiles(Path.Combine(root, "src")))
        {
            string code = WithoutComments(File.ReadAllText(file));
            foreach (string forbidden in new[]
            {
                "SecondFactor", "Reauthenticat", "FreshAuthentication", "ConfirmPassword", "CurrentPassword"
            })
            {
                Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void CoverageGovernanceIsExplicitlyExcludedAndHasZeroImplementation()
    {
        // REQ-0326 是一条**否定性需求**：区域覆盖的发现、录入、复核、批准、二次认证、版本、生效、
        // 回滚与审计均不属于当前需求范围。这条测试是那句「本决定明文排除」的机器形态——它不产功能
        // 代码，它证明功能代码确实没有被悄悄写出来。
        string root = FindRepositoryRoot();
        foreach (string file in SourceFiles(Path.Combine(root, "src")))
        {
            string code = WithoutComments(File.ReadAllText(file));
            foreach (string forbidden in new[]
            {
                "AreaCoverage", "CoverageArea", "CoverageVersion", "CoverageApproval", "CoverageReview",
                "CoverageBinding", "CoverageRollback", "CoverageAudit", "CoverageGovernance"
            })
            {
                Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>扫源码时先把注释剔掉：说明「这件事没做」的那句话本身不该把测试判红。</summary>
    private static string WithoutComments(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                    && !line.StartsWith("/*", StringComparison.Ordinal)
                    && !line.StartsWith('*')));

    private static string[] SourceFiles(string root) =>
        [.. Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }

    private sealed class ActivationFixture : IAsyncDisposable
    {
        private ActivationFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            GovernanceStore governance,
            SlotConfigurationAuthorityStore authority,
            GovernedActivationStore store)
        {
            Connection = connection;
            Context = context;
            Governance = governance;
            Authority = authority;
            Store = store;
        }

        private SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public GovernanceStore Governance { get; }

        public SlotConfigurationAuthorityStore Authority { get; }

        public GovernedActivationStore Store { get; }

        public static async Task<ActivationFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            GovernanceStore governance = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            GovernedConfigurationPublisher publisher = new(governance, governance);
            return new ActivationFixture(
                connection,
                context,
                governance,
                new SlotConfigurationAuthorityStore(context, publisher),
                new GovernedActivationStore(context, governance, publisher, governance));
        }

        public async Task<string> PublishApprovedModelAsync() =>
            (await Authority.EnsureApprovedHardwareFactsAsync(Now, TestContext.Current.CancellationToken))
            .SlotModelVersionId;

        public async Task PublishBindingsAsync(string agvId, string model, int pulseResetMilliseconds) =>
            await Authority.PublishIoBindingsAsync(
                agvId,
                model,
                [
                    .. ApprovedSlotHardwareFacts.IoBindings.Select(binding =>
                        binding with { PulseResetMilliseconds = pulseResetMilliseconds })
                ],
                Now,
                TestContext.Current.CancellationToken);

        public async Task<GovernedConfigurationSnapshot> RequireSnapshotAsync(string objectId, long version) =>
            await ((IConfigurationSnapshotStore)Governance).ReadAsync(
                GovernedObjectKind.ActiveSlotConfiguration,
                objectId,
                version,
                TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"No frozen version {version} of {objectId}.");

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
