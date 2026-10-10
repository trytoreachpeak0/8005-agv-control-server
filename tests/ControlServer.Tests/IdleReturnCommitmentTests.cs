using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 空闲返回的资格、选点与原子承诺（批次8-18，control-server#389；<c>REQ-0290</c>～<c>0292</c>、<c>REQ-0293</c> 前半句）。
/// </summary>
/// <remarks>
/// <para>
/// 入口是 <see cref="IdleReturnEvaluator.EvaluateAsync"/>，派车轮末尾调的就是它。库是迁移过的真库，账本、站点独占与等待点登记都是生产实现，
/// 路网是真的路网访问器读一份种进库的快照：一条单向链 210 → 214 → 215，每段一万毫米；216 在一个没有边的节点上，到不了。
/// </para>
/// <para>
/// <b>每条拒绝都断言「什么也没留下」</b>：用途占有、两张经过表、站点独占全空，而且没有旅程行、没有订单意图——本票不物化旅程，也不建单。
/// 只断言原因码的话，一个先写了占有、再判出不合格的实现一样是绿的。
/// </para>
/// </remarks>
public sealed class IdleReturnCommitmentTests
{
    private const int Map = 25;

    private const string VehicleA = "VK-A";

    private const string VehicleB = "VK-B";

    private static readonly DateTimeOffset Now = Batch7JourneyFixture.Now;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 全满足即承诺 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnIdleVehicleMeetingEveryConditionReservesTheNearestWaitingPointAndClaimsItselfInOneSave()
    {
        await using Harness harness = await Harness.CreateAsync();

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)));

        Assert.Equal(IdleReturnReasons.Committed, verdict.Reason);
        Assert.Equal(214, verdict.StationId);
        Assert.Equal(harness.RegistrationVersion, verdict.WaitingPointVersion);
        Assert.Equal(IdleReturnIdentity.JourneyIdFor(VehicleA, Now), verdict.JourneyId);
        Assert.StartsWith(IdleReturnIdentity.JourneyIdPrefix, verdict.JourneyId, StringComparison.Ordinal);

        VehiclePurposeClaim claim = Assert.IsType<VehiclePurposeClaim>(
            await new VehiclePurposeLedgerStore(harness.Db.NewContext()).ReadClaimAsync(VehicleA, Token));
        Assert.Equal(VehiclePurposes.IdleReturn, claim.Purpose);
        Assert.Equal(verdict.JourneyId, claim.JourneyId);
        StationExclusivity station = Assert.IsType<StationExclusivity>(
            await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 214, Token));
        Assert.Equal(
            (StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Reserved, VehicleA, verdict.JourneyId,
                (long?)harness.RegistrationVersion),
            (station.StationKind, station.State, station.VehicleKey, station.JourneyId, station.WaitingPointVersion));
        Assert.Single(await harness.Db.DumpAsyncOf("VehiclePurposeClaimRecords"));
        Assert.Single(await harness.Db.DumpAsyncOf("StationExclusivityRecords"));
        // 承诺不是旅程：本票不物化旅程、不写订单意图，引擎下一轮也就没有东西可推进（建单与执行在批次8-19）。
        Assert.Empty(await harness.Db.DumpAsyncOf("JourneyRuntimes"));
        Assert.Empty(await harness.Db.DumpAsyncOf("OrderIntents"));
    }

    // ---- 资格：每一条各一条拒绝 ---------------------------------------------------------------------------------

    public static TheoryData<string> Refusals =>
    [
        "disabled",
        "selected-for-transport",
        "has-purpose",
        "holds-station",
        "holds-charger",
        "fault-suspected",
        "fault-isolated",
        "slot-door-held",
        "has-next-business-target",
        "foreign-order",
        "own-order-result-unknown",
        "riot-order-active",
        "battery-below-line",
        "battery-unknown",
        "charging",
        "facts-stale",
        "position-unknown",
        "route-graph-disabled",
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachQualificationThatFailsRefusesWithItsOwnCodeAndLeavesNothingBehind(string failing)
    {
        await using Harness harness = await Harness.CreateAsync(enabled: failing != "disabled");
        IdleReturnCandidate candidate = harness.Candidate(VehicleA);
        string expected;
        switch (failing)
        {
            case "disabled":
                expected = IdleReturnReasons.Disabled;
                break;
            case "selected-for-transport":
                candidate = candidate with { LeftOverByTransport = false };
                expected = IdleReturnReasons.NotLeftOverByTransportThisRound;
                break;
            case "has-purpose":
                await harness.ClaimForTransportAsync(VehicleA);
                expected = IdleReturnReasons.VehicleHasPurpose;
                break;
            case "holds-station":
                // 停在上一次返回的等待点、等离点证据：占有已放，站还是它的。它已经在一个等待点上了，不再去另一个。
                await harness.HoldStationAsync(VehicleA, 216, StationExclusivityStates.Occupied);
                expected = IdleReturnReasons.VehicleHoldsStation;
                break;
            case "holds-charger":
                // 停在充电桩上等离点证据：同样已在一个等待之处。口径由 B9-07 定，今天与等待点一样拒。
                await harness.HoldStationAsync(VehicleA, 211, StationExclusivityStates.Occupied, StationExclusivityKinds.Charger);
                expected = IdleReturnReasons.VehicleHoldsStation;
                break;
            case "fault-suspected":
                // 审查 M1：故障阻断曾经只在派车链里。派车链答这个码的车，空闲返回也答同一个码。
                await harness.RecordFaultAsync(candidate.Vehicle, VehicleFaultLevel.SuspectedBlocked);
                expected = VehicleFaultBlockCriterion.SuspectedReason;
                break;
            case "fault-isolated":
                await harness.RecordFaultAsync(candidate.Vehicle, VehicleFaultLevel.ConfirmedIsolated);
                expected = VehicleFaultBlockCriterion.IsolatedReason;
                break;
            case "slot-door-held":
                // control-server#385（REQ-0364）：门未证明扣着的车不做空闲返回，与派车同一处判（VehicleNewPurposeReadiness）。
                await harness.HoldForUnprovenDoorAsync(candidate.Vehicle);
                expected = DispatchReasonCodes.VehicleSlotDoorHold;
                break;
            case "has-next-business-target":
                await harness.LeaveJourneyWithoutClaimAsync(candidate.Vehicle);
                expected = IdleReturnReasons.HasNextBusinessTarget;
                break;
            case "foreign-order":
                await harness.HoldByForeignOrderAsync(candidate.Vehicle);
                expected = IdleReturnReasons.ForeignOrderRunning;
                break;
            case "own-order-result-unknown":
                await harness.LeaveOwnIntentAsync(VehicleA, "RESULT_UNKNOWN");
                expected = IdleReturnReasons.OwnOrderResultUnknown;
                break;
            case "riot-order-active":
                candidate = harness.Candidate(VehicleA, orderTaskId: "RIOT-TASK-1");
                expected = "RIOT_VEHICLE_ORDER_OCCUPIED";
                break;
            case "battery-below-line":
                candidate = harness.Candidate(VehicleA, battery: 29);
                expected = IdleReturnReasons.BelowMandatoryChargeLine;
                break;
            case "battery-unknown":
                candidate = harness.Candidate(VehicleA, battery: null);
                expected = IdleReturnReasons.BatteryUnknownOrCharging;
                break;
            case "charging":
                candidate = harness.Candidate(VehicleA, batteryState: "CHARGING");
                expected = IdleReturnReasons.BatteryUnknownOrCharging;
                break;
            case "facts-stale":
                candidate = harness.Candidate(VehicleA, observedAt: Now - TimeSpan.FromMinutes(5));
                expected = "RIOT_VEHICLE_FACT_STALE";
                break;
            case "position-unknown":
                candidate = harness.Candidate(VehicleA, station: null);
                expected = IdleReturnReasons.VehiclePositionUnknown;
                break;
            case "route-graph-disabled":
                harness.RouteGraphEnabled = false;
                expected = IdleReturnReasons.RouteGraphUnavailable;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failing), failing, null);
        }
        string[] claimsBefore = await harness.Db.DumpAsyncOf("VehiclePurposeClaims");
        string[] stationsBefore = await harness.Db.DumpAsyncOf("StationExclusivities");
        string[] intentsBefore = await harness.Db.DumpAsyncOf("OrderIntents");
        string[] journeysBefore = await harness.Db.DumpAsyncOf("JourneyRuntimes");

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(candidate));

        Assert.Equal(expected, verdict.Reason);
        Assert.False(verdict.Committed);
        Assert.Equal(claimsBefore, await harness.Db.DumpAsyncOf("VehiclePurposeClaims"));
        Assert.Equal(stationsBefore, await harness.Db.DumpAsyncOf("StationExclusivities"));
        Assert.DoesNotContain(
            await harness.Db.DumpAsyncOf("VehiclePurposeClaimRecords"),
            row => row.Contains(VehiclePurposes.IdleReturn, StringComparison.Ordinal));
        Assert.Equal(intentsBefore, await harness.Db.DumpAsyncOf("OrderIntents"));
        Assert.Equal(journeysBefore, await harness.Db.DumpAsyncOf("JourneyRuntimes"));
    }

    /// <summary>
    /// 强制充电线（cs#389 的接缝，批次9-05 起按车读充电策略版本，control-server#403）：线是测试策略的 <c>MandatoryChargeEntryThreshold</c> 30，
    /// 比较与搬运同一个——低于即拒、等于放行。于是一辆低电量的车，搬运那条链与空闲返回<b>都</b>不给它活：它哪儿也不去（REQ-0290）。
    /// </summary>
    [Fact]
    public async Task ALowBatteryVehicleIsSentNowhereNeitherToTransportNorToAWaitingPoint()
    {
        await using Harness harness = await Harness.CreateAsync();
        int line = TestChargingPolicies.Content.MandatoryChargeEntryThresholdPercent;
        IdleReturnCandidate low = harness.Candidate(VehicleA, battery: line - 1);
        IdleReturnCandidate atLine = harness.Candidate(VehicleB, battery: line);

        IReadOnlyList<IdleReturnVerdict> verdicts = await harness.EvaluateAsync(low, atLine);

        Assert.Equal(
            [IdleReturnReasons.BelowMandatoryChargeLine, IdleReturnReasons.Committed],
            verdicts.Select(verdict => verdict.Reason));
        // 同一份事实，搬运那条链也不接，答的是强制充电码：同一个判定函数，没有「搬运挡住了、空闲返回却放它走」的那一段。
        Assert.Equal(DispatchReasonCodes.MandatoryChargeRequired, VehicleDynamicFactsCriterion.Evaluate(low.Facts, harness.Options));
        Assert.Equal(DispatchAdmissionChain.Eligible, VehicleDynamicFactsCriterion.Evaluate(atLine.Facts, harness.Options));
    }

    /// <summary>
    /// 过渡接缝换了实现之后，线跟着策略走（批次9-05，control-server#403）：同一辆 50% 的车，入口线抬到 55 就不承诺空闲返回、答强制充电线，
    /// 降到 20 就照常承诺（仍高于夹具救命线 15，否则整版不可用）。没有任何配置项参与。
    /// </summary>
    [Theory]
    [InlineData(55, IdleReturnReasons.BelowMandatoryChargeLine)]
    [InlineData(20, IdleReturnReasons.Committed)]
    public async Task TheIdleReturnChargeLineFollowsTheChargingPolicyUpAndDown(int entry, string expected)
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.ChargingPolicy = TestChargingPolicies.AllApprovedWith(TestChargingPolicies.Content with
        {
            MandatoryChargeEntryThresholdPercent = entry,
            MinimumPostTaskBatteryMarginPercent = Math.Min(entry, 30),
        });

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA, battery: 50)));

        Assert.Equal(expected, verdict.Reason);
        PolicyMandatoryChargeLine line = new(harness.ChargingPolicy);
        Assert.Equal(50 < entry, await line.IsBelowLineAsync(VehicleA, 50, Token));
        Assert.StartsWith($"{entry} (charging policy version 1", line.Describe(VehicleA), StringComparison.Ordinal);
    }

    /// <summary>
    /// 生效版本的强制充电线不高于救命线（夹具 15）时整版不可用：空闲返回与派车链答同一个码、一行不写（control-server#403）。
    /// </summary>
    [Fact]
    public async Task AVersionWhoseEntryThresholdIsNotAboveTheRescueLineCommitsNoIdleReturn()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.ChargingPolicy = TestChargingPolicies.AllApprovedWith(TestChargingPolicies.Content with
        {
            MandatoryChargeEntryThresholdPercent = 15,
            MinimumPostTaskBatteryMarginPercent = 10,
        });
        IdleReturnCandidate candidate = harness.Candidate(VehicleA);

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(candidate));
        EventRecordingLogger<ChargingPolicyCommissioningCriterion> log = new();
        string dispatch = await new ChargingPolicyCommissioningCriterion(harness.ChargingPolicy, Options.Create(harness.Options), new ChargingPolicyCommissioningLog(), log)
            .EvaluateAsync(new DispatchCandidateEvaluation(null!, null!, candidate.Facts), Token);
        // Error, with the way out in the line: activate a corrected version, no database edit, no restart.
        EventRecordingLogger<ChargingPolicyCommissioningCriterion>.Entry line = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, line.Level);
        Assert.Contains("MandatoryChargeEntryThreshold 15, not above JourneyRuntime:WaitingJourneyRescueBatteryPercent 15", line.Message, StringComparison.Ordinal);
        Assert.Contains("no database edit and no restart are needed", line.Message, StringComparison.Ordinal);

        Assert.Equal(
            (DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine, DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine),
            (verdict.Reason, dispatch));
        Assert.Empty(await harness.Db.DumpAsyncOf("VehiclePurposeClaims"));
    }

    /// <summary>
    /// 读不到策略时线按「低于」答（fail-closed）：宁可原地不动，也不承诺开往等待点。
    /// </summary>
    [Fact]
    public async Task TheChargeLineAnswersBelowWhenNoPolicyCoversTheVehicle()
    {
        PolicyMandatoryChargeLine line = new(TestChargingPolicies.None);

        Assert.True(await line.IsBelowLineAsync(VehicleA, 100, Token));
        Assert.Contains(ChargingPolicyCommissioningReasons.NotApproved, line.Describe(VehicleA), StringComparison.Ordinal);
    }

    /// <summary>
    /// 逐车投运（control-server#400；REQ-0282，规格 8.6）：没有已批准、已激活、覆盖它的充电策略版本的车，不被承诺空闲返回——与搬运那条链
    /// 同一个判定（<see cref="VehicleNewPurposeReadiness.CommissioningVerdictAsync"/>）。策略只覆盖 B：A 答新原因码、一行不写，B 照常承诺。
    /// </summary>
    [Fact]
    public async Task AVehicleWithoutAnEffectiveChargingPolicyIsNotCommittedToAnIdleReturn()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.ChargingPolicy = TestChargingPolicies.Only(VehicleB);

        IReadOnlyList<IdleReturnVerdict> verdicts = await harness.EvaluateAsync(harness.Candidate(VehicleA), harness.Candidate(VehicleB));

        Assert.Equal(
            [DispatchReasonCodes.ChargingPolicyNotApproved, IdleReturnReasons.Committed],
            verdicts.Select(verdict => verdict.Reason));
        Assert.Null(verdicts[0].JourneyId);
        Assert.Equal(
            [VehicleB],
            await harness.Context.Set<VehiclePurposeClaimRow>().AsNoTracking().Select(row => row.VehicleKey).ToArrayAsync(Token));
    }

    /// <summary>
    /// 先后次序（control-server#400 审查 S3）：一辆故障车同时没有生效策略，空闲返回与派车链都先答故障码，不答投运码——故障是更具体、
    /// 更要紧的原因。空闲返回的次序在共用判定 <see cref="VehicleNewPurposeReadiness.JudgeAsync"/> 里（故障 → 投运 → 动态事实），
    /// 派车链的次序在判据的 Order 上（故障 15、投运 17）。变异 MD（调换 JudgeAsync 里前两格）时这条变红。
    /// </summary>
    [Theory]
    [InlineData(VehicleFaultLevel.SuspectedBlocked, VehicleFaultBlockCriterion.SuspectedReason)]
    [InlineData(VehicleFaultLevel.ConfirmedIsolated, VehicleFaultBlockCriterion.IsolatedReason)]
    public async Task AFaultedVehicleWithoutAPolicyIsRefusedForTheFaultFirstOnBothPaths(VehicleFaultLevel level, string expected)
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.ChargingPolicy = TestChargingPolicies.None;
        IdleReturnCandidate candidate = harness.Candidate(VehicleA);
        await harness.RecordFaultAsync(candidate.Vehicle, level);

        IdleReturnVerdict idle = Assert.Single(await harness.EvaluateAsync(candidate));
        string dispatch = await new DispatchAdmissionChain(
            [
                new ChargingPolicyCommissioningCriterion(TestChargingPolicies.None, Options.Create(new JourneyRuntimeOptions()), new ChargingPolicyCommissioningLog()),
                new VehicleFaultBlockCriterion(new VehicleFaultStore(harness.Context), harness.Context),
            ]).EvaluateAsync(new DispatchCandidateEvaluation(null!, null!, candidate.Facts), Token);

        Assert.Equal((expected, expected), (idle.Reason, dispatch));
    }

    [Fact]
    public async Task TheSwitchIsOffByDefaultSoAVehicleMeetingEveryConditionCommitsNothing()
    {
        Assert.False(new IdleReturnOptions().Enabled);
        // 宿主不配这一节：appsettings.json 里没有 IdleReturn，开关读出来就是关。
        ServiceCollection services = new();
        services.AddIdleReturn(new ConfigurationBuilder().AddJsonFile(
            Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json")).Build());
        services.AddScoped(_ => TestChargingPolicies.AllApproved);
        await using (ServiceProvider provider = services.BuildServiceProvider())
        {
            Assert.False(provider.GetRequiredService<IOptions<IdleReturnOptions>>().Value.Enabled);
            // control-server#403：接缝的实现是按车读策略的那一个，不再是读 MinimumBatteryPercent 的过渡实现。
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            Assert.IsType<PolicyMandatoryChargeLine>(scope.ServiceProvider.GetRequiredService<IMandatoryChargeLine>());
        }

        // 开关关着，评估不产生任何写：本票合入后、批次8-19 合入前，「有 IDLE_RETURN 占有却没有旅程行」的车因此不会出现。
        await using Harness harness = await Harness.CreateAsync(enabled: false);
        Dictionary<string, string[]> before = await harness.DumpEveryTableAsync();

        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleA), harness.Candidate(VehicleB));
        // 先把共享上下文里可能暂存的东西落库再比：只比库，会漏掉「暂存了、等轮次后面那次保存替它写下去」的写。
        await harness.Context.SaveChangesAsync(Token);

        Assert.All(verdicts, verdict => Assert.Equal(IdleReturnReasons.Disabled, verdict.Reason));
        Dictionary<string, string[]> after = await harness.DumpEveryTableAsync();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, table => Assert.Equal(table.Value, after[table.Key]));
        Assert.Contains("VehiclePurposeClaims", before.Keys);
        Assert.Contains("StationExclusivities", before.Keys);
    }

    /// <summary>
    /// 判据与受理前的预读都漏了，受理那一次保存也会被 <c>VehiclePurposeClaims</c> 的主键整个拒掉：已承诺空闲返回的车接不了搬运，
    /// 受理的行一条不留，承诺原样。
    /// </summary>
    [Fact]
    public async Task AnAcceptanceThatSlipsPastTheCriterionIsRefusedWholeByTheClaimsKey()
    {
        await using Harness harness = await Harness.CreateAsync();
        IdleReturnVerdict committed = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)));
        Assert.Equal(IdleReturnReasons.Committed, committed.Reason);
        string[] claims = await harness.Db.DumpAsyncOf("VehiclePurposeClaims");
        string[] stations = await harness.Db.DumpAsyncOf("StationExclusivities");

        await using ControlServerDbContext context = harness.Db.NewContext();
        BusinessIdentityConflictException refusal = await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            Batch7JourneyFixture.AcceptAsync(context, "D-SLIPPED", "AGV-" + VehicleA, VehicleA, Now));

        Assert.Contains("already claimed", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.Db.DumpAsyncOf("AcceptedDemands"));
        Assert.Empty(await harness.Db.DumpAsyncOf("JourneyRuntimes"));
        Assert.Empty(await harness.Db.DumpAsyncOf("OrderIntents"));
        Assert.Equal(claims, await harness.Db.DumpAsyncOf("VehiclePurposeClaims"));
        Assert.Equal(stations, await harness.Db.DumpAsyncOf("StationExclusivities"));
    }

    /// <summary>关掉只挡新承诺：已经形成的承诺不取消、不改写（<c>REQ-0291</c>）。</summary>
    [Fact]
    public async Task TurningTheSwitchOffStopsNewCommitmentsAndLeavesTheOneAlreadyMadeAsItWas()
    {
        await using Harness harness = await Harness.CreateAsync();
        Assert.Equal(IdleReturnReasons.Committed, Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA))).Reason);
        string[] claims = await harness.Db.DumpAsyncOf("VehiclePurposeClaims");
        string[] stations = await harness.Db.DumpAsyncOf("StationExclusivities");

        harness.Enabled = false;
        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleA), harness.Candidate(VehicleB));

        Assert.All(verdicts, verdict => Assert.Equal(IdleReturnReasons.Disabled, verdict.Reason));
        Assert.Equal(claims, await harness.Db.DumpAsyncOf("VehiclePurposeClaims"));
        Assert.Equal(stations, await harness.Db.DumpAsyncOf("StationExclusivities"));
    }

    /// <summary>
    /// 空闲车每一轮都被评估：同一辆车同一个原因只记一次日志，原因变了再记；承诺另有事件 2198。
    /// </summary>
    [Fact]
    public async Task AVehiclesRefusalIsLoggedWhenItChangesNotEveryRound()
    {
        await using Harness harness = await Harness.CreateAsync();
        IdleReturnCandidate low = harness.Candidate(VehicleA, battery: 10);

        await harness.EvaluateAsync(low);
        await harness.EvaluateAsync(low);
        await harness.EvaluateAsync(harness.Candidate(VehicleA, battery: null));
        await harness.EvaluateAsync(harness.Candidate(VehicleA));

        Assert.Equal(
            [(2199, IdleReturnReasons.BelowMandatoryChargeLine), (2199, IdleReturnReasons.BatteryUnknownOrCharging), (2198, "")],
            harness.Log.Entries
                .Where(entry => entry.EventId.Id is 2198 or 2199)
                .Select(entry => (entry.EventId.Id, entry.EventId.Id == 2199 ? entry.Message.Split(": ")[1].Split('.')[0] : "")));
        Assert.Equal(IdleReturnReasons.Committed, harness.VerdictBoard.Reasons["AGV-" + VehicleA]);
    }

    /// <summary>
    /// 卸完货、没有下一单、还占着固定公共站（REQ-0204）的车正该离开那个单车位的站去等待点：持公共站不拒（审查 S4）。
    /// </summary>
    [Fact]
    public async Task AVehicleStillHoldingAFixedTaskStationIsStillSentToAWaitingPoint()
    {
        await using Harness harness = await Harness.CreateAsync();
        await harness.HoldStationAsync(VehicleA, 210, StationExclusivityStates.Occupied, StationExclusivityKinds.FixedTaskStation);

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)));

        Assert.Equal((IdleReturnReasons.Committed, (int?)214), (verdict.Reason, verdict.StationId));
    }

    /// <summary>
    /// 跨票（control-server#391）：旅程在关卡卸完、已经结束，车还占着关卡这个公共站（离点证据之前独占不放）、没有用途占有——
    /// 它正该离开关卡去等待点。开关打开时它被承诺空闲返回；关卡的独占不被这里动（放它是离点证据的事）。
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseJourneyEndedAtTheGateWhileStillHoldingItCommitsToAnIdleReturn()
    {
        await using Harness harness = await Harness.CreateAsync();
        IdleReturnCandidate candidate = harness.Candidate(VehicleA);
        await harness.LeaveJourneyWithoutClaimAsync(candidate.Vehicle);
        await using (ControlServerDbContext context = harness.Db.NewContext())
        {
            Assert.Equal(1, await context.JourneyRuntimes.ExecuteUpdateAsync(
                row => row.SetProperty(journey => journey.Stage, JourneyRuntimeStage.Completed), Token));
        }
        await harness.HoldStationAsync(VehicleA, 210, StationExclusivityStates.Occupied, StationExclusivityKinds.FixedTaskStation);

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(candidate));

        Assert.Equal((IdleReturnReasons.Committed, (int?)214), (verdict.Reason, verdict.StationId));
        StationExclusivity gate = Assert.IsType<StationExclusivity>(
            await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 210, Token));
        Assert.Equal(
            (StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, VehicleA),
            (gate.StationKind, gate.State, gate.VehicleKey));
    }

    /// <summary>
    /// 批次8-19（control-server#390）合入后，单独打开 <c>IdleReturn:Enabled</c> 照常启动：承诺会被执行与释放，批次8-18 那道
    /// 「打开即拒绝启动」的过渡护栏已删。宿主按同一个注册起来，托管服务照常启动。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public async Task TurningIdleReturnOnStartsNowThatCommitmentsAreExecuted(string? enabled)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        if (enabled is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["IdleReturn:Enabled"] = enabled });
        }
        RecordingHostedService started = new();
        builder.Services.AddSingleton<IHostedService>(started);
        builder.Services.AddIdleReturn(builder.Configuration);
        using IHost host = builder.Build();

        await host.StartAsync(Token);

        Assert.True(started.Started);
        Assert.Equal(enabled == "true", host.Services.GetRequiredService<IOptions<IdleReturnOptions>>().Value.Enabled);
        await host.StopAsync(Token);
    }

    /// <summary>
    /// 强制充电线上方、却保不住任务后余量的车照样不承诺（「宁可不动」）：入口线 30、余量 30、每趟估计 10，电量 35——不在强制充电，
    /// 35 − 10 = 25 &lt; 30。共用判定答 <c>BATTERY_POLICY_NOT_SATISFIED</c>，空闲返回先让给自己的线（没低于），再把它收回来（批次9-05）。
    /// </summary>
    [Fact]
    public async Task AVehicleAboveItsIdleReturnLineButBelowTheTransportThresholdIsStillRefused()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.ChargingPolicy = TestChargingPolicies.AllApprovedWith(
            TestChargingPolicies.Content with { EstimatedTaskConsumptionPercent = 10 });

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA, battery: 35)));

        Assert.Equal("BATTERY_POLICY_NOT_SATISFIED", verdict.Reason);
        Assert.Empty(await harness.Db.DumpAsyncOf("VehiclePurposeClaims"));
    }

    /// <summary>
    /// 批次8-18 只给合成 L2 的确认键 <c>AllowWithoutExecutionForL2Only</c> 随护栏一起删了（control-server#390）：仓库里的配置、脚本
    /// （含 L2 编排器）、工作流与源码都不再出现它。不分大小写，理由同 .NET 配置键。
    /// </summary>
    [Fact]
    public void TheRetiredL2OnlyKeyAppearsNowhere()
    {
        const string Key = "AllowWithoutExecutionForL2Only";
        string root = RepositoryRoot();
        static bool Built(string path) =>
            path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        string[] files =
        [
            .. Directory.GetFiles(root, "appsettings*.json", SearchOption.AllDirectories),
            .. Directory.GetFiles(Path.Combine(root, "scripts"), "*", SearchOption.AllDirectories),
            .. Directory.GetFiles(Path.Combine(root, ".github", "workflows"), "*", SearchOption.AllDirectories),
            .. Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories),
        ];
        string[] scanned = [.. files.Where(path => !Built(path))];
        Assert.Contains(scanned, path => path.EndsWith("Invoke-L2Scenario.ps1", StringComparison.Ordinal));

        Assert.Equal(
            [],
            scanned.Where(path => File.ReadAllText(path).Contains(Key, StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(root, path)));
    }

    // ---- 选点：逐点核验，候选不等于拿到 ---------------------------------------------------------------------------

    public static TheoryData<string, string?> Exclusions => new()
    {
        { "vehicle-scope", WaitingPointEligibilityReasons.VehicleNotInScope },
        { "reserved-by-another", IdleReturnReasons.PointTaken },
        { "occupied-by-another", IdleReturnReasons.PointTaken },
        { "unreachable", IdleReturnReasons.PointUnreachable },
        { "disabled", WaitingPointEligibilityReasons.Disabled },
        // 当前版本里没有它：它根本不是候选，逐点原因里也就不出现它。
        { "not-in-current-version", null },
        { "not-in-live-catalog", WaitingPointEligibilityReasons.NotInLiveCatalog },
        { "renamed-in-live-catalog", WaitingPointEligibilityReasons.NotInLiveCatalog },
        { "role-changed-to-fixed-station", WaitingPointEligibilityReasons.RoleChangedToFixedTaskStation },
    };

    /// <summary>
    /// 近的那个点（214）被排除时，车去远的那个（215）；两个都排除时不承诺，逐点原因写在日志的细节里。
    /// </summary>
    /// <remarks>
    /// 断言落在「去了 215」而不只是「没去 214」：一个把排除写反了的实现（排除掉合格的、留下不合格的）也不会去 214。
    /// 「站被绑成固定站」那一格钉住的是调用方真的读了本轮的固定站集合、没有传空集合（批次8-17 的交接）。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Exclusions))]
    public async Task AWaitingPointThatFailsItsCheckIsPassedOverForTheNextOne(string exclusion, string? expectedReason)
    {
        await using Harness harness = await Harness.CreateAsync();
        switch (exclusion)
        {
            case "vehicle-scope":
                await harness.RegisterAsync(Point(214, scope: [VehicleB]), Point(215));
                break;
            case "reserved-by-another":
                await harness.HoldStationAsync(VehicleB, 214, StationExclusivityStates.Reserved);
                break;
            case "occupied-by-another":
                await harness.HoldStationAsync(VehicleB, 214, StationExclusivityStates.Occupied);
                break;
            case "unreachable":
                harness.UnreachableStations.Add(214);
                await harness.SeedRouteGraphAsync();
                break;
            case "disabled":
                await harness.RegisterAsync(Point(214, enabled: false), Point(215));
                break;
            case "not-in-current-version":
                await harness.RegisterAsync(Point(215));
                break;
            case "not-in-live-catalog":
                harness.CatalogStations.RemoveAll(station => station.StationId == 214);
                break;
            case "renamed-in-live-catalog":
                harness.CatalogStations.RemoveAll(station => station.StationId == 214);
                harness.CatalogStations.Add(new RiotMapStation(214, "改名后的站"));
                break;
            case "role-changed-to-fixed-station":
                await harness.BindAsFixedTaskStationAsync(214, "等待点1");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, null);
        }

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)));
        Assert.Equal(IdleReturnReasons.Committed, verdict.Reason);
        Assert.Equal(215, verdict.StationId);

        // 第三辆车：214 照旧被排除，215 已被 VK-A 预占，216 到不了——一个点都不剩，不承诺，逐点原因里点名 214 为什么被排除。
        IdleReturnVerdict refused = Assert.Single(await harness.EvaluateAsync(harness.Candidate("VK-C")));
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, refused.Reason);
        string detail = harness.Log.Entries.Last(entry => entry.EventId.Id == 2199).Message;
        Assert.Contains($"215={IdleReturnReasons.PointTaken}", detail, StringComparison.Ordinal);
        if (expectedReason is null)
        {
            Assert.DoesNotContain("214=", detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"214={expectedReason}", detail, StringComparison.Ordinal);
        }
        Assert.Null(await new VehiclePurposeLedgerStore(harness.Db.NewContext()).ReadClaimAsync("VK-C", Token));
    }

    [Fact]
    public void AmongEligiblePointsTheCheapestRouteWinsAndATieGoesToTheLowerStationId()
    {
        Assert.Equal(
            new IdleReturnPointCandidate(215, 10_000),
            IdleReturnEvaluator.Choose([new(214, 20_000), new(215, 10_000), new(216, 30_000)]));
        Assert.Equal(
            new IdleReturnPointCandidate(214, 10_000),
            IdleReturnEvaluator.Choose([new(216, 10_000), new(214, 10_000), new(215, 10_000)]));
        Assert.Null(IdleReturnEvaluator.Choose([]));
    }

    // ---- 原子承诺 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 两辆车同时挑中同一个等待点：只有一辆承诺成功，另一辆整笔回滚，连用途占有也没留下。
    /// </summary>
    /// <remarks>
    /// 绕开选点的预读，两个上下文直接在承诺那一刻相撞——这是预读挡不住的那一刻，只有主键能挡。
    /// </remarks>
    [Fact]
    public async Task TwoVehiclesCommittingToOnePointAtOnceOnlyOneWinsAndTheOtherLeavesNoClaimBehind()
    {
        await using Harness harness = await Harness.CreateAsync();
        VehiclePurposeLedgerStore first = new(harness.Db.NewContext());
        VehiclePurposeLedgerStore second = new(harness.Db.NewContext());

        VehiclePurposeAcquisitionOutcome[] outcomes =
        [
            await IdleReturnEvaluator.TryCommitAsync(
                first, VehicleA, IdleReturnIdentity.JourneyIdFor(VehicleA, Now), Map, 214, harness.RegistrationVersion, Now, Token),
            await IdleReturnEvaluator.TryCommitAsync(
                second, VehicleB, IdleReturnIdentity.JourneyIdFor(VehicleB, Now), Map, 214, harness.RegistrationVersion, Now, Token),
        ];

        Assert.Equal([VehiclePurposeAcquisitionOutcome.Acquired, VehiclePurposeAcquisitionOutcome.StationHeld], outcomes);
        Assert.Null(await second.ReadClaimAsync(VehicleB, Token));
        Assert.Empty(await second.ListClaimHistoryAsync(VehicleB, Token));
        Assert.DoesNotContain(
            await harness.Db.DumpAsyncOf("StationExclusivityRecords"),
            row => row.Contains(VehicleB, StringComparison.Ordinal));
        Assert.Single(await harness.Db.DumpAsyncOf("StationExclusivityRecords"));
        StationExclusivity holder = Assert.IsType<StationExclusivity>(
            await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 214, Token));
        Assert.Equal(VehicleA, holder.VehicleKey);
    }

    /// <summary>同一轮里两辆车只有一个合格的点：前一辆的预占后一辆看得见，后一辆不承诺，也不去抢。</summary>
    [Fact]
    public async Task InOneRoundASecondVehicleSeesThePointTheFirstJustReserved()
    {
        await using Harness harness = await Harness.CreateAsync();
        await harness.RegisterAsync(Point(214));

        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleA), harness.Candidate(VehicleB));

        Assert.Equal(
            [(VehicleA, IdleReturnReasons.Committed), (VehicleB, IdleReturnReasons.NoWaitingPointAvailable)],
            verdicts.Select(verdict => (verdict.VehicleKey, verdict.Reason)));
        Assert.Null(await new VehiclePurposeLedgerStore(harness.Db.NewContext()).ReadClaimAsync(VehicleB, Token));
    }

    public static TheoryData<string> CommitmentTables =>
        ["StationExclusivities", "StationExclusivityRecords", "VehiclePurposeClaimRecords", "VehiclePurposeClaims"];

    /// <summary>
    /// 承诺那一次保存在写任何一张表时失败：占有、预占、两条经过全不留；而且这辆车的暂存行被丢掉，轮次后面的保存不会把它们写下去；
    /// 后面的车照常评估。
    /// </summary>
    /// <remarks>
    /// 四张表逐一注入，理由与 control-server#386 的端口测试相同：EF 先写站点独占，只在「写预占那一刻」注入时用途占有还没发出，
    /// 「它没留下」是必然的，证明不了什么。最后一张（<c>VehiclePurposeClaims</c>）失败时前三张已经真的执行过。
    /// </remarks>
    [Theory]
    [MemberData(nameof(CommitmentTables))]
    public async Task AFailureWritingAnyPartOfTheCommitmentLeavesNothingAndStagesNothingForTheRoundsNextSave(string table)
    {
        FailOnInsertInto failure = new(table, VehicleA);
        await using Harness harness = await Harness.CreateAsync(interceptor: failure);

        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleA), harness.Candidate(VehicleB));

        Assert.True(failure.Fired, $"The injected failure on {table} never fired, so this proves nothing.");
        Assert.Equal(
            [(VehicleA, IdleReturnReasons.EvaluationFailed), (VehicleB, IdleReturnReasons.Committed)],
            verdicts.Select(verdict => (verdict.VehicleKey, verdict.Reason)));
        // 轮次在承诺之后还会保存（轮次结局）。暂存行要是还在变更跟踪里，这一次保存就会把 VK-A 的半截承诺写下去。
        await harness.Context.SaveChangesAsync(Token);
        Assert.DoesNotContain(await harness.Db.DumpAsyncOf("VehiclePurposeClaims"), row => row.Contains(VehicleA, StringComparison.Ordinal));
        Assert.DoesNotContain(await harness.Db.DumpAsyncOf("VehiclePurposeClaimRecords"), row => row.Contains(VehicleA, StringComparison.Ordinal));
        Assert.DoesNotContain(await harness.Db.DumpAsyncOf("StationExclusivities"), row => row.Contains(VehicleA, StringComparison.Ordinal));
        Assert.DoesNotContain(await harness.Db.DumpAsyncOf("StationExclusivityRecords"), row => row.Contains(VehicleA, StringComparison.Ordinal));
        Assert.Empty(await harness.Db.DumpAsyncOf("OrderIntents"));
        Assert.Contains(harness.Log.Entries, entry => entry.EventId.Id == 2200);
    }

    /// <summary>
    /// 评估里的读失败只是那一辆车这一轮不评估（审查 S5）：外来订单那张表读不出来，评估照样返回、不向外抛，
    /// 派车轮后面的轮次结局记录因此照常；什么也没写。
    /// </summary>
    [Fact]
    public async Task AReadThatFailsInsideTheEvaluationFailsThatVehicleNotTheRound()
    {
        FailOnReadFrom failure = new("ForeignRiotOrders");
        await using Harness harness = await Harness.CreateAsync(interceptor: failure);

        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleA), harness.Candidate(VehicleB));

        Assert.True(failure.Fired, "The injected read failure never fired, so this proves nothing.");
        Assert.Equal(
            [IdleReturnReasons.EvaluationFailed, IdleReturnReasons.EvaluationFailed],
            verdicts.Select(verdict => verdict.Reason));
        Assert.Empty(await harness.Db.DumpAsyncOf("VehiclePurposeClaims"));
        Assert.Empty(await harness.Db.DumpAsyncOf("StationExclusivities"));
    }

    // ---- 承诺之后不被抢 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyAnIdleReturnClaimRefusesTransportAndAnInTransitVehiclesOwnTransportClaimDoesNot()
    {
        await using Harness harness = await Harness.CreateAsync();
        IdleReturnCommitmentCriterion criterion = new(harness.Context);
        Assert.Equal(16, criterion.Order);

        Assert.Equal(DispatchAdmissionChain.Eligible, await criterion.EvaluateAsync(harness.Evaluation(VehicleA), Token));
        await harness.ClaimForTransportAsync(VehicleA);
        // 在途链从空闲链派生、带着这条判据：搬运占有要放行，否则每辆在途车都接不了追加。
        Assert.Equal(DispatchAdmissionChain.Eligible, await criterion.EvaluateAsync(harness.Evaluation(VehicleA), Token));

        Assert.Equal(IdleReturnReasons.Committed, Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleB))).Reason);
        Assert.Equal(
            DispatchReasonCodes.VehicleCommittedToIdleReturn,
            await criterion.EvaluateAsync(harness.Evaluation(VehicleB), Token));
        Assert.Equal(
            DispatchReasonClass.Backlog,
            StructuralDispatchClassification.ClassOf(DispatchReasonCodes.VehicleCommittedToIdleReturn));
    }

    [Fact]
    public void TheCriterionIsPartOfTheIdleChainAndOfTheInTransitChainDerivedFromIt()
    {
        IReadOnlyList<IDispatchAdmissionCriterion> idle = DispatchAdmissionCriteria.Default(
            Options.Create(new JourneyRuntimeOptions()), new MapStationResolver(), null!, null!, null!, null!,
            NullLogger<SlotCapacityCriterion>.Instance, null!, null!, TestChargingPolicies.AllApproved);
        Assert.Single(idle, criterion => criterion is IdleReturnCommitmentCriterion);
        Assert.Single(
            DispatchAdmissionCriteria.InTransit(idle, Options.Create(new JourneyRuntimeOptions())),
            criterion => criterion is IdleReturnCommitmentCriterion);
    }

    private static WaitingPointEntry Point(int stationId, bool enabled = true, IReadOnlyList<string>? scope = null) =>
        new(Map, stationId, $"等待点{stationId - 213}", enabled, scope ?? []);

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly IInterceptor? _interceptor;

        private Harness(Batch7JourneyFixture db, IInterceptor? interceptor, bool enabled)
        {
            Db = db;
            _interceptor = interceptor;
            Enabled = enabled;
            Context = interceptor is null ? db.Context : db.NewContext(interceptor);
        }

        public Batch7JourneyFixture Db { get; }

        /// <summary>评估器用的上下文：派车轮与它共用同一个，轮次后面的保存走的也是它。</summary>
        public ControlServerDbContext Context { get; }

        public bool Enabled { get; set; }

        public bool RouteGraphEnabled { get; set; } = true;

        public long RegistrationVersion { get; private set; }

        public HashSet<int> UnreachableStations { get; } = [];

        public List<RiotMapStation> CatalogStations { get; } =
        [
            new(210, "关卡"),
            new(214, "等待点1"),
            new(215, "等待点2"),
            new(216, "等待点3"),
        ];

        public EventRecordingLogger<IdleReturnEvaluator> Log { get; } = new();

        /// <summary>替换强制充电线；为空即按 <see cref="ChargingPolicy"/> 读的产品实现。</summary>
        public IMandatoryChargeLine? ChargeLine { get; set; }

        /// <summary>逐车投运（control-server#400）：默认每辆车都有已批准的测试策略。</summary>
        public IChargingPolicyResolver ChargingPolicy { get; set; } = TestChargingPolicies.AllApproved;

        /// <summary>跨评估保留，像宿主里的单例。</summary>
        public IdleReturnVerdictBoard VerdictBoard { get; } = new();

        public JourneyRuntimeOptions Options { get; } = new()
        {
            MapId = Map,
            MapIdentity = "MAP-25",
            MaximumEvidenceAge = TimeSpan.FromMinutes(2),
        };

        public static async Task<Harness> CreateAsync(bool enabled = true, IInterceptor? interceptor = null)
        {
            Batch7JourneyFixture db = await Batch7JourneyFixture.CreateAsync();
            Harness harness = new(db, interceptor, enabled);
            await harness.SeedRouteGraphAsync();
            await harness.RegisterAsync(Point(214), Point(215), Point(216));
            return harness;
        }

        public async Task<IReadOnlyList<IdleReturnVerdict>> EvaluateAsync(params IdleReturnCandidate[] candidates)
        {
            IdleReturnEvaluator evaluator = new(
                Context,
                new VehiclePurposeLedgerStore(Context),
                new StationExclusivityStore(Context),
                new VehicleFaultStore(Context),
                ChargingPolicy,
                new WaitingPointRegistry(Context, JourneyRuntimeWorkerTestKit.CreateGovernedPublisher(Context)),
                TaskTypeStationRuntimeSeed.Access(Context).Bindings,
                new RouteGraphAccess(
                    new RouteGraphSnapshotStore(Context),
                    Microsoft.Extensions.Options.Options.Create(new RouteGraphOptions
                    {
                        Enabled = RouteGraphEnabled,
                        MapId = Map,
                        DesignStateTtl = TimeSpan.FromHours(1),
                        RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
                        RuntimeStateMaxAge = TimeSpan.FromHours(1),
                    }),
                    new FixedClock(Now)),
                ChargeLine ?? new PolicyMandatoryChargeLine(ChargingPolicy),
                Microsoft.Extensions.Options.Options.Create(new IdleReturnOptions { Enabled = Enabled }),
                Microsoft.Extensions.Options.Options.Create(Options),
                VerdictBoard,
                new FixedClock(Now),
                Log);
            return await evaluator.EvaluateAsync(
                new RiotMapStationCatalogSnapshot(Map, Now, new string('c', 64), [.. CatalogStations]),
                candidates,
                Token);
        }

        public IdleReturnCandidate Candidate(
            string vehicleKey,
            int? battery = 80,
            string batteryState = "NO_CHARGE",
            int? station = 210,
            DateTimeOffset? observedAt = null,
            string? orderTaskId = null) =>
            new(
                new FleetVehicle(AgvIdOf(vehicleKey), vehicleKey, 1),
                new DispatchVehicleFacts(
                    vehicleKey,
                    AgvIdOf(vehicleKey),
                    new OnboardDispatchFacts(1, [1, 2], true, true, true, true, false),
                    new RiotVehicleObservation(
                        vehicleKey, true, true, "IDLE", Options.MapIdentity, station, battery, batteryState, 0,
                        observedAt ?? Now, 0, orderTaskId),
                    Now,
                    // 轮次为这辆车读的那一份策略（control-server#403），与空闲返回的线读同一个解析器。
                    BatteryPolicy: DispatchBatteryPolicy.From(
                        ChargingPolicy.ResolveForNewDecisionAsync(vehicleKey, CancellationToken.None).GetAwaiter().GetResult())),
                LeftOverByTransport: true);

        public DispatchCandidateEvaluation Evaluation(string vehicleKey) =>
            new(Batch7JourneyFixture.Snapshot("D-1", Now), null!, Candidate(vehicleKey).Facts);

        public async Task SeedRouteGraphAsync()
        {
            RouteGraphSnapshotStore store = new(Db.NewContext());
            // 210 在节点 1，214 在 2，215 在 3，216 在没有边的节点 4。标成到不了的站挪到没有边的节点 9 上。
            RouteGraphEdgeFact[] edges =
            [
                new(1, 1, 2, 10_000, 0, 0, 10_000, 0, 1, false),
                new(2, 2, 3, 10_000, 10_000, 0, 20_000, 0, 1, false),
            ];
            (int Station, string Name, int Node)[] placed = [(210, "关卡", 1), (214, "等待点1", 2), (215, "等待点2", 3), (216, "等待点3", 4)];
            RouteGraphStationFact[] stations =
            [
                .. placed.Select(station => new RouteGraphStationFact(
                    station.Station, station.Name, 1, 0, 0,
                    UnreachableStations.Contains(station.Station) ? 9 : station.Node, 0)),
            ];
            await store.ReplaceDesignStateAsync(Map, edges, stations, null, Now, Token);
            await store.ReplaceRuntimeStateAsync(Map, [], [], Now, Token);
            await store.ReplaceEdgeGroupsAsync(Map, [], "", Now, Token);
            await store.ClearStaleAsync(Map, Now, Token);
        }

        public async Task RegisterAsync(params WaitingPointEntry[] points)
        {
            await using ControlServerDbContext context = Db.NewContext();
            RegistrationVersion = (await new WaitingPointRegistry(context, JourneyRuntimeWorkerTestKit.CreateGovernedPublisher(context))
                .WriteVersionAsync(points, Now, Token)).Version;
        }

        public async Task BindAsFixedTaskStationAsync(int stationId, string stationName)
        {
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(Db.Connection).Options;
            await TaskTypeStationRuntimeSeed.ActivateAsync(
                options, Now, bindings: [new TaskTypeStationBinding(TransportTaskTypes.WireToGate, stationId, stationName, "MAP-25-TEST")],
                mapId: Map);
        }

        public async Task ClaimForTransportAsync(string vehicleKey)
        {
            await using ControlServerDbContext context = Db.NewContext();
            Assert.Equal(
                VehiclePurposeAcquisitionOutcome.Acquired,
                await new VehiclePurposeLedgerStore(context).TryAcquireAsync(
                    new VehiclePurposeClaim(vehicleKey, VehiclePurposes.Transport, $"journey:D-{vehicleKey}", Now), null, Token));
        }

        public async Task HoldStationAsync(
            string vehicleKey, int stationId, string state, string kind = StationExclusivityKinds.WaitingPoint)
        {
            await using ControlServerDbContext context = Db.NewContext();
            Assert.Equal(
                StationExclusivityAcquisitionOutcome.Acquired,
                await new StationExclusivityStore(context).TryAcquireAsync(
                    new StationExclusivityRequest(
                        Map, stationId, kind, state, kind == StationExclusivityKinds.WaitingPoint ? RegistrationVersion : null),
                    vehicleKey,
                    IdleReturnIdentity.JourneyIdFor(vehicleKey, Now.AddHours(-1)),
                    Now.AddHours(-1),
                    Token));
        }

        /// <summary>库里每一张表（迁移历史除外）的全部行。</summary>
        public async Task<Dictionary<string, string[]>> DumpEveryTableAsync()
        {
            List<string> tables = [];
            await using (Microsoft.Data.Sqlite.SqliteCommand command = Db.Connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'";
                await using DbDataReader reader = await command.ExecuteReaderAsync(Token);
                while (await reader.ReadAsync(Token))
                {
                    tables.Add(reader.GetString(0));
                }
            }
            Dictionary<string, string[]> dump = new(StringComparer.Ordinal);
            foreach (string table in tables)
            {
                dump[table] = await Db.DumpAsyncOf(table);
            }
            return dump;
        }

        public async Task RecordFaultAsync(FleetVehicle vehicle, VehicleFaultLevel level)
        {
            await using ControlServerDbContext context = Db.NewContext();
            await new VehicleFaultStore(context).RecordLevelAsync(
                vehicle.AgvId, level, "COMMS_LOST", false, Now.AddMinutes(-1), Token);
        }

        /// <summary>一条未解除的门未证明扣车（control-server#385）。</summary>
        public async Task HoldForUnprovenDoorAsync(FleetVehicle vehicle)
        {
            await using ControlServerDbContext context = Db.NewContext();
            context.SlotDoorHolds.Add(new SlotDoorHoldRow
            {
                HoldId = "f3850000-0000-4000-8000-000000000101",
                AgvId = vehicle.AgvId,
                DemandId = "f3850000-0000-4000-8000-000000000102",
                SlotsJson = "[1]",
                HeldAt = Now.AddMinutes(-1)
            });
            await context.SaveChangesAsync(Token);
        }

        /// <summary>一趟没结束的旅程、却没有用途占有：只剩「有下一业务目标」那一格挡它。</summary>
        public async Task LeaveJourneyWithoutClaimAsync(FleetVehicle vehicle)
        {
            await using ControlServerDbContext context = Db.NewContext();
            await Batch7JourneyFixture.AcceptAsync(context, "D-NEXT", vehicle.AgvId, vehicle.VehicleKey, Now.AddMinutes(-10));
            await context.Set<VehiclePurposeClaimRow>().Where(row => row.VehicleKey == vehicle.VehicleKey)
                .ExecuteDeleteAsync(Token);
            await context.Set<OrderIntentRow>().ExecuteDeleteAsync(Token);
        }

        public async Task HoldByForeignOrderAsync(FleetVehicle vehicle)
        {
            await using ControlServerDbContext context = Db.NewContext();
            context.ForeignRiotOrders.Add(new ForeignRiotOrderRow
            {
                RiotOrderId = "RIOT-FOREIGN-1",
                AgvId = vehicle.AgvId,
                DeviceKey = vehicle.VehicleKey,
                Ownership = ForeignRiotOrderOwnership.Foreign,
                OwnershipBasis = "test",
                State = ForeignRiotOrderStates.Detected,
                DetectedAt = Now.AddMinutes(-1),
            });
            await context.SaveChangesAsync(Token);
        }

        public async Task LeaveOwnIntentAsync(string vehicleKey, string status)
        {
            await using ControlServerDbContext context = Db.NewContext();
            context.OrderIntents.Add(new OrderIntentRow
            {
                MovementLegId = "LEG-UNKNOWN",
                DemandId = "D-OLD",
                UpperId = "W2G-D-OLD-PICKUP-1",
                Purpose = "TO_PICKUP",
                TargetStationId = "ST-PICKUP",
                VehicleKey = vehicleKey,
                MapId = Map,
                DestinationStationId = 101,
                AgvLifecycleGeneration = 1,
                DispatchGeneration = 1,
                CreatedAt = Now.AddHours(-1),
                Status = status,
            });
            await context.SaveChangesAsync(Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (_interceptor is not null)
            {
                await Context.DisposeAsync();
            }
            await Db.DisposeAsync();
        }

        private static string AgvIdOf(string vehicleKey) => "AGV-" + vehicleKey;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHostedService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>读这张表时失败。</summary>
    private sealed class FailOnReadFrom(string table) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException($"Injected failure while reading {table}.");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>写到这张表、而且是这辆车的那一行时失败；别的车照常。</summary>
    private sealed class FailOnInsertInto(string table, string vehicleKey) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailIfTargeted(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailIfTargeted(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void FailIfTargeted(DbCommand command)
        {
            if (command.CommandText.Contains($"INSERT INTO \"{table}\"", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, vehicleKey)))
            {
                Fired = true;
                throw new InvalidOperationException($"Injected failure while writing {table} for {vehicleKey}.");
            }
        }
    }
}

/// <summary>测试里读整张表的简写。</summary>
internal static class IdleReturnDumpExtensions
{
    public static Task<string[]> DumpAsyncOf(this Batch7JourneyFixture fixture, string table) =>
        Batch7JourneyFixture.DumpAsync(fixture.Connection, table);
}
