using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 防饥饿升级告警（REQ-0210 后半、REQ-0203；批次7-09，control-server#214），整轮跑：引擎、派车轮、两个轮末汇总、SQLite。
/// </summary>
/// <remarks>
/// <para>
/// <b>任务被一道硬门禁挡着才留在积压里</b>：夹具只有一辆空闲车，一条什么都不缺的需求第一轮就被接走，
/// 等不到超时。这里挡住它的是「查不到这个批次的箱数」，一道每轮都重判的门禁——超时层不绕过它（REQ-0202），
/// 所以同一条需求可以一轮又一轮地越过阈值却派不出去，正是要看告警会不会重复的局面。
/// </para>
/// <para>
/// <b>每一处「没有再告警」都先断言钟确实走了</b>：固定时钟下「告警时刻没变」恒真，证明不了任何事。
/// </para>
/// </remarks>
public sealed class Batch7StarvationEscalationTests
{
    private const string Waiting = "10000000-0000-4000-8000-000000000001";
    private const string Takeable = "10000000-0000-4000-8000-000000000002";
    private const string Zone = "MAP-25-WIRE_TO_GATE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>越过阈值的那一轮告警一次，写下告警时刻与所用参数版本；之后连续多轮越过得越来越多，也不再告警。</summary>
    [Fact]
    public async Task AnOverdueTaskIsEscalatedOnceAndNotAgainOverFollowingRounds()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        long version = (await fixture.ImportStarvationThresholdsAsync((Zone, 300))).Version;
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 10));
        DateTimeOffset firstRoundAt = fixture.Clock.GetUtcNow();

        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyBacklogRow escalated = await BacklogAsync(fixture);
        for (int round = 0; round < 3; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        Assert.Equal(firstRoundAt, escalated.StarvationEscalatedAt);
        Assert.Equal(version, escalated.StarvationEscalationParameterVersion);
        Assert.Equal(firstRoundAt.AddMinutes(3), fixture.Clock.GetUtcNow());
        JourneyBacklogRow after = await BacklogAsync(fixture);
        Assert.Equal(firstRoundAt, after.StarvationEscalatedAt);
        Assert.Equal(1, Escalations(fixture, Waiting));
        Assert.Null(after.AcceptedAt);
        // 挡住它的是一道瞬时门禁（查不到箱数），不是结构性阻断：防饥饿要抓的正是这一种。
        Assert.Empty(await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 结构性阻断的需求越过阈值也不告警、不进超时层（调度会话 2026-09-21 定）：分区归属表收录了它的 AREA、地图上没有站点，
    /// 立的是 <c>AREA_STATION_NOT_FOUND</c> 结构性阻断；它等了一天、跑了多轮，防饥饿标记与日志都没有。
    /// </summary>
    [Fact]
    public async Task ADemandUnderAStructuralDispatchBlockIsNotEscalatedHoweverLongItWaits()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportStarvationThresholdsAsync((Zone, 60));
        // N22-1 在默认分区归属表里，默认地图上没有它的站点。
        fixture.Catalog.Set(fixture.Demand(Waiting, "SUBLOT-WAITING", JourneyRuntimeWorkerTestKit.Now.AddDays(-1), "N22-1"));
        fixture.BoxCounts.Set("SUBLOT-WAITING", 4);
        DateTimeOffset startedAt = fixture.Clock.GetUtcNow();

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(startedAt.AddMinutes(3), fixture.Clock.GetUtcNow());
        StructuralDispatchBlockRow block = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal("AREA_STATION_NOT_FOUND", block.ReasonCode);
        JourneyBacklogRow backlog = await BacklogAsync(fixture);
        Assert.Null(backlog.StarvationEscalatedAt);
        Assert.Null(backlog.StarvationEscalationParameterVersion);
        Assert.Equal(0, Escalations(fixture, Waiting));
    }

    /// <summary>
    /// 释放改派（批次7-10，control-server#215）之后年龄仍从原来的本地建单时刻算：释放把积压行的受理标记清掉、
    /// 需求重新成为候选，目录项的 <c>CreatedAt</c> 不变。这里按 cs#215 释放后留下的样子摆好积压行（受理标记空、
    /// 首次看到就在一分钟前），需求在 20 分钟前建单，阈值 15 分钟：按建单时刻算它已超时并告警，按首次看到算则不会。
    /// </summary>
    /// <remarks>
    /// cs#215 还没合入集成分支，这里摆的是它的释放写下的积压行（<c>AcceptedAt = null</c>、原因码 <c>RELEASED</c>），
    /// 不经它的释放代码；两票都合入后，整轮经真实释放路径的那一条由后合的一方补上。
    /// </remarks>
    [Fact]
    public async Task AReleasedDemandKeepsTheAgeItHasHadSinceItsLocalCreation()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportStarvationThresholdsAsync((Zone, 900));
        DateTimeOffset createdAt = JourneyRuntimeWorkerTestKit.Now.AddMinutes(-20);
        fixture.Catalog.Set(fixture.Demand(Waiting, "SUBLOT-WAITING", createdAt));
        fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
        {
            DemandId = Waiting,
            TransportDemandKey = "SUBLOT-WAITING|WIRE_TO_GATE",
            FirstSeenAt = fixture.Clock.GetUtcNow(),
            DemandCreatedAt = createdAt,
            DecisionFingerprint = "released",
            ReasonCode = "RELEASED",
            LastSeenAt = fixture.Clock.GetUtcNow(),
            AcceptedAt = null,
        });
        await fixture.Context.SaveChangesAsync(Token);
        DateTimeOffset releasedAt = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(releasedAt.AddMinutes(1), fixture.Clock.GetUtcNow());
        JourneyBacklogRow backlog = await BacklogAsync(fixture);
        Assert.Equal(releasedAt, backlog.FirstSeenAt);
        Assert.Equal(fixture.Clock.GetUtcNow(), backlog.StarvationEscalatedAt);
        Assert.Equal(1, Escalations(fixture, Waiting));
        Assert.Contains(
            fixture.StarvationLog.Entries,
            entry => entry.Message.Contains(Waiting, StringComparison.Ordinal) &&
                entry.Message.Contains("has waited 1260 s", StringComparison.Ordinal));
    }

    /// <summary>
    /// 「已告警」与告警时刻、参数版本在同一次保存里落库：拆成两次，崩在中间就会只剩一半。
    /// </summary>
    /// <remarks>
    /// 先跑一轮不配阈值的，让积压行先落库：新插入的行把每一列都算作「写了」，包括空着的告警列，
    /// 那一次保存会被误认成告警那一次——这条在桩上曾因此恒绿。
    /// </remarks>
    [Fact]
    public async Task TheEscalationMarkAndItsParameterVersionCommitInOneSave()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.ImportStarvationThresholdsAsync((Zone, 300));
        fixture.SaveChanges.Reset();

        await fixture.Engine.ExecuteOnceAsync(Token);

        string[] save = Assert.Single(
            fixture.SaveChanges.Saves,
            written => written.Contains("JourneyBacklogRow.StarvationEscalatedAt"));
        Assert.Contains("JourneyBacklogRow.StarvationEscalationParameterVersion", save);
    }

    /// <summary>重启（新引擎、同一个库）之后不重报：「已告警」在库里，不在进程里。</summary>
    [Fact]
    public async Task ARestartDoesNotRaiseTheEscalationAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportStarvationThresholdsAsync((Zone, 300));
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset escalatedAt = (await BacklogAsync(fixture)).StarvationEscalatedAt!.Value;

        await fixture.RecreateEngineAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.True(fixture.Clock.GetUtcNow() > escalatedAt);
        Assert.Equal(escalatedAt, (await BacklogAsync(fixture)).StarvationEscalatedAt);
        Assert.Equal(1, Escalations(fixture, Waiting));
    }

    /// <summary>
    /// 需求离开目录再回来：积压行保留（只改原因码），首次看到不重置，已告警的不再告警。
    /// </summary>
    [Fact]
    public async Task ADemandThatLeavesTheCatalogAndComesBackKeepsItsAgeAndIsNotEscalatedAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportStarvationThresholdsAsync((Zone, 300));
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyBacklogRow first = await BacklogAsync(fixture);
        (DateTimeOffset firstSeen, DateTimeOffset escalatedAt) = (first.FirstSeenAt, first.StarvationEscalatedAt!.Value);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Catalog.Set();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("DEMAND_LEFT_CATALOG", (await BacklogAsync(fixture)).ReasonCode);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 10));
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyBacklogRow back = await BacklogAsync(fixture);
        Assert.NotEqual("DEMAND_LEFT_CATALOG", back.ReasonCode);
        Assert.Equal(firstSeen, back.FirstSeenAt);
        Assert.Equal(escalatedAt, back.StarvationEscalatedAt);
        Assert.Equal(1, Escalations(fixture, Waiting));
    }

    /// <summary>
    /// 阈值未配置时不升级、只计龄——上线时现场实际走的就是这条路（7-17 未批阈值）。等了一整天、跑了多轮，
    /// 不告警、不写「已告警」；导入阈值之后的下一轮才告警，记下的是那一版。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithNoThresholdConfiguredNothingIsEscalatedUntilOneIsImported(bool importAnEmptyThreshold)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        if (importAnEmptyThreshold)
        {
            await fixture.ImportStarvationThresholdsAsync((Zone, null));
        }
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 24 * 60));

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        }
        JourneyBacklogRow unescalated = await BacklogAsync(fixture);
        Assert.Null(unescalated.StarvationEscalatedAt);
        Assert.Null(unescalated.StarvationEscalationParameterVersion);
        Assert.Equal(0, Escalations(fixture, Waiting));

        long version = (await fixture.ImportStarvationThresholdsAsync((Zone, 3600))).Version;
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyBacklogRow escalated = await BacklogAsync(fixture);
        Assert.Equal(fixture.Clock.GetUtcNow(), escalated.StarvationEscalatedAt);
        Assert.Equal(version, escalated.StarvationEscalationParameterVersion);
        Assert.Equal(1, Escalations(fixture, Waiting));
    }

    /// <summary>
    /// 轮中导入新版本，本轮仍按开轮时读到的旧版本判：旧版 1 小时未超时，新版 1 分钟超时，本轮不告警；
    /// 下一轮按新版告警，记下的是新版号。导入发生在派车轮已读完参数、正在读车的那一刻。
    /// </summary>
    [Fact]
    public async Task AVersionImportedMidRoundTakesEffectFromTheNextRound()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        long oldVersion = (await fixture.ImportStarvationThresholdsAsync((Zone, 3600))).Version;
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 10));
        long? newVersion = null;
        fixture.Riot.BeforeReadVehicle = () =>
        {
            if (newVersion is null)
            {
                newVersion = fixture.ImportStarvationThresholdsAsync((Zone, 60)).GetAwaiter().GetResult().Version;
            }
        };

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.NotNull(newVersion);
        Assert.True(newVersion > oldVersion);
        Assert.Null((await BacklogAsync(fixture)).StarvationEscalatedAt);
        Assert.Equal(0, Escalations(fixture, Waiting));

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyBacklogRow escalated = await BacklogAsync(fixture);
        Assert.Equal(newVersion, escalated.StarvationEscalationParameterVersion);
        Assert.Equal(1, Escalations(fixture, Waiting));
    }

    /// <summary>
    /// 超时不绕过硬门禁：超时的那条排在最前、照样被门禁挡住不派（告警照发），车给了排在它后面、门禁全过的那条。
    /// </summary>
    [Fact]
    public async Task AnOverdueTaskStoppedByAHardGateIsNotDispatchedAndTheVehicleGoesToTheNextTask()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportStarvationThresholdsAsync((Zone, 300));
        fixture.Catalog.Set(
            WaitingDemand(fixture, minutesAgo: 30),
            fixture.Demand(Takeable, "SUBLOT-TAKEABLE", fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.BoxCounts.Set("SUBLOT-TAKEABLE", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Null((await BacklogAsync(fixture)).AcceptedAt);
        Assert.NotNull((await BacklogAsync(fixture)).StarvationEscalatedAt);
        Assert.Equal(
            [Takeable],
            await fixture.Context.AcceptedDemands.Select(row => row.DemandId).ToArrayAsync(Token));
    }

    /// <summary>越过阈值、却在同一轮被接走的任务不告警：它不再等了。</summary>
    [Fact]
    public async Task AnOverdueTaskTakenInTheSameRoundIsNotEscalated()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportStarvationThresholdsAsync((Zone, 300));
        fixture.Catalog.Set(fixture.Demand(Takeable, "SUBLOT-TAKEABLE", fixture.Clock.GetUtcNow().AddMinutes(-30)));
        fixture.BoxCounts.Set("SUBLOT-TAKEABLE", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            [Takeable],
            await fixture.Context.AcceptedDemands.Select(row => row.DemandId).ToArrayAsync(Token));
        Assert.Null((await fixture.BacklogAsync(Takeable)).StarvationEscalatedAt);
        Assert.Equal(0, Escalations(fixture, Takeable));
    }

    /// <summary>
    /// 崩在「已告警」那次保存上：库里没有标记、日志里也没有告警（日志在提交之后才写）；重启后的下一轮补发一次，
    /// 之后不再发。不漏、不重。积压行先由一轮不配阈值的落库，注入点因此只对得上告警那一次保存（新插入的行
    /// 也会把告警列算作「写了」）。
    /// </summary>
    [Fact]
    public async Task ACrashAtTheEscalationSaveLeavesNoMarkAndNoLogAndTheNextRoundRaisesItOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(WaitingDemand(fixture, minutesAgo: 10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.ImportStarvationThresholdsAsync((Zone, 300));
        fixture.SaveChanges.FailWhen = written => written.Contains("JourneyBacklogRow.StarvationEscalatedAt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        Assert.Contains(fixture.SaveChanges.Saves, written => written.Contains("JourneyBacklogRow.StarvationEscalatedAt"));
        await fixture.RecreateEngineAsync();
        Assert.Null((await BacklogAsync(fixture)).StarvationEscalatedAt);
        Assert.Equal(0, Escalations(fixture, Waiting));

        fixture.SaveChanges.FailWhen = null;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(-1), (await BacklogAsync(fixture)).StarvationEscalatedAt);
        Assert.Equal(1, Escalations(fixture, Waiting));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>一条会一直留在积压里的需求：它的批次查不到箱数，每轮都被这道门禁挡住。</summary>
    private static ControlServer.Domain.AcceptedDemandSnapshot WaitingDemand(RuntimeFixture fixture, int minutesAgo) =>
        fixture.Demand(Waiting, "SUBLOT-WAITING", JourneyRuntimeWorkerTestKit.Now.AddMinutes(-minutesAgo));

    private static async Task<JourneyBacklogRow> BacklogAsync(RuntimeFixture fixture) =>
        await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == Waiting, Token);

    private static int Escalations(RuntimeFixture fixture, string demandId)
    {
        lock (fixture.StarvationLog.Entries)
        {
            return fixture.StarvationLog.Entries.Count(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Starvation escalation", StringComparison.Ordinal) &&
                entry.Message.Contains(demandId, StringComparison.Ordinal));
        }
    }
}
