using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 待送点取货停靠不设倒计时、不因超时退回需求（control-server#709，program#184 R3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>同一组事实跑三种停靠</b>（票面「机台回归用参数化」）：待送点（规则开）、机台（规则开）、待送点（规则关）。只有第一种走待送点规则；
/// 后两种必须与改前一样——机台那一种证「规则只认待送点」，规则关那一种证「开关关着时什么都没变」。变异时把判别改成恒假，第一种的用例红；
/// 改成恒真，第二种的用例红。
/// </para>
/// <para>
/// 夹具照 <c>ReversedDirectionJourneyRuntimeTests</c>：25 号图上 WIRE_TO_GATE 绑关卡 210、STAGING_TO_WIRE 绑派工待送站 305，两种任务类型都允许。
/// 机台需求在 N1-1（站 12）取货。离站期限与持货期限都是生产值五分钟。
/// </para>
/// </remarks>
public sealed class StagingPickupNoCountdownTests
{
    private const string Demand = "10000000-0000-4000-8000-000000000001";

    // 夹具的 AdvanceToLoadResultAsync 录的就是这一条。
    private const string Sublot = "SUBLOT-001";

    private const int StagingStationRiotId = 305;

    private const string StagingStationName = "派工待送取货";

    private static readonly TimeSpan StationDeadline = TimeSpan.FromMinutes(5);

    private static readonly TaskTypeStationBinding StagingBinding =
        new(TransportTaskTypes.StagingToWire, StagingStationRiotId, StagingStationName, "SITE-CHECK-STAGING");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>三种停靠。</summary>
    public enum StopKind
    {
        /// <summary>STAGING_TO_WIRE 在派工待送站取货，待送点规则开着：本票的规则生效。</summary>
        StagingRulesOn,

        /// <summary>WIRE_TO_GATE 在机台取货，待送点规则开着：一切照旧。</summary>
        MachineRulesOn,

        /// <summary>STAGING_TO_WIRE 在派工待送站取货，待送点规则关着（出厂）：一切照旧。</summary>
        StagingRulesOff,
    }

    public static TheoryData<StopKind> Kinds => [StopKind.StagingRulesOn, StopKind.MachineRulesOn, StopKind.StagingRulesOff];

    /// <summary>验收 1：待送点的清单不带离站期限；另两种照旧带，等于到站时刻加五分钟。</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task OnlyAStagingPickupIsSentAWorklistWithoutAStationDeparatureDeadline(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        DateTimeOffset arrivedAt = fixture.Clock.GetUtcNow();

