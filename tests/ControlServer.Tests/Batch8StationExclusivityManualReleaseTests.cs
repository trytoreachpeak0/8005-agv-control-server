using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>
/// 站点独占的人工释放（批次8 跟进，control-server#419）：持有车离线、被拖走或退役时，公共站点与等待点的独占由现场人员带理由与核实记录
/// 释放；与离点清扫同一个删除条件，同刻只一方生效。
/// </summary>
/// <remarks>
/// 判定对 <see cref="StationExclusivityManualRelease"/> 直接测——Host 接口与 FieldOps 两条入口都只是转交它，各自的边界在
/// <c>StationExclusivityReleaseEndpointsTests</c> 与 <c>StationExclusivityFieldOpsTests</c>。站号：202 是 <c>WIRE_TO_GATE</c> 的关卡
/// （公共站点），101、12 是机台，300 当等待点用。
/// </remarks>
public sealed class Batch8StationExclusivityManualReleaseTests
{
    private const int Gate = 202;
    private const int Machine = 12;
    private const int WaitingPoint = 300;
    private const string DemandA = "20000000-0000-4000-8000-00000000000a";
    private const string DemandB = "20000000-0000-4000-8000-00000000000b";
    private const string AgvA = "AGV-A";
    private const string KeyA = "KEY-A";
    private const string AgvB = "AGV-B";
    private const string KeyB = "KEY-B";

    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;
    private static readonly DateTimeOffset ReleasedAt = At.AddMinutes(30);
    private static readonly string HolderA = JourneyIdentity.ForAnchorDemand(DemandA);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 票面第 4 条：离线不放、人工放、下一轮别的车可预占 -------------------------------------------------------

