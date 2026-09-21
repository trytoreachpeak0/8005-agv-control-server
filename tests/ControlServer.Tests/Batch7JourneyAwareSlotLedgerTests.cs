using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 按侧的仓位账本：一辆车这一侧还空着的物理仓位，是会话基线减去<b>这辆车自己</b>已预留、已装的货
/// （ADR-cross-0059；批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一个实现覆盖两种车，所以第一条用例问的是「空闲车的答案没变」。</b>空闲车没有未完成的旅程，减数是空集，
/// 答案必须与 <see cref="SessionBaselineSlotLedger"/> 逐字相同——那是批次7-04 定下的口径，本票不该顺手改它。
/// 写成两个实现再按车的状态挑一个，等于把「这辆车在途没在途」这个判断复制到第二个地方，而那正是两处会先后走岔的地方。
/// </para>
/// <para>
/// <b>减的是目标仓位，不是已经装进去的那几个</b>，这是最容易写反的一处，所以有一条用例专门钉它：
/// 受理之后、装货之前，<c>LoadedSlots</c> 还是空的，而那几个仓位已经为这条需求留着了。拿 <c>LoadedSlots</c>
/// 来减，一条还没装的需求就会被当成没占仓位，于是第二条需求被派进它预留的那一排——两条需求抢同一个物理仓。
/// </para>
/// </remarks>
public sealed class Batch7JourneyAwareSlotLedgerTests
{
    private const string AgvId = "AGV-L1-001";
    private const string VehicleKey = "BROKERX-L1-0001";
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static readonly VehicleSlotPositions EightSlot = new(
        AgvId,
        "SLOT-MODEL-1",
        VehicleSlotPositionSource.ActiveSlotConfiguration,
        new Dictionary<int, string>
        {
            [1] = "FRONT",
            [2] = "FRONT",
            [3] = "FRONT",
            [4] = "FRONT",
            [5] = "REAR",
            [6] = "REAR",
            [7] = "REAR",
            [8] = "REAR",
        });

    /// <summary>
    /// 一辆没有未完成旅程的车：账本报的就是会话基线，与批次7-04 那个实现逐字相同。
    /// </summary>
    [Theory]
    [InlineData("FRONT", "1,2,3,4")]
    [InlineData("REAR", "5,6,7,8")]
    public async Task AnIdleVehicleGetsItsWholeSessionBaseline(string side, string expected)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        IReadOnlyList<int> free = await new JourneyAwareSlotLedger(fixture.Context)
            .ReadAvailableSlotsAsync(Vehicle(), side, TestContext.Current.CancellationToken);

