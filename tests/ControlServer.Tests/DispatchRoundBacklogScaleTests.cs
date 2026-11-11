using System.Data.Common;
using System.Text.RegularExpressions;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 派车轮在生产规模的库上不再被积压表与待改派查询拖慢（control-server#881）。11-05 现场一轮 p50 29 s，开门要等一轮；本机用现场 DLL
/// 对库副本复现出两处：待改派需求查询对 <c>JourneyDemands</c> 整表扫（唯一的 DemandId 索引是部分索引，用不上），积压表每轮整表带跟踪加载
/// 两次而其中八成是离开目录、从未受理、从不清理的死行。
/// </summary>
/// <remarks>
/// <para>
/// (a) 计划判据：一轮里每一条按「已释放待改派」判的语句，查询计划里都不对 <c>JourneyDemands</c> 做 <c>SCAN</c>。计划不看机器快慢，
/// 「去掉索引」稳定地红。
/// </para>
/// <para>
/// (b) 物化判据：一轮里一行离开目录的积压行都不物化——不读进来就谈不上跟踪，也就不进每次保存前的钩子。
/// </para>
/// <para>
/// (c) 清理是删数据，条件逐条钉住：每一行「保留」的造数只违反一条条件，放宽任何一条，对应那一行就被删、断言就红。
/// </para>
/// </remarks>
public sealed class DispatchRoundBacklogScaleTests
{
    private const string Demand = "10000000-0000-4000-8000-000000000881";
    private const string Sublot = "SUBLOT-881";
    private const string OfficialIndex = "IX_JourneyDemands_DemandId_DispatchGeneration";
    private const string FieldTemporaryIndex = "IX_tmp_redispatch_JourneyDemands_DemandId_Generation";
    private const string Cs874TemporaryIndex = "IX_tmp_cs874_ProtocolOutbox_DepartureRequestId";
    private const string PreviousMigration = "20261015144738_JourneyStopOperatorDepartedAt";

    private static readonly TimeSpan Retention = TimeSpan.FromDays(3);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- (a) 待改派查询走索引 -------------------------------------------------------------------------------

