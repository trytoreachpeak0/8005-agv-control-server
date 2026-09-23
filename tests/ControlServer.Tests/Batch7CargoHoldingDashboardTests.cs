using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-12（control-server#217）的看板：持货等单卡片与端点、积压卡片的层与等待年龄、多需求旅程的需求列表。
/// </summary>
/// <remarks>
/// 库由迁移建出，本票零迁移：这里读到的每一列都是批次7-01～7-10 已经建好、写好的。端点的时钟与持货期限长度从构造注入，
/// 所以剩余时间是算得出的确定值，不是「大约」。
/// </remarks>
public sealed class Batch7CargoHoldingDashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HoldingTimeout = TimeSpan.FromMinutes(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // --- 持货等单端点 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AVehicleHoldingCargoShowsTheDeadlineFromTheDatabaseAndTheTimeLeftAtTheRequest()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingStationDeparture);
        journey.LoadingPhaseState = LoadingPhaseStates.CargoHoldingWait;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-12);
        await database.SeedAsync(journey);

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));

        Assert.Equal("AGV-01", row.GetProperty("agvId").GetString());
        Assert.Equal(journey.JourneyId, row.GetProperty("journeyId").GetString());
        Assert.Equal("PICKUP-1", row.GetProperty("stationId").GetString());
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, row.GetProperty("loadingPhaseState").GetString());
        Assert.Equal(Now.AddMinutes(-12), row.GetProperty("cargoHoldingStartedAt").GetDateTimeOffset());
        Assert.Equal(Now.AddMinutes(18), row.GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());
        Assert.Equal(18 * 60, row.GetProperty("remainingSeconds").GetInt64());
        Assert.False(row.GetProperty("deadlinePassed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("closedReason").ValueKind);
    }

    /// <summary>
    /// 期限已过、而一批装货还在执行：引擎等这一批安全闭环才结束等单（ADR-cross-0057「到期不打断正在进行的仓位操作」），
    /// 这段时间里看板不给负数，说「已到期，等待本次装货闭环」。
    /// </summary>
    [Fact]
    public async Task PastTheDeadlineWhileALoadBatchIsStillRunningShowsNoNegativeTimeAndSaysItWaitsForTheBatch()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingLoadResult);
        journey.LoadingPhaseState = LoadingPhaseStates.VehicleFull;
        journey.FullSlotPositionsJson = """["FRONT","REAR"]""";
        journey.CargoHoldingStartedAt = Now.AddMinutes(-41);
        await database.SeedAsync(journey);

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));

        Assert.Equal(Now.AddMinutes(-11), row.GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());
        Assert.Equal(0, row.GetProperty("remainingSeconds").GetInt64());
        Assert.True(row.GetProperty("deadlinePassed").GetBoolean());
        Assert.True(row.GetProperty("awaitingLoadBatchClosure").GetBoolean());

        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains("<td>已到期，等待本次装货闭环</td>", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"-\s*\d+\s*(小时|分|秒)", html);
    }

    /// <summary>
    /// 两侧满没满取批次7-07 落库的判定（<c>FullSlotPositionsJson</c>），看板不另算：一侧满、两侧满、都不满、还没判过四格。
    /// 没判过（列为空）说不出来，就说不出来，不当成「未满」。
    /// </summary>
    [Theory]
    [InlineData("""["FRONT"]""", LoadingPhaseStates.CargoHoldingWait, "True", "False", "前侧：已满", "后侧：未满")]
    [InlineData("""["REAR"]""", LoadingPhaseStates.CargoHoldingWait, "False", "True", "前侧：未满", "后侧：已满")]
    [InlineData("""["FRONT","REAR"]""", LoadingPhaseStates.VehicleFull, "True", "True", "前侧：已满", "后侧：已满")]
    [InlineData("[]", LoadingPhaseStates.CargoHoldingWait, "False", "False", "前侧：未满", "后侧：未满")]
    [InlineData(null, LoadingPhaseStates.CargoHoldingWait, null, null, "前侧：未判定", "后侧：未判定")]
    public async Task EachSideIsFullExactlyAsTheLoadingPhaseJudgedIt(
        string? fullSlotPositionsJson,
        string state,
        string? frontFull,
        string? rearFull,
        string frontText,
        string rearText)
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingStationDeparture);
        journey.LoadingPhaseState = state;
        journey.FullSlotPositionsJson = fullSlotPositionsJson;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-5);
        await database.SeedAsync(journey);

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));

        Assert.Equal(frontFull, NullableBool(row.GetProperty("frontFull")));
        Assert.Equal(rearFull, NullableBool(row.GetProperty("rearFull")));
        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains(frontText, html, StringComparison.Ordinal);
        Assert.Contains(rearText, html, StringComparison.Ordinal);
    }

    /// <summary>四种结束原因各一：阶段显示「已结束」，原因照库里的结束原因写，不写成故障或告警（规格 8.8 第 4 条）。</summary>
    [Theory]
    [InlineData(LoadingClosedReasons.VehicleFull, "两侧装满")]
    [InlineData(LoadingClosedReasons.CargoHoldingTimeout, "持货超时")]
    [InlineData(LoadingClosedReasons.WaitingStationYield, "另一辆车以本站为下一停靠，本车结束等单")]
    [InlineData(LoadingClosedReasons.PlannedLoadingComplete, "计划装货完成")]
    public async Task AClosedLoadingPhaseSaysWhyItClosedAndNeverAsAnAlarm(string closedReason, string text)
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingDepartureSafety);
        journey.LoadingPhaseState = LoadingPhaseStates.Closed;
        journey.LoadingClosedReason = closedReason;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-40);
        if (closedReason == LoadingClosedReasons.WaitingStationYield)
        {
            journey.YieldTriggeredAt = Now.AddMinutes(-20);
            journey.YieldTriggeredByVehicleKey = "KEY-AGV-02";
        }
        await database.SeedAsync(journey);

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));
        Assert.Equal(LoadingPhaseStates.Closed, row.GetProperty("loadingPhaseState").GetString());
        Assert.Equal(closedReason, row.GetProperty("closedReason").GetString());
        Assert.False(row.GetProperty("awaitingLoadBatchClosure").GetBoolean());

        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains("已结束", html, StringComparison.Ordinal);
        Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.DoesNotContain("告警", html, StringComparison.Ordinal);
        Assert.DoesNotContain("故障", html, StringComparison.Ordinal);
        Assert.DoesNotContain("alarm", html, StringComparison.OrdinalIgnoreCase);
        // 结束之后不再倒计时：剩余那一格不给时长。
        Assert.DoesNotMatch(@"\d+\s*分\s*\d+\s*秒", html);
    }

    /// <summary>
    /// 让站以结束原因为准（票面评论，cs#213 审查）：持货期限先到、让站触发晚到时，结束原因是持货超时，
    /// 而 <c>YieldTriggeredByVehicleKey</c> 仍然有值。看板不能因为这一列有值就写「让站」。
    /// </summary>
    [Fact]
    public async Task AYieldThatArrivedAfterTheDeadlineIsNotShownAsAYield()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingDepartureSafety);
        journey.LoadingPhaseState = LoadingPhaseStates.Closed;
        journey.LoadingClosedReason = LoadingClosedReasons.CargoHoldingTimeout;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-40);
        journey.YieldTriggeredAt = Now.AddMinutes(-5);
        journey.YieldTriggeredByVehicleKey = "KEY-AGV-02";
        await database.SeedAsync(journey);

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, row.GetProperty("closedReason").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("yieldedToVehicleKey").ValueKind);

        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains("持货超时", html, StringComparison.Ordinal);
        Assert.DoesNotContain("让站", html, StringComparison.Ordinal);
        Assert.DoesNotContain("另一辆车", html, StringComparison.Ordinal);
        Assert.DoesNotContain("KEY-AGV-02", html, StringComparison.Ordinal);
    }

    /// <summary>让站的那一行说出是哪辆车触发的：批次7-08 落了库（<c>YieldTriggeredByVehicleKey</c>）。</summary>
    [Fact]
    public async Task AYieldNamesTheVehicleThatTriggeredIt()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingDepartureSafety);
        journey.LoadingPhaseState = LoadingPhaseStates.Closed;
        journey.LoadingClosedReason = LoadingClosedReasons.WaitingStationYield;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-10);
        journey.YieldTriggeredAt = Now.AddMinutes(-2);
        journey.YieldTriggeredByVehicleKey = "KEY-AGV-02";
        await database.SeedAsync(journey);

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));
        Assert.Equal("KEY-AGV-02", row.GetProperty("yieldedToVehicleKey").GetString());
        Assert.Equal(Now.AddMinutes(-2), row.GetProperty("yieldTriggeredAt").GetDateTimeOffset());

        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains("另一辆车以本站为下一停靠，本车结束等单（KEY-AGV-02）", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 哪些车有一行：站在装货停靠上的（装货阶段从没写过时就是装货中——引擎只在状态变了才写这一列，<c>LOADING</c> 不写），
    /// 以及装货阶段判过、还没结束的；已结束的只在车还停在装货停靠上时列（审查 L3）。还在去第一个取货站路上、从没判过的车，
    /// 装货阶段结束后已离站的车，旅程已完成的车，都不列。四种状态各一。
    /// </summary>
    [Fact]
    public async Task OnlyVehiclesInTheirLoadingPhaseHaveARowAndEachOfTheFourStatesReads()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow loading = Journey("D-LOADING", "AGV-01", JourneyRuntimeStage.AwaitingSublot);
        JourneyRuntimeRow waiting = Journey("D-WAIT", "AGV-02", JourneyRuntimeStage.AwaitingStationDeparture);
        waiting.LoadingPhaseState = LoadingPhaseStates.CargoHoldingWait;
        waiting.CargoHoldingStartedAt = Now.AddMinutes(-1);
        JourneyRuntimeRow full = Journey("D-FULL", "AGV-03", JourneyRuntimeStage.AwaitingStationDeparture);
        full.LoadingPhaseState = LoadingPhaseStates.VehicleFull;
        full.CargoHoldingStartedAt = Now.AddMinutes(-1);
        JourneyRuntimeRow closedAtStation = Journey("D-CLOSED", "AGV-04", JourneyRuntimeStage.AwaitingDepartureSafety);
        closedAtStation.LoadingPhaseState = LoadingPhaseStates.Closed;
        closedAtStation.LoadingClosedReason = LoadingClosedReasons.CargoHoldingTimeout;
        closedAtStation.CargoHoldingStartedAt = Now.AddMinutes(-40);
        JourneyRuntimeRow closedOnTheWay = Journey("D-CLOSED-GONE", "AGV-07", JourneyRuntimeStage.AwaitingGateArrival);
        closedOnTheWay.LoadingPhaseState = LoadingPhaseStates.Closed;
        closedOnTheWay.LoadingClosedReason = LoadingClosedReasons.VehicleFull;
        closedOnTheWay.CargoHoldingStartedAt = Now.AddMinutes(-3);
        JourneyRuntimeRow notYetThere = Journey("D-EN-ROUTE", "AGV-05", JourneyRuntimeStage.AwaitingPickupArrival);
        JourneyRuntimeRow completed = Journey("D-DONE", "AGV-06", JourneyRuntimeStage.Completed);
        completed.LoadingPhaseState = LoadingPhaseStates.Closed;
        completed.LoadingClosedReason = LoadingClosedReasons.PlannedLoadingComplete;
        foreach (JourneyRuntimeRow journey in new[] { loading, waiting, full, closedAtStation, closedOnTheWay, notYetThere, completed })
        {
            await database.SeedAsync(journey);
        }
        await database.CompleteStopAsync(closedOnTheWay, JourneyStopRoles.Pickup);

        JsonElement[] rows = await ReadCargoHoldingAsync(database.NewContext());

        Assert.Equal(
            ["AGV-01:LOADING:PICKUP-1", "AGV-02:CARGO_HOLDING_WAIT:PICKUP-1", "AGV-03:VEHICLE_FULL:PICKUP-1", "AGV-04:CLOSED:PICKUP-1"],
            rows.Select(row =>
                    $"{row.GetProperty("agvId").GetString()}:{row.GetProperty("loadingPhaseState").GetString()}:{row.GetProperty("stationId").GetString()}")
                .Order(StringComparer.Ordinal));
        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains("<td>装货中</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>持货等单</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>已装满</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>已结束</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AGV-05", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AGV-06", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AGV-07", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 审查 L3：已结束的那一行只在车还停在装货停靠上时显示，车离站之后这一行消失——「已结束」的原因在车离站之前操作员已经看得到，
    /// 车去卸货的路上一直挂着只会把卡片占满。同一趟旅程读两次：离站前在，离站后（取货停靠完成、去关卡）不在。
    /// </summary>
    [Fact]
    public async Task AClosedRowDisappearsOnceTheVehicleLeavesTheStation()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingDepartureSafety);
        journey.LoadingPhaseState = LoadingPhaseStates.Closed;
        journey.LoadingClosedReason = LoadingClosedReasons.WaitingStationYield;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-6);
        journey.YieldTriggeredAt = Now.AddMinutes(-1);
        journey.YieldTriggeredByVehicleKey = "KEY-AGV-02";
        await database.SeedAsync(journey);

        JsonElement atStation = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, atStation.GetProperty("closedReason").GetString());

        await database.CompleteStopAsync(journey, JourneyStopRoles.Pickup);
        await database.SetStageAsync(journey, JourneyRuntimeStage.AwaitingGateArrival);

        Assert.Empty(await ReadCargoHoldingAsync(database.NewContext()));
        Assert.Equal(
            "<p>没有处于装货阶段的车</p>",
            new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext())));
    }

    /// <summary>
    /// 持货期限只在适用持货等单时存在（program#94：「不适用持货等单时为 null」），而起算点不论适用与否都会在第一批装货闭环时写下。
    /// 看板不重算适用性（那要读车能服务的分区配置），按状态推：等单、装满、以及因超时／让站／装满而结束的，必然适用，期限由
    /// <c>LoadingPhaseMachine.Deadline</c> 给；计划装货完成的不适用；<b>装货中一律不给</b>（审查 M3）——装货中的适用性引擎每轮按当前分区参数现算，
    /// 有过追加之后参数又改成禁止追加时，引擎不给期限、也不会超时关闭，看板没法跟着这个现算结果走，有追加也不给。
    /// </summary>
    [Theory]
    [InlineData(LoadingPhaseStates.CargoHoldingWait, null, false, true)]
    [InlineData(LoadingPhaseStates.VehicleFull, null, false, true)]
    [InlineData(LoadingPhaseStates.Closed, LoadingClosedReasons.CargoHoldingTimeout, false, true)]
    [InlineData(LoadingPhaseStates.Closed, LoadingClosedReasons.WaitingStationYield, false, true)]
    [InlineData(LoadingPhaseStates.Closed, LoadingClosedReasons.VehicleFull, false, true)]
    [InlineData(LoadingPhaseStates.Closed, LoadingClosedReasons.PlannedLoadingComplete, false, false)]
    [InlineData(null, null, false, false)]
    [InlineData(null, null, true, false)]
    [InlineData(LoadingPhaseStates.Loading, null, false, false)]
    [InlineData(LoadingPhaseStates.Loading, null, true, false)]
    public async Task TheDeadlineIsShownOnlyWhereCargoHoldingApplies(
        string? state,
        string? closedReason,
        bool appendedOnTheWay,
        bool deadlineShown)
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingSublot);
        journey.LoadingPhaseState = state;
        journey.LoadingClosedReason = closedReason;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-5);
        await database.SeedAsync(journey);
        if (appendedOnTheWay)
        {
            await database.AppendDemandAsync(journey, "D-APPENDED", dispatchZoneParameterVersion: 1);
        }

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));

        if (deadlineShown)
        {
            Assert.Equal(Now.AddMinutes(25), row.GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty("cargoHoldingDeadlineAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("remainingSeconds").ValueKind);
            Assert.False(row.GetProperty("deadlinePassed").GetBoolean());
        }
    }

    /// <summary>
    /// 崩溃点与重连（票面「测试接缝」）：期限与让站原因都在服务端库里，服务端重启之后换一个 <c>DbContext</c>、换一个端点实例读同一份库，
    /// 期限一秒不差、让站原因照旧；剩余时间随请求时刻走，不是重启时重新起算。车载端断线不影响这一行——数据不来自车载端。
    /// </summary>
    [Fact]
    public async Task AfterARestartTheDeadlineAndTheYieldReadTheSameFromTheSameDatabase()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow holding = Journey("D-HOLD", "AGV-01", JourneyRuntimeStage.AwaitingStationDeparture);
        holding.LoadingPhaseState = LoadingPhaseStates.CargoHoldingWait;
        holding.CargoHoldingStartedAt = Now.AddMinutes(-10);
        JourneyRuntimeRow yielded = Journey("D-YIELD", "AGV-02", JourneyRuntimeStage.AwaitingDepartureSafety);
        yielded.LoadingPhaseState = LoadingPhaseStates.Closed;
        yielded.LoadingClosedReason = LoadingClosedReasons.WaitingStationYield;
        yielded.CargoHoldingStartedAt = Now.AddMinutes(-10);
        yielded.YieldTriggeredAt = Now.AddMinutes(-4);
        yielded.YieldTriggeredByVehicleKey = "KEY-AGV-03";
        await database.SeedAsync(holding);
        await database.SeedAsync(yielded);
        JsonElement[] before = await ReadCargoHoldingAsync(database.NewContext());

        // 「重启」：旧上下文与旧端点都已丢弃，四分钟之后一个新的上下文、新的端点读同一份库。
        JsonElement[] after = await ReadCargoHoldingAsync(database.NewContext(), now: Now.AddMinutes(4));

        Assert.Equal(Now.AddMinutes(20), after[0].GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());
        Assert.Equal(
            before[0].GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset(),
            after[0].GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());
        Assert.Equal(20 * 60, before[0].GetProperty("remainingSeconds").GetInt64());
        Assert.Equal(16 * 60, after[0].GetProperty("remainingSeconds").GetInt64());
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, after[1].GetProperty("closedReason").GetString());
        Assert.Equal("KEY-AGV-03", after[1].GetProperty("yieldedToVehicleKey").GetString());
    }

    [Fact]
    public void TheCargoHoldingCardIsSelfRegisteredWithAUniqueIdAndReadsItsOwnDashboardQueryEndpoint()
    {
        IDashboardCard card = Assert.Single(
            DashboardCardCatalog.Discovered.Cards, candidate => candidate is CargoHoldingCard);
        Assert.Single(DashboardCardCatalog.Discovered.Cards, candidate => candidate.CardId == card.CardId);
        Assert.Equal(DashboardView.Fleet, card.View);
        Assert.StartsWith(DashboardPaths.QueryPrefix, card.SourcePath, StringComparison.Ordinal);
        Assert.Contains(
            DashboardQueryEndpointCatalog.Discover(typeof(CargoHoldingQueryEndpoint).Assembly).Endpoints,
            endpoint => endpoint is CargoHoldingQueryEndpoint && endpoint.Path == card.SourcePath);
    }

    /// <summary>
    /// 持货期限的长度是服务端配置（<c>JourneyRuntime:CargoHoldingTimeout</c>），库里只有起算点。看板端点挂在宿主上时，
    /// 用的是宿主的同一份 <see cref="IOptions{TOptions}"/>——与引擎判超时用的那一份是同一个对象，不另读一遍配置文件：
    /// 另读一遍，命令行或别的配置源一覆盖，看板上的期限就与车上、引擎里的悄悄差开。这里把期限配成 10 分钟，经真实的
    /// <c>MapDashboardQueries</c> 与 HTTP GET 读回来，期限必须是起算点加 10 分钟，不是默认的 30 分钟。
    /// </summary>
    [Fact]
    public async Task OnTheHostTheDeadlineUsesTheHostsConfiguredCargoHoldingTimeout()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingStationDeparture);
        journey.LoadingPhaseState = LoadingPhaseStates.CargoHoldingWait;
        journey.CargoHoldingStartedAt = startedAt;
        await database.SeedAsync(journey);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{JourneyRuntimeOptions.SectionName}:cargoHoldingTimeout"] = "00:10:00"
        });
        builder.Services.AddOptions<JourneyRuntimeOptions>()
            .Bind(builder.Configuration.GetSection(JourneyRuntimeOptions.SectionName));
        builder.Services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(database.Connection));
        await using WebApplication app = builder.Build();
        app.MapDashboardQueries();
        await app.StartAsync(Token);
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using HttpClient client = new() { BaseAddress = new Uri(address) };

        using HttpResponseMessage response = await client.GetAsync(new CargoHoldingCard().SourcePath, Token);
        response.EnsureSuccessStatusCode();
        using JsonDocument fact = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        await app.StopAsync(Token);

        JsonElement row = Assert.Single(fact.RootElement.GetProperty("journeys").EnumerateArray());
        Assert.Equal(startedAt.AddMinutes(10), row.GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());
    }

    [Fact]
    public void WithNoVehicleInItsLoadingPhaseTheCardSaysSoPlainly()
    {
        using JsonDocument empty = JsonDocument.Parse("""{"journeys":[]}""");

        Assert.Equal("<p>没有处于装货阶段的车</p>", new CargoHoldingCard().RenderFact(empty.RootElement));
    }

    /// <summary>
    /// 并发读改写（票面「测试接缝」）：端点分几次读旅程、停靠、归属、需求，不开事务——这个库的 <c>BeginTransaction</c> 是
    /// <c>BEGIN IMMEDIATE</c>，看板 2 秒一刷就要 2 秒拿一次写锁，与引擎抢。代价是可能读到引擎关旅程的半途：停靠都完成了、
    /// 归属已终结、旅程行还没写完成，或者需求行还没读到。端点对这些中间态照样给出一行、不抛，下一次刷新就是完整的。
    /// </summary>
    [Fact]
    public async Task AJourneyCaughtHalfwayThroughClosingStillReadsWithoutThrowing()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow closing = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingDepartureSafety);
        closing.LoadingPhaseState = LoadingPhaseStates.VehicleFull;
        await database.SeedAsync(closing);
        await database.CompleteStopAsync(closing, JourneyStopRoles.Pickup);
        await database.CompleteStopAsync(closing, JourneyStopRoles.Unload);
        await database.SetMembershipStatusAsync(closing, "D-1", JourneyDemandStatuses.Terminated);
        await database.ExecuteAsync("DELETE FROM \"AcceptedDemands\" WHERE \"DemandId\" = 'D-1'");

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));

        Assert.Equal(JsonValueKind.Null, row.GetProperty("stationId").ValueKind);
        JsonElement demand = Assert.Single(row.GetProperty("demands").EnumerateArray());
        Assert.Equal(JourneyDemandStatuses.Terminated, demand.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, demand.GetProperty("transportDemandKey").ValueKind);
        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains("D-1：已终结", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「已到期，等待本次装货闭环」依赖的前提（审查 L1）：看板认「有一批在装」的判法与引擎给装货阶段状态机的是同一句——
    /// <c>Stage == AwaitingLoadResult</c>。引擎那一句是私有的，本票不碰引擎，所以这里逐字核引擎源码；引擎改了判法，这里红，
    /// 改的人顺着 <c>CargoHoldingQueryEndpoint.LoadBatchInProgress</c> 的注释把看板一起改掉。
    /// </summary>
    [Fact]
    public void TheLoadBatchInProgressPremiseIsTheEnginesOwn()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ControlServer.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }
        string engine = File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "Runtime", "JourneyRuntimeEngine.cs"));
        string dashboard = File.ReadAllText(
            Path.Combine(root, "src", "ControlServer.Host", "Dashboard", "CargoHoldingQueryEndpoint.cs"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(engine, @"LoadBatchInProgress\s*:"));
        Assert.Contains(
            "LoadBatchInProgress: runtime.Stage == JourneyRuntimeStage.AwaitingLoadResult,", engine, StringComparison.Ordinal);
        Assert.Contains(
            "journey.Stage == JourneyRuntimeStage.AwaitingLoadResult;", dashboard, StringComparison.Ordinal);
    }

    // --- 多需求旅程的需求列表 -----------------------------------------------------------------------------------------

    /// <summary>
    /// 一趟旅程一行，行内列出它的全部需求与各自状态：一条已卸、一条待装，列表两项、状态各自正确；
    /// 归属已被移除的需求（批次7-10 释放改派）不出现。
    /// </summary>
    [Fact]
    public async Task AJourneyOfSeveralDemandsListsEachDemandInForceWithItsOwnStatus()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-ANCHOR", "AGV-01", JourneyRuntimeStage.AwaitingSublot);
        journey.LoadingPhaseState = LoadingPhaseStates.Loading;
        journey.CargoHoldingStartedAt = Now.AddMinutes(-8);
        await database.SeedAsync(journey);
        await database.SetMembershipStatusAsync(journey, journey.DemandId, JourneyDemandStatuses.Unloaded);
        await database.AppendDemandAsync(journey, "D-SECOND", dispatchZoneParameterVersion: 1);
        await database.AppendDemandAsync(journey, "D-RELEASED", dispatchZoneParameterVersion: 1);
        await database.RemoveMembershipAsync(journey, "D-RELEASED");

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));

        Assert.Equal(
            ["D-ANCHOR:UNLOADED", "D-SECOND:PENDING_LOAD"],
            row.GetProperty("demands").EnumerateArray()
                .Select(demand => $"{demand.GetProperty("demandId").GetString()}:{demand.GetProperty("status").GetString()}"));
        Assert.Equal(
            "SUBLOT-D-SECOND|WIRE_TO_GATE",
            row.GetProperty("demands")[1].GetProperty("transportDemandKey").GetString());

        string html = new CargoHoldingCard().RenderFact(await ReadCargoHoldingFactAsync(database.NewContext()));
        Assert.Contains("SUBLOT-D-ANCHOR|WIRE_TO_GATE：已卸", html, StringComparison.Ordinal);
        Assert.Contains("SUBLOT-D-SECOND|WIRE_TO_GATE：待装", html, StringComparison.Ordinal);
        Assert.DoesNotContain("D-RELEASED", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 改派之后同一个锚需求有两行旅程（批次7-10）：旧的一趟已完成、归属已移除，新的一趟（旅程 id 带代次）在另一辆车上装货。
    /// 看板只列新的那一趟，需求在它的列表里出现一次，不在旧车上出现。
    /// </summary>
    [Fact]
    public async Task AfterARedispatchTheDemandShowsOnceOnItsNewJourneyOnly()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow released = Journey("D-1", "AGV-01", JourneyRuntimeStage.Completed);
        released.LoadingPhaseState = LoadingPhaseStates.Loading;
        await database.SeedAsync(released);
        await database.RemoveMembershipAsync(released, "D-1");
        JourneyRuntimeRow again = Journey(
            "D-1", "AGV-02", JourneyRuntimeStage.AwaitingSublot, journeyId: JourneyIdentity.ForAnchorDemand("D-1|g2"));
        await database.SeedAsync(again);

        JsonElement row = Assert.Single(await ReadCargoHoldingAsync(database.NewContext()));

        Assert.Equal("AGV-02", row.GetProperty("agvId").GetString());
        Assert.Equal(again.JourneyId, row.GetProperty("journeyId").GetString());
        JsonElement demand = Assert.Single(row.GetProperty("demands").EnumerateArray());
        Assert.Equal("D-1", demand.GetProperty("demandId").GetString());
    }

    /// <summary>
    /// 旅程阻断卡片也一趟旅程一行，列出全部未移除的需求与各自状态，不再只显示锚需求；改派之后的新旅程与旧旅程共用一个锚需求，
    /// 阻断卡片按旅程列，旧的那一趟已完成、不列。
    /// </summary>
    [Fact]
    public async Task TheBlockedJourneyCardListsEveryDemandOfTheJourneyWithItsStatus()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow released = Journey("D-ANCHOR", "AGV-01", JourneyRuntimeStage.Completed);
        await database.SeedAsync(released);
        await database.RemoveMembershipAsync(released, "D-ANCHOR");
        JourneyRuntimeRow blocked = Journey(
            "D-ANCHOR", "AGV-02", JourneyRuntimeStage.Blocked, journeyId: JourneyIdentity.ForAnchorDemand("D-ANCHOR|g2"));
        blocked.SetBlockReason("VEHICLE_CHECKPOINT_WAIT_EXCEEDED", Now.AddMinutes(-3));
        await database.SeedAsync(blocked);
        await database.SetMembershipStatusAsync(blocked, "D-ANCHOR", JourneyDemandStatuses.Loaded);
        await database.AppendDemandAsync(blocked, "D-SECOND", dispatchZoneParameterVersion: 1);
        await database.AppendDemandAsync(blocked, "D-GONE", dispatchZoneParameterVersion: 1);
        await database.RemoveMembershipAsync(blocked, "D-GONE");

        using JsonDocument fact = await ReadBlockedAsync(database.NewContext());

        JsonElement row = Assert.Single(fact.RootElement.GetProperty("journeys").EnumerateArray());
        Assert.Equal(blocked.JourneyId, row.GetProperty("journeyId").GetString());
        Assert.Equal("D-ANCHOR", row.GetProperty("demandId").GetString());
        Assert.Equal(
            ["D-ANCHOR:LOADED", "D-SECOND:PENDING_LOAD"],
            row.GetProperty("demands").EnumerateArray()
                .Select(demand => $"{demand.GetProperty("demandId").GetString()}:{demand.GetProperty("status").GetString()}"));

        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);
        Assert.Contains("SUBLOT-D-ANCHOR|WIRE_TO_GATE：已装；SUBLOT-D-SECOND|WIRE_TO_GATE：待装", html, StringComparison.Ordinal);
        Assert.DoesNotContain("D-GONE", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 批次7-10 的释放拒绝码写在旅程阻断码上（<c>DemandReleaseReasons</c>）；阻断卡片给它们中文说明，原码照旧显示。
    /// </summary>
    [Fact]
    public async Task EveryReleaseRefusalCodeOnABlockedJourneyHasAChineseDescription()
    {
        string[] refusals =
        [
            .. typeof(ControlServer.Host.Runtime.Release.DemandReleaseReasons)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Where(ControlServer.Host.Runtime.Release.DemandReleaseReasons.IsRefusalCode),
        ];
        Assert.Contains("RELEASE_FAULT_SUPERVISION_IN_EFFECT", refusals);
        Assert.Equal([], refusals.Where(code => !BlockedJourneysQueryEndpoint.Descriptions.ContainsKey(code)).ToArray());

        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingPickupArrival);
        journey.SetBlockReason("RELEASE_FAULT_SUPERVISION_IN_EFFECT", Now.AddMinutes(-1));
        await database.SeedAsync(journey);

        using JsonDocument fact = await ReadBlockedAsync(database.NewContext());
        JsonElement row = Assert.Single(fact.RootElement.GetProperty("journeys").EnumerateArray());
        Assert.Equal(
            BlockedJourneysQueryEndpoint.Descriptions["RELEASE_FAULT_SUPERVISION_IN_EFFECT"],
            row.GetProperty("blockReasonDescription").GetString());
        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);
        Assert.Contains("RELEASE_FAULT_SUPERVISION_IN_EFFECT", html, StringComparison.Ordinal);
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode(BlockedJourneysQueryEndpoint.Descriptions["RELEASE_FAULT_SUPERVISION_IN_EFFECT"]),
            html,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 在途单停住的三个码（control-server#316）上了阻断卡片：原码照旧显示，另给中文说明，告诉走到车前的人该做什么。
    /// </summary>
    /// <remarks>
    /// 码写成字面量：它们是现场看得见的东西，改名应当让这里红，而不是跟着常量悄悄改。
    /// </remarks>
    [Theory]
    [InlineData("ORDER_HANG")]
    [InlineData("ORDER_STATE_UNRECOGNIZED")]
    [InlineData("ORDER_ENDED_WITHOUT_ARRIVAL")]
    [InlineData("VEHICLE_ORDER_FAILED")]
    [InlineData("VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD")]
    // control-server#331：推进每轮抛异常时写的码。之前这种情况看板上只剩上一次写下的旧码。
    [InlineData("JOURNEY_ADVANCE_FAILED")]
    public async Task AStalledInTransitOrderIsShownWithAChineseDescription(string code)
    {
        Assert.True(BlockedJourneysQueryEndpoint.Descriptions.ContainsKey(code), $"{code} has no description");

        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow journey = Journey("D-1", "AGV-01", JourneyRuntimeStage.AwaitingPickupArrival);
        journey.SetBlockReason(code, Now.AddMinutes(-1));
        await database.SeedAsync(journey);

        using JsonDocument fact = await ReadBlockedAsync(database.NewContext());
        JsonElement row = Assert.Single(fact.RootElement.GetProperty("journeys").EnumerateArray());
        Assert.Equal(code, row.GetProperty("blockReasonCode").GetString());
        Assert.Equal(BlockedJourneysQueryEndpoint.Descriptions[code], row.GetProperty("blockReasonDescription").GetString());
        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);
        Assert.Contains(code, html, StringComparison.Ordinal);
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode(BlockedJourneysQueryEndpoint.Descriptions[code]), html, StringComparison.Ordinal);
    }

    // --- 积压卡片：层、阈值、等待年龄 -----------------------------------------------------------------------------------

    /// <summary>
    /// 三层各一条（REQ-0202）：已升级进超时层的普通任务、<c>STAGING_TO_WIRE</c> 最高带、普通带；顺序与派车排序同一个层序
    /// （超时层 → 最高带 → 等待年龄）。已升级告警的那一条带标记与所用参数版本（批次7-09 写在积压行上的两列）。
    /// </summary>
    [Fact]
    public async Task EachBacklogRowSaysWhichTierItIsInAndTheEscalatedOneCarriesItsMark()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.AddZoneParametersAsync(1, ("MAP-25-WIRE_TO_GATE", 3600));
        await database.AddBacklogAsync("D-NORMAL", TransportTaskTypes.WireToGate, createdAt: Now.AddMinutes(-20));
        await database.AddBacklogAsync("D-TOP", TransportTaskTypes.StagingToWire, createdAt: Now.AddMinutes(-10));
        await database.AddBacklogAsync(
            "D-OVERDUE", TransportTaskTypes.WireToGate, createdAt: Now.AddHours(-3), escalatedAt: Now.AddMinutes(-5), escalationVersion: 1);

        using JsonDocument fact = await ReadBacklogAsync(database.NewContext());

        JsonElement[] rows = [.. fact.RootElement.GetProperty("backlog").EnumerateArray()];
        Assert.Equal(
            ["D-OVERDUE:STARVATION_TIMEOUT", "D-TOP:TOP_BAND", "D-NORMAL:NORMAL_BAND"],
            rows.Select(row => $"{row.GetProperty("demandId").GetString()}:{row.GetProperty("tier").GetString()}"));
        Assert.Equal(Now.AddMinutes(-5), rows[0].GetProperty("starvationEscalatedAt").GetDateTimeOffset());
        Assert.Equal(1, rows[0].GetProperty("starvationEscalationParameterVersion").GetInt64());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("starvationEscalatedAt").ValueKind);
        Assert.True(fact.RootElement.GetProperty("starvationThresholdsApproved").GetBoolean());

        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.Contains("超时层", RowOf(html, "SUBLOT-D-OVERDUE"), StringComparison.Ordinal);
        Assert.Contains("已升级告警", RowOf(html, "SUBLOT-D-OVERDUE"), StringComparison.Ordinal);
        Assert.Contains("最高带（STAGING_TO_WIRE）", RowOf(html, "SUBLOT-D-TOP"), StringComparison.Ordinal);
        Assert.Contains("普通带", RowOf(html, "SUBLOT-D-NORMAL"), StringComparison.Ordinal);
        Assert.DoesNotContain("已升级告警", RowOf(html, "SUBLOT-D-NORMAL"), StringComparison.Ordinal);
        Assert.DoesNotContain("本区阈值未批准", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 优先级带与派车排序是同一个判断（调度 2026-09-22：不维护第二份）。积压行没有任务类型列，看板从业务键取任务类型，
    /// 取法是业务键拼法的逆（<see cref="TransportDemandKeys"/>，<c>HttpMesIngestCatalog</c> 用同一处拼）；带由
    /// <see cref="TaskStarvation.IsTopBandWorkType"/> 判，派车的 <see cref="TaskStarvation.InTopBand"/> 也是它。六类任务逐个对一遍，
    /// 批次号里带竖线也拆得对。
    /// </summary>
    [Theory]
    [InlineData(TransportTaskTypes.DieToWireStaging)]
    [InlineData(TransportTaskTypes.DieToOven)]
    [InlineData(TransportTaskTypes.WireToGate)]
    [InlineData(TransportTaskTypes.WireToOptical)]
    [InlineData(TransportTaskTypes.StagingToWire)]
    [InlineData(TransportTaskTypes.WireToNitrogen)]
    public void TheBacklogBandIsTheDispatchBandReadBackFromTheBusinessKey(string workType)
    {
        string key = TransportDemandKeys.Compose("SUB|LOT-7", workType);

        Assert.Equal(workType, TransportDemandKeys.WorkTypeOf(key));
        JourneyBacklogRow row = new()
        {
            DemandId = "D-1",
            TransportDemandKey = key,
            DecisionFingerprint = "f",
            ReasonCode = "R"
        };
        Assert.Equal(
            workType == TransportTaskTypes.StagingToWire,
            BacklogStanding.TierOf(row) == BacklogTier.TopBand);
        Assert.Equal(workType == TransportTaskTypes.StagingToWire, TaskStarvation.IsTopBandWorkType(workType));
        // 派车排序判带用的是需求的 WorkType（TaskStarvation.InTopBand）：拼键再取回之后，看板的带与它的结论相同。
        // 以后改了拼法而没改拆法，这里红（审查 M2）。
        AcceptedDemandSnapshot dispatched = new("D-1", key, 0, "epoch", 0, default, WorkType: workType);
        Assert.Equal(TaskStarvation.InTopBand(dispatched), BacklogStanding.TierOf(row) == BacklogTier.TopBand);
    }

    /// <summary>
    /// 积压卡片的次序就是派车排序的次序（审查 L1）：同一批数据，一份交给看板端点，一份在这里独立地拼成派车轮的任务交给
    /// <see cref="DispatchCandidateOrdering.Ranker"/>，两边次序逐条相同。数据覆盖每一层的分辨：超时层、最高带、建单时刻先后、
    /// 建单时刻不明排在同带最后、建单时刻相同再比首次看到、再比需求 id。
    /// </summary>
    [Fact]
    public async Task TheBacklogIsListedInTheDispatchRankingsOwnOrder()
    {
        (string Id, string WorkType, DateTimeOffset Created, DateTimeOffset FirstSeen, DateTimeOffset? Escalated)[] rows =
        [
            ("D-A", TransportTaskTypes.WireToGate, Now.AddMinutes(-30), Now.AddMinutes(-5), null),
            ("D-B", TransportTaskTypes.StagingToWire, Now.AddMinutes(-2), Now.AddMinutes(-1), null),
            ("D-C", TransportTaskTypes.WireToGate, Now.AddHours(-4), Now.AddMinutes(-9), Now.AddMinutes(-1)),
            ("D-D", TransportTaskTypes.WireToGate, default, Now.AddMinutes(-50), null),
            ("D-E", TransportTaskTypes.WireToGate, Now.AddMinutes(-30), Now.AddMinutes(-7), null),
            ("D-F", TransportTaskTypes.WireToGate, Now.AddMinutes(-30), Now.AddMinutes(-7), null),
            ("D-G", TransportTaskTypes.StagingToWire, Now.AddMinutes(-40), Now.AddMinutes(-3), null),
            ("D-H", TransportTaskTypes.WireToGate, Now.AddHours(-6), Now.AddMinutes(-2), Now.AddMinutes(-2)),
        ];
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        foreach ((string id, string workType, DateTimeOffset created, DateTimeOffset firstSeen, DateTimeOffset? escalated) in rows.Reverse())
        {
            await database.AddBacklogAsync(id, workType, created, firstSeen, escalatedAt: escalated, escalationVersion: escalated is null ? null : 1);
        }

        using JsonDocument fact = await ReadBacklogAsync(database.NewContext());

        DispatchTask[] tasks =
        [
            .. rows.Select(row => new DispatchTask(
                new AcceptedDemandSnapshot(
                    row.Id, $"SUBLOT-{row.Id}|{row.WorkType}", 0, "epoch", 0, default, WorkType: row.WorkType, CreatedAt: row.Created),
                row.FirstSeen)
            {
                Starvation = row.Escalated is null ? null : new TaskStarvationStanding(TimeSpan.Zero, null, null, 1, Overdue: true)
            })
        ];
        string[] dispatchOrder = [.. DispatchCandidateOrdering.Ranker().Order(tasks).Select(task => task.Snapshot.DemandId)];
        Assert.Equal(["D-H", "D-C", "D-G", "D-B", "D-E", "D-F", "D-A", "D-D"], dispatchOrder);
        Assert.Equal(
            dispatchOrder,
            fact.RootElement.GetProperty("backlog").EnumerateArray().Select(row => row.GetProperty("demandId").GetString()));
    }

    /// <summary>
    /// 超时层只读派车记下的事实（调度 2026-09-22）：<c>StarvationEscalatedAt</c> 非空就在超时层，不叠加现算的条件——排序用的参数版本不落库，
    /// 看板能依据的只有这个标记。阈值后来撤回了，标记照样显示，卡片加一句说明它是撤回之前记下的，而不是改判定。
    /// </summary>
    [Fact]
    public async Task AnEscalationRecordedBeforeTheThresholdWasWithdrawnStaysInTheTimeoutTierWithANote()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.AddZoneParametersAsync(1, ("MAP-25-WIRE_TO_GATE", 3600));
        await database.AddZoneParametersAsync(2, ("MAP-25-WIRE_TO_GATE", null));
        await database.AddBacklogAsync(
            "D-ONCE-ESCALATED", TransportTaskTypes.WireToGate, createdAt: Now.AddHours(-5), escalatedAt: Now.AddHours(-1), escalationVersion: 1);
        await database.AddBacklogAsync("D-NORMAL", TransportTaskTypes.WireToGate, createdAt: Now.AddMinutes(-3));

        using JsonDocument fact = await ReadBacklogAsync(database.NewContext());

        Assert.False(fact.RootElement.GetProperty("starvationThresholdsApproved").GetBoolean());
        Assert.Equal(
            ["D-ONCE-ESCALATED:STARVATION_TIMEOUT", "D-NORMAL:NORMAL_BAND"],
            fact.RootElement.GetProperty("backlog").EnumerateArray()
                .Select(row => $"{row.GetProperty("demandId").GetString()}:{row.GetProperty("tier").GetString()}"));
        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.Contains("本区阈值未批准：只累计等待年龄，不跨带升级", html, StringComparison.Ordinal);
        Assert.Contains("已进入超时层", RowOf(html, "SUBLOT-D-ONCE-ESCALATED"), StringComparison.Ordinal);
        Assert.Contains("参数版本 1", RowOf(html, "SUBLOT-D-ONCE-ESCALATED"), StringComparison.Ordinal);
        Assert.Contains("超时层的标记是阈值撤回之前记下的", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-0203：本区阈值未批准时整张卡片写明只计龄、不跨带升级，而且没有任何需求在超时层——派车轮在未批准时从不写升级标记，
    /// 所以这里没有标记可读。三种「未批准」：一版参数都没有、参数表里该分区阈值为空、分区归属表里有而参数表里没有。
    /// 阈值撤回之前记下的标记是另一回事，见 <see cref="AnEscalationRecordedBeforeTheThresholdWasWithdrawnStaysInTheTimeoutTierWithANote"/>。
    /// </summary>
    [Theory]
    [InlineData("no-parameter-version")]
    [InlineData("threshold-empty")]
    [InlineData("zone-missing-from-parameters")]
    public async Task WithNoApprovedThresholdTheCardSaysAgeOnlyAndNothingIsInTheTimeoutTier(string shape)
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        switch (shape)
        {
            case "threshold-empty":
                await database.AddZoneParametersAsync(1, ("MAP-25-WIRE_TO_GATE", null));
                break;
            case "zone-missing-from-parameters":
                await database.AddAreaAssignmentAsync(1, ("AREA-1", "MAP-25-WIRE_TO_GATE"));
                await database.AddZoneParametersAsync(1, ("SOME-OTHER-ZONE", null));
                break;
        }
        await database.AddBacklogAsync("D-OLD", TransportTaskTypes.WireToGate, createdAt: Now.AddHours(-5));
        await database.AddBacklogAsync("D-NORMAL", TransportTaskTypes.WireToGate, createdAt: Now.AddMinutes(-3));

        using JsonDocument fact = await ReadBacklogAsync(database.NewContext());

        Assert.False(fact.RootElement.GetProperty("starvationThresholdsApproved").GetBoolean());
        Assert.All(
            fact.RootElement.GetProperty("backlog").EnumerateArray(),
            row => Assert.NotEqual("STARVATION_TIMEOUT", row.GetProperty("tier").GetString()));
        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.Contains("本区阈值未批准：只累计等待年龄，不跨带升级", html, StringComparison.Ordinal);
        Assert.DoesNotContain("超时层", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 等待年龄从需求的建单时刻起算（票面评论，批次7-09 定的起点），不从服务端首次看到起算——后者在全车队阻断或服务端停机时不写，
    /// 会让年龄在车辆短缺期间暂停（REQ-0201 禁止）。负钟差夹到 0；建单时刻为默认值时读作不知道，年龄给 0 并标明。
    /// </summary>
    [Fact]
    public async Task TheWaitingAgeRunsFromTheDemandsCreationClampsASkewAndSaysWhenItIsUnknown()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.AddBacklogAsync(
            "D-OLD", TransportTaskTypes.WireToGate, createdAt: Now.AddMinutes(-90), firstSeenAt: Now.AddMinutes(-10));
        await database.AddBacklogAsync("D-SKEWED", TransportTaskTypes.WireToGate, createdAt: Now.AddMinutes(2));
        await database.AddBacklogAsync("D-UNKNOWN", TransportTaskTypes.WireToGate, createdAt: default);

        using JsonDocument fact = await ReadBacklogAsync(database.NewContext());

        Dictionary<string, JsonElement> rows = fact.RootElement.GetProperty("backlog").EnumerateArray()
            .ToDictionary(row => row.GetProperty("demandId").GetString()!, row => row.Clone());
        Assert.Equal(90 * 60, rows["D-OLD"].GetProperty("waitingSeconds").GetInt64());
        Assert.True(rows["D-OLD"].GetProperty("waitingAgeKnown").GetBoolean());
        Assert.Equal(Now.AddMinutes(-90), rows["D-OLD"].GetProperty("demandCreatedAt").GetDateTimeOffset());
        Assert.Equal(0, rows["D-SKEWED"].GetProperty("waitingSeconds").GetInt64());
        Assert.Equal(0, rows["D-UNKNOWN"].GetProperty("waitingSeconds").GetInt64());
        Assert.False(rows["D-UNKNOWN"].GetProperty("waitingAgeKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rows["D-UNKNOWN"].GetProperty("demandCreatedAt").ValueKind);

        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.Contains("1 小时 30 分", RowOf(html, "SUBLOT-D-OLD"), StringComparison.Ordinal);
        Assert.Contains("建单时刻不明", RowOf(html, "SUBLOT-D-UNKNOWN"), StringComparison.Ordinal);
        // 同带里知道年龄的按年龄从长到短，不知道的排在最后——派车排序的口径（DemandCreatedAtLayer）。
        Assert.Equal(
            ["D-OLD", "D-SKEWED", "D-UNKNOWN"],
            fact.RootElement.GetProperty("backlog").EnumerateArray().Select(row => row.GetProperty("demandId").GetString()));
    }

    /// <summary>
    /// 同一需求离开目录再回来，年龄不重置：积压行还是那一行（离开只改原因码），建单时刻还是那一刻；看板一直按它算。
    /// </summary>
    [Fact]
    public async Task ADemandThatLeftTheCatalogueAndCameBackKeepsItsAge()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.AddBacklogAsync("D-1", TransportTaskTypes.WireToGate, createdAt: Now.AddMinutes(-40));

        await database.SetBacklogReasonAsync("D-1", DispatchReasonCodes.DemandLeftCatalog);
        using (JsonDocument away = await ReadBacklogAsync(database.NewContext(), Now.AddMinutes(5)))
        {
            Assert.Empty(away.RootElement.GetProperty("backlog").EnumerateArray());
        }
        await database.SetBacklogReasonAsync("D-1", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable);
        using JsonDocument back = await ReadBacklogAsync(database.NewContext(), Now.AddMinutes(10));

        JsonElement row = Assert.Single(back.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Equal(50 * 60, row.GetProperty("waitingSeconds").GetInt64());
    }

    /// <summary>
    /// 票面第 4 条：派车原因码都有中文说明。按反射扫 <see cref="DispatchReasonCodes"/> 的每个常量，所以以后再加一个码而忘了说明，这里就红；
    /// 不列入积压的两个（静默的未映射 AREA、已离开目录）除外。释放改派写在积压行上的原因（批次7-10）一并要有说明。
    /// </summary>
    [Fact]
    public void EveryDispatchReasonCodeTheBacklogCanShowHasAChineseDescription()
    {
        string[] codes =
        [
            .. typeof(DispatchReasonCodes)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Where(code => !DispatchReasonCodes.IsSilent(code) && code != DispatchReasonCodes.DemandLeftCatalog),
            ControlServer.Host.Runtime.Release.DemandReleaseReasons.Released,
        ];

        string[] undescribed = [.. codes.Where(code => !DispatchBacklogQueryEndpoint.Descriptions.ContainsKey(code))];
        Assert.Equal([], undescribed);
    }

    // --- helpers ----------------------------------------------------------------------------------------------------

    private static async Task<JsonElement[]> ReadCargoHoldingAsync(
        ControlServerDbContext context,
        DateTimeOffset? now = null,
        TimeSpan? timeout = null)
    {
        await using ControlServerDbContext owned = context;
        object fact = await new CargoHoldingQueryEndpoint(timeout ?? HoldingTimeout, new FixedClock(now ?? Now))
            .ReadAsync(owned, Token);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(fact));
        return [.. document.RootElement.GetProperty("journeys").EnumerateArray().Select(row => row.Clone())];
    }

    private static async Task<JsonElement> ReadCargoHoldingFactAsync(
        ControlServerDbContext context,
        DateTimeOffset? now = null)
    {
        await using ControlServerDbContext owned = context;
        object fact = await new CargoHoldingQueryEndpoint(HoldingTimeout, new FixedClock(now ?? Now))
            .ReadAsync(owned, Token);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(fact));
        return document.RootElement.Clone();
    }

    private static async Task<JsonDocument> ReadBacklogAsync(ControlServerDbContext context, DateTimeOffset? now = null)
    {
        await using ControlServerDbContext owned = context;
        object fact = await new DispatchBacklogQueryEndpoint(new FixedClock(now ?? Now)).ReadAsync(owned, Token);
        return JsonDocument.Parse(JsonSerializer.Serialize(fact));
    }

    private static async Task<JsonDocument> ReadBlockedAsync(ControlServerDbContext context)
    {
        await using ControlServerDbContext owned = context;
        object fact = await new BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions.Default, new FixedClock(Now))
            .ReadAsync(owned, Token);
        return JsonDocument.Parse(JsonSerializer.Serialize(fact));
    }

    private static string RowOf(string html, string text)
    {
        int at = html.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, "No row for " + text + ".");
        int start = html.LastIndexOf("<tr", at, StringComparison.Ordinal);
        int end = html.IndexOf("</tr>", at, StringComparison.Ordinal);
        return html[start..end];
    }

    private static string? NullableBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => null,
        _ => throw new InvalidOperationException($"Not a nullable boolean: {value.GetRawText()}")
    };

    private static JourneyRuntimeRow Journey(
        string demandId,
        string agvId,
        JourneyRuntimeStage stage,
        string? journeyId = null) => new()
        {
            JourneyId = journeyId ?? JourneyIdentity.ForAnchorDemand(demandId),
            DemandId = demandId,
            Stage = stage,
            AgvId = agvId,
            VehicleKey = "KEY-" + agvId,
            AgvLifecycleGeneration = 1,
            MapId = 25,
            MapIdentity = "map-25",
            DispatchZone = "MAP-25-WIRE_TO_GATE",
            RouteEvidenceId = "route-" + demandId,
            PickupStationId = "PICKUP-1",
            PickupStationRiotId = 11,
            GateStationId = "GATE-1",
            GateStationRiotId = 22,
            ExpectedBasketCount = 2,
            TargetSlotsJson = "[1,2]",
            OperationSessionId = "session-" + demandId,
            PickupMovementLegId = "pickup-leg-" + demandId,
            PickupUpperId = "UPPER-PICKUP-" + demandId,
            GateMovementLegId = "gate-leg-" + demandId,
            GateUpperId = "UPPER-GATE-" + demandId,
            DispatchGeneration = 1,
            VehicleBusinessRevision = 1,
            WorklistRevision = 1,
            PlanRevision = 1,
            VehicleBusinessMessageId = "vb-" + demandId,
            WorklistMessageId = "wl-" + demandId,
            PlanMessageId = "plan-" + demandId,
            SublotRequestMessageId = "sublot-" + demandId,
            LoadCommandMessageId = "load-" + demandId,
            LoadSlotOperationAttemptId = "load-attempt-" + demandId,
            PreDepartureSafetyCheckMessageId = "check-message-" + demandId,
            PreDepartureSafetyCheckId = "check-" + demandId,
            GateVehicleBusinessMessageId = "gate-vb-" + demandId,
            GateWorklistMessageId = "gate-wl-" + demandId,
            GatePlanMessageId = "gate-plan-" + demandId,
            UnloadCommandMessageId = "unload-" + demandId,
            UnloadSlotOperationAttemptId = "unload-attempt-" + demandId,
            CreatedAt = Now.AddHours(-1),
            UpdatedAt = Now
        };

    private static AcceptedDemandRow Demand(string demandId, DemandExecutionStatus status = DemandExecutionStatus.Accepted) => new()
    {
        DemandId = demandId,
        SeriesId = "SERIES-" + demandId,
        TransportDemandKey = $"SUBLOT-{demandId}|{TransportTaskTypes.WireToGate}",
        WorkType = TransportTaskTypes.WireToGate,
        Sublot = "SUBLOT-" + demandId,
        Generation = 1,
        DemandRevision = 1,
        HistoryEpoch = "epoch-1",
        CatalogRevision = 1,
        CreatedAt = Now.AddHours(-2),
        ValueObservedAt = Now.AddHours(-2),
        ValuePollTraceId = "TRACE-" + demandId,
        ValueProjectionCommitId = "COMMIT-" + demandId,
        LiveMesFieldsJson = "{}",
        AcceptedAt = Now.AddHours(-1),
        Status = status
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>一份迁移建出的内存库；<see cref="NewContext"/> 在同一个连接上开一个全新的上下文，等同服务端重启后再读。</summary>
    private sealed class DashboardDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DashboardDatabase(SqliteConnection connection)
        {
            _connection = connection;
        }

        public static async Task<DashboardDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            DashboardDatabase database = new(connection);
            await using ControlServerDbContext context = database.NewContext();
            await context.Database.MigrateAsync(Token);
            return database;
        }

        public SqliteConnection Connection => _connection;

        public ControlServerDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);

        /// <summary>写一趟单需求旅程：旅程行、它的需求、两个停靠与锚需求的归属，与受理写下的形状相同。</summary>
        public async Task SeedAsync(JourneyRuntimeRow journey)
        {
            await using ControlServerDbContext context = NewContext();
            if (!await context.AcceptedDemands.AnyAsync(row => row.DemandId == journey.DemandId, Token))
            {
                context.AcceptedDemands.Add(Demand(journey.DemandId));
            }
            context.JourneyRuntimes.Add(journey);
            JourneyMembershipSeed.Seed(context, journey);
            await context.SaveChangesAsync(Token);
        }

        /// <summary>
        /// 给旅程加一条需求与它的归属，状态为 <paramref name="status"/>。<paramref name="dispatchZoneParameterVersion"/> 非空即途中追加进来的
        /// （追加所用的每区参数版本，<c>JourneyDemandRow.DispatchZoneParameterVersion</c>）。
        /// </summary>
        public async Task AppendDemandAsync(
            JourneyRuntimeRow journey,
            string demandId,
            long? dispatchZoneParameterVersion = null,
            string status = JourneyDemandStatuses.PendingLoad,
            DemandExecutionStatus demandStatus = DemandExecutionStatus.Accepted)
        {
            await using ControlServerDbContext context = NewContext();
            context.AcceptedDemands.Add(Demand(demandId, demandStatus));
            JourneyDemandRow membership = JourneyMembershipSeed.Member(journey, demandId);
            membership.Status = status;
            membership.DispatchZoneParameterVersion = dispatchZoneParameterVersion;
            membership.AddedAt = Now.AddMinutes(-3);
            context.Set<JourneyDemandRow>().Add(membership);
            await context.SaveChangesAsync(Token);
        }

        public async Task SetMembershipStatusAsync(JourneyRuntimeRow journey, string demandId, string status)
        {
            await using ControlServerDbContext context = NewContext();
            JourneyDemandRow membership = await context.Set<JourneyDemandRow>()
                .SingleAsync(row => row.JourneyId == journey.JourneyId && row.DemandId == demandId, Token);
            membership.Status = status;
            await context.SaveChangesAsync(Token);
        }

        /// <summary>标记归属移除，与释放改派写下的一样（<c>RELEASED_FOR_REDISPATCH</c>）；归属行只标移除、从不删行。</summary>
        public async Task RemoveMembershipAsync(JourneyRuntimeRow journey, string demandId)
        {
            await using ControlServerDbContext context = NewContext();
            JourneyDemandRow membership = await context.Set<JourneyDemandRow>()
                .SingleAsync(row => row.JourneyId == journey.JourneyId && row.DemandId == demandId, Token);
            membership.RemovedAt = Now.AddMinutes(-1);
            membership.RemovalReason = DemandJourneyLookup.ReleasedForRedispatchReason;
            await context.SaveChangesAsync(Token);
        }

        /// <summary>把旅程的某个停靠标成已完成，与车离开它之后引擎写下的一样。</summary>
        public async Task CompleteStopAsync(JourneyRuntimeRow journey, string role)
        {
            await using ControlServerDbContext context = NewContext();
            JourneyStopRow stop = await context.Set<JourneyStopRow>()
                .SingleAsync(row => row.JourneyId == journey.JourneyId && row.StopRole == role, Token);
            stop.Status = JourneyStopStatuses.Completed;
            await context.SaveChangesAsync(Token);
        }

        /// <summary>
        /// 一行受理前的积压，与派车轮写下的形状相同：业务键是 <c>{sublot}|{workType}</c>（<c>HttpMesIngestCatalog</c> 的拼法），
        /// <c>DemandCreatedAt</c> 是 MesIngest 的建单时刻。
        /// </summary>
        public async Task AddBacklogAsync(
            string demandId,
            string workType,
            DateTimeOffset createdAt,
            DateTimeOffset? firstSeenAt = null,
            string reasonCode = "SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE",
            DateTimeOffset? escalatedAt = null,
            long? escalationVersion = null)
        {
            await using ControlServerDbContext context = NewContext();
            context.JourneyBacklog.Add(new JourneyBacklogRow
            {
                DemandId = demandId,
                TransportDemandKey = $"SUBLOT-{demandId}|{workType}",
                FirstSeenAt = firstSeenAt ?? Now.AddMinutes(-2),
                DemandCreatedAt = createdAt,
                DecisionFingerprint = "fingerprint-" + demandId,
                ReasonCode = reasonCode,
                LastSeenAt = Now.AddSeconds(-2),
                StarvationEscalatedAt = escalatedAt,
                StarvationEscalationParameterVersion = escalationVersion
            });
            await context.SaveChangesAsync(Token);
        }

        /// <summary>一版每区派车参数：每个分区的防饥饿阈值（秒），空即未配置（未批准）。</summary>
        public async Task AddZoneParametersAsync(long version, params (string Zone, long? ThresholdSeconds)[] zones)
        {
            await using ControlServerDbContext context = NewContext();
            context.Set<DispatchZoneParameterVersionRow>().Add(new DispatchZoneParameterVersionRow
            {
                Version = version,
                ContentSha256 = "sha-" + version,
                LoadedAt = Now.AddDays(-1),
                Source = DispatchZoneParameterSources.L2Preset
            });
            context.Set<DispatchZoneParameterRow>().AddRange(zones.Select(zone => new DispatchZoneParameterRow
            {
                Version = version,
                DispatchZone = zone.Zone,
                EnRouteAdditionMaxPathCostIncrease = 5000,
                StarvationThresholdSeconds = zone.ThresholdSeconds
            }));
            await context.SaveChangesAsync(Token);
        }

        public async Task SetBacklogReasonAsync(string demandId, string reasonCode)
        {
            await using ControlServerDbContext context = NewContext();
            JourneyBacklogRow row = await context.JourneyBacklog.SingleAsync(item => item.DemandId == demandId, Token);
            row.ReasonCode = reasonCode;
            await context.SaveChangesAsync(Token);
        }

        /// <summary>一版分区归属表：AREA → 分区。</summary>
        public async Task AddAreaAssignmentAsync(long version, params (string Area, string Zone)[] areas)
        {
            await using ControlServerDbContext context = NewContext();
            context.Set<DispatchZoneAreaAssignmentVersionRow>().Add(new DispatchZoneAreaAssignmentVersionRow
            {
                Version = version,
                ContentSha256 = "sha-area-" + version,
                SnapshotId = "snapshot-area-" + version,
                EntryCount = areas.Length,
                ImportedAt = Now.AddDays(-1)
            });
            context.Set<DispatchZoneAreaAssignmentRow>().AddRange(areas.Select(area => new DispatchZoneAreaAssignmentRow
            {
                Version = version,
                Area = area.Area,
                DispatchZone = area.Zone,
                SlotPosition = "FRONT"
            }));
            await context.SaveChangesAsync(Token);
        }

        public async Task SetStageAsync(JourneyRuntimeRow journey, JourneyRuntimeStage stage)
        {
            await using ControlServerDbContext context = NewContext();
            JourneyRuntimeRow row = await context.JourneyRuntimes.SingleAsync(item => item.JourneyId == journey.JourneyId, Token);
            row.Stage = stage;
            await context.SaveChangesAsync(Token);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Token);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
