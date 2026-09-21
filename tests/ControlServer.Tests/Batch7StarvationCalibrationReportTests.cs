using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// 防饥饿阈值的标定证据报表（REQ-0203；批次7-09，control-server#214）：给定一组旅程、从属需求、积压行与站点作业，
/// 各分区的中位数、P95、最大值与样本计数取精确值，并按时间窗过滤。
/// </summary>
/// <remarks>
/// <para>
/// 口径（与运维说明一致）：<b>完整周期</b>＝一趟已完成旅程从建立（绑车）到完成（车再次空闲、可接单）；
/// <b>建单至绑车等待</b>＝需求的本地建单时刻（积压行 <c>DemandCreatedAt</c>，即 MesIngest 的 <c>CreatedAt</c>）到它被绑进旅程
/// （从属需求行 <c>AddedAt</c>）；<b>站点作业耗时</b>＝一次装或卸从下发到人工确认提交（<c>CreatedAt</c> 到 <c>CommittedAt</c>）。
/// 样本期取 [from, to)：旅程按建立时刻、需求按绑车时刻、作业按下发时刻落在窗里。中位数在偶数个样本时取中间两个的平均，
/// P95 取最近秩（第 ⌈0.95·n⌉ 小）。
/// </para>
/// <para>
/// 数据故意让每个统计量都与相邻的错误口径分得开：五个周期的中位数与平均数不同、P95 与第四个不同，
/// 窗外、未完成、未提交的行各放一条，漏过滤任何一种都会改变某个精确值。
/// </para>
/// </remarks>
public sealed class Batch7StarvationCalibrationReportTests
{
    private const string ZoneA = "MAP-25-WIRE_TO_GATE";
    private const string ZoneB = "MAP-26-STAGING";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly int[] FiveCycles = [600, 3000, 900, 1500, 1200];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TheSummaryIsTheExactMedianNearestRankP95AndMaximum()
    {
        DurationSummary twenty = StarvationCalibrationQuery.Summarize(
            Enumerable.Range(1, 20).Reverse().Select(seconds => TimeSpan.FromSeconds(seconds)));
        DurationSummary five = StarvationCalibrationQuery.Summarize(
            FiveCycles.Select(seconds => TimeSpan.FromSeconds(seconds)));
        DurationSummary none = StarvationCalibrationQuery.Summarize([]);

        Assert.Equal(new DurationSummary(20, 10.5, 19, 20), twenty);
        Assert.Equal(new DurationSummary(5, 1200, 3000, 3000), five);
        Assert.Equal(new DurationSummary(0, null, null, null), none);
    }

    [Fact]
    public async Task EachZoneGetsItsExactFullCycleWaitToBindCountsAndStationOperationTimes()
    {
        await using ReportDatabase database = await ReportDatabase.CreateAsync();
        await SeedAsync(database.Context);

        StarvationCalibrationReport report = await StarvationCalibrationQuery.ReadAsync(
            database.Context, T0, T0.AddHours(1), T0.AddDays(1), Token);

        Assert.Equal([ZoneA, ZoneB], report.Zones.Select(zone => zone.DispatchZone));
        ZoneCalibrationEvidence a = report.Zones[0];
        Assert.Equal(new DurationSummary(5, 1200, 3000, 3000), a.FullCycle);
        Assert.Equal(new DurationSummary(4, 150, 240, 240), a.WaitToBind);
        Assert.Equal(3, a.VehicleCount);
        Assert.Equal(6, a.TaskCount);
        Assert.Equal(
            [
                new StationOperationSummary("LOAD", new DurationSummary(2, 45, 60, 60)),
                new StationOperationSummary("UNLOAD", new DurationSummary(1, 45, 45, 45)),
            ],
            a.StationOperations);

        ZoneCalibrationEvidence b = report.Zones[1];
        Assert.Equal(new DurationSummary(1, 500, 500, 500), b.FullCycle);
        Assert.Equal(new DurationSummary(1, 30, 30, 30), b.WaitToBind);
        Assert.Equal((1, 1), (b.VehicleCount, b.TaskCount));
        Assert.Equal(
            [
                new StationOperationSummary("LOAD", new DurationSummary(0, null, null, null)),
                new StationOperationSummary("UNLOAD", new DurationSummary(0, null, null, null)),
            ],
            b.StationOperations);
    }