    /// <summary>
    /// A 在关卡上完成了旅程然后离线（RIoT 最后记得的站是机台）：离点清扫连跑两轮都不放（离线不算离开）。人工释放之后行没了、经过记下人工释放、审计记下操作员；
    /// 正开往关卡的 B 下一轮补预占拿到它。
    /// </summary>
    [Fact]
    public async Task AnOfflineHolderKeepsItsStationThroughTheSweepAndAManualReleaseGivesItToTheNextVehicle()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);
        // RIoT's last known station is elsewhere: only "offline" keeps the sweep from reading it as a departure.
        Facts offline = new(Seen(connected: false, station: Machine));

        await Sweep(fixture.NewContext(), offline).ReleaseDepartedAsync(Token);
        await Sweep(fixture.NewContext(), offline).ReleaseDepartedAsync(Token);
        Assert.Equal(KeyA, (await HeldAtAsync(fixture, Gate))!.VehicleKey);

        StationExclusivityManualReleaseResult result = await ReleaseAsync(fixture.NewContext(), offline, Request());

        Assert.Equal(
            (true, 0, StationExclusivityManualRelease.CrossCheckOffline, KeyA, HolderA),
            (result.Released, result.Codes.Count, result.RiotCrossCheck, result.Holder!.VehicleKey, result.Holder.JourneyId));
        Assert.Null(await HeldAtAsync(fixture, Gate));
        StationExclusivityRecord record = Assert.Single(await HistoryAsync(fixture, Gate));
        Assert.Equal(
            (StationExclusivityManualRelease.ReleasedByOperator, (DateTimeOffset?)ReleasedAt),
            (record.ReleaseReason, record.ReleasedAt));
        AdministratorAuditRecordRow audit = Assert.Single(await AuditsAsync(fixture));
        Assert.Equal(
            (result.AuditRecordId, GovernanceActionOutcome.Succeeded, GovernedObjectKind.StationExclusivity, "25/202", "班长"),
            (audit.AuditRecordId, audit.Outcome, audit.ObjectKind, audit.ObjectId, audit.ClaimedAdministratorRole));
        JsonElement detail = JsonDocument.Parse(audit.DetailJson).RootElement;
        Assert.Equal(
            ("OP-7", "SITE-2026-0930-01", HolderA, "RELEASED"),
            (detail.GetProperty("said").GetProperty("operatorId").GetString(),
                detail.GetProperty("said").GetProperty("siteVerification").GetString(),
                detail.GetProperty("holder").GetProperty("journeyId").GetString(),
                detail.GetProperty("result").GetString()));

        await SeedApproachingAsync(fixture, DemandB, AgvB, KeyB);
        await Sweep(fixture.NewContext(), offline).ReserveApproachingAsync(new HashSet<int> { Gate }, Token);

        StationExclusivity given = (await HeldAtAsync(fixture, Gate))!;
        Assert.Equal(
            (KeyB, JourneyIdentity.ForAnchorDemand(DemandB), StationExclusivityStates.Reserved),
            (given.VehicleKey, given.JourneyId, given.State));
    }

    // ---- 同刻只一方生效：数据库裁决 ------------------------------------------------------------------------------

    /// <summary>
    /// 人工释放读到行、正在读 RIoT 的那一刻，清扫凭离点证据把它放了：人工这一方删到 0 行，拒绝 <c>HOLDER_CHANGED_SINCE_READ</c>，
    /// 经过仍是离点释放、只关一次；审计记下这次失败。
    /// </summary>
    [Fact]
    public async Task ASweepReleasingBetweenTheManualReadAndItsDeleteWinsAndTheManualReleaseWritesNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);
        Facts elsewhere = new(Seen(connected: true, station: Machine));
        Facts offline = new(Seen(connected: false, station: null))
        {
            During = () => Sweep(fixture.NewContext(), elsewhere).ReleaseDepartedAsync(Token)
        };

        StationExclusivityManualReleaseResult result = await ReleaseAsync(fixture.NewContext(), offline, Request());

        Assert.True(offline.DuringRan, "The sweep never ran between the read and the delete, so this proves nothing.");
        Assert.False(result.Released);
        Assert.Equal([StationExclusivityManualRelease.HolderChanged], result.Codes);
        StationExclusivityRecord record = Assert.Single(await HistoryAsync(fixture, Gate));
        Assert.Equal(
            (FixedStationExclusivity.ReleasedOnDepartureEvidence, (DateTimeOffset?)At.AddMinutes(5)),
            (record.ReleaseReason, record.ReleasedAt));
        Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await AuditsAsync(fixture)).Outcome);
    }

    /// <summary>
    /// 反过来：清扫读到行、正在读 RIoT（车在线、报在机台，离点证据成立）的那一刻，人工释放先放了。清扫删到 0 行，不再写经过、不记释放日志；
    /// 经过是人工释放、只关一次。
    /// </summary>
    [Fact]
    public async Task AManualReleaseBetweenTheSweepsReadAndItsDeleteWinsAndTheSweepWritesNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);
        StationExclusivityManualReleaseResult? manual = null;
        Facts elsewhere = new(Seen(connected: true, station: Machine))
        {
            During = async () => manual = await ReleaseAsync(fixture.NewContext(), vehicleFacts: null, Request())
        };
        EventRecordingLogger<FixedStationSweepWarnings> log = new();

        await Sweep(fixture.NewContext(), elsewhere, log).ReleaseDepartedAsync(Token);

        Assert.True(elsewhere.DuringRan, "The manual release never ran between the read and the delete, so this proves nothing.");
        Assert.True(manual!.Released);
        Assert.Null(await HeldAtAsync(fixture, Gate));
        StationExclusivityRecord record = Assert.Single(await HistoryAsync(fixture, Gate));
        Assert.Equal(
            (StationExclusivityManualRelease.ReleasedByOperator, (DateTimeOffset?)ReleasedAt),
            (record.ReleaseReason, record.ReleasedAt));
        Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id == 2210);
    }

    /// <summary>
    /// 读到之后，同一辆车的新旅程接手了这个站（交接改了 <c>JourneyId</c> 与 <c>RecordId</c>）：人工核实的不再是这一次，拒绝，行留给新旅程。
    /// </summary>
    [Fact]
    public async Task AHandOverBetweenTheReadAndTheDeleteRefusesTheReleaseAndKeepsTheNewHolding()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);
        Facts offline = new(Seen(connected: false, station: null))
        {
            During = async () =>
            {
                await using ControlServerDbContext handing = fixture.NewContext();
                await FixedStationExclusivity.StageReserveAsync(handing, 25, Gate, KeyA, "journey:next", At.AddMinutes(10), Token);
                await handing.SaveChangesAsync(Token);
            }
        };

        StationExclusivityManualReleaseResult result = await ReleaseAsync(fixture.NewContext(), offline, Request());

        Assert.True(offline.DuringRan);
        Assert.False(result.Released);
        Assert.Equal([StationExclusivityManualRelease.HolderChanged], result.Codes);
        Assert.Equal("journey:next", (await HeldAtAsync(fixture, Gate))!.JourneyId);
    }

    // ---- 治理：缺身份、理由、证据被拒 -----------------------------------------------------------------------------

    /// <summary>缺操作员、理由、现场核实记录或持有车，任一条都拒绝：行不动，审计记下失败与理由码。空白等于没写。</summary>
    [Theory]
    [InlineData("operator", StationExclusivityManualRelease.OperatorRequired)]
    [InlineData("reason", StationExclusivityManualRelease.ReasonRequired)]
    [InlineData("site", StationExclusivityManualRelease.SiteVerificationRequired)]
    [InlineData("vehicle", StationExclusivityManualRelease.VehicleRequired)]
    public async Task ARequestWithoutAnOperatorReasonSiteVerificationOrVehicleIsRefusedAndAudited(string missing, string code)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);
        StationExclusivityManualReleaseRequest request = missing switch
        {
            "operator" => Request() with { OperatorId = "  " },
            "reason" => Request() with { Reason = null },
            "site" => Request() with { SiteVerification = "" },
            _ => Request() with { VehicleKey = null }
        };

        StationExclusivityManualReleaseResult result = await ReleaseAsync(fixture.NewContext(), vehicleFacts: null, request);

        Assert.False(result.Released);
        Assert.Equal([code], result.Codes);
        Assert.Equal(HolderA, (await HeldAtAsync(fixture, Gate))!.JourneyId);
        Assert.Null(Assert.Single(await HistoryAsync(fixture, Gate)).ReleasedAt);
        AdministratorAuditRecordRow audit = Assert.Single(await AuditsAsync(fixture));
        Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
        Assert.Equal(
            [code],
            JsonDocument.Parse(audit.DetailJson).RootElement.GetProperty("codes").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>太长的文字不进审计原文，只记长度；请求照样被拒。</summary>
    [Fact]
    public async Task AnOverlongFieldIsRefusedAndRecordedByItsLengthOnly()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);
        string overlong = new('x', StationExclusivityManualRelease.MaxTextLength + 1);

        StationExclusivityManualReleaseResult result = await ReleaseAsync(
            fixture.NewContext(), vehicleFacts: null, Request() with { Reason = overlong });

        Assert.Equal([StationExclusivityManualRelease.FieldTooLong], result.Codes);
        JsonElement said = JsonDocument.Parse(Assert.Single(await AuditsAsync(fixture)).DetailJson).RootElement.GetProperty("said");
        Assert.Equal(overlong.Length, said.GetProperty("reasonLength").GetInt32());
        Assert.False(said.TryGetProperty("reason", out _));
    }

    // ---- 只释放指定的那一行 --------------------------------------------------------------------------------------

    /// <summary>
    /// 库里同时有：A 在关卡 202（公共站点）、A 在等待点 300、B 在另一个公共站点 210、A 在充电桩 211。点名 202 只放 202；点名等待点 300 只放 300；
    /// 点名 210 却说持有车是 A——不是它，拒绝；充电桩 211 不归这里（批次 9 的人工清桩）；没人占的 213 拒绝。
    /// </summary>
    [Fact]
    public async Task OnlyTheNamedHoldingOfTheNamedVehicleIsReleasedAndNeverAChargerHere()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);
        StationExclusivityStore store = new(fixture.NewContext());
        // An idle return that has ended: no journey row and no purpose claim on its JourneyId any more, so nothing binds it
        // (the shape still being driven to is AnIdleReturnStillClaimingItsVehicleKeepsItsWaitingPoint).
        await store.TryAcquireAsync(
            new StationExclusivityRequest(25, WaitingPoint, StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Occupied, 1),
            KeyA, "idle-return:ended-a", At, Token);
        await store.TryAcquireAsync(
            new StationExclusivityRequest(25, 210, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyB, "journey:b", At, Token);
        await store.TryAcquireAsync(
            new StationExclusivityRequest(25, 211, StationExclusivityKinds.Charger, StationExclusivityStates.Reserved, null, 1),
            KeyA, "journey:charge-a", At, Token);

        Assert.True((await ReleaseAsync(fixture.NewContext(), null, Request())).Released);
        Assert.Equal("210,211,300", await HeldStationsAsync(fixture));

        Assert.True((await ReleaseAsync(fixture.NewContext(), null, Request() with { StationId = WaitingPoint })).Released);
        Assert.Equal("210,211", await HeldStationsAsync(fixture));

        Assert.Equal(
            [StationExclusivityManualRelease.HolderMismatch],
            (await ReleaseAsync(fixture.NewContext(), null, Request() with { StationId = 210 })).Codes);
        Assert.Equal(
            [StationExclusivityManualRelease.KindNotReleasable],
            (await ReleaseAsync(fixture.NewContext(), null, Request() with { StationId = 211 })).Codes);
        Assert.Equal(
            [StationExclusivityManualRelease.StationNotHeld],
            (await ReleaseAsync(fixture.NewContext(), null, Request() with { StationId = 213 })).Codes);
        Assert.Equal("210,211", await HeldStationsAsync(fixture));
    }

    // ---- 持有旅程仍在途、RIoT 说车在站上 --------------------------------------------------------------------------

    /// <summary>
    /// A 的旅程还在途、关卡仍是它未完成的下一站、站是预占（车在路上）：拒绝，放了下一轮补预占就还给它。阻断之后仍拒（#422 审查建议 1）：
    /// 修好恢复后车会照原单开往已经放给别的车的关卡，要先用故障恢复的放弃出口收尾。旅程收尾（完成）之后可以放。
    /// </summary>
    [Fact]
    public async Task AHolderOnItsWayIsRefusedLiveOrBlockedAndReleasedOnceItsJourneyIsOver()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedApproachingAsync(fixture, DemandA, AgvA, KeyA);
        await new StationExclusivityStore(fixture.NewContext()).TryAcquireAsync(
            new StationExclusivityRequest(25, Gate, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Reserved, null),
            KeyA, HolderA, At, Token);

        Assert.Equal(
            [StationExclusivityManualRelease.HolderJourneyStillBound],
            (await ReleaseAsync(fixture.NewContext(), null, Request())).Codes);

        await SetStageAsync(fixture, DemandA, JourneyRuntimeStage.Blocked);
        Assert.Equal(
            [StationExclusivityManualRelease.HolderJourneyBlockedOnApproach],
            (await ReleaseAsync(fixture.NewContext(), null, Request())).Codes);
        Assert.Equal(HolderA, (await HeldAtAsync(fixture, Gate))!.JourneyId);

        await SetStageAsync(fixture, DemandA, JourneyRuntimeStage.Completed);
        Assert.True((await ReleaseAsync(fixture.NewContext(), null, Request())).Released);
    }

    /// <summary>
    /// 阻断在站上（站为占用、关卡仍是未完成停靠）：清扫一律不放，等人判——现场核实车已不在，人工可以放。
    /// </summary>
    [Fact]
    public async Task AHolderBlockedWhereItStandsIsReleasedOnTheSiteCheck()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedApproachingAsync(fixture, DemandA, AgvA, KeyA);
        await new StationExclusivityStore(fixture.NewContext()).TryAcquireAsync(
            new StationExclusivityRequest(25, Gate, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, HolderA, At, Token);
        await SetStageAsync(fixture, DemandA, JourneyRuntimeStage.Blocked);

        StationExclusivityManualReleaseResult result = await ReleaseAsync(fixture.NewContext(), null, Request());

        Assert.True(result.Released, string.Join(',', result.Codes));
        Assert.Null(await HeldAtAsync(fixture, Gate));
    }

    /// <summary>
    /// cs#389 的空闲返回（#422 审查必修 1）：承诺只写用途占有 <c>IDLE_RETURN</c> 与等待点预占，同一个 <c>JourneyId</c>，不建旅程行。
    /// 车在去等待点的路上离线，现场核实「车不在点上」为真——照样拒：放给别的车之后原车重连会照原单开来，两车同点。用途占有释放之后可以放。
    /// </summary>
    [Fact]
    public async Task AnIdleReturnStillClaimingItsVehicleKeepsItsWaitingPoint()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        const string idleReturn = "idle-return:KEY-A:1";
        await new StationExclusivityStore(fixture.NewContext()).TryAcquireAsync(
            new StationExclusivityRequest(25, WaitingPoint, StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Reserved, 1),
            KeyA, idleReturn, At, Token);
        await using (ControlServerDbContext write = fixture.NewContext())
        {
            write.Add(new VehiclePurposeClaimRow
            {
                VehicleKey = KeyA, Purpose = VehiclePurposes.IdleReturn, JourneyId = idleReturn, ClaimedAt = At
            });
            await write.SaveChangesAsync(Token);
        }
        StationExclusivityManualReleaseRequest request = Request() with { StationId = WaitingPoint };

        Assert.Equal(
            [StationExclusivityManualRelease.HolderJourneyStillBound],
            (await ReleaseAsync(fixture.NewContext(), new Facts(Seen(connected: false, station: null)), request)).Codes);
        Assert.Equal(idleReturn, (await HeldAtAsync(fixture, WaitingPoint))!.JourneyId);

        await using (ControlServerDbContext write = fixture.NewContext())
        {
            await write.Set<VehiclePurposeClaimRow>().Where(row => row.VehicleKey == KeyA).ExecuteDeleteAsync(Token);
        }
        Assert.True((await ReleaseAsync(fixture.NewContext(), null, request)).Released);
    }

    /// <summary>
    /// 服务端在线时的交叉核对：RIoT 说车在线且就在这个站上，与现场核实冲突，拒绝。读不到 RIoT 不挡（离线正是这条出口的理由），如实记
    /// <c>UNREADABLE</c>。
    /// </summary>
    [Fact]
    public async Task AVehicleRiotReportsOnlineAtTheStationIsRefusedWhileAnUnreadableOneIsReleased()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFinishedHolderAtGateAsync(fixture);

        StationExclusivityManualReleaseResult atStation = await ReleaseAsync(
            fixture.NewContext(), new Facts(Seen(connected: true, station: Gate)), Request());
        Assert.Equal([StationExclusivityManualRelease.VehicleReportedAtStation], atStation.Codes);
        Assert.Equal(StationExclusivityManualRelease.CrossCheckAtThisStation, atStation.RiotCrossCheck);
        Assert.NotNull(await HeldAtAsync(fixture, Gate));

        StationExclusivityManualReleaseResult unreadable = await ReleaseAsync(
            fixture.NewContext(), new Facts(() => throw new HttpRequestException("RIoT down")), Request());
        Assert.Equal((true, StationExclusivityManualRelease.CrossCheckUnreadable), (unreadable.Released, unreadable.RiotCrossCheck));
        Assert.Null(await HeldAtAsync(fixture, Gate));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static StationExclusivityManualReleaseRequest Request() =>
        new(25, Gate, KeyA, "OP-7", "A 车离线，已拖离关卡", "SITE-2026-0930-01", "班长");

    private static async Task<StationExclusivityManualReleaseResult> ReleaseAsync(
        ControlServerDbContext context, IRiotVehicleFacts? vehicleFacts, StationExclusivityManualReleaseRequest request)
    {
        await using (context)
        {
            GovernanceStore governance = new(
                context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
            return await StationExclusivityManualRelease.ReleaseAsync(context, governance, vehicleFacts, request, ReleasedAt, Token);
        }
    }

    /// <summary>A 走完了一趟 [取货 101, 关卡 202] 的旅程，停在关卡上占用着它。</summary>
    private static async Task SeedFinishedHolderAtGateAsync(Batch7JourneyFixture fixture)
    {
        await AcceptAsync(fixture.Context, DemandA, AgvA, KeyA);
        await new StationExclusivityStore(fixture.Context).TryAcquireAsync(
            new StationExclusivityRequest(25, Gate, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, HolderA, At, Token);
        await SetStageAsync(fixture, DemandA, JourneyRuntimeStage.Completed);
    }

    /// <summary>车站在取货停靠 101 上持货等单，下一站是关卡。</summary>
    private static async Task SeedApproachingAsync(Batch7JourneyFixture fixture, string demandId, string agvId, string vehicleKey)
    {
        await AcceptAsync(fixture.Context, demandId, agvId, vehicleKey);
        await using ControlServerDbContext write = fixture.NewContext();
        JourneyRuntimeRow journey = await write.JourneyRuntimes.SingleAsync(row => row.DemandId == demandId, Token);
        journey.Stage = JourneyRuntimeStage.AwaitingStationDeparture;
        journey.LoadingPhaseState = LoadingPhaseStates.CargoHoldingWait;
        await write.SaveChangesAsync(Token);
        await fixture.RenewContextAsync();
    }

    private static async Task SetStageAsync(Batch7JourneyFixture fixture, string demandId, JourneyRuntimeStage stage)
    {
        await using ControlServerDbContext write = fixture.NewContext();
        JourneyRuntimeRow journey = await write.JourneyRuntimes.SingleAsync(row => row.DemandId == demandId, Token);
        journey.Stage = stage;
        if (stage == JourneyRuntimeStage.Blocked)
        {
            journey.SetBlockReason("MOVEMENT_RESULT_UNKNOWN", At.AddMinutes(1));
        }
        await write.SaveChangesAsync(Token);
        await fixture.RenewContextAsync();
    }

    private static async Task AcceptAsync(ControlServerDbContext context, string demandId, string agvId, string vehicleKey)
    {
        JourneyExecutionPlan plan = Batch7JourneyFixture.Plan(demandId, agvId, vehicleKey, At) with
        {
            PickupStationId = "ST-101",
            PickupStationRiotId = 101,
            FixedTaskStationRiotId = Gate,
        };
        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            Batch7JourneyFixture.Snapshot(demandId, At),
            JourneyPlanBuilder.PickupIntent(plan, demandId, At),
            plan,
            Token);
    }

    private static FixedStationExclusivitySweep Sweep(
        ControlServerDbContext context, IRiotVehicleFacts facts, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        new(context, facts, new FixedClock(At.AddMinutes(5)), logger ?? NullLogger.Instance, new FixedStationSweepWarnings());

    private static async Task<StationExclusivity?> HeldAtAsync(Batch7JourneyFixture fixture, int station)
    {
        await using ControlServerDbContext read = fixture.NewContext();
        return await new StationExclusivityStore(read).ReadAsync(25, station, Token);
    }

    /// <summary>此刻被占着的站号，升序、逗号分隔。</summary>
    private static async Task<string> HeldStationsAsync(Batch7JourneyFixture fixture)
    {
        await using ControlServerDbContext read = fixture.NewContext();
        int[] held = await read.Set<StationExclusivityRow>().AsNoTracking()
            .OrderBy(row => row.StationId).Select(row => row.StationId).ToArrayAsync(Token);
        return string.Join(',', held);
    }

    private static async Task<IReadOnlyList<StationExclusivityRecord>> HistoryAsync(Batch7JourneyFixture fixture, int station)
    {
        await using ControlServerDbContext read = fixture.NewContext();
        return await new StationExclusivityStore(read).ListHistoryAsync(25, station, Token);
    }

    private static async Task<AdministratorAuditRecordRow[]> AuditsAsync(Batch7JourneyFixture fixture)
    {
        await using ControlServerDbContext read = fixture.NewContext();
        return await read.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == StationExclusivityManualRelease.AuditAction)
            .ToArrayAsync(Token);
    }

    private static RiotVehicleObservation Seen(bool connected, int? station) =>
        new(KeyA, connected, true, "IDLE", "MAP-25", station, 50, "DISCHARGING", 0, At);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// RIoT 的车辆读数。<see cref="During"/> 在第一次读的时候跑一次——两条释放路径都在读到独占行之后、删之前读 RIoT，
    /// 所以这是把另一方确定地插进那个窗口的办法，不靠时序。
    /// </summary>
    private sealed class Facts(Func<RiotVehicleObservation> read) : IRiotVehicleFacts
    {
        public Facts(RiotVehicleObservation observation)
            : this(() => observation)
        {
        }

        public Func<Task>? During { get; init; }

        public bool DuringRan { get; private set; }

        public async Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            if (During is { } during && !DuringRan)
            {
                DuringRan = true;
                await during();
            }
            return read();
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A station release reads no order.");

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A station release creates no order.");
    }
}
