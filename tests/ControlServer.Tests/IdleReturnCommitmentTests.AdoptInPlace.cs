using ControlServer.Application;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// 原地认领（control-server#793，#787 审查 H1）：车停在已登记的等待点上、库里却没有这个点的独占行（人手把车开过去、换库之后）。
/// </summary>
/// <remarks>
/// <para>
/// 修之前评估器把车所在的点当成空点：到自己所在站的路网代价是 0，<see cref="IdleReturnEvaluator.Choose"/> 必选它，于是承诺「开往当前站」；
/// 另一辆车也会被派到这个有车的点上。审查的探针就是下面头两条用例的场景：车 A 停在 214、没有占用行；车 B 停在 210。
/// </para>
/// <para>
/// 路网同 <see cref="IdleReturnCommitmentTests"/>：210 → 214 → 215 单向链，216 到不了。
/// </para>
/// </remarks>
public sealed partial class IdleReturnCommitmentTests
{
    /// <summary>不建单、不取用途，只写下这个点归它的占用行——与空闲返回到点之后的样子相同。</summary>
    [Fact]
    public async Task AVehicleStandingOnARegisteredWaitingPointWithoutARowAdoptsItInPlaceAndCreatesNothing()
    {
        await using Harness harness = await Harness.CreateAsync();

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA, station: 214)));

        Assert.Equal(
            (IdleReturnReasons.AdoptedInPlace, (int?)214, (long?)harness.RegistrationVersion, IdleReturnIdentity.JourneyIdFor(VehicleA, Now)),
            (verdict.Reason, verdict.StationId, verdict.WaitingPointVersion, verdict.JourneyId));
        Assert.False(verdict.Committed);
        StationExclusivity station = Assert.IsType<StationExclusivity>(
            await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 214, Token));
        Assert.Equal(
            (StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Occupied, VehicleA, verdict.JourneyId,
                (long?)harness.RegistrationVersion),
            (station.StationKind, station.State, station.VehicleKey, station.JourneyId, station.WaitingPointVersion));
        Assert.Single(await harness.Db.DumpAsyncOf("StationExclusivities"));
        Assert.Single(await harness.Db.DumpAsyncOf("StationExclusivityRecords"));
        // 没有用途占有：车不被承诺去任何地方，派车照样能把它派去搬运；也没有旅程与订单意图，引擎没有东西可推进、可建单。
        Assert.Empty(await harness.Db.DumpAsyncOf("VehiclePurposeClaims"));
        Assert.Empty(await harness.Db.DumpAsyncOf("VehiclePurposeClaimRecords"));
        Assert.Empty(await harness.Db.DumpAsyncOf("JourneyRuntimes"));
        Assert.Empty(await harness.Db.DumpAsyncOf("OrderIntents"));
        Assert.Contains(harness.Log.Entries, entry => entry.EventId.Id == 2318);
    }

    /// <summary>
    /// 下一轮：认领了 214 的车留在原地（它现在持有这个点），另一辆车去下一个最近的 215，不去 214。
    /// </summary>
    [Fact]
    public async Task TheNextRoundSendsNoOtherVehicleToThePointAnAdoptedVehicleStandsOn()
    {
        await using Harness harness = await Harness.CreateAsync();
        Assert.Equal(
            IdleReturnReasons.AdoptedInPlace,
            Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA, station: 214))).Reason);

        IReadOnlyList<IdleReturnVerdict> next =
            await harness.EvaluateAsync(harness.Candidate(VehicleB), harness.Candidate(VehicleA, station: 214));

        Assert.Equal(
            [(VehicleB, IdleReturnReasons.Committed, (int?)215), (VehicleA, IdleReturnReasons.VehicleHoldsStation, null)],
            next.Select(verdict => (verdict.VehicleKey, verdict.Reason, verdict.StationId)));
        StationExclusivity held = Assert.IsType<StationExclusivity>(
            await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 214, Token));
        Assert.Equal((VehicleA, StationExclusivityStates.Occupied), (held.VehicleKey, held.State));
    }

    /// <summary>
    /// 审查探针的另一半：同一轮里 B 先评估。A 还没认领、214 还没有行，B 照样不去 214——A 就站在上面。
    /// </summary>
    [Fact]
    public async Task InOneRoundAVehicleJudgedFirstIsNotSentToThePointAnotherStandsOn()
    {
        await using Harness harness = await Harness.CreateAsync();

        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleB), harness.Candidate(VehicleA, station: 214));

        Assert.Equal(
            [(VehicleB, IdleReturnReasons.Committed, (int?)215), (VehicleA, IdleReturnReasons.AdoptedInPlace, (int?)214)],
            verdicts.Select(verdict => (verdict.VehicleKey, verdict.Reason, verdict.StationId)));
    }

    /// <summary>
    /// 停在点上的车自己这一轮不合格（电量低于强制充电线），没有认领，点上仍没有行：别的车照样不被派过去。
    /// </summary>
    [Fact]
    public async Task AVehicleStandingOnAPointKeepsOthersAwayEvenWhenItIsNotEligibleItself()
    {
        await using Harness harness = await Harness.CreateAsync();

        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleA, station: 214, battery: 29), harness.Candidate(VehicleB));

        Assert.Equal(
            [(VehicleA, IdleReturnReasons.BelowMandatoryChargeLine, (int?)null), (VehicleB, IdleReturnReasons.Committed, (int?)215)],
            verdicts.Select(verdict => (verdict.VehicleKey, verdict.Reason, verdict.StationId)));
        StationExclusivity reserved = Assert.IsType<StationExclusivity>(
            Assert.Single(await new StationExclusivityStore(harness.Db.NewContext()).ListByVehicleAsync(VehicleB, Token)));
        Assert.Equal(215, reserved.StationId);
        Assert.Null(await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 214, Token));
    }

    /// <summary>
    /// 与车所在的站吸附到同一个路网节点、代价为 0 的等待点按同样方式认领：车停在 212（不是等待点，与 214 同在节点 2）。
    /// </summary>
    [Fact]
    public async Task AWaitingPointOnTheSameNodeAsTheVehiclesStationIsAdoptedTheSameWay()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.ExtraPlacedStations.Add((212, "等待点1旁", 2));
        await harness.SeedRouteGraphAsync();

        IReadOnlyList<IdleReturnVerdict> verdicts =
            await harness.EvaluateAsync(harness.Candidate(VehicleA, station: 212), harness.Candidate(VehicleB));

        Assert.Equal(
            [(VehicleA, IdleReturnReasons.AdoptedInPlace, (int?)214), (VehicleB, IdleReturnReasons.Committed, (int?)215)],
            verdicts.Select(verdict => (verdict.VehicleKey, verdict.Reason, verdict.StationId)));
        Assert.DoesNotContain(
            await harness.Db.DumpAsyncOf("VehiclePurposeClaimRecords"), row => row.Contains(VehicleA, StringComparison.Ordinal));
    }

    /// <summary>
    /// 不认领的两种：点不接这辆车（白名单只有 B），点被别的车的行占着（B 正开过来）。车照常选点去别处，它站着的点也不派给别人。
    /// </summary>
    [Theory]
    [InlineData("vehicle-scope")]
    [InlineData("held-by-another")]
    public async Task AVehicleOnAPointItMayNotAdoptIsSentOnAndThePointIsNotTakenFromUnderIt(string why)
    {
        await using Harness harness = await Harness.CreateAsync();
        if (why == "vehicle-scope")
        {
            await harness.RegisterAsync(Point(214, scope: [VehicleB]), Point(215), Point(216));
        }
        else
        {
            await harness.HoldStationAsync(VehicleB, 214, StationExclusivityStates.Reserved);
        }

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA, station: 214)));

        Assert.Equal((IdleReturnReasons.Committed, (int?)215), (verdict.Reason, verdict.StationId));
        StationExclusivity? standingOn = await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 214, Token);
        Assert.True(
            why == "vehicle-scope" ? standingOn is null : standingOn?.VehicleKey == VehicleB,
            $"214 should be as it was, not adopted by {VehicleA}: {standingOn}.");
    }

    /// <summary>
    /// 只做待送的车停在一个没有行的等待点上，同样原地认领，不开回待送固定起点——与停在点上、有占用行的车（资格里的
    /// <see cref="IdleReturnReasons.VehicleHoldsStation"/>）同一个结局。做完下一单之后它照常回待送点。
    /// </summary>
    [Fact]
    public async Task AStagingOnlyVehicleStandingOnAWaitingPointAdoptsItRatherThanDrivingToTheStagingOrigin()
    {
        await using Harness harness = await Harness.CreateStagingOriginElsewhereAsync();
        harness.Configure(VehicleA, TransportTaskTypes.StagingToWire);

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA, station: 214)));

        Assert.Equal((IdleReturnReasons.AdoptedInPlace, (int?)214), (verdict.Reason, verdict.StationId));
        Assert.Null(await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 215, Token));
    }
}
