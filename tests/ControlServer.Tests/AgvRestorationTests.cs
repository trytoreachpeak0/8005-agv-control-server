using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// AGV 归档恢复最小闭环（<c>FP-C5</c>）：只能恢复原车、恢复不等于可以干活、四种结局各留审计。
/// </summary>
public sealed class AgvRestorationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private const string Binding = """{"deviceKey":"AGV-01-KEY"}""";

    [Fact]
    public async Task RestorationReusesTheOriginalAgvIdAndASecondArchiveForTheSameCarIsRefused()
    {
        await using RestorationFixture fixture = await RestorationFixture.CreateAsync();
        await fixture.Store.ArchiveAsync(
            "AGV-01", "退役大修", "fp-1", Now, TestContext.Current.CancellationToken);

        AgvRestorationAttemptRow attempt = await fixture.Store.RestoreAsync(
            "AGV-01", "ATT-1", Binding, Now.AddDays(1), TestContext.Current.CancellationToken);
        Assert.Equal("AGV-01", attempt.AgvId);
        Assert.Equal(2, attempt.RestoredLifecycleGeneration);

        // 同一实体车不会有第二份档案：档案表以 agvId 为主键，第二份在形状上就不存在。
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.ArchiveAsync(
                "AGV-01", "又一次归档", null, Now.AddDays(2), TestContext.Current.CancellationToken));
        Assert.Contains("exactly one, ever", refused.Message, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Context.Set<AgvArchiveRow>()
            .CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestorationCompletesWhileTheVehicleIsOfflineAndEstablishesTheCandidateBindingAtomically()
    {
        await using RestorationFixture fixture = await RestorationFixture.CreateAsync();
        await fixture.Store.ArchiveAsync(
            "AGV-01", "退役大修", null, Now, TestContext.Current.CancellationToken);

        // 库里没有任何该车在线的痕迹——恢复只要求原 agvId 可用。
        Assert.Empty(await fixture.Context.SessionRecoveries.ToArrayAsync(
            TestContext.Current.CancellationToken));
        await fixture.Store.RestoreAsync(
            "AGV-01", "ATT-1", Binding, Now.AddDays(1), TestContext.Current.CancellationToken);

        AgvLifecycleRow lifecycle = await fixture.Context.Set<AgvLifecycleRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.False(lifecycle.Archived);
        Assert.Equal(2, lifecycle.LifecycleGeneration);
        Assert.Equal(Binding, lifecycle.CandidateRiotBindingJson);
    }

    [Fact]
    public async Task RestoringACarThatWasNeverArchivedLeavesNothingBehind()
    {
        await using RestorationFixture fixture = await RestorationFixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.RestoreAsync(
            "AGV-99", "ATT-1", Binding, Now, TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        // 新生命周期与候选绑定同成同败：一个都没有留下来。
        Assert.Empty(await fixture.Context.Set<AgvLifecycleRow>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<AgvRestorationAttemptRow>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARestoredVehicleIsNotBusinessAvailableAndNothingCanSetThatDirectly()
    {
        await using RestorationFixture fixture = await RestorationFixture.CreateAsync();
        await fixture.Store.ArchiveAsync(
            "AGV-01", "退役大修", null, Now, TestContext.Current.CancellationToken);
        await fixture.Store.RestoreAsync(
            "AGV-01", "ATT-1", Binding, Now.AddDays(1), TestContext.Current.CancellationToken);

        AgvLifecycleFacts facts = await fixture.Store.ReadLifecycleFactsAsync(
            "AGV-01", TestContext.Current.CancellationToken);
        Assert.False(facts.Archived);
        Assert.False(facts.Commissioned);

        AgvGenerationEvidence complete = new(true, true, true);
        Assert.False(BusinessAvailability.IsAvailable(facts, complete));
        Assert.Contains(BusinessAvailabilityBlocker.NotCommissioned, BusinessAvailability.Evaluate(facts, complete));

        // 业务可用性是派生量：这个类型没有任何可写状态，也没有 setter 可以直接置真。
        Assert.Empty(typeof(BusinessAvailability).GetProperties());
        Assert.Empty(typeof(BusinessAvailability).GetFields());
    }

    [Theory]
    [InlineData(null, null, null)]           // 证据不完整
    [InlineData(true, null, true)]           // 有一项还不知道
    [InlineData(true, false, true)]          // 有一项是否定的
    public void IncompleteOrNegativeGenerationEvidenceKeepsTheVehicleUnavailable(
        bool? riot, bool? readiness, bool? safety)
    {
        AgvLifecycleFacts commissioned = new("AGV-01", 2, Archived: false, Commissioned: true, HasCandidateRiotBinding: true);
        Assert.False(BusinessAvailability.IsAvailable(commissioned, new AgvGenerationEvidence(riot, readiness, safety)));
        // 完整且全部为正时才通过——这就是「完整、正面」的全部含义。
        Assert.True(BusinessAvailability.IsAvailable(
            commissioned, new AgvGenerationEvidence(true, true, true)));
    }

    [Theory]
    [InlineData(GovernanceActionOutcome.Succeeded)]
    [InlineData(GovernanceActionOutcome.Failed)]
    [InlineData(GovernanceActionOutcome.TimedOut)]
    [InlineData(GovernanceActionOutcome.ResultUnknown)]
    public async Task EachOfTheFourOutcomesWritesOneImmutableAuditCarryingAttemptIdAgvIdAndArchiveReason(
        GovernanceActionOutcome outcome)
    {
        await using RestorationFixture fixture = await RestorationFixture.CreateAsync();
        await fixture.Store.ArchiveAsync(
            "AGV-01", "退役大修", null, Now, TestContext.Current.CancellationToken);

        AgvRestorationAttemptRow attempt = outcome == GovernanceActionOutcome.Succeeded
            ? await fixture.Store.RestoreAsync(
                "AGV-01", "ATT-1", Binding, Now.AddDays(1), TestContext.Current.CancellationToken)
            : await fixture.Store.RecordUnsuccessfulAttemptAsync(
                "AGV-01", "ATT-1", outcome, Now.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal(outcome, attempt.Outcome);
        BusinessAuditRecordRow audit = await fixture.Context.Set<BusinessAuditRecordRow>()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(attempt.AuditRecordId, audit.AuditRecordId);
        Assert.Equal(outcome, audit.Outcome);
        // DetailJson 里的中文被 System.Text.Json 默认转义成 \uXXXX，所以按字段读回来比对，
        // 而不是在原始串里找子串。
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal("ATT-1", detail.RootElement.GetProperty("restorationAttemptId").GetString());
        Assert.Equal("AGV-01", detail.RootElement.GetProperty("agvId").GetString());
        Assert.Equal("退役大修", detail.RootElement.GetProperty("archiveReason").GetString());

        // 不可改写：改一条恢复审计被拒。改成一个**确实不同**的值，否则 EF 根本不会把它标成
        // Modified，测试就会在什么都没发生的情况下「通过」。
        audit.Outcome = outcome == GovernanceActionOutcome.Succeeded
            ? GovernanceActionOutcome.Failed
            : GovernanceActionOutcome.Succeeded;
        await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() =>
            fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();
    }

    [Fact]
    public void TheRestorationEntryPointExistsNeitherOnTheDashboardNorOnTheOnboardSide()
    {
        // 恢复入口只在受控运维流程里。这条守卫扫的是「有没有一条可以从外面走进来的路」：
        // 没有任何 HTTP 端点映射恢复，也没有任何协议消息分支通向它。
        string root = FindRepositoryRoot();
        foreach (string file in SourceFiles(Path.Combine(root, "src")))
        {
            string source = File.ReadAllText(file);
            foreach (string mapping in new[] { "MapPost", "MapPut", "MapDelete", "MapGet" })
            {
                int index = source.IndexOf(mapping, StringComparison.Ordinal);
                while (index >= 0)
                {
                    string window = source.Substring(index, Math.Min(240, source.Length - index));
                    Assert.DoesNotContain("restor", window, StringComparison.OrdinalIgnoreCase);
                    index = source.IndexOf(mapping, index + mapping.Length, StringComparison.Ordinal);
                }
            }
        }

        // 车载端消息处理不认识恢复 store。
        string processor = File.ReadAllText(
            Path.Combine(root, "src", "ControlServer.Host", "Transport", "OnboardMessageProcessor.cs"));
        Assert.DoesNotContain(nameof(AgvRestorationStore), processor, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestorationPathContainsNoSharedEnvironmentVariableSecretPosingAsIdentity()
    {
        // 本期唯一的「认证」是对一个全场共用环境变量做定时安全比较，持有密钥者可以自称任何角色。
        // 恢复路径宁可空着这一半，也不拿它冒充身份核验。
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "ControlServer.Infrastructure", "Persistence", "AgvRestorationStore.cs"));
        Assert.DoesNotContain("GetEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FixedTimeEquals", source, StringComparison.Ordinal);
        Assert.DoesNotContain("administratorRole", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sharedSecret", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", source, StringComparison.OrdinalIgnoreCase);
    }

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

    private sealed class RestorationFixture : IAsyncDisposable
    {
        private RestorationFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            AgvRestorationStore store)
        {
            Connection = connection;
            Context = context;
            Store = store;
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public AgvRestorationStore Store { get; }

        public static async Task<RestorationFixture> CreateAsync()
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
            return new RestorationFixture(connection, context, new AgvRestorationStore(context, governance));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
