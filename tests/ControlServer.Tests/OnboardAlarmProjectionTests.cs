using System.Text.Json;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 服务端消费车载告警快照并投影到看板（<c>FP-IS-15</c> 服务端半边，REQ-0270、REQ-0269）。
/// </summary>
/// <remarks>
/// <para>
/// 本文件固化的是收到之后怎么存、怎么收敛、怎么显示。#16 落地时协议 v2 消息 9 的收发还没做，所以
/// 这里一条切片 trait 都没有；线上那一半接好之后，这些测试站在
/// <c>OnboardAlarmSnapshot</c> 背后，归 <c>FP-IS-15</c>。
/// </para>
/// <para>
/// <see cref="TheAlarmCardWasAddedWithoutTouchingTheDashboardMainFiles"/> 是个例外，它没有切片
/// trait：那一条证的是 #12 立的看板自注册约定，与协议无关，把它挂在切片上等于让约定随切片一起被推迟。
/// </para>
/// </remarks>
public sealed class OnboardAlarmProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task ALaterSnapshotReplacesTheEarlierOneWholeAndNeverMergesIntoIt()
    {
        await using AlarmFixture fixture = await AlarmFixture.CreateAsync();
        await fixture.MarkSessionReadyAsync("AGV-01");

        await fixture.Store.RecordSnapshotAsync(
            Snapshot("AGV-01", 1, Alarm("ONBOARD_RULE_GATEWAY_DISCONNECTED"), Alarm("ONBOARD_FLEET_CLOCK_SKEW")),
            sessionGeneration: 1,
            Now,
            TestContext.Current.CancellationToken);

        // 后一份只带一条：前一份里多出来的那条不会被「保留」，整体取代就是整体取代。
        await fixture.Store.RecordSnapshotAsync(
            Snapshot("AGV-01", 2, Alarm("ONBOARD_FLEET_CLOCK_SKEW")),
            sessionGeneration: 1,
            Now.AddMinutes(1),
            TestContext.Current.CancellationToken);

        OnboardAlarmSnapshotRow row = await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, row.SnapshotSequence);
        Assert.DoesNotContain("ONBOARD_RULE_GATEWAY_DISCONNECTED", row.AlarmsJson, StringComparison.Ordinal);

        // 序号倒退的快照被忽略：收下它等于让看板倒着走。
        await fixture.Store.RecordSnapshotAsync(
            Snapshot("AGV-01", 1, Alarm("ONBOARD_RULE_GATEWAY_DISCONNECTED")),
            sessionGeneration: 1,
            Now.AddMinutes(2),
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            2,
            (await fixture.Context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).SnapshotSequence);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AVehicleOutOfContactShowsTheReasonRatherThanItsLastKnownAlarms()
    {
        await using AlarmFixture fixture = await AlarmFixture.CreateAsync();
        await fixture.MarkSessionReadyAsync("AGV-01");
        await fixture.Store.RecordSnapshotAsync(
            Snapshot("AGV-01", 1, Alarm("ONBOARD_FLEET_CLOCK_SKEW")),
            sessionGeneration: 1,
            Now,
            TestContext.Current.CancellationToken);

        VehicleAlarmProjection linked = Assert.Single(
            await fixture.Store.ReadDashboardProjectionAsync(TestContext.Current.CancellationToken));
        Assert.True(linked.IsAvailable);
        Assert.Equal(["ONBOARD_FLEET_CLOCK_SKEW"], linked.Alarms.Select(alarm => alarm.AlarmCode));

        // 车失联了。库里那份快照还在，但看板显示的是失联本身——不确定新旧的旧值比没有值更糟。
        await fixture.DropSessionAsync("AGV-01");
        VehicleAlarmProjection lost = Assert.Single(
            await fixture.Store.ReadDashboardProjectionAsync(TestContext.Current.CancellationToken));
        Assert.False(lost.IsAvailable);
        Assert.Equal(VehicleAlarmProjection.LinkDownReason, lost.UnavailableReason);
        Assert.Empty(lost.Alarms);
        Assert.Null(lost.CapturedAt);

        // 「在线但还没报过快照」是另一回事，原因码也不同：没有值，不是拿不到值。
        await fixture.MarkSessionReadyAsync("AGV-02");
        VehicleAlarmProjection silent = (await fixture.Store.ReadDashboardProjectionAsync(
            TestContext.Current.CancellationToken)).Single(row => row.AgvId == "AGV-02");
        Assert.Equal(VehicleAlarmProjection.NeverReportedReason, silent.UnavailableReason);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task OnlyAlarmsUnrelatedToAnyVehiclesOwnSituationReachTheDashboard()
    {
        await using AlarmFixture fixture = await AlarmFixture.CreateAsync();
        await fixture.MarkSessionReadyAsync("AGV-01");

        // 本期没有人员认证，所以收敛不是按人分权，是按「哪个界面看得到什么」：与当前 AGV／当前停靠／
        // 当前操作直接相关的三类归车载端界面，其余进看板。两边加起来是全集。
        await fixture.Store.RecordSnapshotAsync(
            new OnboardAlarmSnapshotView("AGV-01", 1, Now,
            [
                Alarm("ONBOARD_IO_MODULE_DISCONNECTED", OnboardAlarmScope.CurrentVehicle),
                Alarm("ONBOARD_STATION_OPERATION_OVERDUE", OnboardAlarmScope.CurrentStop),
                Alarm("ONBOARD_SLOT_LOCK_FEEDBACK_LOST", OnboardAlarmScope.CurrentOperation),
                Alarm("ONBOARD_FLEET_CLOCK_SKEW", OnboardAlarmScope.Fleet)
            ]),
            sessionGeneration: 1,
            Now,
            TestContext.Current.CancellationToken);

        VehicleAlarmProjection projection = Assert.Single(
            await fixture.Store.ReadDashboardProjectionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["ONBOARD_FLEET_CLOCK_SKEW"], projection.Alarms.Select(alarm => alarm.AlarmCode));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public void AlarmVisibilityIsEvaluatedWithNoIdentityOrKeyInputAnywhereOnItsPath()
    {
        // 本期没有人员认证。把收敛做成需要身份才能求值的权限判断，会让它落在一个全场共用环境变量密钥
        // 的地基上——所以求值函数的入参里只有一份快照，没有身份也没有角色。
        System.Reflection.ParameterInfo[] parameters = typeof(OnboardAlarmDashboardVisibility)
            .GetMethod(nameof(OnboardAlarmDashboardVisibility.ForDashboard))!
            .GetParameters();
        Assert.Equal([typeof(OnboardAlarmSnapshotView)], parameters.Select(parameter => parameter.ParameterType));

        string root = FindRepositoryRoot();
        foreach (string file in new[]
        {
            Path.Combine(root, "src", "ControlServer.Domain", "OnboardAlarmModels.cs"),
            Path.Combine(root, "src", "ControlServer.Infrastructure", "Persistence", "OnboardAlarmProjectionStore.cs"),
            Path.Combine(root, "src", "ControlServer.Host", "Dashboard", "OnboardAlarmsQueryEndpoint.cs"),
            Path.Combine(root, "src", "ControlServer.Dashboard", "OnboardAlarmCard.cs")
        })
        {
            string source = File.ReadAllText(file);
            foreach (string forbidden in new[]
            {
                "GetEnvironmentVariable", "FixedTimeEquals", "administratorRole", "sharedSecret",
                "password", "operatorId"
            })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public void AlarmCodesStayAnOpenStringSetAndShareNothingWithTheClosedProtocolErrorCodeRegistry()
    {
        // 告警 code 不被规约进 ErrorCode：开放集合塞进封闭 enum 等于每加一个故障码就发一次 breaking
        // change。这里断言的是类型本身，不是一句约定。
        Assert.Equal(
            typeof(string),
            typeof(OnboardAlarmEntry).GetProperty(nameof(OnboardAlarmEntry.AlarmCode))!.PropertyType);

        foreach (string code in new[]
        {
            "ONBOARD_IO_MODULE_DISCONNECTED", "ONBOARD_SLOT_LOCK_FEEDBACK_LOST",
            "ONBOARD_SLOT_LIGHT_CURTAIN_BLOCKED", "ONBOARD_RULE_GATEWAY_DISCONNECTED",
            "ONBOARD_STATION_OPERATION_OVERDUE", "ONBOARD_FLEET_CLOCK_SKEW"
        })
        {
            Assert.False(ProtocolErrorCodes.Contains(code));
        }

        // 服务端侧也没有任何一处把告警码往 ErrorCode 上映射。
        string root = FindRepositoryRoot();
        foreach (string file in new[]
        {
            Path.Combine(root, "src", "ControlServer.Domain", "OnboardAlarmModels.cs"),
            Path.Combine(root, "src", "ControlServer.Infrastructure", "Persistence", "OnboardAlarmProjectionStore.cs")
        })
        {
            // 扫描前剔除注释：说明「这里不用 ErrorCode」的那句话本身不该把测试判红。
            Assert.DoesNotContain("ErrorCode", WithoutComments(File.ReadAllText(file)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheAlarmCardWasAddedWithoutTouchingTheDashboardMainFiles()
    {
        // **这是 #12 那条自注册约定的第一次真实检验。**卡片被目录接住、被主渲染器渲染，而看板主文件
        // 一个字节都没有提到过它——主文件里连 alarm 这个词都不出现。
        DashboardCardCatalog catalog = DashboardCardCatalog.Discovered;
        IDashboardCard card = catalog.Cards.Single(candidate => candidate.CardId == "onboard-alarms");
        Assert.Equal(DashboardView.Fleet, card.View);
        Assert.Equal(DashboardPaths.QueryPrefix + "onboard-alarms", card.SourcePath);

        DashboardQueryEndpointCatalog endpoints =
            DashboardQueryEndpointCatalog.Discover(typeof(FleetSessionsQueryEndpoint).Assembly);
        Assert.Contains(card.SourcePath, endpoints.Endpoints.Select(endpoint => endpoint.Path));

        string root = FindRepositoryRoot();
        foreach (string mainFile in new[]
        {
            Path.Combine(root, "src", "ControlServer.Dashboard", "DashboardPageRenderer.cs"),
            Path.Combine(root, "src", "ControlServer.Dashboard", "DashboardCardCatalog.cs"),
            Path.Combine(root, "src", "ControlServer.Dashboard", "Program.cs"),
            Path.Combine(root, "src", "ControlServer.Host", "Composition", "DashboardQueryModule.cs")
        })
        {
            Assert.DoesNotContain("alarm", File.ReadAllText(mainFile), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task TheEndpointServesRealRowsAndTheCardRendersThemIncludingTheLostContactReason()
    {
        await using AlarmFixture fixture = await AlarmFixture.CreateAsync();
        await fixture.MarkSessionReadyAsync("AGV-01");
        await fixture.MarkSessionReadyAsync("AGV-02");
        await fixture.Store.RecordSnapshotAsync(
            Snapshot("AGV-01", 1, Alarm("ONBOARD_FLEET_CLOCK_SKEW")),
            sessionGeneration: 1,
            Now,
            TestContext.Current.CancellationToken);
        await fixture.Store.RecordSnapshotAsync(
            Snapshot("AGV-03", 1, Alarm("ONBOARD_RULE_GATEWAY_DISCONNECTED")),
            sessionGeneration: 1,
            Now,
            TestContext.Current.CancellationToken);

        IDashboardCard card = DashboardCardCatalog.Discovered.Cards
            .Single(candidate => candidate.CardId == "onboard-alarms");
        IDashboardQueryEndpoint endpoint = DashboardQueryEndpointCatalog
            .Discover(typeof(FleetSessionsQueryEndpoint).Assembly)
            .Endpoints.Single(candidate => candidate.Path == card.SourcePath);

        object rows = await endpoint.ReadAsync(fixture.Context, TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        string html = card.RenderFact(document.RootElement);

        Assert.Contains("ONBOARD_FLEET_CLOCK_SKEW", html, StringComparison.Ordinal);

        // AGV-02 在线但没报过快照；AGV-03 报过但已失联——两者都显示原因，都不显示旧告警。
        Assert.Contains(VehicleAlarmProjection.NeverReportedReason, html, StringComparison.Ordinal);
        Assert.Contains(VehicleAlarmProjection.LinkDownReason, html, StringComparison.Ordinal);
        Assert.DoesNotContain("ONBOARD_RULE_GATEWAY_DISCONNECTED", html, StringComparison.Ordinal);
    }

    private static OnboardAlarmSnapshotView Snapshot(string agvId, long sequence, params OnboardAlarmEntry[] alarms) =>
        new(agvId, sequence, Now, alarms);

    private static OnboardAlarmEntry Alarm(string code, OnboardAlarmScope scope = OnboardAlarmScope.Fleet) =>
        new(code, "WARNING", Now, scope, $"{code} 触发。");

    private static string WithoutComments(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                    && !line.StartsWith("/*", StringComparison.Ordinal)
                    && !line.StartsWith('*')));

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

    private sealed class AlarmFixture : IAsyncDisposable
    {
        private AlarmFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            OnboardAlarmProjectionStore store)
        {
            Connection = connection;
            Context = context;
            Store = store;
        }

        private SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public OnboardAlarmProjectionStore Store { get; }

        public static async Task<AlarmFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new AlarmFixture(connection, context, new OnboardAlarmProjectionStore(context));
        }

        public async Task MarkSessionReadyAsync(string agvId)
        {
            Context.SessionRecoveries.Add(new SessionRecoveryRow
            {
                AgvId = agvId,
                SessionGeneration = 1,
                ProtocolCommit = "c",
                ManifestSha256 = "m",
                ProfileId = "WIRE_TO_GATE_MVP",
                ProtocolVersion = 3,
                Readiness = SessionReadiness.Ready,
                ReasonCode = "READY",
                UpdatedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task DropSessionAsync(string agvId)
        {
            Context.SessionRecoveries.RemoveRange(
                await Context.SessionRecoveries.Where(row => row.AgvId == agvId)
                    .ToArrayAsync(TestContext.Current.CancellationToken));
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
