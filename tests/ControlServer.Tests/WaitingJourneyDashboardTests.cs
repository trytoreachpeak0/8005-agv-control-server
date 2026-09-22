using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 车队视图的「等人中的旅程」（control-server#273）：只读查询端点给的字段，与卡片把它们写成的字。
/// </summary>
/// <remarks>
/// <para>
/// <b>看板规则：只显示落库的事实或派车时算出的值。</b>已等多久从落库的 <c>StageSince</c> 算，不从 <c>UpdatedAt</c>——
/// 这里的行都故意让 <c>UpdatedAt</c> 晚于阶段起点，谁拿错了列，数字就对不上。电量与读数时间是监看落库的那一次读数，看板从不自己去读 RIoT。
/// </para>
/// <para>
/// 库由迁移建出，所以读到的四列就是 <c>WaitingJourneyStageSince</c> 那个迁移加的。
/// </para>
/// </remarks>
public sealed class WaitingJourneyDashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 停着等的都列：闸口等卸货（没有原因码）、阻断；路上的只在说出了原因时列；正常在路上的、已完成的不列。
    /// 已等多久从阶段起点算，电量与读数时间照库里的写，等级按两道线判，过没过门槛一并给出。
    /// </summary>
    [Fact]
    public async Task EveryWaitingJourneyIsListedWithHowLongItHasWaitedFromItsStageStartAndItsLastBatteryReading()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.AddAsync("D-GATE", JourneyRuntimeStage.AwaitingUnloadResult, stageStart: Now.AddMinutes(-125),
            battery: 9, readAt: Now.AddSeconds(-2));
        await database.AddAsync("D-BLOCKED", JourneyRuntimeStage.Blocked, stageStart: Now.AddMinutes(-4),
            battery: 35, readAt: Now.AddSeconds(-2), reason: "LOAD_RESULT_REQUIRES_RECOVERY");
        await database.AddAsync("D-HANG", JourneyRuntimeStage.AwaitingGateArrival, stageStart: Now.AddMinutes(-20),
            battery: 70, readAt: Now.AddSeconds(-2), reason: "VEHICLE_ORDER_FAILED");
        await database.AddAsync("D-MOVING", JourneyRuntimeStage.AwaitingGateArrival, stageStart: Now.AddMinutes(-3),
            battery: null, readAt: null);
        await database.AddAsync("D-DONE", JourneyRuntimeStage.Completed, stageStart: Now.AddHours(-2),
            battery: 50, readAt: Now.AddHours(-2));

        using JsonDocument fact = await ReadAsync(database);

        Assert.Equal(600, fact.RootElement.GetProperty("warningAfterSeconds").GetInt64());
        Assert.Equal(30, fact.RootElement.GetProperty("minimumBatteryPercent").GetInt32());
        Assert.Equal(15, fact.RootElement.GetProperty("rescueBatteryPercent").GetInt32());
        JsonElement[] journeys = [.. fact.RootElement.GetProperty("journeys").EnumerateArray()];
        Assert.Equal(
            ["AGV-D-BLOCKED", "AGV-D-GATE", "AGV-D-HANG"],
            journeys.Select(journey => journey.GetProperty("agvId").GetString()));

        JsonElement gate = journeys[1];
        Assert.Equal(JourneyIdentity.ForAnchorDemand("D-GATE"), gate.GetProperty("journeyId").GetString());
        Assert.Equal("AwaitingUnloadResult", gate.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, gate.GetProperty("blockReasonCode").ValueKind);
        Assert.Equal(Now.AddMinutes(-125), gate.GetProperty("stageSince").GetDateTimeOffset());
        Assert.Equal(125 * 60, gate.GetProperty("waitedSeconds").GetInt64());
        Assert.True(gate.GetProperty("pastWarningThreshold").GetBoolean());
        Assert.Equal(9, gate.GetProperty("batteryPercent").GetInt32());
        Assert.Equal(Now.AddSeconds(-2), gate.GetProperty("batteryObservedAt").GetDateTimeOffset());
        Assert.Equal("BelowRescueLine", gate.GetProperty("batteryLevel").GetString());

        JsonElement blocked = journeys[0];
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", blocked.GetProperty("blockReasonCode").GetString());
        Assert.Equal(4 * 60, blocked.GetProperty("waitedSeconds").GetInt64());
        Assert.False(blocked.GetProperty("pastWarningThreshold").GetBoolean());
        Assert.Equal("Sufficient", blocked.GetProperty("batteryLevel").GetString());

        JsonElement hang = journeys[2];
        Assert.Equal("VEHICLE_ORDER_FAILED", hang.GetProperty("blockReasonCode").GetString());
        Assert.Equal("Sufficient", hang.GetProperty("batteryLevel").GetString());
    }

    /// <summary>
    /// 读不到电量（监看那一刻 RIoT 没给百分比）与还没读过，都是「未知」：一行照列，不省略。等级是 Unknown，百分比是 null；
    /// 读过但读不到的，读数时间照写。
    /// </summary>
    [Fact]
    public async Task AJourneyWhoseBatteryWasNotReadOrCouldNotBeReadIsStillListedAsUnknown()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.AddAsync("D-FAILED-READ", JourneyRuntimeStage.Blocked, stageStart: Now.AddMinutes(-30),
            battery: null, readAt: Now.AddSeconds(-2), reason: "UNLOAD_RESULT_REQUIRES_RECOVERY");
        await database.AddAsync("D-NEVER-READ", JourneyRuntimeStage.AwaitingSublot, stageStart: Now.AddSeconds(-5),
            battery: null, readAt: null);

        using JsonDocument fact = await ReadAsync(database);

        JsonElement[] journeys = [.. fact.RootElement.GetProperty("journeys").EnumerateArray()];
        Assert.Equal(2, journeys.Length);
        foreach (JsonElement journey in journeys)
        {
            Assert.Equal(JsonValueKind.Null, journey.GetProperty("batteryPercent").ValueKind);
            Assert.Equal("Unknown", journey.GetProperty("batteryLevel").GetString());
        }
        Assert.Equal(Now.AddSeconds(-2), journeys[0].GetProperty("batteryObservedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, journeys[1].GetProperty("batteryObservedAt").ValueKind);
    }

    /// <summary>卡片把等级写成人看的字：救命线下写需要人工挪车充电，接单线下写低于接单线，读不到写未知；时长写成小时与分。</summary>
    [Fact]
    public async Task TheCardSaysWhatToDoAtEachBatteryLevelAndWritesUnknownForAMissingReading()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.AddAsync("D-RESCUE", JourneyRuntimeStage.AwaitingUnloadResult, stageStart: Now.AddMinutes(-125),
            battery: 9, readAt: Now.AddSeconds(-2));
        await database.AddAsync("D-LOW", JourneyRuntimeStage.Blocked, stageStart: Now.AddMinutes(-12),
            battery: 22, readAt: Now.AddSeconds(-2), reason: "LOAD_RESULT_REQUIRES_RECOVERY");
        await database.AddAsync("D-UNKNOWN", JourneyRuntimeStage.Blocked, stageStart: Now.AddMinutes(-12),
            battery: null, readAt: Now.AddSeconds(-2), reason: "LOAD_RESULT_REQUIRES_RECOVERY");
        await database.AddAsync("D-FINE", JourneyRuntimeStage.AwaitingSublot, stageStart: Now.AddMinutes(-1),
            battery: 80, readAt: Now.AddSeconds(-2));
        using JsonDocument fact = await ReadAsync(database);

        string html = new WaitingJourneyCard().RenderFact(fact.RootElement);

        AssertRowContains(html, "AGV-D-RESCUE", ["闸口等卸货", "2 小时 5 分", "9%", "需要人工挪车充电"]);
        AssertRowContains(html, "AGV-D-LOW", ["阻断", "LOAD_RESULT_REQUIRES_RECOVERY", "12 分", "22%", "低于接单线"]);
        AssertRowContains(html, "AGV-D-UNKNOWN", ["未知"]);
        AssertRowContains(html, "AGV-D-FINE", ["取货站等录入", "80%"]);
        Assert.DoesNotContain("需要人工挪车充电", RowOf(html, "AGV-D-LOW"), StringComparison.Ordinal);
        Assert.DoesNotContain("低于接单线", RowOf(html, "AGV-D-FINE"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardSaysSoWhenNobodyIsWaited()
    {
        using JsonDocument empty = JsonDocument.Parse("""{"journeys":[]}""");
        Assert.Contains("没有等人中的旅程", new WaitingJourneyCard().RenderFact(empty.RootElement), StringComparison.Ordinal);
    }

    /// <summary>卡片与端点都被各自的目录发现：新增一个数据面只加文件，这里核它真的挂上了、路径对得上。</summary>
    [Fact]
    public void TheCardAndItsEndpointAreDiscoveredOnTheSamePath()
    {
        IDashboardCard card = Assert.Single(
            DashboardCardCatalog.Discovered.Cards, candidate => candidate.CardId == "waiting-journeys");
        Assert.Equal(DashboardView.Fleet, card.View);
        Assert.Contains(
            DashboardQueryEndpointCatalog.Discover(typeof(WaitingJourneysQueryEndpoint).Assembly).Endpoints,
            endpoint => endpoint.Path == card.SourcePath);
    }

    // --- helpers ----------------------------------------------------------------------------------------------

    private static void AssertRowContains(string html, string agvId, string[] expected)
    {
        string row = RowOf(html, agvId);
        foreach (string text in expected)
        {
            Assert.Contains(text, row, StringComparison.Ordinal);
        }
    }

    /// <summary>只取这辆车那一行，免得一条断言读到邻行的字而通过。</summary>
    private static string RowOf(string html, string agvId)
    {
        int start = html.IndexOf($"<td>{agvId}</td>", StringComparison.Ordinal);
        Assert.True(start >= 0, html);
        int rowStart = html.LastIndexOf("<tr", start, StringComparison.Ordinal);
        int rowEnd = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(html[rowStart..rowEnd]);
    }

    private static async Task<JsonDocument> ReadAsync(DashboardDatabase database)
    {
        await using ControlServerDbContext reading = database.NewContext();
        object result = await new WaitingJourneysQueryEndpoint(new JourneyRuntimeOptions(), new FixedClock(Now))
            .ReadAsync(reading, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(JsonSerializer.Serialize(result));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DashboardDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DashboardDatabase(SqliteConnection connection) => _connection = connection;

        public static async Task<DashboardDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DashboardDatabase database = new(connection);
            await using ControlServerDbContext context = database.NewContext();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public ControlServerDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);

        /// <summary>
        /// 按生产的写法落一行：先以阶段起点那一刻新增（上下文据此盖 <c>StageSince</c>），再以一个更晚的 <c>UpdatedAt</c> 记下监看的读数——
        /// 所以 <c>UpdatedAt</c> 与阶段起点必然不同。
        /// </summary>
        public async Task AddAsync(
            string demandId,
            JourneyRuntimeStage stage,
            DateTimeOffset stageStart,
            int? battery,
            DateTimeOffset? readAt,
            string? reason = null)
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await using (ControlServerDbContext add = NewContext())
            {
                JourneyRuntimeRow row = WaitingJourneyBatteryWatchTests.Runtime(demandId, stage, stageStart);
                row.SetBlockReason(reason, stageStart);
                add.JourneyRuntimes.Add(row);
                await add.SaveChangesAsync(cancellationToken);
            }
            await using ControlServerDbContext update = NewContext();
            JourneyRuntimeRow saved = await update.JourneyRuntimes.SingleAsync(
                row => row.DemandId == demandId, cancellationToken);
            Assert.Equal(stageStart, saved.StageSince);
            saved.WaitingBatteryPercent = battery;
            saved.WaitingBatteryObservedAt = readAt;
            saved.UpdatedAt = Now.AddSeconds(-1);
            await update.SaveChangesAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