        JourneyRuntimeRow waiting = await fixture.AdvanceToSublotWaitAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        DateTimeOffset?[] deadlines = await WorklistDeadlinesAsync(fixture);
        Assert.NotEmpty(deadlines);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.All(deadlines, deadline => Assert.Null(deadline));
        }
        else
        {
            Assert.All(deadlines, deadline => Assert.Equal(waiting.StationDepartureWaitStartedAt + StationDeadline, deadline));
            Assert.True(waiting.StationDepartureWaitStartedAt >= arrivedAt);
        }
    }

    /// <summary>
    /// 验收 2：没人扫码，钟拨过 60 分钟。待送点：本站不结束，需求不放回、不终结、不计 control-server#703 的放回次数、不挂告警。
    /// 另两种：第一个期限就把本站结束、需求放回（control-server#703 的第一次放回）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnHourWithNobodyScanningEndsOnlyAMachinePickup(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        JourneyRuntimeRow first = await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();

        for (int step = 0; step < 12; step++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
            if ((await JourneyAsync(fixture, first.JourneyId)).Stage == JourneyRuntimeStage.Completed)
            {
                break;
            }
        }

        JourneyRuntimeRow journey = await JourneyAsync(fixture, first.JourneyId);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Equal(JourneyRuntimeStage.AwaitingSublot, journey.Stage);
            Assert.Null(journey.BlockReasonCode);
            Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
            JourneyDemandRow membership = Assert.Single(await MembershipsAsync(fixture));
            Assert.Null(membership.RemovedAt);
            Assert.Equal(0, await StationDeadlineReleasesAsync(fixture));
            Assert.Empty(await AlarmsAsync(fixture));
            await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
            Assert.Equal(1, await fixture.Context.JourneyRuntimes.CountAsync(Token));
            Assert.True(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token), "The waiting vehicle lost its journey.");
        }
        else
        {
            Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
            Assert.Equal(DemandJourneyLookup.ReleasedAtStationDeadlineReason, journey.BlockReasonCode);
            Assert.Equal(1, await StationDeadlineReleasesAsync(fixture));
            Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        }
    }

    /// <summary>
    /// 验收 3：装了一篮，钟拨过 60 分钟，分区允许途中追加（持货等单适用）。待送点：装货阶段不关闭为 <c>CARGO_HOLDING_TIMEOUT</c>、
    /// 不发离站核验、车不离站，车上与看板都没有持货期限。另两种：持货期限到，装货阶段以 <c>CARGO_HOLDING_TIMEOUT</c> 关闭、发离站核验。
    /// </summary>
    /// <remarks>
    /// 截断（control-server#290）只在装货阶段以持货超时关闭之后才发生（<c>TruncateAtCargoHoldingDeadlineAsync</c> 的头一道判断），
    /// 所以「不关闭」就是「不截断」；这里一并断言归属一条都没被移除。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnHourAfterTheFirstBasketClosesTheLoadingPhaseOnlyAwayFromTheStagingStation(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        JourneyRuntimeRow loaded = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, loaded.Stage);
        Assert.NotNull(loaded.CargoHoldingStartedAt);

        await fixture.ProveSlotDoorsClosedAsync();
        for (int step = 0; step < 12; step++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Batch7CargoHoldingTests.LoadingPhaseSnapshot[] snapshots = await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture);
        JsonElement dashboard = Assert.Single(await CargoHoldingRowsAsync(fixture));
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
            Assert.NotEqual(LoadingClosedReasons.CargoHoldingTimeout, journey.LoadingClosedReason);
            Assert.NotEqual(LoadingPhaseStates.Closed, journey.LoadingPhaseState);
            Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The staging vehicle was sent on.");
            Assert.Null(Assert.Single(await MembershipsAsync(fixture)).RemovedAt);
            Assert.NotEmpty(snapshots);
            Assert.All(snapshots, snapshot => Assert.Null(snapshot.CargoHoldingDeadlineAt));
            Assert.Equal(JsonValueKind.Null, dashboard.GetProperty("cargoHoldingDeadlineAt").ValueKind);
            Assert.False(dashboard.GetProperty("deadlinePassed").GetBoolean());
        }
        else
        {
            Assert.Equal(LoadingPhaseStates.Closed, journey.LoadingPhaseState);
            Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, journey.LoadingClosedReason);
            Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The holding deadline sent nobody on.");
            Assert.Contains(snapshots, snapshot => snapshot.CargoHoldingDeadlineAt == loaded.CargoHoldingStartedAt + StationDeadline);
        }
    }

    /// <summary>
    /// 验收 4：开门（装货命令发出）之后迟迟没有结果、仓门没证明关着。待送点：命令发出满 N（这里 5 秒）挂仓门未闭告警，停靠不结束；
    /// 落定后告警撤销。另两种照旧按离站期限（这里 10 秒）挂。
    /// </summary>
    /// <remarks>
    /// 钟只拨几秒、不另发心跳，与 <c>JourneyRuntimeWorkerLoadDeadlineTests.ADoorLeftOpenPastTheDeadlineRaisesTheAlarmWithoutEndingTheStop</c>
    /// 同一个量级：拨过车载端证据的有效期，推进段会先因证据过期停在别处，量到的就不是这条告警了。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ADoorLeftOpenAtTheStagingStationIsReportedByTheTimeSinceTheLoadCommand(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Options.StagingDepartureRules.LoadNotSettledAlarmAfter = TimeSpan.FromSeconds(5);
        JourneyRuntimeRow loading = await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loading.Stage);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);

        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow atThree = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, atThree.Stage);
        Assert.Equal(
            kind == StopKind.StagingRulesOn ? JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason : null,
            atThree.BlockReasonCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow atSix = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, atSix.Stage);
        Assert.Equal(JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason, atSix.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow settled = await fixture.RuntimeAsync();
        Assert.NotEqual(JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason, settled.BlockReasonCode);
        Assert.NotEqual(JourneyRuntimeStage.AwaitingLoadResult, settled.Stage);
    }

    /// <summary>
    /// 验收 5：一篮没装，电量跌到强制充电线（夹具策略 40%）以下。待送点：需求按释放改派放回（不计站点超时次数、不抑制、不告警），旅程收尾、
    /// 车放开；下一轮派车不再给这辆车派搬运，它排进充电队列（积压行 <c>MANDATORY_CHARGE_REQUIRED</c>）。另两种：什么都不变，车照旧等录入。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AFlatBatteryWithNothingLoadedReleasesOnlyAStagingStopToCharging(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        JourneyRuntimeRow waiting = await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow journey = await JourneyAsync(fixture, waiting.JourneyId);
        if (kind != StopKind.StagingRulesOn)
        {
            Assert.Equal(JourneyRuntimeStage.AwaitingSublot, journey.Stage);
            Assert.True(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token));
            Assert.Null(Assert.Single(await MembershipsAsync(fixture)).RemovedAt);
            return;
        }

        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, journey.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        JourneyDemandRow released = Assert.Single(await MembershipsAsync(fixture));
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
        Assert.Equal(0, await StationDeadlineReleasesAsync(fixture));
        Assert.Empty(await AlarmsAsync(fixture));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.False(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token), "The vehicle is still claimed.");
        Assert.True(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token),
            "The demand is not waiting to be dispatched again.");
        Assert.Contains(fixture.EngineLog.Entries, entry => entry.Message.Contains("mandatory charge line", StringComparison.Ordinal));

        // 车成了空闲车：下一轮派车把它排进充电队列，不再给它搬运。
        await ChargingTestKit.WriteRosterWithAChargerNobodyIsSentToAsync(fixture.Context, 25, fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(1, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.Equal(
            Host.Runtime.Dispatch.DispatchReasonCodes.MandatoryChargeRequired,
            (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    /// <summary>
    /// 验收 5 的另一半：车上已经有货、电量跌到线下，本票什么也不做（带货充电是 control-server#711 的）——不走一篮没装那一支：不收尾、不放回、
    /// 车上的货仍有需求认领。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AFlatBatteryWithABasketLoadedDoesNotTakeTheEmptyVehiclesWayOut()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await LoadTheBasketAsync(fixture);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };

        for (int step = 0; step < 3; step++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
        JourneyDemandRow membership = Assert.Single(await MembershipsAsync(fixture));
        Assert.Null(membership.RemovedAt);
        Assert.Equal(JourneyDemandStatuses.Loaded, membership.Status);
        Assert.True(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token));
    }

    /// <summary>
    /// 验收 7：「等人中的旅程」卡片上，停在待送点的车写「在待送点等客户出发」，并给出已装篮数、各侧剩余空仓、待装条数。机台的那一行与之前逐字相同，
    /// 不多 <c>stagingStop</c> 这个字段。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheWaitingJourneysCardSaysAStagingVehicleWaitsForTheCustomersDeparture(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        await fixture.AdvanceToSublotWaitAsync();

        JsonElement row = Assert.Single(
            (await WaitingJourneysAsync(fixture)).RootElement.GetProperty("journeys").EnumerateArray());
        string html = new WaitingJourneyCard().RenderFact((await WaitingJourneysAsync(fixture)).RootElement);
        if (kind != StopKind.StagingRulesOn)
        {
            Assert.False(row.TryGetProperty("stagingStop", out _));
            Assert.DoesNotContain("待送点", html, StringComparison.Ordinal);
            return;
        }

        JsonElement staging = row.GetProperty("stagingStop");
        Assert.Equal(0, staging.GetProperty("loadedBaskets").GetInt32());
        Assert.Equal(1, staging.GetProperty("pendingDemands").GetInt32());
        JsonElement free = staging.GetProperty("freeSlotsByGroup");
        Assert.Equal(JsonValueKind.Object, free.ValueKind);
        Assert.NotEmpty(free.EnumerateObject());
        Assert.Contains("在待送点等客户出发：已装 0 篮", html, StringComparison.Ordinal);
        Assert.Contains("待装 1 条", html, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static async Task<RuntimeFixture> ArrangeAsync(StopKind kind)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AllowedWorkTypes = [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire];
        fixture.Options.StationDepartureWaitTimeout = StationDeadline;
        fixture.Options.CargoHoldingTimeout = StationDeadline;
        fixture.Options.StagingDepartureRules.Enabled = kind != StopKind.StagingRulesOff;
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, TaskTypeStationRuntimeSeed.GateStationName),
            new RiotMapStation(300, "等待点"),
            new RiotMapStation(StagingStationRiotId, StagingStationName));
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now,
            requiredTaskTypes: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings: [TaskTypeStationRuntimeSeed.GateBinding, StagingBinding]);
        AcceptedDemandSnapshot demand = fixture.Demand(Demand, Sublot, Now.AddMinutes(-10));
        fixture.Catalog.Set(kind == StopKind.MachineRulesOn
            ? demand
            : demand with
            {
                WorkType = TransportTaskTypes.StagingToWire,
                TransportDemandKey = $"{Sublot}|{TransportTaskTypes.StagingToWire}",
            });
        fixture.BoxCounts.Set(Sublot, 4);
        return fixture;
    }

    /// <summary>本区允许途中追加：持货等单适用（与 <c>Batch7CargoHoldingTests.HoldingFixtureAsync</c> 同一版参数）。</summary>
    private static async Task AllowEnRouteAppendAsync(RuntimeFixture fixture)
    {
        await new DispatchZoneParameterStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .WriteVersionAsync(
                [new DispatchZoneParameters(fixture.Options.DispatchZone, 10_000_000, null)],
                fixture.Clock.GetUtcNow(),
                Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>到站、录入、装货落定，停在离站等待。</summary>
    private static async Task LoadTheBasketAsync(RuntimeFixture fixture)
    {
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    private static async Task<DateTimeOffset?[]> WorklistDeadlinesAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        return
        [
            .. payloads.Select(json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement deadline = document.RootElement.GetProperty("payload").GetProperty("stationDepartureDeadlineAt");
                return deadline.ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : deadline.GetDateTimeOffset();
            })
        ];
    }

    private static async Task<JsonElement[]> CargoHoldingRowsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = fixture.OpenConnectionContext();
        object fact = await new CargoHoldingQueryEndpoint(
                fixture.Options.CargoHoldingTimeout, fixture.Clock, fixture.Options.StagingDepartureRules)
            .ReadAsync(reading, Token);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(fact));
        return [.. document.RootElement.GetProperty("journeys").EnumerateArray().Select(row => row.Clone())];
    }

    private static async Task<JsonDocument> WaitingJourneysAsync(RuntimeFixture fixture)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => fixture.ChargingPolicy);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using ControlServerDbContext reading = fixture.OpenConnectionContext();
        object result = await new WaitingJourneysQueryEndpoint(
                fixture.Options, fixture.Clock, provider.GetRequiredService<IServiceScopeFactory>())
            .ReadAsync(reading, Token);
        return JsonDocument.Parse(JsonSerializer.Serialize(result));
    }

    private static Task<JourneyRuntimeRow> JourneyAsync(RuntimeFixture fixture, string journeyId) =>
        fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journeyId, Token);

    private static Task<JourneyDemandRow[]> MembershipsAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking().Where(row => row.DemandId == Demand).ToArrayAsync(Token);

    private static Task<int> StationDeadlineReleasesAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .CountAsync(row => row.RemovalReason == DemandJourneyLookup.ReleasedAtStationDeadlineReason, Token);

    private static Task<StructuralDispatchBlockRow[]> AlarmsAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token);
}