        IReadOnlyList<int> baseline = await new SessionBaselineSlotLedger()
            .ReadAvailableSlotsAsync(Vehicle(), side, TestContext.Current.CancellationToken);
        Assert.Equal(baseline, free);
        Assert.Equal(expected, string.Join(',', free));
    }

    /// <summary>
    /// 车正跑着一趟旅程：它那条需求预留的两个仓位从这一侧减掉，另一侧一个不动。
    /// </summary>
    [Fact]
    public async Task AVehicleUnderWayLosesTheSlotsItsOwnDemandHasReserved()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, DemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now);

        JourneyAwareSlotLedger ledger = new(fixture.NewContext());
        Assert.Equal(
            [3, 4],
            await ledger.ReadAvailableSlotsAsync(Vehicle(), "FRONT", TestContext.Current.CancellationToken));
        Assert.Equal(
            [5, 6, 7, 8],
            await ledger.ReadAvailableSlotsAsync(Vehicle(), "REAR", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <b>减的是目标仓位，不是已经装进去的那几个。</b>受理之后、装货之前 <c>LoadedSlots</c> 还是空的，
    /// 而那两个仓位已经为这条需求留着了。
    /// </summary>
    /// <remarks>
    /// 这一条与上一条的库状态完全相同，断言的却是另一件事：上一条问「减了没有」，这一条问「凭什么减的」。
    /// 把实现改成读 <c>LoadedSlots</c>，上一条会红，这一条给出的是红在哪里——受理写下的 <c>LoadedSlots</c>
    /// 确实是 NULL，所以那不是「数据还没到」，是判据取错了列。
    /// </remarks>
    [Fact]
    public async Task TheSlotsSubtractedAreTheReservedOnesWhileNothingIsLoadedYet()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, DemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now);

        JourneyDemandRow membership = await fixture.NewContext().Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == DemandId, TestContext.Current.CancellationToken);
        Assert.Null(membership.LoadedSlotsJson);
        Assert.Equal([1, 2], JsonSerializer.Deserialize<int[]>(membership.TargetSlotsJson) ?? []);

        Assert.Equal(
            [3, 4],
            await new JourneyAwareSlotLedger(fixture.NewContext())
                .ReadAvailableSlotsAsync(Vehicle(), "FRONT", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 需求终结之后仓位回到基线：它的货已经不在车上，或者从未上车。
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnEndedDemandHoldsNoSlot(bool membershipTerminated, bool demandCancelled)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, DemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now);

        // 两种终结分开写，因为迁移回填的在途旅程可能只有后者：归属行是批次7-01 才有的，而那之前已经在跑的
        // 旅程，它的需求被取消时只有 AcceptedDemands 那一侧留下了痕迹。账本要认得两种，与 IsDoneAt 同口径。
        ControlServerDbContext context = fixture.NewContext();
        if (membershipTerminated)
        {
            JourneyDemandRow membership = await context.Set<JourneyDemandRow>()
                .SingleAsync(row => row.DemandId == DemandId, TestContext.Current.CancellationToken);
            membership.Status = JourneyDemandStatuses.Terminated;
        }

        if (demandCancelled)
        {
            AcceptedDemandRow demand = await context.AcceptedDemands
                .SingleAsync(row => row.DemandId == DemandId, TestContext.Current.CancellationToken);
            demand.Status = DemandExecutionStatus.Cancelled;
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [1, 2, 3, 4],
            await new JourneyAwareSlotLedger(fixture.NewContext())
                .ReadAvailableSlotsAsync(Vehicle(), "FRONT", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 减的只是<b>这辆车自己</b>的货：别的车跑着的旅程一个仓位都不占它的。
    /// </summary>
    /// <remarks>
    /// 物理仓位号在车与车之间是重名的——每辆车都有 1 号仓。按仓位号去减而不先按车筛，一辆车装了货会让
    /// 全车队的同号仓位一起消失，而那种错在单车现场永远不会显形。
    /// </remarks>
    [Fact]
    public async Task AnotherVehiclesJourneyTakesNoneOfThisVehiclesSlots()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, DemandId, "AGV-L1-002", "BROKERX-L1-0002", Batch7JourneyFixture.Now);

        Assert.Equal(
            [1, 2, 3, 4],
            await new JourneyAwareSlotLedger(fixture.NewContext())
                .ReadAvailableSlotsAsync(Vehicle(), "FRONT", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 没有会话基线——没有 Ready 会话，或者它的快照还没进来——是这一侧一个空仓都没有，而不是一辆空车。
    /// </summary>
    [Fact]
    public async Task AVehicleWithoutASessionBaselineHasNoFreeSlot()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        Assert.Empty(await new JourneyAwareSlotLedger(fixture.Context)
            .ReadAvailableSlotsAsync(VehicleWithoutBaseline(), "FRONT", TestContext.Current.CancellationToken));
    }

    // ---- 判据把两种「装不下」分开（票面第 4 条） ----------------------------------------------

    /// <summary>
    /// 这一侧装不下，而<b>会话基线本来是够的</b>：差额是这辆车自己的货占掉的，所以是
    /// <c>SLOT_GROUP_OCCUPIED_BY_OWN_CARGO</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一条与下一条只差一处：会话基线。</b>账本的答案两条里都一样（只剩一个空仓，要两个），所以红的时候
    /// 能直接定位到判据里分这两种情况的那一步，而不是定位到账本。
    /// </para>
    /// <para>
    /// 两个码要人做的事不同：本车的货占着，等这趟卸完就能接；仓位被禁用或本来就没那么多，等多久都没用。
    /// 批次7-07（control-server#212）判「这一侧装满」只认前一种，所以它们必须可区分。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGroupFullOfThisVehiclesOwnCargoIsNotTheSameAsAGroupThatIsTooSmall()
    {
        DispatchCandidateEvaluation evaluation = EvaluationWithBaseline([1, 2, 3, 4]);

        Assert.Equal(
            DispatchReasonCodes.SlotGroupOccupiedByOwnCargo,
            await EvaluateWithLedgerAsync(evaluation, ledgerAnswer: [1], baskets: 2));
    }

    /// <summary>会话基线本身就不够：这是仓位被禁用或本来就没那么多，不是本车的货占的。</summary>
    [Fact]
    public async Task AGroupWhoseSessionBaselineIsAlreadyTooSmallIsTemporarilyUnavailable()
    {
        DispatchCandidateEvaluation evaluation = EvaluationWithBaseline([1]);

        Assert.Equal(
            DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable,
            await EvaluateWithLedgerAsync(evaluation, ledgerAnswer: [1], baskets: 2));
    }

    private const int BoxesPerBasket = 4;

    private static Task<string> EvaluateWithLedgerAsync(
        DispatchCandidateEvaluation evaluation, int[] ledgerAnswer, int baskets) =>
        new SlotCapacityCriterion(
                new FixedBoxCount(baskets * BoxesPerBasket),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SlotCapacityCriterion>.Instance,
                new FixedSlotLedger(ledgerAnswer))
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

    /// <summary>一条候选，车的会话基线由调用方给定，所需分组是 FRONT。</summary>
    private static DispatchCandidateEvaluation EvaluationWithBaseline(int[] sessionBaseline)
    {
        AcceptedDemandSnapshot candidate = new(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Batch7JourneyFixture.Now,
            "SERIES-1",
            "WIRE_TO_GATE",
            "SUBLOT-001",
            1,
            Batch7JourneyFixture.Now.AddMinutes(-10),
            Batch7JourneyFixture.Now.AddMinutes(-9),
            "TRACE-1",
            "COMMIT-1",
            new LiveMesFieldSet("N1-3", "EQP-01", "STEP-01", Batch7JourneyFixture.Now, "PDFN5×6-8L(12R)"));
        return new DispatchCandidateEvaluation(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate]),
                new RiotMapStationCatalogSnapshot(25, Batch7JourneyFixture.Now, new string('c', 64), []),
                new SingleStationView(new RiotMapStation(210, "关卡")),
                new HashSet<string>(StringComparer.Ordinal),
                Batch7JourneyFixture.Now,
                new VehicleDispatchPolicy(
                    [], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY")),
            VehicleWith(sessionBaseline))
        {
            AreaAssignment = new AreaAssignment("N1-3", "ZONE-TEST", "FRONT"),
            PackageCapacity = BoxesPerBasket,
        };
    }

    private sealed class FixedBoxCount(int boxes) : ISublotBoxCountReader
    {
        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(boxes);
    }

    /// <summary>账本的答案由用例给定，好让「基线够不够」成为两条用例之间唯一的差别。</summary>
    private sealed class FixedSlotLedger(int[] answer) : IVehicleSlotLedger
    {
        public Task<IReadOnlyList<int>> ReadAvailableSlotsAsync(
            DispatchVehicleFacts vehicle, string slotPosition, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(answer);
    }

    /// <summary>八个仓位全在会话基线里的那辆车。</summary>
    private static DispatchVehicleFacts Vehicle() => VehicleWith([1, 2, 3, 4, 5, 6, 7, 8]);

    /// <summary>没有会话基线的那辆车：Onboard 事实整个缺席。</summary>
    private static DispatchVehicleFacts VehicleWithoutBaseline() => VehicleWith(null);

    private static DispatchVehicleFacts VehicleWith(int[]? available) => new(
        VehicleKey,
        AgvId,
        available is null ? null : new OnboardDispatchFacts(1, available, true, true, true, true, false),
        new RiotVehicleObservation(
            VehicleKey, true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Batch7JourneyFixture.Now),
        Batch7JourneyFixture.Now,
        EightSlot);
}
