using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 电量阈值与强制充电优先在派车轮次上的样子（批次9-05，control-server#403；REQ-0208、REQ-0281、REQ-0282、REQ-0290）。夹具的测试策略
/// 两道线都是 40（<see cref="TestChargingPolicies.AllApprovedAt"/>），合成 RIoT 报 80，除非用例把某辆车的电量改掉。
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 强制充电优先（REQ-0290）：同一轮两车，A 低于入口线、B 充足。A 在轮次里排在前面（默认夹具下这一条需求是 A 接），这一轮它被新原因码挡下，
    /// 需求派给 B；A 一张单都没有。
    /// </summary>
    [Fact]
    public async Task AVehicleBelowItsMandatoryChargeLineTakesNoTransportAndTheOtherVehicleDoes()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2]);
        fixture.Riot.BatteryByVehicle[FleetFixture.VehicleKeys[0]] = 39;
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        await fixture.RunRoundAsync();

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(
            [DispatchReasonCodes.MandatoryChargeRequired, DispatchAdmissionChain.Eligible],
            outcome.CompletedVehicles.Select(vehicle => Assert.Single(vehicle.Verdicts).ReasonCode).ToArray());
        Assert.Equal([FleetFixture.VehicleKeys[1]], fixture.Context.JourneyRuntimes.Select(row => row.VehicleKey).ToArray());
        Assert.DoesNotContain(fixture.Riot.Creates, create => create.VehicleKey == FleetFixture.VehicleKeys[0]);
    }

    /// <summary>
    /// 只有一辆低于入口线的车：需求留在积压里，原因码是新码；车一张单都没有、也没有被任何用途占有——批次9-06 去桩之前它原地不动，
    /// 与此前低于 30% 的车一样（本票的准入线护栏）。连跑三轮，结论不变。
    /// </summary>
    [Fact]
    public async Task AVehicleBelowItsMandatoryChargeLineIsSentNowhere()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1]);
        fixture.Riot.BatteryByVehicle[FleetFixture.VehicleKeys[0]] = 20;
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        for (int round = 0; round < 3; round++)
        {
            await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(
            DispatchReasonCodes.MandatoryChargeRequired,
            Assert.Single(await fixture.Context.JourneyBacklog.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Empty(fixture.Context.JourneyRuntimes);
        Assert.Empty(fixture.Riot.Creates);
        Assert.Empty(fixture.Context.Set<VehiclePurposeClaimRow>());
    }

    /// <summary>
    /// 派车时把判它的那一版策略记到旅程上（REQ-0282），连同 <c>batteryState</c> 的第一版投影，与受理同一次保存。
    /// </summary>
    [Fact]
    public async Task TheDispatchRecordsThePolicyVersionItWasJudgedUnderOnTheJourney()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1]);
        TestChargingPolicies.Versions versions = new(TestChargingPolicies.ContentAt(40));
        versions.Activate(TestChargingPolicies.ContentAt(35));
        fixture.ChargingPolicy = versions;
        await fixture.RecreateEngineAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        await fixture.RunRoundAsync();

        JourneyRuntimeRow journey = Assert.Single(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal((2L, BatteryStates.Sufficient), (journey.ChargingPolicyVersion, journey.PublishedBatteryState));
    }

    /// <summary>
    /// 轮中激活（票面点名的并发读改写）：这一轮为车读过策略之后、受理之前，激活一版更严的（入口线 90，车是 80）。这一轮一次读定：受理前的复查
    /// 用的仍是读定的那一版，照常派出，旅程记下的也是它；新版本下一轮才生效。
    /// </summary>
    [Fact]
    public async Task AVersionActivatedMidRoundAppliesFromTheNextRoundAndTheJourneyKeepsTheOneItWasJudgedUnder()
    {
        // The route graph so that the vehicle under way in the second round is asked about an append the regular way (and
        // refused for the zone allowing none), not through a chain without its en-route gate.
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2], withRouteGraph: true);
        TestChargingPolicies.Versions versions = new(TestChargingPolicies.ContentAt(40));
        fixture.ChargingPolicy = versions;
        await fixture.RecreateEngineAsync();
        ChargingPolicyContent stricter = TestChargingPolicies.ContentAt(90) with { ChargingCompletionThresholdPercent = 95 };
        versions.AfterNextResolve = () => versions.Activate(stricter);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        await fixture.RunRoundAsync();

        Assert.Equal(2, versions.Active);
        JourneyRuntimeRow journey = await fixture.JourneyOfAsync(fixture.Options.Fleet[0].AgvId);
        Assert.Equal(1L, journey.ChargingPolicyVersion);

        // 下一轮：第二辆车按新版本判，80 < 90，被挡。
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        DispatchVehicleOutcome second = fixture.RoundOutcomes.Outcomes[^1].CompletedVehicles
            .Single(vehicle => vehicle.VehicleKey == FleetFixture.VehicleKeys[1]);
        Assert.Equal(
            DispatchReasonCodes.MandatoryChargeRequired,
            second.Verdicts.Single(verdict => verdict.Evaluation.Candidate.DemandId == FleetFixture.Demand(1, "N1-2", 1).DemandId).ReasonCode);
        Assert.Single(fixture.Context.JourneyRuntimes);
    }

    /// <summary>
    /// 强制充电线不高于救命线的版本在运行中被激活（激活走 FieldOps、不经服务端，服务端只能在用的时候拦）：下一轮起每辆车都拿不到新用途、
    /// 原因码是 <c>CHARGING_POLICY_ENTRY_NOT_ABOVE_RESCUE_LINE</c>，那几轮一张单都不建；再激活一版合格的，下一轮恢复派车。救命线是夹具默认的 15。
    /// </summary>
    [Fact]
    public async Task AVersionWhoseEntryThresholdIsNotAboveTheRescueLineBlocksEveryVehicleUntilACorrectOneIsActivated()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2]);
        Assert.Equal(15, fixture.Options.WaitingJourneyRescueBatteryPercent);
        TestChargingPolicies.Versions versions = new(TestChargingPolicies.ContentAt(40));
        fixture.ChargingPolicy = versions;
        await fixture.RecreateEngineAsync();
        versions.Activate(TestChargingPolicies.Content with
        {
            MandatoryChargeEntryThresholdPercent = 15,
            MinimumPostTaskBatteryMarginPercent = 10,
        });
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        for (int round = 0; round < 3; round++)
        {
            await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));
            Assert.All(fixture.RoundOutcomes.Outcomes[^1].CompletedVehicles, vehicle => Assert.Equal(
                DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine, Assert.Single(vehicle.Verdicts).ReasonCode));
        }
        Assert.Empty(fixture.Riot.Creates);
        Assert.Empty(fixture.Context.JourneyRuntimes);
        Assert.Equal(
            DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine,
            Assert.Single(await fixture.Context.JourneyBacklog.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken)).ReasonCode);

        versions.Activate(TestChargingPolicies.ContentAt(16));
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        JourneyRuntimeRow journey = Assert.Single(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3L, journey.ChargingPolicyVersion);
        Assert.Single(fixture.Riot.Creates);
    }

    /// <summary>
    /// 在途的判断按旅程记下的版本读回（REQ-0282）：旅程在版本 1（线 40）下派出，之后激活版本 2（线 90）。途中追加按版本 1 判，80 合格，
    /// 追加进同一趟旅程；同一轮里空闲的第二辆车按版本 2 判，被挡。
    /// </summary>
    [Fact]
    public async Task AnEnRouteAppendIsJudgedUnderTheVersionTheJourneyFrozeWhileANewDispatchUsesTheActiveOne()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        TestChargingPolicies.Versions versions = new(TestChargingPolicies.ContentAt(40));
        fixture.ChargingPolicy = versions;
        await fixture.RecreateEngineAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow first = Assert.Single(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, first.ChargingPolicyVersion);

        versions.Activate(TestChargingPolicies.ContentAt(90) with { ChargingCompletionThresholdPercent = 95 });
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([first.JourneyId, first.JourneyId], memberships.Select(row => row.JourneyId));
        Assert.Equal(1L, Assert.Single(fixture.Context.JourneyRuntimes.AsNoTracking()).ChargingPolicyVersion);
        DispatchVehicleOutcome idle = fixture.RoundOutcomes.Outcomes[^1].CompletedVehicles
            .Single(vehicle => vehicle.VehicleKey != first.VehicleKey);
        Assert.Equal(
            DispatchReasonCodes.MandatoryChargeRequired,
            idle.Verdicts.Single(verdict => verdict.Evaluation.Candidate.DemandId == FleetFixture.Demand(1, "N1-2", 1).DemandId).ReasonCode);
    }

    /// <summary>
    /// 途中越过入口线的在途车不再接追加（REQ-0281：追加就是新任务）；它当前那一趟不动——旅程还在、一条需求都没被释放。
    /// </summary>
    [Fact]
    public async Task AVehicleUnderWayThatFellBelowItsMandatoryChargeLineTakesNoAppendButKeepsItsJourney()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow first = Assert.Single(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));

        fixture.Riot.BatteryByVehicle[first.VehicleKey] = 39;
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        JourneyDemandRow membership = Assert.Single(
            await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(first.JourneyId, membership.JourneyId);
        Assert.Equal(first.Stage, Assert.Single(fixture.Context.JourneyRuntimes.AsNoTracking()).Stage);
        Assert.Contains(
            fixture.RoundOutcomes.Outcomes[^1].CompletedVehicles.Single().Verdicts,
            verdict => verdict.ReasonCode == DispatchReasonCodes.MandatoryChargeRequired);
    }
}