    /// <summary>时间窗收窄到 [T0+2m, T0+4m)：只剩 j2、j3 与它们的需求、作业。人工标定票要在 REQ-0357 上线之后取样，靠的就是这个。</summary>
    [Fact]
    public async Task TheWindowSelectsJourneysByStartDemandsByBindingAndOperationsByIssue()
    {
        await using ReportDatabase database = await ReportDatabase.CreateAsync();
        await SeedAsync(database.Context);

        StarvationCalibrationReport report = await StarvationCalibrationQuery.ReadAsync(
            database.Context, T0.AddMinutes(2), T0.AddMinutes(4), T0.AddDays(1), Token);

        ZoneCalibrationEvidence a = Assert.Single(report.Zones);
        Assert.Equal(ZoneA, a.DispatchZone);
        Assert.Equal(new DurationSummary(2, 1050, 1200, 1200), a.FullCycle);
        Assert.Equal(new DurationSummary(2, 150, 180, 180), a.WaitToBind);
        Assert.Equal((2, 2), (a.VehicleCount, a.TaskCount));
        Assert.Equal(new DurationSummary(1, 60, 60, 60), a.StationOperations.Single(op => op.OperationType == "LOAD").Duration);
        Assert.Equal((T0.AddMinutes(2), T0.AddMinutes(4)), (report.From, report.To));
    }

    [Fact]
    public async Task TheCsvIsOneRowPerZoneUnderAFixedHeader()
    {
        await using ReportDatabase database = await ReportDatabase.CreateAsync();
        await SeedAsync(database.Context);
        StarvationCalibrationReport report = await StarvationCalibrationQuery.ReadAsync(
            database.Context, T0, T0.AddHours(1), T0.AddDays(1), Token);

        string[] lines = StarvationCalibrationQuery.ToCsv(report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            [
                "dispatch_zone,window_from,window_to,vehicle_count,task_count," +
                "full_cycle_count,full_cycle_median_s,full_cycle_p95_s,full_cycle_max_s," +
                "wait_to_bind_count,wait_to_bind_median_s,wait_to_bind_p95_s,wait_to_bind_max_s," +
                "load_count,load_median_s,load_p95_s,load_max_s," +
                "unload_count,unload_median_s,unload_p95_s,unload_max_s",
                "MAP-25-WIRE_TO_GATE,2026-10-01T00:00:00.0000000+00:00,2026-10-01T01:00:00.0000000+00:00,3,6," +
                "5,1200,3000,3000,4,150,240,240,2,45,60,60,1,45,45,45",
                "MAP-26-STAGING,2026-10-01T00:00:00.0000000+00:00,2026-10-01T01:00:00.0000000+00:00,1,1," +
                "1,500,500,500,1,30,30,30,0,,,,0,,,",
            ],
            lines);
    }

    /// <summary>端点：JSON 与 CSV 两种形式，缺时间窗或窗不成立时 400，只读。</summary>
    [Fact]
    public async Task TheEndpointAnswersJsonAndADownloadableCsvAndRefusesABadWindow()
    {
        await using ReportDatabase database = await ReportDatabase.CreateAsync();
        await SeedAsync(database.Context);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(database.Connection));
        builder.Services.AddSingleton(TimeProvider.System);
        await using WebApplication app = builder.Build();
        app.MapStarvationCalibrationReport();
        await app.StartAsync(Token);
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using HttpClient client = new() { BaseAddress = new Uri(address) };
        string window = "from=2026-10-01T00:00:00Z&to=2026-10-01T01:00:00Z";

        using HttpResponseMessage json = await client.GetAsync($"{StarvationCalibrationEndpoints.Route}?{window}", Token);
        using HttpResponseMessage csv = await client.GetAsync($"{StarvationCalibrationEndpoints.Route}?{window}&format=csv", Token);
        using HttpResponseMessage missing = await client.GetAsync($"{StarvationCalibrationEndpoints.Route}?from=2026-10-01T00:00:00Z", Token);
        using HttpResponseMessage backwards = await client.GetAsync(
            $"{StarvationCalibrationEndpoints.Route}?from=2026-10-01T01:00:00Z&to=2026-10-01T00:00:00Z", Token);
        using HttpResponseMessage post = await client.PostAsync(
            $"{StarvationCalibrationEndpoints.Route}?{window}", new StringContent(""), Token);
        await app.StopAsync(Token);

        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        JsonElement body = await json.Content.ReadFromJsonAsync<JsonElement>(Token);
        JsonElement zoneA = body.GetProperty("zones")[0];
        Assert.Equal(ZoneA, zoneA.GetProperty("dispatchZone").GetString());
        Assert.Equal(3000, zoneA.GetProperty("fullCycle").GetProperty("p95Seconds").GetDouble());
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", csv.Content.Headers.ContentDisposition?.DispositionType);
        Assert.StartsWith("dispatch_zone,", await csv.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.False(post.IsSuccessStatusCode);
    }

    // ---- data ----------------------------------------------------------------------------------------

