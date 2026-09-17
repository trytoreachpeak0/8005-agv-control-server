using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 结构性派车阻断（control-server#74，REQ-0210、REQ-0352）：轮末跨车汇总，把「换哪辆车、等多久都不会通过」的需求与
/// 正常积压分开，立即告警、按需求与原因去重、原因消失即清除。
/// </summary>
public sealed class StructuralDispatchBlockTests
{
    private const string AgvA = "AGV-01";
    private const string AgvB = "AGV-02";
    private const string Zone = "MAP-25-WIRE_TO_GATE";
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private const int BoxesPerBasket = 4;
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 6, 0, 0, TimeSpan.Zero);
    private static readonly int[] AllEight = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly string[] SixFrontTwoRear = ["FRONT", "FRONT", "FRONT", "FRONT", "FRONT", "FRONT", "REAR", "REAR"];

    // ---- 分类表 ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("DEMAND_ALREADY_ACCEPTED", DispatchReasonClass.Backlog)]
    [InlineData("VEHICLE_FAULT_SUSPECTED_BLOCK", DispatchReasonClass.Backlog)]
    [InlineData("VEHICLE_FAULT_ISOLATED", DispatchReasonClass.Backlog)]
    [InlineData("VEHICLE_FAULT_IDENTITY_UNRESOLVED", DispatchReasonClass.Backlog)]
    [InlineData("OUT_OF_SCOPE_WORK_TYPE", DispatchReasonClass.Backlog)]
    [InlineData("VEHICLE_NOT_IN_DISPATCH_POLICY", DispatchReasonClass.Backlog)]
    [InlineData("VEHICLE_TASK_TYPE_NOT_ADMITTED", DispatchReasonClass.Backlog)]
    [InlineData("REQUIRED_MES_FACT_MISSING", DispatchReasonClass.Backlog)]
    [InlineData("OUT_OF_SCOPE_AREA", DispatchReasonClass.Silent)]
    [InlineData("AREA_EQP_NOT_UNIQUE", DispatchReasonClass.Backlog)]
    [InlineData("CATALOG_PARAMETERS_NOT_APPROVED", DispatchReasonClass.Backlog)]
    [InlineData("CATALOG_NEVER_CONFIRMED", DispatchReasonClass.Backlog)]
    [InlineData("CATALOG_FRESHNESS_EXCEEDED", DispatchReasonClass.Backlog)]
    [InlineData("CATALOG_BUILD_INCOMPATIBLE", DispatchReasonClass.Backlog)]
    [InlineData("AREA_STATION_NOT_FOUND", DispatchReasonClass.Structural)]
    [InlineData("AREA_STATION_NOT_UNIQUE", DispatchReasonClass.Structural)]
    [InlineData("DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING", DispatchReasonClass.Structural)]
    [InlineData("ROUTE_EVIDENCE_MISSING", DispatchReasonClass.Backlog)]
    [InlineData("DISPATCH_ZONE_HAS_NO_VEHICLES", DispatchReasonClass.Structural)]
    [InlineData("VEHICLE_NOT_ADMITTED_IN_ZONE", DispatchReasonClass.Backlog)]
    [InlineData("PACKAGE_CAPACITY_NOT_UNIQUE", DispatchReasonClass.Backlog)]
    [InlineData("ONBOARD_FACTS_NOT_READY", DispatchReasonClass.Backlog)]
    [InlineData("ONBOARD_DEPARTURE_UNSAFE", DispatchReasonClass.Backlog)]
    [InlineData("RIOT_VEHICLE_NOT_AVAILABLE", DispatchReasonClass.Backlog)]
    [InlineData("RIOT_VEHICLE_BINDING_MISMATCH", DispatchReasonClass.Backlog)]
    [InlineData("RIOT_VEHICLE_NOT_IDLE", DispatchReasonClass.Backlog)]
    [InlineData("RIOT_VEHICLE_MAP_MISMATCH", DispatchReasonClass.Backlog)]
    [InlineData("RIOT_VEHICLE_FACT_STALE", DispatchReasonClass.Backlog)]
    [InlineData("BATTERY_FACT_UNKNOWN", DispatchReasonClass.Backlog)]
    [InlineData("BATTERY_POLICY_NOT_SATISFIED", DispatchReasonClass.Backlog)]
    [InlineData("RIOT_VEHICLE_NOT_STOPPED", DispatchReasonClass.Backlog)]
    [InlineData("RIOT_VEHICLE_ORDER_OCCUPIED", DispatchReasonClass.Backlog)]
    [InlineData("TASK_TYPE_NOT_ALLOWED_AT_STATION", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_NEVER_REFRESHED", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_DESIGN_STATE_EXPIRED", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_RUNTIME_STATE_EXPIRED", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_REFRESH_FAILED", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_VEHICLE_POSITION_UNKNOWN", DispatchReasonClass.Backlog)]
    [InlineData("ROUTE_GRAPH_PICKUP_UNREACHABLE", DispatchReasonClass.StructuralWhenEveryVehicleReturnsIt)]
    [InlineData("CREATE_GATE_STATION_UNREACHABLE", DispatchReasonClass.Backlog)]
    [InlineData("CREATE_GATE_ROUTE_COST_UNAVAILABLE", DispatchReasonClass.Backlog)]
    [InlineData("CREATE_GATE_EVIDENCE_CONFLICT", DispatchReasonClass.Backlog)]
    [InlineData("CREATE_GATE_FROZEN_STATION_ABSENT", DispatchReasonClass.Backlog)]
    [InlineData("SUBLOT_BOX_COUNT_UNAVAILABLE", DispatchReasonClass.Backlog)]
    [InlineData("EXPECTED_BASKET_COUNT_OUT_OF_RANGE", DispatchReasonClass.StructuralWhenNoVehicleHasTheSlots)]
    [InlineData("AREA_SLOT_GROUP_NOT_ASSIGNED", DispatchReasonClass.Backlog)]
    [InlineData("VEHICLE_SLOT_MODEL_UNRESOLVED", DispatchReasonClass.Backlog)]
    [InlineData("EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP", DispatchReasonClass.StructuralWhenNoVehicleHasTheSlots)]
    [InlineData("SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE", DispatchReasonClass.Backlog)]
    [InlineData("FINAL_DYNAMIC_FACTS_NOT_READY", DispatchReasonClass.Backlog)]
    [InlineData("FINAL_CATALOG_CANDIDATE_GONE", DispatchReasonClass.Backlog)]
    [InlineData("FINAL_CATALOG_DECISION_FACT_CHANGED", DispatchReasonClass.Backlog)]
    [InlineData("DEMAND_DECISION_FACT_CHANGED", DispatchReasonClass.Backlog)]
    [InlineData("DEMAND_LEFT_CATALOG", DispatchReasonClass.Backlog)]
    public void EveryReasonCodeHasItsClassAndARationale(string reasonCode, DispatchReasonClass expected)
    {
        DispatchReasonClassification row = Assert.Contains(reasonCode, StructuralDispatchClassification.ByCode);

        Assert.Equal(expected, row.Class);
        Assert.Equal(expected, StructuralDispatchClassification.ClassOf(reasonCode));
        Assert.False(string.IsNullOrWhiteSpace(row.Rationale));
    }

    /// <summary>
    /// 上面的逐码清单与分类表一一对应，而分类表覆盖派车链现有的全部原因码：判据源码里的码字面量、各原因码常量类、
    /// 引擎在判据链之后写进积压的码。新加一个码而不登记分类，这里会红。
    /// </summary>
    [Fact]
    public void TheTableCoversEveryReasonCodeTheChainAndTheIntakeBehindItWrite()
    {
        string root = FindRepositoryRoot();
        string runtime = Path.Combine(root, "src", "ControlServer.Host", "Runtime");
        Regex code = new("\"([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)\"");
        HashSet<string> notReasonCodes = new(StringComparer.Ordinal) { "WIRE_TO_GATE" };

        HashSet<string> written = new(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(Path.Combine(runtime, "Dispatch", "Criteria"), "*.cs"))
        {
            written.UnionWith(code.Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value));
        }
        // ResolveUniquePickup's two codes; FIXED_STATION_BINDING_INVALID is the gate station, resolved before any
        // round starts, and never a candidate's verdict.
        written.UnionWith(code.Matches(File.ReadAllText(Path.Combine(runtime, "MapStationResolver.cs")))
            .Select(match => match.Groups[1].Value)
            .Where(value => value != "FIXED_STATION_BINDING_INVALID"));
        written.UnionWith(Regex.Matches(
                File.ReadAllText(Path.Combine(runtime, "JourneyRuntimeEngine.cs")),
                "\"((?:FINAL|DEMAND)_[A-Z_]+)\"")
            .Select(match => match.Groups[1].Value));
        foreach (Type constants in new[]
                 {
                     typeof(DispatchReasonCodes), typeof(RouteGraphStaleReasons),
                     typeof(CatalogAvailabilityReasons), typeof(CreateGateReasons),
                 })
        {
            written.UnionWith(constants.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!));
        }
        written.ExceptWith(notReasonCodes);

        string[] listed =
        [
            .. typeof(StructuralDispatchBlockTests)
                .GetMethod(nameof(EveryReasonCodeHasItsClassAndARationale))!
                .GetCustomAttributes<InlineDataAttribute>()
                .Select(data => (string)data.Data[0]!)
                .Order(StringComparer.Ordinal)
        ];
        Assert.Equal(written.Order(StringComparer.Ordinal), StructuralDispatchClassification.ByCode.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(listed, StructuralDispatchClassification.ByCode.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 分类表记的判据顺序与判据本身的 <c>Order</c> 一致：「清除要有证据」靠这个数判断链走到了哪一步。
    /// </summary>
    [Fact]
    public void EveryChainOrderInTheTableIsTheOrderOfARealCriterion()
    {
        HashSet<int> orders =
        [
            .. typeof(IDispatchAdmissionCriterion).Assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } &&
                    typeof(IDispatchAdmissionCriterion).IsAssignableFrom(type))
                .Select(type => ((IDispatchAdmissionCriterion)RuntimeHelpers.GetUninitializedObject(type)).Order)
        ];

        Assert.All(
            StructuralDispatchClassification.ByCode.Values.Where(row => row.ChainOrder is not null),
            row => Assert.Contains(row.ChainOrder!.Value, orders));
        Assert.Equal(60, StructuralDispatchClassification.ByCode["AREA_STATION_NOT_FOUND"].ChainOrder);
        Assert.Equal(100, StructuralDispatchClassification.ByCode["EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP"].ChainOrder);
    }

    [Fact]
    public void EverySilentCodeIsClassifiedSilentAndAnUnlistedCodeIsOrdinaryBacklog()
    {
        Assert.All(DispatchReasonCodes.Silent, reasonCode =>
            Assert.Equal(DispatchReasonClass.Silent, StructuralDispatchClassification.ClassOf(reasonCode)));
        Assert.Equal(DispatchReasonClass.Backlog, StructuralDispatchClassification.ClassOf("SOME_CODE_NOBODY_REGISTERED"));
    }

    /// <summary>主机注册的轮末钩子是本票的汇总；四个批次 4 端口仍只由 GovernanceModule 注册，这里不重复注册。</summary>
    [Fact]
    public void TheHostRegistersTheStructuralSummaryAsTheRoundEndHookAndNothingElseOfBatch4()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        ServiceDescriptor hook = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDispatchRoundOutcomeSink));
        Assert.Equal(typeof(StructuralDispatchBlockSink), hook.ImplementationType);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IStructuralDispatchBlockStore) ||
            descriptor.ServiceType == typeof(IVehicleSlotPositionReader));
    }

    // ---- 超大需求 -----------------------------------------------------------------------------------

    /// <summary>
    /// 两台车都是已批准八仓（每组 4 个物理仓），一条 FRONT 需求要 5 个花篮：换哪辆车都装不下，立即形成一条阻断、写一条
    /// Warning，明细带花篮数、分组和全车队该组最大物理仓位数。
    /// </summary>
    [Fact]
    public async Task ADemandLargerThanItsGroupOnEveryVehicleRaisesOneBlockAndOneWarning()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        await harness.RecordAsync(round,
            await harness.SlotVerdictAsync(round, AgvA, "FRONT", baskets: 5, AllEight),
            await harness.SlotVerdictAsync(round, AgvB, "FRONT", baskets: 5, AllEight));

        StructuralDispatchBlock block = Assert.Single(await harness.UnclearedAsync());
        Assert.Equal(DemandId, block.DemandId);
        Assert.Equal(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, block.ReasonCode);
        Assert.Equal(Now, block.FirstRaisedAt);
        using JsonDocument detail = JsonDocument.Parse(block.DetailJson);
        Assert.Equal(5, detail.RootElement.GetProperty("expectedBasketCount").GetInt32());
        Assert.Equal("FRONT", detail.RootElement.GetProperty("slotPosition").GetString());
        Assert.Equal(4, detail.RootElement.GetProperty("largestPhysicalSlotCount").GetInt32());
        (LogLevel level, string message) = Assert.Single(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(DemandId, message, StringComparison.Ordinal);
        Assert.Contains(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 持续成立：只有一行、首次形成时间不变、最近仍成立时间前进，刷新不再写 Warning；需求离开目录后清除。
    /// </summary>
    [Fact]
    public async Task WhileItHoldsTheBlockStaysOneRowThatOnlyMovesLastSeenAndItClearsWhenTheDemandLeavesTheCatalog()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        foreach (int round in new[] { 0, 1, 2 })
        {
            DispatchRoundFacts facts = Round(Now.AddSeconds(round * 5), Candidate());
            await harness.RecordAsync(facts,
                await harness.SlotVerdictAsync(facts, AgvA, "FRONT", baskets: 6, AllEight),
                await harness.SlotVerdictAsync(facts, AgvB, "FRONT", baskets: 6, AllEight));
        }

        StructuralDispatchBlock held = Assert.Single(await harness.UnclearedAsync());
        Assert.Equal(Now, held.FirstRaisedAt);
        Assert.Equal(Now.AddSeconds(10), held.LastSeenAt);
        Assert.Single(await harness.AllRowsAsync());
        Assert.Single(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);

        await harness.RecordAsync(Round(Now.AddSeconds(15)));

        Assert.Empty(await harness.UnclearedAsync());
        StructuralDispatchBlock cleared = Assert.Single(await harness.AllRowsAsync());
        Assert.Equal(Now.AddSeconds(15), cleared.ClearedAt);
        Assert.Single(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ABlockClearsOnceTheDemandIsAccepted()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA]);
        DispatchRoundFacts first = Round(Now, Candidate());
        await harness.RecordAsync(first, await harness.SlotVerdictAsync(first, AgvA, "FRONT", baskets: 5, AllEight));
        Assert.Single(await harness.UnclearedAsync());

        await harness.RecordAsync(Round(Now.AddSeconds(5), [DemandId], Candidate()));

        Assert.Empty(await harness.UnclearedAsync());
    }

    /// <summary>
    /// 原因消失：MES 把盒数改小后需求放得进 FRONT 组，这一轮就清除；之后又变大，重新形成一段，再写一条 Warning。
    /// </summary>
    [Fact]
    public async Task ABlockClearsWhenTheReasonGoesAndARecurrenceIsRaisedAsNewAgain()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA]);
        DispatchRoundFacts first = Round(Now, Candidate());
        await harness.RecordAsync(first, await harness.SlotVerdictAsync(first, AgvA, "FRONT", baskets: 5, AllEight));

        DispatchRoundFacts second = Round(Now.AddSeconds(5), Candidate());
        await harness.RecordAsync(second, await harness.SlotVerdictAsync(second, AgvA, "FRONT", baskets: 4, [5, 6, 7, 8]));

        Assert.Empty(await harness.UnclearedAsync());
        Assert.Equal(Now.AddSeconds(5), Assert.Single(await harness.AllRowsAsync()).ClearedAt);

        DispatchRoundFacts third = Round(Now.AddSeconds(10), Candidate());
        await harness.RecordAsync(third, await harness.SlotVerdictAsync(third, AgvA, "FRONT", baskets: 5, AllEight));

        Assert.Equal(Now.AddSeconds(10), Assert.Single(await harness.UnclearedAsync()).FirstRaisedAt);
        Assert.Equal(2, harness.Log.Entries.Count(entry => entry.Level >= LogLevel.Warning));
    }

    // ---- 正常积压不形成阻断 ------------------------------------------------------------------------

    /// <summary>
    /// 同图 AGV-01 是 4+4、AGV-02 是 6 FRONT + 2 REAR：5 花篮的 FRONT 需求在 AGV-01 上是「超过本车分组物理仓位数」，
    /// 但 AGV-02 装得下，所以只是正常积压。
    /// </summary>
    [Fact]
    public async Task OneVehicleWhoseGroupIsTooSmallIsNotStructuralWhileAnotherOnTheMapHasTheSlots()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA], sixFront: [AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        DispatchCandidateVerdict onA = await harness.SlotVerdictAsync(round, AgvA, "FRONT", baskets: 5, AllEight);
        DispatchCandidateVerdict onB = await harness.SlotVerdictAsync(round, AgvB, "FRONT", baskets: 5, [7, 8]);
        await harness.RecordAsync(round, onA, onB);

        Assert.Equal(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, onA.ReasonCode);
        Assert.Equal(DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, onB.ReasonCode);
        Assert.Empty(await harness.AllRowsAsync());
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// 装得下的那台车本轮不在（忙着别的旅程，或预算超时），也不算结构性：车队的物理仓位数取服务端车型，不取本轮判定。
    /// </summary>
    [Fact]
    public async Task TheVehicleThatHasTheSlotsCountsEvenWhenItWasNotInTheRound()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA], sixFront: [AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        await harness.RecordAsync(round, await harness.SlotVerdictAsync(round, AgvA, "FRONT", baskets: 5, AllEight));

        Assert.Empty(await harness.AllRowsAsync());
    }

    /// <summary>
    /// 两台车的 FRONT 组都有 4 个物理仓，但 1、2 号禁用、3 号占用：3 花篮的需求这一轮装不进去，那是「所需分组暂时空仓
    /// 不足」，不是结构性阻断（REQ-0352：仓位临时禁用造成的不足不属于此情形）。
    /// </summary>
    [Fact]
    public async Task SlotsThatAreDisabledOrOccupiedNeverMakeADemandStructural()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        DispatchCandidateVerdict onA = await harness.SlotVerdictAsync(round, AgvA, "FRONT", baskets: 3, [4, 5, 6, 7, 8]);
        DispatchCandidateVerdict onB = await harness.SlotVerdictAsync(round, AgvB, "FRONT", baskets: 4, [5, 6, 7, 8]);
        await harness.RecordAsync(round, onA, onB);

        Assert.Equal(DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, onA.ReasonCode);
        Assert.Equal(DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, onB.ReasonCode);
        Assert.Empty(await harness.AllRowsAsync());
    }

    /// <summary>车队里有一台车查不到车型：它可能正是装得下的那台，所以不下结论。</summary>
    [Fact]
    public async Task AVehicleWithNoSlotModelOnRecordLeavesTheFleetCheckUndecided()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA], unresolved: [AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        await harness.RecordAsync(round, await harness.SlotVerdictAsync(round, AgvA, "FRONT", baskets: 5, AllEight));

        Assert.Empty(await harness.AllRowsAsync());
    }

    // ---- 静默原因码 --------------------------------------------------------------------------------

    [Fact]
    public async Task AnOutOfScopeAreaNeverRaisesABlockOrAWarning()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        await harness.RecordAsync(round,
            Harness.Verdict(round, AgvA, DispatchReasonCodes.OutOfScopeArea),
            Harness.Verdict(round, AgvB, DispatchReasonCodes.OutOfScopeArea));

        Assert.Empty(await harness.AllRowsAsync());
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// 需求原先因站点缺失阻断，之后导入的归属表不再映射它的 AREA：它变成静默跳过，告警随之清除，不留一条永远挂着的告警。
    /// </summary>
    [Fact]
    public async Task ADemandThatTurnsOutOfScopeClearsItsBlock()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA]);
        DispatchRoundFacts first = Round(Now, Candidate());
        await harness.RecordAsync(first, Harness.Verdict(first, AgvA, "AREA_STATION_NOT_FOUND"));
        Assert.Single(await harness.UnclearedAsync());

        DispatchRoundFacts second = Round(Now.AddSeconds(5), Candidate());
        await harness.RecordAsync(second, Harness.Verdict(second, AgvA, DispatchReasonCodes.OutOfScopeArea));

        Assert.Empty(await harness.UnclearedAsync());
    }

    // ---- 与车无关的结构性原因码 --------------------------------------------------------------------

    /// <summary>
    /// 站点缺失与车无关，一台车走到站点解析就够了。下一轮唯一的车被故障隔离、链在第 15 步就停了，这一轮证明不了原因
    /// 消失，阻断原样保留（不刷新也不清除）；再下一轮链走过站点解析，才清除。
    /// </summary>
    [Fact]
    public async Task AVehicleIndependentReasonHoldsOnOneVerdictAndOnlyClearsOnProofItIsGone()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        DispatchRoundFacts first = Round(Now, Candidate());
        await harness.RecordAsync(first,
            Harness.Verdict(first, AgvA, VehicleFaultBlockCriterion.IsolatedReason),
            Harness.Verdict(first, AgvB, "AREA_STATION_NOT_FOUND"));
        Assert.Equal("AREA_STATION_NOT_FOUND", Assert.Single(await harness.UnclearedAsync()).ReasonCode);

        DispatchRoundFacts second = Round(Now.AddSeconds(5), Candidate());
        await harness.RecordAsync(second,
            Harness.Verdict(second, AgvA, VehicleFaultBlockCriterion.IsolatedReason),
            Harness.Verdict(second, AgvB, VehicleFaultBlockCriterion.SuspectedReason));

        StructuralDispatchBlock untouched = Assert.Single(await harness.UnclearedAsync());
        Assert.Equal(Now, untouched.LastSeenAt);

        DispatchRoundFacts third = Round(Now.AddSeconds(10), Candidate());
        await harness.RecordAsync(third,
            Harness.Verdict(third, AgvA, VehicleFaultBlockCriterion.IsolatedReason),
            Harness.Verdict(third, AgvB, "RIOT_VEHICLE_NOT_IDLE"));

        Assert.Empty(await harness.UnclearedAsync());
        Assert.Single(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData("AREA_STATION_NOT_UNIQUE")]
    [InlineData("DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING")]
    [InlineData("DISPATCH_ZONE_HAS_NO_VEHICLES")]
    public async Task EachVehicleIndependentReasonRaisesItsOwnBlock(string reasonCode)
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA]);
        DispatchRoundFacts round = Round(Now, Candidate());

        await harness.RecordAsync(round, Harness.Verdict(round, AgvA, reasonCode));

        Assert.Equal(reasonCode, Assert.Single(await harness.UnclearedAsync()).ReasonCode);
    }

    // ---- 全部车辆不可达与单车预算超时 --------------------------------------------------------------

    [Fact]
    public async Task EveryVehicleOnTheRosterFindingThePickupUnreachableIsStructural()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        await harness.RecordAsync(round,
            Harness.Verdict(round, AgvA, "ROUTE_GRAPH_PICKUP_UNREACHABLE"),
            Harness.Verdict(round, AgvB, "ROUTE_GRAPH_PICKUP_UNREACHABLE"));

        Assert.Equal("ROUTE_GRAPH_PICKUP_UNREACHABLE", Assert.Single(await harness.UnclearedAsync()).ReasonCode);
    }

    [Fact]
    public async Task OneVehicleFindingThePickupUnreachableWhileAnotherIsMerelyBusyIsBacklog()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        DispatchRoundFacts round = Round(Now, Candidate());

        await harness.RecordAsync(round,
            Harness.Verdict(round, AgvA, "ROUTE_GRAPH_PICKUP_UNREACHABLE"),
            Harness.Verdict(round, AgvB, "RIOT_VEHICLE_NOT_IDLE"));

        Assert.Empty(await harness.AllRowsAsync());
    }

    /// <summary>
    /// AGV-02 本轮预算超时，钩子只拿到 AGV-01 的判定：AGV-01 不可达不能推出「全部车辆不可达」，不形成阻断；已形成的
    /// 阻断也不因缺车被清除。
    /// </summary>
    [Fact]
    public async Task ARoundWhereAVehicleRanOutItsBudgetConcludesOnlyFromTheVehiclesThatFinished()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA, AgvB]);
        DispatchRoundFacts cutOff = Round(Now, Candidate());

        await harness.RecordAsync(cutOff, Harness.Verdict(cutOff, AgvA, "ROUTE_GRAPH_PICKUP_UNREACHABLE"));

        Assert.Empty(await harness.AllRowsAsync());

        DispatchRoundFacts full = Round(Now.AddSeconds(5), Candidate());
        await harness.RecordAsync(full,
            Harness.Verdict(full, AgvA, "ROUTE_GRAPH_PICKUP_UNREACHABLE"),
            Harness.Verdict(full, AgvB, "ROUTE_GRAPH_PICKUP_UNREACHABLE"));
        DispatchRoundFacts cutOffAgain = Round(Now.AddSeconds(10), Candidate());
        await harness.RecordAsync(cutOffAgain, Harness.Verdict(cutOffAgain, AgvB, "ROUTE_GRAPH_PICKUP_UNREACHABLE"));

        StructuralDispatchBlock held = Assert.Single(await harness.UnclearedAsync());
        Assert.Equal(Now.AddSeconds(5), held.LastSeenAt);
    }

    /// <summary>预算超时的车即使本来能装下，车队车型照样把它算进去，超大需求的结论不因缺车而误判。</summary>
    [Fact]
    public async Task ARoundWhereTheVehicleWithTheSlotsRanOutItsBudgetDoesNotMakeTheDemandOversized()
    {
        await using Harness harness = await Harness.CreateAsync(eightSlot: [AgvA], sixFront: [AgvB]);
        DispatchRoundFacts cutOff = Round(Now, Candidate());

        await harness.RecordAsync(cutOff, await harness.SlotVerdictAsync(cutOff, AgvA, "FRONT", baskets: 6, AllEight));

        Assert.Empty(await harness.AllRowsAsync());
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static AcceptedDemandSnapshot Candidate(string demandId = DemandId, string area = "N1-3") => new(
        demandId,
        $"SUBLOT-{demandId[..4]}|WIRE_TO_GATE",
        7,
        "11111111-1111-4111-8111-111111111111",
        21,
        Now,
        "SERIES-1",
        "WIRE_TO_GATE",
        $"SUBLOT-{demandId[..4]}",
        1,
        Now.AddMinutes(-10),
        Now.AddMinutes(-9),
        "TRACE-1",
        "COMMIT-1",
        new LiveMesFieldSet(area, "EQP-01", "STEP-01", Now, "PDFN5×6-8L(12R)"));

    private static DispatchRoundFacts Round(DateTimeOffset now, params AcceptedDemandSnapshot[] catalog) =>
        Round(now, [], catalog);

    private static DispatchRoundFacts Round(
        DateTimeOffset now,
        string[] accepted,
        params AcceptedDemandSnapshot[] catalog) => new(
        new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, catalog),
        new RiotMapStationCatalogSnapshot(25, now, new string('c', 64), []),
        new RiotMapStation(210, "关卡"),
        accepted.ToHashSet(StringComparer.Ordinal),
        now,
        new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"),
        new AreaAssignmentTableVersion(
            1,
            new string('a', 64),
            "SNAPSHOT-1",
            now,
            new Dictionary<string, AreaAssignment>(StringComparer.Ordinal) { ["N1-3"] = new("N1-3", Zone, "FRONT") }));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }

    /// <summary>真库的阻断存储与车型读取，加一份两台车的名册；判定由测试逐车给出。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly AreaAssignmentPersistenceFixture _fixture;
        private readonly StructuralDispatchBlockSink _sink;

        private Harness(AreaAssignmentPersistenceFixture fixture, string[] roster)
        {
            _fixture = fixture;
            JourneyRuntimeOptions options = new()
            {
                Fleet = [.. roster.Select(agvId => new FleetVehicleOptions { AgvId = agvId, VehicleKey = $"KEY-{agvId}" })],
            };
            _sink = new StructuralDispatchBlockSink(
                fixture.Blocks, fixture.SlotPositions, new VehicleRoster(Options.Create(options)), Log);
        }

        public RecordingLogger<StructuralDispatchBlockSink> Log { get; } = new();

        public static async Task<Harness> CreateAsync(
            string[]? eightSlot = null,
            string[]? sixFront = null,
            string[]? unresolved = null)
        {
            AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
            CancellationToken token = TestContext.Current.CancellationToken;
            SlotModelVersionRow approved = await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(Now, token);
            foreach (string agvId in eightSlot ?? [])
            {
                await BindAsync(fixture, agvId, approved);
            }
            if (sixFront is { Length: > 0 })
            {
                SlotTemplateRow template = await fixture.SlotAuthority.PublishTemplateVersionAsync(
                    "six-front-template", new SlotTemplateSpecification(600, 400, 300, ["PDFN5"]), Now, token);
                SlotModelVersionRow model = await fixture.SlotAuthority.PublishModelVersionAsync(
                    "six-front",
                    [
                        .. SixFrontTwoRear
                            .Select((group, index) => new SlotModelSlotSpecification(
                                index + 1, group, template.TemplateKey, template.Version))
                    ],
                    Now,
                    token);
                foreach (string agvId in sixFront)
                {
                    await BindAsync(fixture, agvId, model);
                }
            }
            fixture.Context.ChangeTracker.Clear();
            return new Harness(
                fixture,
                [.. (eightSlot ?? []).Concat(sixFront ?? []).Concat(unresolved ?? [])]);
        }

        public Task RecordAsync(DispatchRoundFacts round, params DispatchCandidateVerdict[] verdicts) =>
            _sink.RecordAsync(
                new DispatchRoundOutcome(
                    round,
                    [
                        .. verdicts
                            .GroupBy(verdict => verdict.Evaluation.Vehicle.AgvId, StringComparer.Ordinal)
                            .Select(vehicle => new DispatchVehicleOutcome(vehicle.Key, $"KEY-{vehicle.Key}", [.. vehicle]))
                    ]),
                TestContext.Current.CancellationToken);

        /// <summary>A verdict whose code the test states, for a candidate that did not reach the slot check.</summary>
        public static DispatchCandidateVerdict Verdict(DispatchRoundFacts round, string agvId, string reasonCode) =>
            new(Evaluation(round, agvId, positions: null, AllEight), reasonCode);

        /// <summary>A verdict the real slot capacity criterion reaches against this vehicle's slot model on record.</summary>
        public async Task<DispatchCandidateVerdict> SlotVerdictAsync(
            DispatchRoundFacts round,
            string agvId,
            string group,
            int baskets,
            int[] available)
        {
            VehicleSlotPositions? positions =
                await _fixture.SlotPositions.ReadAsync(agvId, TestContext.Current.CancellationToken);
            DispatchCandidateEvaluation evaluation = Evaluation(round, agvId, positions, available);
            evaluation.AreaAssignment = new AreaAssignment("N1-3", Zone, group);
            evaluation.PackageCapacity = BoxesPerBasket;
            string reason = await new SlotCapacityCriterion(
                    new FixedBoxCount(baskets * BoxesPerBasket), NullLogger<SlotCapacityCriterion>.Instance)
                .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);
            return new DispatchCandidateVerdict(evaluation, reason);
        }

        public Task<IReadOnlyList<StructuralDispatchBlock>> UnclearedAsync() =>
            _fixture.Blocks.ListUnclearedAsync(TestContext.Current.CancellationToken);

        public Task<StructuralDispatchBlock[]> AllRowsAsync()
        {
            _fixture.Context.ChangeTracker.Clear();
            return Task.FromResult<StructuralDispatchBlock[]>(
            [
                .. _fixture.Context.Set<StructuralDispatchBlockRow>().AsEnumerable().Select(row => new StructuralDispatchBlock(
                    row.DemandId, row.ReasonCode, row.TransportDemandKey, row.FirstRaisedAt, row.LastSeenAt,
                    row.ClearedAt, row.DetailJson))
            ]);
        }

        public ValueTask DisposeAsync() => _fixture.DisposeAsync();

        private static DispatchCandidateEvaluation Evaluation(
            DispatchRoundFacts round,
            string agvId,
            VehicleSlotPositions? positions,
            int[] available) => new(
            round.Catalog.Items[0],
            round,
            new DispatchVehicleFacts(
                $"KEY-{agvId}",
                agvId,
                new OnboardDispatchFacts(1, available, true, true, true, true, false),
                new RiotVehicleObservation($"KEY-{agvId}", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, round.Now),
                round.Now,
                positions));

        private static Task<IReadOnlyList<SlotIoBindingRow>> BindAsync(
            AreaAssignmentPersistenceFixture fixture,
            string agvId,
            SlotModelVersionRow model) =>
            fixture.SlotAuthority.PublishIoBindingsAsync(
                agvId,
                model.SlotModelVersionId,
                [.. ApprovedSlotHardwareFacts.IoBindings.Take(model.SlotCount)],
                Now,
                TestContext.Current.CancellationToken);
    }

    private sealed class FixedBoxCount(int boxes) : ISublotBoxCountReader
    {
        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(boxes);
    }
}
