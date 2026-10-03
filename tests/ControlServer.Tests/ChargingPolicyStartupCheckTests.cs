using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static ControlServer.Tests.ChargingGovernanceHarness;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// 启动时的阈值关系与救命线校验（批次9-05，control-server#403；REQ-0281）：生效的策略版本、以及在途旅程冻结的版本，不满足
/// <c>ChargingCompletionThreshold &gt; MandatoryChargeEntryThreshold &gt;= 最低任务后电量余量</c>，或救命线不低于它的入口线，就拒绝启动，
/// 报错写明版本号、三个值与哪一条不成立。一版都没有照常启动。关系只有 <see cref="ChargingPolicyRules.ThresholdRelationViolations"/> 一份。
/// </summary>
/// <remarks>
/// 导入会挡住坏版本（control-server#400 的 <c>EachThresholdRelationViolationRejectsTheWholeFile</c>），所以这里的坏版本绕过导入、直接经
/// 存储写进库，再批准、激活——这正是启动校验要兜住的那一种：不是经导入进来的版本，或规则在版本写下之后收紧了。
/// </remarks>
public sealed class ChargingPolicyStartupCheckTests
{
    /// <summary>满足关系的生效版本：起来。等号那一边（入口线 = 余量）是合法的。</summary>
    [Theory]
    [InlineData(80, 30, 30)]
    [InlineData(31, 30, 20)]
    public async Task AnActiveVersionThatKeepsTheRelationStarts(int completion, int entry, int margin)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await WriteActiveVersionAsync(harness, completion, entry, margin);

