using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>
/// 按需求 AREA 指派的仓位分组选仓（control-server#73，REQ-0349／0351／0352、REQ-0208 仓位半句）：整条需求装进
/// 所需分组内编号最小的空仓，其它分组的空仓不计入，分组取服务端的车型记录而不是仓号区间。推翻的是不变量 I8
/// 「任何仓位服务任何站点」。
/// </summary>
public sealed class SlotGroupSelectionTests
{
    private const string AgvId = "AGV-1";
    private const string Zone = "MAP-25-WIRE_TO_GATE";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 6, 0, 0, TimeSpan.Zero);

    /// <summary>该包装每篮 4 盒：要 N 个花篮就报 N × 4 盒。</summary>
    private const int BoxesPerBasket = 4;

    private static readonly int[] AllEight = [1, 2, 3, 4, 5, 6, 7, 8];

    // ---- 组内选仓 --------------------------------------------------------------------------------

    [Theory]
    [InlineData("REAR", 3, "5,6,7")]
    [InlineData("FRONT", 3, "1,2,3")]
    [InlineData("REAR", 4, "5,6,7,8")]
    public async Task TheWholeDemandGoesIntoTheLowestFreeSlotsOfItsAssignedGroup(
        string group, int baskets, string expected)
    {
        DispatchCandidateEvaluation evaluation = Evaluation(group, ApprovedEightSlot(), AllEight);

        string reason = await EvaluateAsync(evaluation, baskets);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Equal(expected, string.Join(',', evaluation.TargetSlots));
        Assert.Equal(baskets, evaluation.ExpectedBasketCount);
        Assert.Equal(group, evaluation.RequiredSlotPosition);
    }

    /// <summary>
    /// 整车空着六个仓，需求要 3 个：旧的 I8 取仓会取 [1,2,3] 放行。所需分组 REAR 只空着 6、8 两个，所以不受理，
    /// 也不借前侧的空仓——这是正常积压（REQ-0210），不是结构性问题。
    /// </summary>
    [Fact]
    public async Task FreeSlotsInAnotherGroupDoNotCountEvenWhenTheWholeVehicleHasEnough()
    {
        DispatchCandidateEvaluation evaluation = Evaluation("REAR", ApprovedEightSlot(), [1, 2, 3, 4, 6, 8]);

        string reason = await EvaluateAsync(evaluation, baskets: 3);

        Assert.Equal(DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, reason);
        Assert.Empty(evaluation.TargetSlots);
    }

    [Fact]
    public async Task TargetSlotsAreAscendingWhateverOrderTheVehicleReportsItsFreeSlotsIn()
    {
        DispatchCandidateEvaluation evaluation = Evaluation("REAR", ApprovedEightSlot(), [8, 3, 7, 6, 1]);

        string reason = await EvaluateAsync(evaluation, baskets: 2);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Equal([6, 7], evaluation.TargetSlots);
    }

    // ---- 四个原因码 ------------------------------------------------------------------------------

    /// <summary>
    /// 比分组的物理仓位数还多，与占用无关：整车八个仓全空也一样。这台车永远装不下这条需求（一条需求不跨组），
    /// 与「暂时空仓不足」是两回事，所以先判这一条。
    /// </summary>
    [Theory]
    [InlineData("1,2,3,4,5,6,7,8")]
    [InlineData("5,6")]
    public async Task MoreBasketsThanTheGroupHasPhysicalSlotsIsRefusedWhateverIsFree(string available)
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            "REAR", ApprovedEightSlot(), [.. available.Split(',').Select(int.Parse)]);

        string reason = await EvaluateAsync(evaluation, baskets: 5);

        Assert.Equal(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, reason);
        Assert.Empty(evaluation.TargetSlots);
    }

    [Fact]
    public async Task AGroupTheVehiclesModelDoesNotHaveAtAllHasNoPhysicalSlots()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            "REAR", Positions("front-only", "FRONT", "FRONT", "FRONT", "FRONT"), [1, 2, 3, 4]);

        string reason = await EvaluateAsync(evaluation, baskets: 1);

        Assert.Equal(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, reason);
    }

    /// <summary>
    /// 白名单（control-server#72）在更前面已经挡掉没指派的 AREA，这里是它身后的防御：没有分组就不猜一个。
    /// </summary>
    [Fact]
    public async Task AnAreaWithNoAssignedGroupIsRefusedRatherThanGivenAnyGroup()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(group: null, ApprovedEightSlot(), AllEight);

        string reason = await EvaluateAsync(evaluation, baskets: 1);

        Assert.Equal(DispatchReasonCodes.AreaSlotGroupNotAssigned, reason);
        Assert.Empty(evaluation.TargetSlots);
    }

    /// <summary>车的仓位模型未解析时 fail-closed：不回退到默认八仓，也不按仓号区间推断 1～4 前、5～8 后。</summary>
    [Fact]
    public async Task AVehicleWhoseSlotModelIsUnresolvedIsRefusedRatherThanAssumedEightSlot()
    {
        DispatchCandidateEvaluation evaluation = Evaluation("FRONT", positions: null, AllEight);

        string reason = await EvaluateAsync(evaluation, baskets: 1);

        Assert.Equal(DispatchReasonCodes.VehicleSlotModelUnresolved, reason);
        Assert.Empty(evaluation.TargetSlots);
    }

    [Fact]
    public async Task ABasketCountOutsideOneToEightIsStillRefusedAsOutOfRange()
    {
        DispatchCandidateEvaluation evaluation = Evaluation("FRONT", ApprovedEightSlot(), AllEight);

        string reason = await EvaluateAsync(evaluation, baskets: 9);

        Assert.Equal("EXPECTED_BASKET_COUNT_OUT_OF_RANGE", reason);
    }

    // ---- 分组来自服务端模型，取数顺序 ------------------------------------------------------------

    /// <summary>
    /// 同一台车、同一份车报空仓、同一条需求，只换库里的车型：1～2 FRONT、3～8 REAR 之后，REAR 的 3 个仓变成
    /// [3,4,5]，FRONT 装不下 3 个。选仓若按仓号区间写死，这里仍会是 [5,6,7]。
    /// </summary>
    [Fact]
    public async Task ChangingTheModelInTheDatabaseChangesTheSlotsChosen()
    {
        await using AreaAssignmentPersistenceFixture approved = await AreaAssignmentPersistenceFixture.CreateAsync();
        await BindAsync(approved, await approved.SlotAuthority.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken), Now);
        await using AreaAssignmentPersistenceFixture reshaped = await AreaAssignmentPersistenceFixture.CreateAsync();
        await ActivateAsync(reshaped, await PublishModelAsync(
            reshaped, "two-front", "FRONT", "FRONT", "REAR", "REAR", "REAR", "REAR", "REAR", "REAR"));

        Assert.Equal("5,6,7", string.Join(',', await ChooseAsync(approved, "REAR", baskets: 3)));
        Assert.Equal("3,4,5", string.Join(',', await ChooseAsync(reshaped, "REAR", baskets: 3)));
        Assert.Equal("1,2,3", string.Join(',', await ChooseAsync(approved, "FRONT", baskets: 3)));
        Assert.Equal("1,2", string.Join(',', await ChooseAsync(reshaped, "FRONT", baskets: 2)));

        DispatchCandidateEvaluation tooMany = Evaluation(
            "FRONT", await reshaped.SlotPositions.ReadAsync(AgvId, TestContext.Current.CancellationToken), AllEight);
        Assert.Equal(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, await EvaluateAsync(tooMany, baskets: 3));
    }

    /// <summary>有生效配置时用它引用的车型，哪怕之后又给另一个车型发布了 IO 绑定。</summary>
    [Fact]
    public async Task WithAnActiveConfigurationTheGroupsAreThatConfigurationsModel()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow active = await PublishModelAsync(
            fixture, "two-front", "FRONT", "FRONT", "REAR", "REAR", "REAR", "REAR", "REAR", "REAR");
        await ActivateAsync(fixture, active);
        await BindAsync(fixture, await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken), Now.AddDays(1));

        Assert.Equal("3,4,5", string.Join(',', await ChooseAsync(fixture, "REAR", baskets: 3)));
    }

    /// <summary>没有生效配置时，用该车最新一版已发布 IO 绑定引用的车型——L2 与 G3 因此不必为每个派车场景做激活握手。</summary>
    [Fact]
    public async Task WithNoActiveConfigurationTheGroupsAreTheModelOfTheLatestPublishedIoBinding()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await BindAsync(fixture, await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken), Now);
        await BindAsync(fixture, await PublishModelAsync(
            fixture, "two-front", "FRONT", "FRONT", "REAR", "REAR", "REAR", "REAR", "REAR", "REAR"), Now.AddHours(1));

        Assert.Equal("3,4,5", string.Join(',', await ChooseAsync(fixture, "REAR", baskets: 3)));
    }

    /// <summary>两份记录都没有：已批准的八仓模型就在库里也不拿来当默认，不派车。</summary>
    [Fact]
    public async Task WithNeitherRecordTheCandidateIsRefusedEvenThoughTheApprovedModelIsPublished()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(Now, TestContext.Current.CancellationToken);

        DispatchCandidateEvaluation evaluation = Evaluation(
            "FRONT", await fixture.SlotPositions.ReadAsync(AgvId, TestContext.Current.CancellationToken), AllEight);

        Assert.Equal(DispatchReasonCodes.VehicleSlotModelUnresolved, await EvaluateAsync(evaluation, baskets: 1));
        Assert.Empty(evaluation.TargetSlots);
    }

    // ---- 排序不占优 ------------------------------------------------------------------------------

    /// <summary>
    /// 通过判据之后，组内空仓多少不影响排序（REQ-0208 仓位半句的后半）：排序器只比路径成本、首次看到、创建时间与
    /// 需求号。把两条候选各自装进的分组、目标仓与花篮数对调，选中的仍是同一条。
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(100L, 100L)]
    public void RankingIgnoresHowMuchRoomACandidatesGroupHas(long? olderCost, long? newerCost)
    {
        LayeredDispatchCandidateRanker ranker = DispatchCandidateOrdering.Ranker();
        EligibleDispatchCandidate older = Eligible("10000000-0000-4000-8000-000000000001", Now.AddMinutes(-10), olderCost);
        EligibleDispatchCandidate newer = Eligible("10000000-0000-4000-8000-000000000002", Now.AddMinutes(-5), newerCost);
        // 旧的那条挤进 FRONT 最后两个空仓，新的那条在全空的 REAR 里还有富余。
        EligibleDispatchCandidate tightOlder = older with { TargetSlots = [3, 4], ExpectedBasketCount = 2, RequiredSlotPosition = "FRONT" };
        EligibleDispatchCandidate roomyNewer = newer with { TargetSlots = [5], ExpectedBasketCount = 1, RequiredSlotPosition = "REAR" };
        EligibleDispatchCandidate roomyOlder = older with { TargetSlots = [5], ExpectedBasketCount = 1, RequiredSlotPosition = "REAR" };
        EligibleDispatchCandidate tightNewer = newer with { TargetSlots = [3, 4], ExpectedBasketCount = 2, RequiredSlotPosition = "FRONT" };

        Assert.Same(tightOlder, ranker.SelectNext([roomyNewer, tightOlder]));
        Assert.Same(roomyOlder, ranker.SelectNext([tightNewer, roomyOlder]));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, int baskets) =>
        new SlotCapacityCriterion(
                new FixedBoxCount(baskets * BoxesPerBasket),
                NullLogger<SlotCapacityCriterion>.Instance,
                new SessionBaselineSlotLedger())
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

    private static async Task<int[]> ChooseAsync(AreaAssignmentPersistenceFixture fixture, string group, int baskets)
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            group, await fixture.SlotPositions.ReadAsync(AgvId, TestContext.Current.CancellationToken), AllEight);
        Assert.Equal(DispatchAdmissionChain.Eligible, await EvaluateAsync(evaluation, baskets));
        return evaluation.TargetSlots;
    }

    private static VehicleSlotPositions ApprovedEightSlot() =>
        Positions("approved", "FRONT", "FRONT", "FRONT", "FRONT", "REAR", "REAR", "REAR", "REAR");

    private static VehicleSlotPositions Positions(string modelId, params string[] groupBySlot) => new(
        AgvId,
        modelId,
        VehicleSlotPositionSource.ActiveSlotConfiguration,
        groupBySlot.Select((group, index) => (Slot: index + 1, Group: group))
            .ToDictionary(pair => pair.Slot, pair => pair.Group));

    private static DispatchCandidateEvaluation Evaluation(string? group, VehicleSlotPositions? positions, int[] available)
    {
        AcceptedDemandSnapshot candidate = new(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            "SERIES-1",
            "WIRE_TO_GATE",
            "SUBLOT-001",
            1,
            Now.AddMinutes(-10),
            Now.AddMinutes(-9),
            "TRACE-1",
            "COMMIT-1",
            new LiveMesFieldSet("N1-3", "EQP-01", "STEP-01", Now, "PDFN5×6-8L(12R)"));
        return new DispatchCandidateEvaluation(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate]),
                new RiotMapStationCatalogSnapshot(25, Now, new string('c', 64), []),
                new SingleStationView(new RiotMapStation(210, "关卡")),
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY")),
            new DispatchVehicleFacts(
                "BROKERX-0001",
                AgvId,
                new OnboardDispatchFacts(1, available, true, true, true, true, false),
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now,
                positions))
        {
            AreaAssignment = group is null ? null : new AreaAssignment("N1-3", Zone, group),
            PackageCapacity = BoxesPerBasket,
        };
    }

    private static EligibleDispatchCandidate Eligible(string demandId, DateTimeOffset firstSeen, long? cost)
    {
        AcceptedDemandSnapshot snapshot = new(
            demandId,
            $"{demandId}|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            $"SERIES-{demandId}",
            "WIRE_TO_GATE",
            $"SUBLOT-{demandId}",
            1,
            firstSeen,
            firstSeen.AddMinutes(1),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet("N1-3", "EQP-01", "STEP-01", Now, "PDFN5×6-8L(12R)"));
        return new EligibleDispatchCandidate(
            snapshot,
            new ResolvedJourneyRoute(Zone, "ROUTE-1", "N1-3", 12, "关卡", 210, FixedTaskStationResolution.Resolved("WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
            1,
            [1],
            firstSeen,
            cost);
    }

    private static async Task<SlotModelVersionRow> PublishModelAsync(
        AreaAssignmentPersistenceFixture fixture,
        string modelKey,
        params string[] groupBySlot)
    {
        SlotTemplateRow template = await fixture.SlotAuthority.PublishTemplateVersionAsync(
            $"{modelKey}-template",
            new SlotTemplateSpecification(600, 400, 300, ["PDFN5"]),
            Now,
            TestContext.Current.CancellationToken);
        return await fixture.SlotAuthority.PublishModelVersionAsync(
            modelKey,
            [
                .. groupBySlot.Select((group, index) => new SlotModelSlotSpecification(
                    index + 1, group, template.TemplateKey, template.Version))
            ],
            Now,
            TestContext.Current.CancellationToken);
    }

    private static Task<IReadOnlyList<SlotIoBindingRow>> BindAsync(
        AreaAssignmentPersistenceFixture fixture,
        SlotModelVersionRow model,
        DateTimeOffset boundAt) =>
        fixture.SlotAuthority.PublishIoBindingsAsync(
            AgvId,
            model.SlotModelVersionId,
            [.. ApprovedSlotHardwareFacts.IoBindings.Take(model.SlotCount)],
            boundAt,
            TestContext.Current.CancellationToken);

    /// <summary>直接写生效配置行；真实路径是与车载端的激活握手，那不是这里要证的。</summary>
    private static async Task ActivateAsync(AreaAssignmentPersistenceFixture fixture, SlotModelVersionRow model)
    {
        fixture.Context.Set<ActiveSlotConfigurationRow>().Add(new ActiveSlotConfigurationRow
        {
            AgvId = AgvId,
            SlotModelVersionId = model.SlotModelVersionId,
            ConfigurationVersion = 1,
            Fingerprint = $"fingerprint-{AgvId}",
            ActivatedAt = Now,
            ActivationId = $"activation-{AgvId}",
            SnapshotId = $"snapshot-{AgvId}"
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }

    private sealed class FixedBoxCount(int boxes) : ISublotBoxCountReader
    {
        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(boxes);
    }
}