    /// <summary>
    /// 一轮里按「已释放待改派」判的每条语句（受理集合、改派代次、孤儿检查……），计划里都不对 <c>JourneyDemands</c> 做 <c>SCAN</c>。
    /// 取红：模型里没有 <c>(DemandId, DispatchGeneration)</c> 的非部分索引时，移除过的归属（<c>RemovedAt IS NOT NULL</c>）与「更晚的代次」
    /// 两个子查询用不上部分索引 <c>IX_JourneyDemands_DemandId</c>，计划是 <c>SCAN</c>。
    /// </summary>
    [Fact]
    public async Task EveryRedispatchReadOfARoundSearchesJourneyDemandsByIndex()
    {
        CommandLog commands = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: commands);
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, fixture.Clock.GetUtcNow().AddMinutes(-5)));
        fixture.BoxCounts.Set(Sublot, 4);
        commands.Reset();

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await fixture.Context.AcceptedDemands.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));
        (string Text, (string Name, object? Value)[] Parameters)[] redispatchReads =
            [.. commands.Commands.Where(command => command.Text.Contains(DemandJourneyLookup.ReleasedForRedispatchReason, StringComparison.Ordinal))];
        // 派车轮开头读一次待改派集合（受理集合与改派代次共用），引擎的孤儿检查另有一条。少于两条说明有语句没被看见。
        Assert.True(redispatchReads.Length >= 2, $"only {redispatchReads.Length} redispatch read(s) seen");
        foreach ((string text, (string Name, object? Value)[] parameters) in redispatchReads)
        {
            string[] aliases = [.. Regex.Matches(text, "FROM \"JourneyDemands\" AS \"(?<alias>\\w+)\"")
                .Select(match => match.Groups["alias"].Value)];
            Assert.NotEmpty(aliases);
            string[] plan = await ExplainAsync(fixture.Context, text, parameters);
            TestContext.Current.TestOutputHelper?.WriteLine($"aliases=[{string.Join(",", aliases)}] plan=[{string.Join(" | ", plan)}]");
            string[] scans = [.. plan.Where(detail => aliases.Any(alias =>
                detail.Equals($"SCAN {alias}", StringComparison.Ordinal) ||
                detail.StartsWith($"SCAN {alias} ", StringComparison.Ordinal)))];
            Assert.True(scans.Length == 0, $"JourneyDemands is scanned: [{string.Join(" | ", plan)}] in {text}");
        }
    }

    /// <summary>
    /// 有一条已释放待改派的需求时，改派代次那条读（按 <c>json_each</c> 里的需求 id 读 <c>JourneyDemands</c> 取最大代次）也走索引
    /// （审查 S5）。上一条用例的轮次里没有待改派的需求，那条读提前返回、不发语句。
    /// </summary>
    [Fact]
    public async Task TheRedispatchGenerationReadSearchesJourneyDemandsByIndex()
    {
        CommandLog commands = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: commands);
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, fixture.Clock.GetUtcNow().AddMinutes(-5)));
        fixture.BoxCounts.Set(Sublot, 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await ReleaseForRedispatchAsync(fixture);
        commands.Reset();

        await fixture.Engine.ExecuteOnceAsync(Token);

        (string Text, (string Name, object? Value)[] Parameters)[] generationReads = [.. commands.Commands.Where(command =>
            command.Text.Contains("FROM \"JourneyDemands\" AS", StringComparison.Ordinal) &&
            command.Text.Contains("json_each", StringComparison.Ordinal) &&
            command.Text.Contains("MAX(", StringComparison.Ordinal) &&
            command.Text.Contains("GROUP BY", StringComparison.Ordinal))];
        (string text, (string Name, object? Value)[] parameters) = Assert.Single(generationReads);
        string[] aliases = [.. Regex.Matches(text, "FROM \"JourneyDemands\" AS \"(?<alias>\\w+)\"")
            .Select(match => match.Groups["alias"].Value)];
        Assert.NotEmpty(aliases);
        string[] plan = await ExplainAsync(fixture.Context, text, parameters);
        TestContext.Current.TestOutputHelper?.WriteLine($"plan=[{string.Join(" | ", plan)}]");
        Assert.DoesNotContain(plan, detail => aliases.Any(alias =>
            detail.Equals($"SCAN {alias}", StringComparison.Ordinal) ||
            detail.StartsWith($"SCAN {alias} ", StringComparison.Ordinal)));
        Assert.Contains(plan, detail => detail.Contains(OfficialIndex, StringComparison.Ordinal));
    }

    // ---- (b) 积压表不整表加载 -------------------------------------------------------------------------------

    /// <summary>
    /// 三百行离开目录、未受理的积压行（保留期内，不会被清掉），两轮里一行都不物化；目录里那条需求照常受理，它的积压行照常写。
    /// 取红：整表 <c>ToDictionaryAsync</c> 每轮把三百行都读进来并跟踪。
    /// </summary>
    [Fact]
    public async Task ARoundMaterialisesNoBacklogRowOfADemandThatLeftTheCatalog()
    {
        BacklogMaterialisations materialised = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: materialised);
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        string[] dead = [.. Enumerable.Range(0, 300).Select(index => $"20000000-0000-4000-8000-{index:D12}")];
        foreach (string demandId in dead)
        {
            fixture.Context.JourneyBacklog.Add(Backlog(demandId, now - TimeSpan.FromHours(1)));
        }
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        const string waiting = "10000000-0000-4000-8000-000000000882";
        fixture.Catalog.Set(
            fixture.Demand(Demand, Sublot, now.AddMinutes(-5)),
            fixture.Demand(waiting, "SUBLOT-882", now.AddMinutes(-4)));
        fixture.BoxCounts.Set(Sublot, 4);
        fixture.BoxCounts.Set("SUBLOT-882", 4);
        materialised.Reset();

        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);

        string[] deadRead = [.. materialised.DemandIds.Intersect(dead, StringComparer.Ordinal)];
        Assert.True(deadRead.Length == 0, $"{deadRead.Length} backlog row(s) of demands that left the catalog were read by the rounds");
        Assert.Contains(Demand, await fixture.Context.AcceptedDemands.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));
        Assert.NotNull((await fixture.BacklogAsync(Demand)).AcceptedAt);
        Assert.Equal(dead.Length + 2, await fixture.Context.JourneyBacklog.AsNoTracking().CountAsync(Token));
    }

    /// <summary>
    /// 一行刚离开目录、还没标记的积压行照样被标成 <c>DEMAND_LEFT_CATALOG</c>，已标过的不再读：缩小加载范围不能丢掉标记。
    /// </summary>
    [Fact]
    public async Task ADemandThatJustLeftTheCatalogIsStillMarked()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        const string left = "10000000-0000-4000-8000-000000000883";
        JourneyBacklogRow row = Backlog(left, now - TimeSpan.FromMinutes(1));
        row.ReasonCode = DispatchReasonCodes.FinalAppendJourneyEnded;
        fixture.Context.JourneyBacklog.Add(row);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(DispatchReasonCodes.DemandLeftCatalog, (await fixture.BacklogAsync(left)).ReasonCode);
    }

    // ---- (c) 死行清理 ---------------------------------------------------------------------------------------

    /// <summary>
    /// 只删同时满足全部条件的行：未受理、原因码 <c>DEMAND_LEFT_CATALOG</c>、不在本轮目录里、<c>AcceptedDemands</c> 与 <c>JourneyDemands</c>
    /// 都没有它、最后一次在目录里距今严格超过保留期。每一行「保留」只违反其中一条；放宽任何一条，对应那一行被删，断言红。
    /// </summary>
    [Fact]
    public async Task ThePurgeDeletesOnlyRowsThatMeetEveryCondition()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        DateTimeOffset old = now - Retention - TimeSpan.FromSeconds(1);
        ControlServerDbContext context = fixture.Context;
        context.JourneyBacklog.Add(Backlog("purge-just-past-retention", old));
        context.JourneyBacklog.Add(Backlog("purge-a-month-old", now - TimeSpan.FromDays(30)));

        context.JourneyBacklog.Add(Backlog("keep-exactly-at-retention", now - Retention));
        context.JourneyBacklog.Add(Backlog("keep-within-retention", now - TimeSpan.FromDays(1)));
        JourneyBacklogRow accepted = Backlog("keep-accepted-at", old);
        accepted.AcceptedAt = old;
        context.JourneyBacklog.Add(accepted);
        JourneyBacklogRow otherReason = Backlog("keep-other-reason", old);
        otherReason.ReasonCode = DispatchReasonCodes.DemandOlderThanCatalogWindow;
        context.JourneyBacklog.Add(otherReason);
        context.JourneyBacklog.Add(Backlog("keep-accepted-demand-row", old));
        context.AcceptedDemands.Add(AcceptedDemand("keep-accepted-demand-row", old));
        context.JourneyBacklog.Add(Backlog("keep-removed-membership", old));
        context.Set<JourneyDemandRow>().Add(Membership("keep-removed-membership", old));
        context.JourneyBacklog.Add(Backlog("keep-listed-in-catalog", old));
        await context.SaveChangesAsync(Token);
        context.ChangeTracker.Clear();

        JourneyBacklogPurge purge = await JourneyBacklogRetention.PurgeAsync(
            context, new HashSet<string>(["keep-listed-in-catalog"], StringComparer.Ordinal), now, Retention, Token);

        Assert.Equal(2, purge.Deleted);
        Assert.Empty(purge.Unreadable);
        Assert.Equal(
            ["keep-accepted-at", "keep-accepted-demand-row", "keep-exactly-at-retention", "keep-listed-in-catalog",
             "keep-other-reason", "keep-removed-membership", "keep-within-retention"],
            await context.JourneyBacklog.AsNoTracking().Select(row => row.DemandId).OrderBy(id => id).ToArrayAsync(Token));
    }

    /// <summary>
    /// 派车轮自己清：第一轮删掉过了保留期的死行；一小时内的下一轮不再清（新过期的那行留着）；钟走过一小时，下一轮清掉它。
    /// </summary>
    [Fact]
    public async Task TheRoundSweepsAtMostOncePerInterval()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Assert.Equal(Retention, fixture.Options.BacklogRetention);
        fixture.Context.JourneyBacklog.Add(Backlog("first", now - Retention - TimeSpan.FromMinutes(1)));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Empty(await fixture.Context.JourneyBacklog.AsNoTracking().ToArrayAsync(Token));

        fixture.Context.JourneyBacklog.Add(Backlog("second", now - Retention - TimeSpan.FromMinutes(1)));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(JourneyBacklogRetention.SweepInterval - TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(["second"], await fixture.Context.JourneyBacklog.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Empty(await fixture.Context.JourneyBacklog.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 保留期从离开目录那一刻算（审查 S1）：一条在目录里多日没被哪辆车判过的需求，<c>LastSeenAt</c> 停在多日前；离开目录时标记写下
    /// 标记时刻，此后各轮不再刷新；满保留期（不含）之前不删，过了保留期、下一次清扫时才删。
    /// 取红：标记时不写 <c>LastSeenAt</c>，离开目录后的下一次清扫就把这行删了，远没到保留期。
    /// </summary>
    [Fact]
    public async Task ARowLeftLongUnjudgedIsKeptForTheWholeRetentionAfterItLeaves()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        DateTimeOffset left = fixture.Clock.GetUtcNow();
        JourneyBacklogRow row = Backlog("unjudged", left - TimeSpan.FromDays(10));
        row.ReasonCode = "VEHICLE_TASK_TYPE_NOT_ADMITTED";
        fixture.Context.JourneyBacklog.Add(row);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyBacklogRow marked = await fixture.BacklogAsync("unjudged");
        Assert.Equal(DispatchReasonCodes.DemandLeftCatalog, marked.ReasonCode);
        Assert.Equal(left, marked.LastSeenAt);

        fixture.Clock.Advance(Retention);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(left, (await fixture.BacklogAsync("unjudged")).LastSeenAt);

        fixture.Clock.Advance(JourneyBacklogRetention.SweepInterval);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Empty(await fixture.Context.JourneyBacklog.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 本轮目录里列着的需求，积压行即使是 <c>DEMAND_LEFT_CATALOG</c> 加过期的 <c>LastSeenAt</c>，整轮跑完仍是原来那一行（审查 S3）。
    /// 看 <c>FirstSeenAt</c>：行被删掉再由本轮写回时它会变成本轮时刻。取红：传给清扫的目录集合换成空集合。
    /// </summary>
    [Fact]
    public async Task TheRoundNeverPurgesARowOfADemandItsCatalogLists()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        JourneyBacklogRow row = Backlog(Demand, now - Retention - TimeSpan.FromDays(1));
        row.FirstSeenAt = now - TimeSpan.FromDays(5);
        fixture.Context.JourneyBacklog.Add(row);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, now.AddMinutes(-5)));
        fixture.BoxCounts.Set(Sublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(now - TimeSpan.FromDays(5), (await fixture.BacklogAsync(Demand)).FirstSeenAt);
    }

    /// <summary>
    /// 清扫的删除语句抛异常时，这一轮照常受理目录里的需求，并记一条清扫失败的 Warning（2882，审查 S4）；死行留着。
    /// </summary>
    [Fact]
    public async Task ASweepThatThrowsIsLoggedAndTheRoundGoesOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: new FailingBacklogDelete());
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        fixture.Context.JourneyBacklog.Add(Backlog("dead", now - Retention - TimeSpan.FromDays(1)));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, now.AddMinutes(-5)));
        fixture.BoxCounts.Set(Sublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await fixture.Context.AcceptedDemands.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.StartsWith("Sweeping the backlog of demands that left the catalog failed", StringComparison.Ordinal));
        Assert.NotNull(await fixture.Context.JourneyBacklog.AsNoTracking().SingleOrDefaultAsync(row => row.DemandId == "dead", Token));
    }

    /// <summary>
    /// 一行 <c>LastSeenAt</c> 读不出来时，清扫跳过它、把它报回去，别的过期行照删，不让整次清扫失败（审查 L4）。
    /// </summary>
    [Fact]
    public async Task ARowWhoseLastSeenAtCannotBeReadIsSkippedAndReported()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        ControlServerDbContext context = fixture.Context;
        context.JourneyBacklog.Add(Backlog("expired", now - Retention - TimeSpan.FromDays(1)));
        context.JourneyBacklog.Add(Backlog("garbled", now - Retention - TimeSpan.FromDays(1)));
        await context.SaveChangesAsync(Token);
        context.ChangeTracker.Clear();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE \"JourneyBacklog\" SET \"LastSeenAt\" = 'not a time' WHERE \"DemandId\" = 'garbled'", Token);

        JourneyBacklogPurge purge = await JourneyBacklogRetention.PurgeAsync(
            context, new HashSet<string>(StringComparer.Ordinal), now, Retention, Token);

        Assert.Equal(1, purge.Deleted);
        Assert.Equal(["garbled"], purge.Unreadable);
        Assert.Equal(["garbled"], await context.JourneyBacklog.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));
    }

    /// <summary>保留期缺省三天；短于一小时（一次清扫间隔）拒绝启动，免得一条短暂离开目录的需求丢掉它的首次看到时刻与防饥饿升级记录。</summary>
    [Fact]
    public void TheRetentionDefaultsToThreeDaysAndMayNotBeShorterThanAnHour()
    {
        const string failure = "BacklogRetention must be at least 1 hour.";
        Assert.Equal(TimeSpan.FromDays(3), new JourneyRuntimeOptions().BacklogRetention);
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        JourneyRuntimeOptions options = JourneyRuntimeOptionsTests.ValidEnabledOptions();
        foreach (TimeSpan accepted in new[] { TimeSpan.FromHours(1), TimeSpan.FromDays(3), TimeSpan.FromDays(30) })
        {
            options.BacklogRetention = accepted;
            Assert.DoesNotContain(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }
        foreach (TimeSpan refused in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(59) })
        {
            options.BacklogRetention = refused;
            Assert.Contains(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }
    }

    /// <summary>上次清扫的时刻要活过每轮新建的作用域：宿主里是单例。</summary>
    [Fact]
    public void TheHostRegistersTheRetentionAsASingleton()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(JourneyBacklogRetention)).Lifetime);
    }

    // ---- 迁移 -----------------------------------------------------------------------------------------------

    /// <summary>
    /// 迁移加非部分索引 <c>(DemandId, DispatchGeneration)</c>，名字不与现场手工建的临时索引相同；现场那个临时索引（同列）若在就删掉，
    /// cs#874 的临时索引不动；Down 只删正式索引、不重建临时索引；在没有临时索引的库上照样升得上去。
    /// </summary>
    [Fact]
    public async Task TheMigrationAddsTheIndexAndDropsTheFieldTemporaryOne()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await ExecuteAsync(fixture.Connection,
            $"CREATE INDEX \"{FieldTemporaryIndex}\" ON \"JourneyDemands\" (\"DemandId\",\"DispatchGeneration\")");
        await ExecuteAsync(fixture.Connection,
            $"CREATE INDEX \"{Cs874TemporaryIndex}\" ON \"ProtocolOutbox\" (\"MessageType\")");
        string[] before = await IndexesAsync(fixture.Connection);

        await fixture.Context.Database.MigrateAsync(Token);
        string[] up = await IndexesAsync(fixture.Connection);
        Assert.Contains(
            $"{OfficialIndex}|CREATE INDEX \"{OfficialIndex}\" ON \"JourneyDemands\" (\"DemandId\", \"DispatchGeneration\")", up);
        Assert.DoesNotContain(up, index => index.StartsWith(FieldTemporaryIndex + "|", StringComparison.Ordinal));
        Assert.Contains(up, index => index.StartsWith(Cs874TemporaryIndex + "|", StringComparison.Ordinal));
        Assert.Equal(
            [.. before.Where(index => !index.StartsWith(FieldTemporaryIndex + "|", StringComparison.Ordinal))],
            [.. up.Where(index => !index.StartsWith(OfficialIndex + "|", StringComparison.Ordinal))]);

        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Token);
        string[] down = await IndexesAsync(fixture.Connection);
        Assert.DoesNotContain(down, index => index.StartsWith(OfficialIndex + "|", StringComparison.Ordinal));
        Assert.DoesNotContain(down, index => index.StartsWith(FieldTemporaryIndex + "|", StringComparison.Ordinal));

        await fixture.Context.Database.MigrateAsync(Token);
        Assert.Equal(up, await IndexesAsync(fixture.Connection));
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private static JourneyBacklogRow Backlog(string demandId, DateTimeOffset lastSeenAt) => new()
    {
        DemandId = demandId,
        TransportDemandKey = $"KEY-{demandId}|WIRE_TO_GATE",
        FirstSeenAt = lastSeenAt - TimeSpan.FromHours(2),
        DemandCreatedAt = lastSeenAt - TimeSpan.FromHours(3),
        DecisionFingerprint = "fingerprint",
        ReasonCode = DispatchReasonCodes.DemandLeftCatalog,
        LastSeenAt = lastSeenAt,
    };

    private static AcceptedDemandRow AcceptedDemand(string demandId, DateTimeOffset at) => new()
    {
        DemandId = demandId,
        SeriesId = $"SERIES-{demandId}",
        TransportDemandKey = $"KEY-{demandId}|WIRE_TO_GATE",
        WorkType = "WIRE_TO_GATE",
        Sublot = $"SUBLOT-{demandId}",
        HistoryEpoch = "11111111-1111-4111-8111-111111111111",
        CreatedAt = at,
        ValueObservedAt = at,
        ValuePollTraceId = "TRACE",
        ValueProjectionCommitId = "COMMIT",
        LiveMesFieldsJson = "{}",
        AcceptedAt = at,
        Status = ControlServer.Domain.DemandExecutionStatus.Cancelled,
    };

    private static JourneyDemandRow Membership(string demandId, DateTimeOffset at) => new()
    {
        JourneyId = $"journey:{demandId}",
        DemandId = demandId,
        PickupStopId = "PICKUP",
        UnloadStopId = "UNLOAD",
        ExpectedBasketCount = 1,
        TargetSlotsJson = "[]",
        LoadSlotOperationAttemptId = "LOAD-ATTEMPT",
        LoadCommandMessageId = "LOAD-MESSAGE",
        UnloadSlotOperationAttemptId = "UNLOAD-ATTEMPT",
        UnloadCommandMessageId = "UNLOAD-MESSAGE",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        DispatchGeneration = 1,
        Status = "REMOVED",
        AddedAt = at,
        RemovedAt = at,
        RemovalReason = "CANCELLED",
    };

    private static async Task<string[]> ExplainAsync(
        ControlServerDbContext context, string text, (string Name, object? Value)[] parameters)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await using DbCommand explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + text;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = explain.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            explain.Parameters.Add(parameter);
        }
        List<string> details = [];
        await using DbDataReader reader = await explain.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            details.Add(reader.GetString(3));
        }
        return [.. details];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>每个索引：名字与建索引语句（自动索引没有语句），按名字排。</summary>
    private static async Task<string[]> IndexesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """SELECT "name" || '|' || IFNULL("sql", '') FROM sqlite_master WHERE "type" = 'index' ORDER BY "name" """;
        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetString(0));
        }
        return [.. values];
    }

    /// <summary>记下每条语句与参数，同步与异步、读行与读标量都记（同 cs#874 审查 M1）。</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<(string Text, (string Name, object? Value)[] Parameters)> Commands { get; } = [];

        public void Reset() => Commands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command) =>
            Commands.Add((command.CommandText,
                [.. command.Parameters.Cast<DbParameter>().Select(parameter => (parameter.ParameterName, (object?)parameter.Value))]));
    }

    /// <summary>
    /// 释放的落库形状，同 <c>CatalogAgeWindowTests.ReleaseForRedispatchAsync</c>：归属标 RELEASED_FOR_REDISPATCH，旅程按释放服务同一条路
    /// 关闭，积压行清掉受理时刻。
    /// </summary>
    private static async Task ReleaseForRedispatchAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().SingleAsync(Token);
        membership.RemovedAt = fixture.Clock.GetUtcNow();
        membership.RemovalReason = DemandJourneyLookup.ReleasedForRedispatchReason;
        await new PickupStopTermination(fixture.Context)
            .StageJourneyClosureAsync(journey, DemandJourneyLookup.ReleasedForRedispatchReason, fixture.Clock.GetUtcNow(), Token);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(row => row.DemandId == Demand, Token);
        backlog.AcceptedAt = null;
        backlog.ReasonCode = DemandJourneyLookup.ReleasedForRedispatchReason;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.True(await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token));
    }

    /// <summary>积压表的 DELETE 一律抛异常，别的语句照常。</summary>
    private sealed class FailingBacklogDelete : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result) =>
            IsBacklogDelete(command) ? throw new InvalidOperationException("cs881 test: backlog delete refused") : result;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            IsBacklogDelete(command)
                ? throw new InvalidOperationException("cs881 test: backlog delete refused")
                : ValueTask.FromResult(result);

        private static bool IsBacklogDelete(DbCommand command) =>
            command.CommandText.TrimStart().StartsWith("DELETE FROM \"JourneyBacklog\"", StringComparison.Ordinal);
    }

    /// <summary>记下物化出来的每一行积压行的 DemandId（带不带跟踪都算：读进来就是读进来了）。</summary>
    private sealed class BacklogMaterialisations : IMaterializationInterceptor
    {
        private readonly List<string> demandIds = [];

        public IReadOnlyList<string> DemandIds => demandIds;

        public void Reset() => demandIds.Clear();

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is JourneyBacklogRow row)
            {
                demandIds.Add(row.DemandId);
            }
            return entity;
        }
    }
}