        Assert.Null(await Record.ExceptionAsync(() => EnsureAsync(harness, rescue: 15)));
    }

    /// <summary>
    /// 等号（完成线 = 入口线；入口线 &lt; 余量）与倒置各一条：拒绝启动，报错含原因码、版本号、三个值与关系那一句。
    /// </summary>
    [Theory]
    [InlineData(30, 30, 30, "ChargingCompletionThreshold 30 must be greater than MandatoryChargeEntryThreshold 30")]
    [InlineData(80, 29, 30, "MandatoryChargeEntryThreshold 29 must be at least the minimum post-task battery margin 30")]
    [InlineData(20, 30, 80, "ChargingCompletionThreshold 20 must be greater than MandatoryChargeEntryThreshold 30")]
    public async Task AnActiveVersionThatBreaksTheRelationRefusesToStart(int completion, int entry, int margin, string relation)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        long version = await WriteActiveVersionAsync(harness, completion, entry, margin);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => EnsureAsync(harness, rescue: 15));

        Assert.StartsWith(ChargingPolicyStartupCheck.ReasonCode, refused.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"charging policy version {version} (active): ChargingCompletionThreshold {completion}, MandatoryChargeEntryThreshold {entry}, " +
            $"minimum post-task battery margin {margin}",
            refused.Message,
            StringComparison.Ordinal);
        Assert.Contains(relation, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>没有任何策略版本不是拒绝启动的理由：那是逐车不投运（control-server#400）。</summary>
    [Fact]
    public async Task NoVersionAtAllStartsNormally()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        Assert.Null(await Record.ExceptionAsync(() => EnsureAsync(harness, rescue: 15)));
    }

    /// <summary>
    /// 救命线的关系不因旧选项退场而静默失效：此前是「救命线低于 MinimumBatteryPercent」，现在是「低于每个生效版本的入口线」。
    /// 救命线 15、入口线 15 拒绝；入口线 16 起来。
    /// </summary>
    [Theory]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public async Task TheRescueLineMustStayBelowTheMandatoryChargeEntryThresholdOfTheActiveVersion(int entry, bool refused)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        long version = await WriteActiveVersionAsync(harness, completion: 80, entry: entry, margin: 10);

        Exception? error = await Record.ExceptionAsync(() => EnsureAsync(harness, rescue: 15));

        if (!refused)
        {
            Assert.Null(error);
            return;
        }
        InvalidOperationException refusal = Assert.IsType<InvalidOperationException>(error);
        Assert.StartsWith(ChargingPolicyStartupCheck.RescueLineReasonCode, refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"charging policy version {version} (active)", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"JourneyRuntime:WaitingJourneyRescueBatteryPercent 15 must be below MandatoryChargeEntryThreshold {entry}", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在途旅程冻结的版本同样算生效（REQ-0282：它一直被读回，直到旅程结束）：现在激活的版本是好的，而一趟没完成的旅程记着一个坏版本，
    /// 照样拒绝，报错点名那一趟旅程；旅程完成了就不再算。
    /// </summary>
    [Theory]
    [InlineData(JourneyRuntimeStage.AwaitingGateArrival, true)]
    [InlineData(JourneyRuntimeStage.Completed, false)]
    public async Task AVersionFrozenOnAJourneyStillUnderWayCountsAsInEffect(JourneyRuntimeStage stage, bool refused)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        long bad = await WriteActiveVersionAsync(harness, completion: 30, entry: 30, margin: 30);
        await WriteActiveVersionAsync(harness, completion: 80, entry: 30, margin: 30);
        await using (ControlServerDbContext context = harness.Open())
        {
            context.JourneyRuntimes.Add(Journey("D-FROZEN", stage, bad));
            await context.SaveChangesAsync(Token);
        }

        Exception? error = await Record.ExceptionAsync(() => EnsureAsync(harness, rescue: 15));

        if (!refused)
        {
            Assert.Null(error);
            return;
        }
        InvalidOperationException refusal = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains($"charging policy version {bad} (frozen on journey {JourneyIdentity.ForAnchorDemand("D-FROZEN")})", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>判定函数本身：两个版本各违例一处时两处都报，按版本号排；满足时为空。</summary>
    [Fact]
    public void TheJudgementListsEveryVersionThatBreaksARuleInVersionOrder()
    {
        ChargingPolicyVersion Version(long number, int completion, int entry, int margin) =>
            new(number, "sha", null, Now, null, TestChargingPolicies.Content with
            {
                ChargingCompletionThresholdPercent = completion,
                MandatoryChargeEntryThresholdPercent = entry,
                MinimumPostTaskBatteryMarginPercent = margin,
            });

        Assert.Null(ChargingPolicyStartupCheck.Judge([(Version(1, 80, 30, 30), "active")], 15));
        string? detail = ChargingPolicyStartupCheck.Judge(
            [(Version(4, 80, 20, 30), "frozen on journey J"), (Version(2, 30, 30, 30), "active")], 15);

        Assert.NotNull(detail);
        int two = detail.IndexOf("version 2 (active)", StringComparison.Ordinal);
        int four = detail.IndexOf("version 4 (frozen on journey J)", StringComparison.Ordinal);
        Assert.True(two >= 0 && four > two, detail);
    }

    /// <summary>一趟旅程行，只为它记下的策略版本号（形状照 <c>DemandAreaAssignmentFreezeTests.Runtime</c>）。</summary>
    private static JourneyRuntimeRow Journey(string demandId, JourneyRuntimeStage stage, long chargingPolicyVersion) => new()
    {
        JourneyId = JourneyIdentity.ForAnchorDemand(demandId),
        DemandId = demandId,
        Stage = stage,
        AgvId = "AGV-01",
        VehicleKey = VehicleA,
        AgvLifecycleGeneration = 1,
        MapId = Map,
        MapIdentity = "MAP-26",
        DispatchZone = "MAP-26-WIRE_TO_GATE",
        RouteEvidenceId = "ROUTE-01",
        PickupStationId = "PICKUP",
        PickupStationRiotId = 11,
        GateStationId = "GATE",
        GateStationRiotId = 22,
        ExpectedBasketCount = 2,
        TargetSlotsJson = "[1,2]",
        OperationSessionId = $"session-{demandId}",
        PickupMovementLegId = $"pickup-leg-{demandId}",
        PickupUpperId = $"UPPER-PICKUP-{demandId}",
        GateMovementLegId = $"gate-leg-{demandId}",
        GateUpperId = $"UPPER-GATE-{demandId}",
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = $"vb-{demandId}",
        WorklistMessageId = $"wl-{demandId}",
        PlanMessageId = $"plan-{demandId}",
        SublotRequestMessageId = $"sublot-{demandId}",
        LoadCommandMessageId = $"load-{demandId}",
        LoadSlotOperationAttemptId = $"load-attempt-{demandId}",
        PreDepartureSafetyCheckMessageId = $"safety-msg-{demandId}",
        PreDepartureSafetyCheckId = $"safety-{demandId}",
        GateVehicleBusinessMessageId = $"gate-vb-{demandId}",
        GateWorklistMessageId = $"gate-wl-{demandId}",
        GatePlanMessageId = $"gate-plan-{demandId}",
        UnloadCommandMessageId = $"unload-{demandId}",
        UnloadSlotOperationAttemptId = $"unload-attempt-{demandId}",
        ChargingPolicyVersion = chargingPolicyVersion,
        CreatedAt = Now.AddMinutes(-8),
        UpdatedAt = Now
    };

    private static Task EnsureAsync(WaitingPointImportHarness harness, int rescue)
    {
        ControlServerDbContext context = harness.Open();
        return EnsureAndDisposeAsync();

        async Task EnsureAndDisposeAsync()
        {
            await using (context)
            {
                GovernanceStore governance = Governance(context);
                GovernedConfigurationPublisher publisher = new(governance, governance);
                await ChargingPolicyStartupCheck.EnsureAsync(
                    context,
                    new ChargingPolicyStore(context, publisher),
                    new ChargingGovernanceFacts(context, new TaskTypeStationBindingStore(context, publisher)),
                    new JourneyRuntimeOptions { WaitingJourneyRescueBatteryPercent = rescue },
                    NullLogger.Instance,
                    Token);
            }
        }
    }

    /// <summary>
    /// 绕过导入的校验，直接经存储写一版、批准、激活（每步一个写事务，像 FieldOps 那样）。返回版本号。
    /// </summary>
    private static async Task<long> WriteActiveVersionAsync(WaitingPointImportHarness harness, int completion, int entry, int margin)
    {
        await using ControlServerDbContext context = harness.Open();
        ChargingPolicyStore store = PolicyStore(context);
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        ChargingPolicyVersion written = await store.WriteVersionAsync(
            TestChargingPolicies.Content with
            {
                ChargingCompletionThresholdPercent = completion,
                MandatoryChargeEntryThresholdPercent = entry,
                MinimumPostTaskBatteryMarginPercent = margin,
            },
            "written around the import",
            Now,
            Token);
        await store.ApproveAsync(written.Version, "test", "TEST", Now, "test", ChargingPolicyApprovalSources.TestFixture, Token);
        await store.ActivateAsync(written.Version, "test", Now, Token);
        await transaction.CommitAsync(Token);
        return written.Version;
    }
}