    /// <summary>
    /// 区 A：j1～j5 完成（周期 600、900、1200、1500、3000 秒），j6 未完成，j7 在窗外；d1～d4 有积压行（等待 60、120、180、240 秒），
    /// d5、d6 没有；车 agv1、agv2、agv3 在窗内，agv4 只在窗外。区 B：j8 一趟（周期 500，等待 30）。
    /// 作业：d1 装 30 秒、卸 45 秒，d2 装 60 秒，d3 装未提交，d7 装在窗外。
    /// </summary>
    private static async Task SeedAsync(ControlServerDbContext context)
    {
        (string Id, string Agv, int StartMinute, int? CycleSeconds, int? WaitSeconds, string Zone)[] journeys =
        [
            ("d1", "agv1", 1, 600, 60, ZoneA),
            ("d2", "agv2", 2, 900, 120, ZoneA),
            ("d3", "agv1", 3, 1200, 180, ZoneA),
            ("d4", "agv2", 4, 1500, 240, ZoneA),
            ("d5", "agv1", 5, 3000, null, ZoneA),
            ("d6", "agv3", 6, null, null, ZoneA),
            ("d7", "agv4", -10, 700, 90, ZoneA),
            ("d8", "agv5", 7, 500, 30, ZoneB),
        ];
        foreach ((string id, string agv, int startMinute, int? cycle, int? wait, string zone) in journeys)
        {
            DateTimeOffset start = T0.AddMinutes(startMinute);
            context.JourneyRuntimes.Add(Runtime(id, agv, zone, start, cycle));
            context.Set<JourneyDemandRow>().Add(Membership(id, zone, start));
            if (wait is { } seconds)
            {
                context.JourneyBacklog.Add(new JourneyBacklogRow
                {
                    DemandId = id,
                    TransportDemandKey = $"SUBLOT-{id}|WIRE_TO_GATE",
                    FirstSeenAt = start.AddSeconds(-seconds / 2),
                    DemandCreatedAt = start.AddSeconds(-seconds),
                    DecisionFingerprint = "fp",
                    ReasonCode = "ACCEPTED",
                    LastSeenAt = start,
                    AcceptedAt = start,
                });
            }
        }

        context.StationOperations.AddRange(
            Operation("d1", SlotOperationType.Load, T0.AddMinutes(10), 30),
            Operation("d1", SlotOperationType.Unload, T0.AddMinutes(20), 45),
            Operation("d2", SlotOperationType.Load, T0.AddMinutes(3), 60),
            Operation("d3", SlotOperationType.Load, T0.AddMinutes(12), null),
            Operation("d7", SlotOperationType.Load, T0.AddMinutes(-5), 10));
        await context.SaveChangesAsync(Token);
    }

    private static StationOperationRow Operation(string demandId, SlotOperationType type, DateTimeOffset issuedAt, int? seconds) => new()
    {
        SlotOperationAttemptId = $"{type}-{demandId}",
        DemandId = demandId,
        SublotId = $"SUBLOT-{demandId}",
        TargetSlotsJson = "[1]",
        OperationType = type,
        ContentHash = "hash",
        Status = seconds is null ? StationOperationStatus.Prepared : StationOperationStatus.Committed,
        CreatedAt = issuedAt,
        CommittedAt = seconds is { } s ? issuedAt.AddSeconds(s) : null,
    };

    private static JourneyDemandRow Membership(string demandId, string zone, DateTimeOffset addedAt) => new()
    {
        JourneyId = JourneyIdentity.ForAnchorDemand(demandId),
        DemandId = demandId,
        PickupStopId = $"pickup-{demandId}",
        UnloadStopId = $"unload-{demandId}",
        ExpectedBasketCount = 1,
        TargetSlotsJson = "[1]",
        LoadSlotOperationAttemptId = $"load-attempt-{demandId}",
        LoadCommandMessageId = $"load-{demandId}",
        UnloadSlotOperationAttemptId = $"unload-attempt-{demandId}",
        UnloadCommandMessageId = $"unload-{demandId}",
        DispatchZone = zone,
        DispatchGeneration = 1,
        Status = "ACTIVE",
        AddedAt = addedAt,
    };

    private static JourneyRuntimeRow Runtime(string demandId, string agvId, string zone, DateTimeOffset start, int? cycleSeconds) => new()
    {
        JourneyId = JourneyIdentity.ForAnchorDemand(demandId),
        DemandId = demandId,
        Stage = cycleSeconds is null ? JourneyRuntimeStage.AwaitingSublot : JourneyRuntimeStage.Completed,
        AgvId = agvId,
        VehicleKey = "KEY-" + agvId,
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "map-25",
        DispatchZone = zone,
        RouteEvidenceId = "route-" + demandId,
        PickupStationId = "PICKUP-1",
        PickupStationRiotId = 11,
        GateStationId = "GATE-1",
        GateStationRiotId = 22,
        ExpectedBasketCount = 1,
        TargetSlotsJson = "[1]",
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
        CreatedAt = start,
        UpdatedAt = start.AddSeconds(cycleSeconds ?? 30),
    };

    private sealed class ReportDatabase : IAsyncDisposable
    {
        private ReportDatabase(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public static async Task<ReportDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.MigrateAsync(Token);
            return new ReportDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
