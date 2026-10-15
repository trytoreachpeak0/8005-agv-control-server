using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
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
    internal const string Demand = "10000000-0000-4000-8000-000000000001";

    // 夹具的 AdvanceToLoadResultAsync 录的就是这一条。
    private const string Sublot = "SUBLOT-001";

    internal const int StagingStationRiotId = 305;

    internal const string SecondDemand = "10000000-0000-4000-8000-000000000709";

    internal const string SecondSublot = "SUBLOT-709";

    internal const string StagingStationName = "派工待送取货";

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
    /// 验收 3：装了一篮，钟拨过 60 分钟，分区允许途中追加（持货等单适用）。三种停靠都在持货期限到时以 <c>CARGO_HOLDING_TIMEOUT</c> 关闭装货阶段、
    /// 发离站核验，车上看得到期限。control-server#709 时待送点在这里不关闭、没有期限；CP-0014（control-server#727）改为装上一篮后与机台同一个期限，
    /// 细节（追加重新起算、到期即走）见 <see cref="StagingCargoHoldingDeadlineTests"/>。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnHourAfterTheFirstBasketClosesTheLoadingPhaseAtEveryKindOfStop(StopKind kind)
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
        Assert.Equal(LoadingPhaseStates.Closed, journey.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, journey.LoadingClosedReason);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The holding deadline sent nobody on.");
        Assert.Contains(snapshots, snapshot => snapshot.CargoHoldingDeadlineAt == loaded.CargoHoldingStartedAt + StationDeadline);
        Assert.Null(Assert.Single(await MembershipsAsync(fixture)).RemovedAt);
    }

    /// <summary>
    /// 验收 4：开门（装货命令发出）之后迟迟没有结果、仓门没证明关着。待送点：命令发出满 N（这里 5 秒）挂仓门未闭告警，停靠不结束；
    /// 落定后告警撤销。另两种照旧按离站期限（这里 10 秒，从到站算）挂。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 车到站 4 秒后才扫码、下装货命令，所以「从到站算」与「从命令算」在第 7 秒分得开（独立审查 L2）：从到站算 N 已过，从命令算还差 2 秒。
    /// 第 9 秒待送点挂上（命令后 5 秒）、另两种还没有（到站后 9 秒，期限 10 秒）；第 11 秒三种都挂上。
    /// </para>
    /// <para>
    /// <remarks>
    /// 钟只拨几秒、不另发心跳，与 <c>JourneyRuntimeWorkerLoadDeadlineTests.ADoorLeftOpenPastTheDeadlineRaisesTheAlarmWithoutEndingTheStop</c>
    /// 同一个量级：拨过车载端证据的有效期，推进段会先因证据过期停在别处，量到的就不是这条告警了。
    /// </para>
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
        JourneyRuntimeRow arrived = await fixture.AdvanceToSublotWaitAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        await fixture.SubmitSublotAsync(Sublot);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);

        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow atSeven = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, atSeven.Stage);
        Assert.Null(atSeven.BlockReasonCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow atNine = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, atNine.Stage);
        Assert.Equal(
            kind == StopKind.StagingRulesOn ? JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason : null,
            atNine.BlockReasonCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow atEleven = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, atEleven.Stage);
        Assert.Equal(JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason, atEleven.BlockReasonCode);
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
        // 生产的 RIoT 网关在 HTTP 调用返回之后才给读数盖时间戳（HttpRiotMovementGateway.ReadVehicleAsync），所以读数总比推进段进门时取的
        // 那个「此刻」晚一点。替身默认不走钟，看不出这件事；这里让每次读车先把钟拨前 1 毫秒（独立审查 M1）。
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));

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
        // 收尾原因说缺电（control-server#713，协议 4.0.0 候选）：车上收尾清单的 stopEndedReason 是 CHARGING_REQUIRED，不再是 DEMAND_RELEASED。
        Assert.Equal(CurrentStopDepartures.ChargingRequiredReason, journey.BlockReasonCode);
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
        fixture.Riot.BeforeReadVehicle = null;
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
    /// 验收 5 的另一半，自 control-server#713 起改为「先送货再充电」（CP-0013，用户 10-14 改定）：车上已经有货、电量跌到线下，不走一篮没装那一支
    /// （不收尾、货仍有需求认领），而是按自动出发离站去送——装货阶段以 <c>CHARGING_REQUIRED</c> 关闭、发离站核验。详细的断言在
    /// <c>StagingChargingRequiredDepartureTests</c>。
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
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));

        for (int step = 0; step < 3; step++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.NotEqual(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal((LoadingPhaseStates.Closed, LoadingClosedReasons.ChargingRequired), (journey.LoadingPhaseState, journey.LoadingClosedReason));
        JourneyDemandRow membership = Assert.Single(await MembershipsAsync(fixture));
        Assert.Null(membership.RemovedAt);
        Assert.Equal(JourneyDemandStatuses.Loaded, membership.Status);
        Assert.True(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token));
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The loaded vehicle did not set off to deliver.");
    }

    /// <summary>
    /// 验收 5 的守卫（独立审查 L1），自 control-server#713 起：同站追加之后，第 1 个待送点停靠已装上 A，车「原地到站」第 2 个停靠、在等 B 录入；
    /// 此时缺电。旅程不收尾、A 不放回——A 在车上，结束旅程会让它没有任何需求认领；B 没开始装，按出发的放回语义放回待派，车带着 A 离站去送。
    /// </summary>
    /// <remarks>
    /// 删掉 <c>TryReleaseLowBatteryStagingStopAsync</c> 开头按整趟判「有没有已装或在装」的那两个条件，这一条红：A 被一起放回、旅程收尾。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AFlatBatteryWaitingForTheSecondSameStationDemandDoesNotStrandTheFirstOnTheVehicle()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        JourneyStopRow first = await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, Demand);
        Assert.Equal(StagingStationRiotId, first.StationRiotId);

        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        // 纠错宽限（出厂 30 秒）过后，同站追加让第 1 个停靠的离站等待结束：发离站核验，车原地「到站」第 2 个停靠。
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(
            fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        JourneyRuntimeRow waiting = await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, SecondDemand, "TO_GATE");
        JourneyStopRow second = await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, SecondDemand);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        Assert.NotEqual(first.StopId, second.StopId);
        Assert.Equal(StagingStationRiotId, second.StationRiotId);

        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        for (int step = 0; step < 3; step++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.NotEqual(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(LoadingClosedReasons.ChargingRequired, journey.LoadingClosedReason);
        JourneyDemandRow loaded = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token);
        JourneyDemandRow released = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand, Token);
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (loaded.Status, loaded.RemovedAt));
        Assert.NotNull(released.RemovedAt);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
        Assert.True(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token), "The loaded vehicle was let go.");
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Message.Contains("went back to the backlog and the journey ended", StringComparison.Ordinal));
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

    internal static async Task<RuntimeFixture> ArrangeAsync(StopKind kind)
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
    internal static async Task AllowEnRouteAppendAsync(RuntimeFixture fixture)
    {
        await new DispatchZoneParameterStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .WriteVersionAsync(
                [new DispatchZoneParameters(fixture.Options.DispatchZone, 10_000_000, null)],
                fixture.Clock.GetUtcNow(),
                Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>到站、录入、装货落定，停在离站等待。</summary>
    internal static async Task LoadTheBasketAsync(RuntimeFixture fixture)
    {
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    /// <summary>
    /// 第二条 STAGING_TO_WIRE 需求追加进这趟旅程，取货在同一个待送站：新开一个停靠排在当前停靠之后（REQ-0196），按受理同一个做法冻结
    /// 第一条需求的那一版规则与绑定（REQ-0344）。照 <c>Batch7ThreeStopJourneyTests.AppendDemandAsync</c>，那一份写死了 WIRE_TO_GATE。
    /// </summary>
    internal static async Task AppendStagingDemandAsync(RuntimeFixture fixture, string demandId, string sublot)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(Demand);
        DemandTaskTypeStationFreeze freeze = await new DemandTaskTypeStationFreezeStore(fixture.Context).ReadAsync(Demand, Token)
            ?? throw new InvalidOperationException("The first demand froze no task type station binding.");
        long catalogRevision = await fixture.Context.FrozenDemandStations.AsNoTracking()
            .Where(row => row.DemandId == Demand)
            .Select(row => row.CatalogRevision)
            .FirstAsync(Token);
        JourneyStopRow[] stops = [.. (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .ToArrayAsync(Token))
            .OrderBy(row => row.Sequence)];
        int currentIndex = stops.ToList().FindIndex(
            row => row.Status != JourneyStopStatuses.Completed && row.Status != JourneyStopStatuses.Removed);
        string zone = runtime.DispatchZone;
        EnRouteStop pickup = new(
            JourneyIdentity.AppendedPickupStopId(demandId), StagingStationName, StagingStationRiotId, zone, JourneyStopRoles.Pickup);
        EnRouteStop unload = new(
            JourneyIdentity.AppendedUnloadStopId(demandId), stops[^1].StationId, stops[^1].StationRiotId, zone, JourneyStopRoles.Unload);
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            new EnRouteVehiclePlan(
                [.. stops.Select(row => new EnRouteStop(row.StopId, row.StationId, row.StationRiotId, row.DispatchZone, row.StopRole))],
                fixture.Riot.Vehicle.CurrentStationId ?? 0,
                currentIndex,
                stops.ToDictionary(row => row.StopId, _ => 1, StringComparer.Ordinal)),
            new EnRouteAppendCandidate(pickup, unload, zone),
            new DispatchZoneParameterTableVersion(
                7, new string('0', 64), null, DateTimeOffset.UnixEpoch, "test",
                new Dictionary<string, DispatchZoneParameters>(StringComparer.Ordinal)
                {
                    [zone] = new DispatchZoneParameters(zone, 10_000_000, null)
                }),
            (from, to) => Math.Abs(from - to) * 1000L);
        EnRouteAppendPlacement placement = decision.Placement
            ?? throw new InvalidOperationException($"追加被拒：{decision.RefusalReasonCode}");

        JourneyExecutionPlan plan = new(
            runtime.AgvId,
            runtime.VehicleKey,
            runtime.AgvLifecycleGeneration,
            runtime.MapId,
            runtime.MapIdentity,
            zone,
            $"route-{demandId}",
            StagingStationName,
            StagingStationRiotId,
            stops[^1].StationId,
            stops[^1].StationRiotId,
            2,
            [5, 6],
            JourneyPlanBuilder.StableGuid(demandId, "operation-session"),
            JourneyPlanBuilder.StableGuid(demandId, "pickup-leg"),
            $"W2G-{demandId}-PICKUP-1",
            JourneyPlanBuilder.StableGuid(demandId, "gate-leg"),
            $"W2G-{demandId}-GATE-1",
            1,
            fixture.Clock.GetUtcNow())
        {
            StationCatalogRevision = catalogRevision,
            TaskTypeStationRuleVersion = freeze.RuleVersion,
            TaskTypeStationBindingSetVersion = freeze.BindingSetVersion,
        };
        JourneyAppendPlan append = new(
            runtime.JourneyId,
            demandId,
            plan,
            placement.MergeIntoPickupStopId ?? JourneyIdentity.AppendedPickupStopId(demandId),
            placement.MergeIntoUnloadStopId ?? JourneyIdentity.AppendedUnloadStopId(demandId),
            zone,
            DispatchZoneParameterVersion: 7,
            [.. placement.Resequenced.Select(stop => new JourneyStopSequenceChange(stop.StopId, stop.Sequence))],
            fixture.Clock.GetUtcNow());
        fixture.BoxCounts.Set(sublot, 2);
        await new WireToGateStore(fixture.Context).AppendToJourneyAsync(
            fixture.Demand(demandId, sublot, Now.AddMinutes(-9)) with
            {
                WorkType = TransportTaskTypes.StagingToWire,
                TransportDemandKey = $"{sublot}|{TransportTaskTypes.StagingToWire}",
            },
            append,
            Token);
        fixture.Context.ChangeTracker.Clear();
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

    internal static async Task<JsonElement[]> CargoHoldingRowsAsync(RuntimeFixture fixture)
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
