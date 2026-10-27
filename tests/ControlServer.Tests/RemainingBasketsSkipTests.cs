using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 多篮装货减篮（REQ-0367、修订后的 REQ-0211 与 REQ-0352，CP-0021，control-server#830）：操作员在 N 篮里装进 k 篮（1 ≤ k &lt; N）之后申请
/// 「剩下的不装」，服务端授权或拒绝；授权之后车按 <c>COMPLETED_REMAINING_SKIPPED</c> 报结果，服务端把这条需求按 k 篮提交、释放其余仓位与预留、
/// 卸货只开那 k 个仓，需求按正常送达完成。消息形状见协议 6.0.0 候选 <c>docs/wire-notes.md</c> 的减篮一节。
/// </summary>
/// <remarks>
/// 判定读的是车报来的逐仓读数（<c>SafetyStateSnapshot</c>）。<c>SafetyStateChanged</c> 只说哪几个仓变了、不带状态，所以快照之后有目标仓变过，
/// 读数就算过期：服务端先要一份快照，快照到了再判。
/// </remarks>
public sealed class RemainingBasketsSkipTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private const string Sublot = "SUBLOT-001";
    private const string SkipRequestId = "83000000-0000-4000-8000-000000000001";
    private const string OperatorId = "OP-001";
    private static readonly int[] OneTwo = [1, 2];
    private static readonly int[] OneTwoThree = [1, 2, 3];
    private static readonly int[] OnlyOne = [1];
    private static readonly int[] OnlyFour = [4];
    private static readonly int[] ThreeAndFour = [3, 4];
    private const string SecondSkipRequestId = "83000000-0000-4000-8000-000000000002";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 三篮装进两篮（仓 1、2 有货锁闭复位，仓 3 空着锁闭复位），申请减篮：授权，<c>loadedSlots</c> 就是那两个仓。车报
    /// <c>COMPLETED_REMAINING_SKIPPED</c> 之后，装货按两篮提交、仓 3 释放、需求照常往下走；到卸货站只开仓 1、2，卸完需求按正常送达完成——
    /// 不按取消终结、不抑制、不进人工搬运。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task ThreeBasketsLoadedTwoTheSkipIsAuthorizedSettledAsTwoAndOnlyTheTwoAreUnloaded()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        Assert.Equal(OneTwoThree, rig.TargetSlots);

        await rig.SendSnapshotAsync(Occupied(1, 2));
        string requestAnswer = await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        Assert.Equal("DurableAck", MessageType(requestAnswer.Split('\n')[0]));
        JsonElement result = rig.SingleSkipResult();
        Assert.Equal(SkipRequestId, result.GetProperty("skipRequestId").GetString());
        Assert.Equal(DemandId, result.GetProperty("demandId").GetString());
        Assert.Equal(rig.AttemptId, result.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal("AUTHORIZED", result.GetProperty("outcome").GetString());
        Assert.Equal(OneTwo, Slots(result.GetProperty("loadedSlots")));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("problem").ValueKind);

        string resultAnswer = await rig.SendSkippedResultAsync(loaded: [1, 2], remaining: [3]);
        Assert.Equal("DurableAck", MessageType(resultAnswer.Split('\n')[0]));

        StationOperationRow load = await rig.Fixture.OperationAsync(SlotOperationType.Load);
        Assert.Equal(StationOperationStatus.Committed, load.Status);
        Assert.Equal(OneTwo, Slots(load.TargetSlotsJson));
        Assert.Equal(OneTwo, Slots((await rig.MembershipAsync()).TargetSlotsJson));
        Assert.Equal(DemandExecutionStatus.Accepted, (await rig.Fixture.DemandRowAsync()).Status);
        Assert.Empty(await rig.Fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking().ToArrayAsync(Token));

        await rig.Fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await rig.MembershipAsync()).Status);

        await rig.DepartAndArriveAtTheGateAsync();
        StationOperationRow unload = await rig.Fixture.OperationAsync(SlotOperationType.Unload);
        Assert.Equal(OneTwo, Slots(unload.TargetSlotsJson));
        JsonElement command = await rig.Fixture.OutboxPayloadAsync((await rig.MembershipAsync()).UnloadCommandMessageId);
        Assert.Equal(OneTwo, Slots(command.GetProperty("slots")));
        Assert.Equal(2, command.GetProperty("expectedBasketCount").GetInt32());

        await rig.Fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(DemandExecutionStatus.Succeeded, (await rig.Fixture.DemandRowAsync()).Status);
        Assert.Single(await rig.Fixture.Context.TransportDemandCompletions.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 10-21 agv03 @160 的形状：两篮装进一篮，另一仓开过锁、空着关上并已证明锁闭——授权，结算按一篮、另一仓释放。那天操作员没有这个出口，
    /// 只能按取消装货，服务端授权了有货的取消，旅程卡死（control-server#837 的来由）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task TwoBasketsOneLoadedTheOtherOpenedAndClosedEmptyIsAuthorizedAndSettledAsOne()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 8);
        Assert.Equal(OneTwo, rig.TargetSlots);

        await rig.SendSnapshotAsync(Occupied(1));
        await rig.SendSkipRequestAsync(SkipRequestId, [1]);
        JsonElement result = rig.SingleSkipResult();
        Assert.Equal("AUTHORIZED", result.GetProperty("outcome").GetString());
        Assert.Equal(OnlyOne, Slots(result.GetProperty("loadedSlots")));

        await rig.SendSkippedResultAsync(loaded: [1], remaining: [2]);
        Assert.Equal(StationOperationStatus.Committed, (await rig.Fixture.OperationAsync(SlotOperationType.Load)).Status);
        Assert.Equal(OnlyOne, Slots((await rig.MembershipAsync()).TargetSlotsJson));
    }

    /// <summary>
    /// 释放的那一仓立刻可以分给别的需求（REQ-0352、REQ-0367）：派车与扫码后分仓用的是同一个账本，它扣的是这条需求此刻占着的仓。结算之前
    /// 它占着 1、2、3，前侧只剩 4；结算之后只占 1、2，3 回来了。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task TheSkippedSlotIsFreeForAnotherDemandFromTheSettlement()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        int[] before = [.. await rig.AssignableFrontSlotsAsync()];
        Assert.Equal(OnlyFour, before);

        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        int[] authorizedOnly = [.. await rig.AssignableFrontSlotsAsync()];
        Assert.Equal(OnlyFour, authorizedOnly);

        await rig.SendSkippedResultAsync(loaded: [1, 2], remaining: [3]);
        int[] after = [.. await rig.AssignableFrontSlotsAsync()];
        Assert.Equal(ThreeAndFour, after);
    }

    /// <summary>
    /// 读数过期：快照之后车报了仓 2 变过（关门那一下），服务端手上就没有仓 2 此刻的状态。申请到时不判，先要一份快照；快照到了再判。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task ARequestOverStaleReadingsWaitsForASnapshotAndIsDecidedWhenItComes()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1));
        await rig.SendSafetyChangedAsync(2);

        string answer = await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        Assert.Empty(rig.SkipResults());
        Assert.Contains(answer.Split('\n'), line => MessageType(line) == "SafetyStateSnapshotRequested");
        await rig.SendSnapshotAsync(Occupied(1, 2));
        JsonElement result = rig.SingleSkipResult();
        Assert.Equal("AUTHORIZED", result.GetProperty("outcome").GetString());
        Assert.Equal(OneTwo, Slots(result.GetProperty("loadedSlots")));
    }

    /// <summary>
    /// 安全基线不可信（车报的一条安全变化被拒收）时，此前那份快照不算读数：申请到时不判、要一份快照，快照被接受、基线可信了再判（control-server#837
    /// 审查 S2 下沉到读数本身）。修之前申请到时就按那份旧快照授权了。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task ARequestWhileTheSafetyBaselineIsUntrustedWaitsForASnapshotAndIsDecidedWhenItComes()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1));
        await rig.SendRefusedSafetyChangedAsync();
        rig.Fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval + TimeSpan.FromSeconds(1));

        string answer = await rig.SendSkipRequestAsync(SkipRequestId, [1]);

        Assert.Empty(rig.SkipResults());
        Assert.Contains(answer.Split('\n'), line => MessageType(line) == "SafetyStateSnapshotRequested");
        await rig.SendSnapshotAsync(Occupied(1));
        JsonElement result = rig.SingleSkipResult();
        Assert.Equal("AUTHORIZED", result.GetProperty("outcome").GetString());
        Assert.Equal([1], Slots(result.GetProperty("loadedSlots")));
    }

    /// <summary>
    /// 拒绝的各格（wire-notes 的检查先后），每一格：答 <c>REJECTED</c>、带那个码、<c>loadedSlots</c> 为空，业务状态一样不动——装货照旧在进行。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-REJECTED")]
    [InlineData("door-not-proven-locked", "LOCK_NOT_CLOSED")]
    [InlineData("output-not-reset", "UNLOCK_OUTPUT_NOT_RESET")]
    [InlineData("a-slot-unknown", "SLOT_STATE_UNKNOWN")]
    [InlineData("unknown-ahead-of-unlocked", "SLOT_STATE_UNKNOWN")]
    [InlineData("loaded-slots-disagree", "SLOT_SET_INVALID")]
    [InlineData("every-slot-loaded", "SLOT_SET_INVALID")]
    [InlineData("old-attempt", "DEMAND_NOT_CURRENT")]
    [InlineData("other-demand", "DEMAND_NOT_CURRENT")]
    [InlineData("other-operation-session", "OPERATION_SESSION_MISMATCH")]
    [InlineData("exception-recovery-open", "ACTION_NOT_ALLOWED_IN_STATE")]
    [InlineData("slot-fault-declared", "ACTION_NOT_ALLOWED_IN_STATE")]
    [InlineData("correction-in-progress", "ACTION_NOT_ALLOWED_IN_STATE")]
    [InlineData("cancellation-in-progress", "ACTION_NOT_ALLOWED_IN_STATE")]
    public async Task ARefusedSkipNamesItsReasonAndChangesNothing(string shape, string reasonCode)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        SlotReading[] readings = Occupied(1, 2);
        int[] requested = [1, 2];
        string? demandId = null;
        string? attemptId = null;
        string? operationSessionId = null;
        switch (shape)
        {
            case "door-not-proven-locked":
                readings[2] = readings[2] with { Lock = "UNLOCKED" };
                break;
            case "output-not-reset":
                readings[1] = readings[1] with { Output = "ACTIVE" };
                break;
            case "a-slot-unknown":
                readings[2] = readings[2] with { Physical = "UNKNOWN" };
                break;
            case "unknown-ahead-of-unlocked":
                readings[0] = readings[0] with { Lock = "UNLOCKED" };
                readings[2] = readings[2] with { Lock = "UNKNOWN" };
                break;
            case "loaded-slots-disagree":
                requested = [1];
                break;
            case "every-slot-loaded":
                readings = Occupied(1, 2, 3);
                requested = [1, 2, 3];
                break;
            case "old-attempt":
                attemptId = await rig.AddEarlierLoadAttemptAsync();
                break;
            case "other-demand":
                demandId = "83000000-0000-4000-8000-0000000000bb";
                break;
            case "other-operation-session":
                operationSessionId = "83000000-0000-4000-8000-0000000000cc";
                break;
            case "exception-recovery-open":
                await rig.OpenExceptionRecoverySessionAsync();
                break;
            case "slot-fault-declared":
                await rig.DeclareSlotFaultAsync(3);
                break;
            case "correction-in-progress":
                await rig.OpenWorkflowAsync("LOAD_CORRECTION");
                break;
            case "cancellation-in-progress":
                await rig.OpenWorkflowAsync("LOAD_CANCELLATION");
                break;
        }
        await rig.SendSnapshotAsync(readings);

        await rig.SendAsync(rig.SkipRequest(
            SkipRequestId, requested, demandId: demandId, attemptId: attemptId, operationSessionId: operationSessionId));

        AssertRejected(rig.SingleSkipResult(), reasonCode);
        await rig.AssertLoadUntouchedAsync();
    }

    /// <summary>需求只有一篮：没有「剩下的」，拒。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-REJECTED")]
    public async Task ASingleBasketDemandHasNothingToSkip()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 4);
        Assert.Equal(OnlyOne, rig.TargetSlots);
        await rig.SendSnapshotAsync(Occupied(1));

        await rig.SendSkipRequestAsync(SkipRequestId, [1]);

        AssertRejected(rig.SingleSkipResult(), "ACTION_NOT_ALLOWED_IN_STATE");
        Assert.Equal(StationOperationStatus.Prepared, (await rig.Fixture.OperationAsync(SlotOperationType.Load)).Status);
    }

    /// <summary>k = 0：一篮都没装（车按协议写不出这种请求，服务端按自己的读数判）。拒，原因是专用码；一篮不装应当取消。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-NOTHING-LOADED")]
    public async Task NothingLoadedByTheServersReadingsIsRefusedAsNothingLoaded()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied());

        await rig.SendSkipRequestAsync(SkipRequestId, [1]);

        AssertRejected(rig.SingleSkipResult(), "REMAINING_BASKETS_SKIP_NOTHING_LOADED");
        await rig.AssertLoadUntouchedAsync();
    }

    /// <summary>
    /// 一次装货至多授权一次：授权之后同一次装货再来一条（新的 skipRequestId）减篮请求，拒；第一次授权照旧有效，结果照它结算。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-REJECTED")]
    public async Task ASecondSkipOfAnAuthorizedAttemptIsRefusedAndTheFirstStillSettles()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        await rig.SendSkipRequestAsync(SecondSkipRequestId, [1, 2]);

        JsonElement[] results = rig.SkipResults();
        Assert.Equal(2, results.Length);
        Assert.Equal("AUTHORIZED", results[0].GetProperty("outcome").GetString());
        AssertRejected(results[1], "ACTION_NOT_ALLOWED_IN_STATE");
        await rig.SendSkippedResultAsync(loaded: [1, 2], remaining: [3]);
        Assert.Equal(StationOperationStatus.Committed, (await rig.Fixture.OperationAsync(SlotOperationType.Load)).Status);
    }

    /// <summary>
    /// 同号同内容（换了 messageId，重连或重启后的重提）：原样重放存下的那条答复，不重判、没有第二个效果；同号不同内容：
    /// <c>ProtocolProblem</c>（<c>BUSINESS_ID_CONTENT_CONFLICT</c>），不答减篮结果、不进收件箱。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-DUPLICATE-REQUEST")]
    public async Task TheSameSkipRequestIdIsAnsweredFromTheRecordAndADifferentPayloadIsAConflict()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        await rig.AcknowledgeSkipResultsAsync();
        // 读数变了也不重判：答复是存下的那一份。
        await rig.SendSnapshotAsync(Occupied(1));

        string resubmitted = await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        Assert.Equal("DurableAck", MessageType(resubmitted.Split('\n')[0]));
        JsonElement[] results = rig.SkipResults();
        Assert.Equal(2, results.Length);
        Assert.Equal(results[0].GetRawText(), results[1].GetRawText());
        Assert.Single(await rig.Fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "RemainingBasketsSkipResult").ToArrayAsync(Token));

        string conflicting = rig.SkipRequest(SkipRequestId, [1]);
        ProtocolProblemAssert.RefusedLine(await rig.SendAsync(conflicting), "BUSINESS_ID_CONTENT_CONFLICT", conflicting);
        Assert.Equal(2, rig.SkipResults().Length);
    }

    /// <summary>
    /// 车按 <c>COMPLETED_REMAINING_SKIPPED</c> 报、服务端却没授权过这次装货：不当完成结算——确认收到，转人工恢复、旅程挡住，剩余仓不释放。
    /// 只有一条 <c>REJECTED</c>、或者结果里的仓与授权的不一致，同样处理。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-UNAUTHORIZED-RESULT")]
    [InlineData("never-asked")]
    [InlineData("only-rejected")]
    [InlineData("other-slots")]
    [InlineData("remaining-slot-not-empty")]
    [InlineData("remaining-slot-unlocked")]
    [InlineData("remaining-slot-output-active")]
    public async Task ASkippedResultWithoutAMatchingAuthorizationIsNeverSettledAsComplete(string shape)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        int[] loaded = [1, 2];
        int[] remaining = [3];
        switch (shape)
        {
            case "only-rejected":
                await rig.SendSnapshotAsync([.. Occupied(1, 2).Select(item => item.SlotNo == 3 ? item with { Lock = "UNLOCKED" } : item)]);
                await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
                AssertRejected(rig.SingleSkipResult(), "LOCK_NOT_CLOSED");
                break;
            case "other-slots":
                await rig.SendSnapshotAsync(Occupied(1, 2));
                await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
                loaded = [1];
                remaining = [2, 3];
                break;
            case "remaining-slot-not-empty":
            case "remaining-slot-unlocked":
            case "remaining-slot-output-active":
                await rig.SendSnapshotAsync(Occupied(1, 2));
                await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
                break;
        }

        object? remainingSlot = shape switch
        {
            "remaining-slot-not-empty" => SkipRig.SlotResult(3, "NOT_STARTED", "OCCUPIED", ["REMAINING_BASKETS_SKIPPED"]),
            "remaining-slot-unlocked" => SkipRig.SlotResult(3, "NOT_STARTED", "EMPTY", ["REMAINING_BASKETS_SKIPPED"], lockState: "UNLOCKED"),
            "remaining-slot-output-active" => SkipRig.SlotResult(3, "NOT_STARTED", "EMPTY", ["REMAINING_BASKETS_SKIPPED"], output: "ACTIVE"),
            _ => null
        };
        string answer = remainingSlot is not null
            ? await rig.SendAsync(rig.OperationResult(RemainingBasketsSkipRecords.SkippedOutcome,
            [
                SkipRig.SlotResult(1, "COMPLETED", "OCCUPIED", []),
                SkipRig.SlotResult(2, "COMPLETED", "OCCUPIED", []),
                remainingSlot
            ]))
            : await rig.SendSkippedResultAsync(loaded, remaining);

        Assert.Equal("DurableAck", MessageType(answer.Split('\n')[0]));
        StationOperationRow load = await rig.Fixture.OperationAsync(SlotOperationType.Load);
        Assert.Equal(StationOperationStatus.RecoveryRequired, load.Status);
        Assert.Equal(OneTwoThree, Slots(load.TargetSlotsJson));
        Assert.Equal(OneTwoThree, Slots((await rig.MembershipAsync()).TargetSlotsJson));
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await rig.Fixture.DemandRowAsync()).Status);
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow runtime = await rig.Fixture.RuntimeAsync();
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
    }

    /// <summary>
    /// 授权之后这次装货以别的结果结束（车上读数变了，按没有减篮报），授权作废：结果按它自己的规则结算——这里 <c>UNKNOWN</c> 转人工恢复——
    /// 授权从此不再用，剩余仓不按它释放。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-UNAUTHORIZED-RESULT")]
    public async Task AnAuthorizationIsSpentWhenTheAttemptSettlesAnotherWay()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        await rig.SendAsync(rig.OperationResult("UNKNOWN",
        [
            SkipRig.SlotResult(1, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(2, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(3, "UNKNOWN", "UNKNOWN", ["SLOT_STATE_UNKNOWN"], lockState: "UNKNOWN", output: "UNKNOWN")
        ]));
        StationOperationRow load = await rig.Fixture.OperationAsync(SlotOperationType.Load);
        Assert.Equal(StationOperationStatus.RecoveryRequired, load.Status);
        Assert.Equal(OneTwoThree, Slots((await rig.MembershipAsync()).TargetSlotsJson));
    }

    /// <summary>
    /// 授权之后车照常装满了三篮、报 <c>COMPLETED</c>：照常按三篮结算，授权不起作用。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-UNAUTHORIZED-RESULT")]
    public async Task AFullLoadAfterAnAuthorizationSettlesAsAFullLoad()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        await rig.SendAsync(rig.OperationResult("COMPLETED",
        [
            SkipRig.SlotResult(1, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(2, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(3, "COMPLETED", "OCCUPIED", [])
        ]));
        StationOperationRow load = await rig.Fixture.OperationAsync(SlotOperationType.Load);
        Assert.Equal(StationOperationStatus.Committed, load.Status);
        Assert.Equal(OneTwoThree, Slots(load.TargetSlotsJson));
        Assert.Equal(OneTwoThree, Slots((await rig.MembershipAsync()).TargetSlotsJson));
    }

    /// <summary>
    /// 待判期间这次装货的结果先到（车上先结束了）：结果照常结算，不被待判的请求挡住；请求随后判为 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，
    /// 车按协议丢掉它。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-REJECTED")]
    public async Task AResultArrivingWhileASkipIsPendingSettlesAndTheRequestIsRefused()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1));
        await rig.SendSafetyChangedAsync(2);
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        Assert.Empty(rig.SkipResults());

        await rig.SendAsync(rig.OperationResult("COMPLETED",
        [
            SkipRig.SlotResult(1, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(2, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(3, "COMPLETED", "OCCUPIED", [])
        ]));

        Assert.Equal(StationOperationStatus.Committed, (await rig.Fixture.OperationAsync(SlotOperationType.Load)).Status);
        AssertRejected(rig.SingleSkipResult(), "ACTION_NOT_ALLOWED_IN_STATE");
    }

    /// <summary>
    /// 减篮待判或已授权时申请取消装货：拒，<c>LOAD_CANCELLATION_BASKET_LOADED</c>——已经装进花篮的装货不取消（修订后的 REQ-0211）；
    /// 取消被拒不改任何状态，授权照旧可以结算。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancellationWhileASkipIsPendingOrAuthorizedIsRefused(bool authorized)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1));
        if (!authorized)
        {
            await rig.SendSafetyChangedAsync(2);
        }
        else
        {
            await rig.SendSnapshotAsync(Occupied(1, 2));
        }
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        Assert.Equal(authorized ? 1 : 0, rig.SkipResults().Length);

        string answer = await rig.SendAsync(rig.Envelope(Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", new
        {
            cancellationId = "83000000-0000-4000-8000-0000000000dd",
            demandId = DemandId,
            slotOperationAttemptId = rig.AttemptId,
            @operator = new { operatorId = OperatorId, verificationMethod = "BADGE", verifiedAt = Now },
            reason = "Operator pressed cancel after loading."
        }));

        using JsonDocument authorization = JsonDocument.Parse(answer.Split('\n')[0]);
        JsonElement payload = authorization.RootElement.GetProperty("payload");
        Assert.Equal("LoadCancellationAuthorization", authorization.RootElement.GetProperty("messageType").GetString());
        Assert.Equal("REJECTED", payload.GetProperty("decision").GetString());
        Assert.Equal("LOAD_CANCELLATION_BASKET_LOADED", payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Empty(await rig.Fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(Token));
        await rig.AssertLoadUntouchedAsync();
    }

    /// <summary>
    /// 断线重连：车还没确认的授权答复跟着恢复报告之后的补发，改写到新会话的代次再发；车以新的 messageId 重提同一个请求，判过的不重判。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-DUPLICATE-REQUEST")]
    public async Task AnUnacknowledgedAnswerIsReplayedIntoTheNextSessionAndNotDecidedAgain()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        string firstResultId = Assert.Single(await rig.UnacknowledgedSkipResultIdsAsync());

        await rig.MoveToGenerationAsync(2);
        await TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                rig.Connection, new WireToGateStore(rig.Connection), rig.Fixture.Clock, new ConfigurationBuilder().Build(), rig.Peer)
            .ReplayPendingCommandsAsync(rig.Fixture.Options.AgvId, 2, Token);

        JsonElement[] lines = rig.SkipResultLines();
        Assert.Equal(2, lines.Length);
        Assert.Equal(2, lines[1].GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(firstResultId, lines[1].GetProperty("messageId").GetString());

        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        Assert.Single(await rig.Fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "RemainingBasketsSkipResult").ToArrayAsync(Token));
    }

    /// <summary>
    /// 车行驶全程会话都是「因本服务端自己的在途单而未就绪」（真车载端的样子，合成车载端看不见）。判定不读会话就绪与旅程原因码，只读这次装货与
    /// 读数，所以会话停在这个状态时照样判得出——这里在取货站上装货，就绪状态正是这样留着的。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task ASessionNotReadyForThisServersOwnOrderStillHasItsSkipDecided()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(rig.Fixture);
        rig.Fixture.Context.ChangeTracker.Clear();
        await rig.SendSnapshotAsync(Occupied(1, 2));

        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        Assert.Equal("AUTHORIZED", rig.SingleSkipResult().GetProperty("outcome").GetString());
    }

    /// <summary>
    /// 看板与审计（票面第 4 条）：每次判定一行——批号、车、N→k、已装仓位、操作员工号、申请与判定时刻、同意或拒绝（拒绝带原因码与说明），
    /// 同意的标出是否已按 k 篮结算。拒绝原因码都有中文说明。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task EverySkipDecisionIsOnTheDashboardWithNToKTheOperatorAndItsTimes()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync([.. Occupied(1, 2).Select(item => item.SlotNo == 3 ? item with { Lock = "UNLOCKED" } : item)]);
        await rig.SendSkipRequestAsync(SecondSkipRequestId, [1, 2]);
        rig.Fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        await rig.SendSkippedResultAsync(loaded: [1, 2], remaining: [3]);

        object fact = await new ControlServer.Host.Dashboard.RemainingBasketsSkipsQueryEndpoint()
            .ReadAsync(rig.Fixture.Context, Token);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(fact, SerializerOptions));
        JsonElement[] rows = [.. document.RootElement.GetProperty("skips").EnumerateArray()];

        Assert.Equal(2, rows.Length);
        JsonElement granted = rows[0];
        Assert.Equal(SkipRequestId, granted.GetProperty("skipRequestId").GetString());
        Assert.Equal(Sublot, granted.GetProperty("sublot").GetString());
        Assert.Equal(rig.Fixture.Options.AgvId, granted.GetProperty("agvId").GetString());
        Assert.Equal(3, granted.GetProperty("expectedBasketCount").GetInt32());
        Assert.Equal(2, granted.GetProperty("loadedBasketCount").GetInt32());
        Assert.Equal(OperatorId, granted.GetProperty("operatorId").GetString());
        Assert.Equal("AUTHORIZED", granted.GetProperty("outcome").GetString());
        Assert.True(granted.GetProperty("settledAsLoaded").GetBoolean());
        Assert.Equal(rig.Fixture.Clock.GetUtcNow(), granted.GetProperty("decidedAt").GetDateTimeOffset());
        JsonElement refused = rows[1];
        Assert.Equal("REJECTED", refused.GetProperty("outcome").GetString());
        Assert.Equal("LOCK_NOT_CLOSED", refused.GetProperty("reasonCode").GetString());
        Assert.False(string.IsNullOrEmpty(refused.GetProperty("reasonDescription").GetString()));
        Assert.All(
            typeof(ServerReasonCodes).GetFields()
                .Select(field => (string)field.GetValue(null)!)
                .Where(code => code is "DEMAND_NOT_CURRENT" or "OPERATION_SESSION_MISMATCH" or "ACTION_NOT_ALLOWED_IN_STATE"
                    or "SLOT_STATE_UNKNOWN" or "LOCK_NOT_CLOSED" or "UNLOCK_OUTPUT_NOT_RESET"
                    or "REMAINING_BASKETS_SKIP_NOTHING_LOADED" or "SLOT_SET_INVALID"),
            code => Assert.True(ControlServer.Host.Dashboard.RemainingBasketsSkipsQueryEndpoint.Descriptions.ContainsKey(code), code));

        string html = new ControlServer.Dashboard.RemainingBasketsSkipCard().RenderFact(document.RootElement);
        Assert.Contains("3 → 2", html, StringComparison.Ordinal);
        Assert.Contains(OperatorId, html, StringComparison.Ordinal);
        Assert.Contains("LOCK_NOT_CLOSED", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 授权作废之后又来一条复工替换结果（审查 U1）：第一条结果以 <c>UNKNOWN</c> 结束、转人工恢复，管理员授权复工，车复工后报
    /// <c>COMPLETED_REMAINING_SKIPPED</c>。授权在第一条结果结算时就作废了，替换结果不按它结算：仓位不释放、操作仍要人工恢复。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-UNAUTHORIZED-RESULT")]
    public async Task AResumedReplacementResultNeverUsesAnAuthorizationTheFirstResultSpent()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);
        await rig.SendAsync(rig.OperationResult("UNKNOWN",
        [
            SkipRig.SlotResult(1, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(2, "COMPLETED", "OCCUPIED", []),
            SkipRig.SlotResult(3, "UNKNOWN", "UNKNOWN", ["SLOT_STATE_UNKNOWN"], lockState: "UNKNOWN", output: "UNKNOWN")
        ]));
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await rig.Fixture.OperationAsync(SlotOperationType.Load)).Status);
        await rig.AuthorizeResumeAsync();

        await rig.SendSkippedResultAsync(loaded: [1, 2], remaining: [3]);

        StationOperationRow load = await rig.Fixture.OperationAsync(SlotOperationType.Load);
        Assert.Equal(StationOperationStatus.RecoveryRequired, load.Status);
        Assert.Equal(OneTwoThree, Slots(load.TargetSlotsJson));
        Assert.Equal(OneTwoThree, Slots((await rig.MembershipAsync()).TargetSlotsJson));
    }

    /// <summary>
    /// 读数只认当前会话（审查 U3）：上一代会话里有一份「仓 1、2 有货」的快照，版本号还比新会话的基线高；车重连之后申请减篮，服务端按新会话的读数
    /// （全空）判，答 <c>REMAINING_BASKETS_SKIP_NOTHING_LOADED</c>，不拿旧会话的读数授权。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-NOTHING-LOADED")]
    public async Task ReadingsOfAnEarlierSessionNeverDecideASkip()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1, 2));
        await rig.MoveToGenerationAsync(2);

        await rig.SendSkipRequestAsync(SkipRequestId, [1, 2]);

        AssertRejected(rig.SingleSkipResult(), "REMAINING_BASKETS_SKIP_NOTHING_LOADED");
    }

    /// <summary>
    /// 要的快照没来（审查 M2）：真车载端在有未确认的 <c>SafetyStateChanged</c> 时不回快照请求，而它每条变化的 <c>affectedSlots</c> 都是 1..8。
    /// 服务端对待判的减篮请求按 cs#478 的间隔（<see cref="OnboardMessageProcessor.SafetySnapshotReaskInterval"/>）再要：间隔内不重复要，过了间隔
    /// 车再来一条报文就再要一次；快照到了就判出来。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task AnUnansweredSnapshotRequestIsAskedAgainAfterTheReaskInterval()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1));
        await rig.SendSafetyChangedAsync(2);
        string line = rig.SkipRequest(SkipRequestId, [1, 2], messageId: "83000000-0000-4000-8000-0000000000b1");
        Assert.Equal(1, SnapshotRequests(await rig.SendAsync(line)));

        // A change of a target slot during the load asks for its own snapshot (control-server#837); the skip's re-ask is what is
        // spaced, so the retransmit within the interval asks for none.
        string withinInterval = await rig.SendSafetyChangedAsync(1, 2, 3, 4, 5, 6, 7, 8);
        string retransmitWithin = await rig.SendAsync(line);
        Assert.Equal(1, SnapshotRequests(withinInterval));
        Assert.Equal(0, SnapshotRequests(retransmitWithin));

        rig.Fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval + TimeSpan.FromSeconds(1));
        string retransmitAfter = await rig.SendAsync(line);
        Assert.Equal(1, SnapshotRequests(retransmitAfter));
        Assert.Empty(rig.SkipResults());

        await rig.SendSnapshotAsync(Occupied(1, 2));
        Assert.Equal("AUTHORIZED", rig.SingleSkipResult().GetProperty("outcome").GetString());
        rig.Fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(0, SnapshotRequests(await rig.SendAsync(line)));
        Assert.Equal(0, SnapshotRequests(await rig.SendSafetyChangedAsync(5)));
    }

    /// <summary>
    /// 装货中取消、授权范围为空（6.0.0）之后，车报 <c>ALL_EMPTY</c>（审查 M1）：v6 车载端没有开门做 IO，<c>slotResults</c> 报空（向量的
    /// <c>REPORT_ALL_EMPTY_WITHOUT_SLOT_IO</c>）；逐仓报全部目标仓的旧形也照旧收。两种都对账成功——空报的那种由服务端自己的当前读数核全部目标仓。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADuringLoadCancellationReportedAllEmptyReconcilesWithOrWithoutSlotResults(bool perSlot)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied());

        RecoveryWorkflowState state = await rig.CancelAndReportAllEmptyAsync(perSlot);

        Assert.Equal(RecoveryWorkflowState.Reconciled, state);
    }

    /// <summary>
    /// 空报的 <c>ALL_EMPTY</c> 只在服务端读数证明全部目标仓空、锁闭、复位而且读数新鲜时才算对上：读数过期、有仓有货、有门未锁闭，都转人工恢复。
    /// 这些读数是授权之后、结果之前到的：授权本身要求当时的读数证明全空（control-server#837），所以只有授权之后变了的读数走得到这里。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    [InlineData("stale")]
    [InlineData("occupied")]
    [InlineData("door-unlocked")]
    [InlineData("output-active")]
    [InlineData("baseline-untrusted")]
    public async Task AnAllEmptyWithoutSlotResultsOverReadingsThatDoNotProveItGoesToRecovery(string shape)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        SlotReading[] readings = Occupied();
        switch (shape)
        {
            case "occupied":
                readings = Occupied(2);
                break;
            case "door-unlocked":
                readings[1] = readings[1] with { Lock = "UNLOCKED" };
                break;
            case "output-active":
                readings[2] = readings[2] with { Output = "ACTIVE" };
                break;
        }
        await rig.SendSnapshotAsync(Occupied());

        RecoveryWorkflowState state = await rig.CancelAndReportAllEmptyAsync(perSlot: false, afterAuthorization: async () =>
        {
            if (shape == "baseline-untrusted")
            {
                // The snapshot before the authorization still reads as the latest with no change after it: only the refused
                // change says the vehicle holds something else (control-server#837 review S2).
                await rig.SendRefusedSafetyChangedAsync();
                return;
            }
            await rig.SendSafetyChangedAsync(1, 2, 3, 4, 5, 6, 7, 8);
            if (shape != "stale")
            {
                await rig.SendSnapshotAsync(readings);
            }
        });

        Assert.Equal(RecoveryWorkflowState.RecoveryRequired, state);
    }

    private static int SnapshotRequests(string answer) =>
        answer.Split('\n').Count(line => line.Length > 0 && MessageType(line) == "SafetyStateSnapshotRequested");

    private static void AssertRejected(JsonElement result, string reasonCode)
    {
        Assert.Equal("REJECTED", result.GetProperty("outcome").GetString());
        Assert.Equal(reasonCode, result.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Empty(result.GetProperty("loadedSlots").EnumerateArray());
    }

    internal static SlotReading[] Occupied(params int[] occupied) =>
    [
        .. Enumerable.Range(1, 8).Select(slot => occupied.Contains(slot)
            ? new SlotReading(slot, "OCCUPIED", "LOCKED", "RESET")
            : new SlotReading(slot, "EMPTY", "LOCKED", "RESET"))
    ];

    internal static int[] Slots(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetInt32())];

    internal static int[] Slots(string json) => JsonSerializer.Deserialize<int[]>(json) ?? [];

    internal static string MessageType(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    internal sealed record SlotReading(int SlotNo, string Physical, string Lock, string Output);

    /// <summary>
    /// 一辆停在取货站、正在装 <c>SUBLOT-001</c> 的车，与一条到它的连接：入站报文走 <see cref="OnboardMessageProcessor"/>，连接已过握手。
    /// </summary>
    internal sealed class SkipRig : IAsyncDisposable
    {
        private long _safetyVersion = 7;

        private SkipRig(RuntimeFixture fixture, StationOperationRow load, JourneyRuntimeRow runtime)
        {
            Fixture = fixture;
            AttemptId = load.SlotOperationAttemptId;
            TargetSlots = Slots(load.TargetSlotsJson);
            OperationSessionId = runtime.OperationSessionId!;
            Connection = fixture.OpenConnectionContext();
            Processor = TestOnboardProcessorFactory.Create(
                Connection,
                new WireToGateStore(Connection),
                fixture.Clock,
                new ConfigurationBuilder().Build(),
                peer: Peer,
                runtimeOptions: fixture.Options,
                readinessRecheck: fixture.ReadinessRecheck);
            State = new OnboardConnectionState
            {
                AgvId = fixture.Options.AgvId,
                SessionGeneration = 1,
                CapabilityRevision = 1,
                SafetyRevision = 7,
                Readiness = SessionReadiness.Ready,
                HandshakeCompleted = true
            };
        }

        public RuntimeFixture Fixture { get; }

        public string AttemptId { get; }

        public int[] TargetSlots { get; }

        public string OperationSessionId { get; }

        public ControlServerDbContext Connection { get; }

        public OnboardMessageProcessor Processor { get; }

        public OnboardConnectionState State { get; }

        public RecordingPeer Peer { get; } = new();

        public static async Task<SkipRig> AtLoadAsync(int boxes)
        {
            RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            fixture.Catalog.Set(fixture.Demand(DemandId, Sublot, Now.AddMinutes(-10)));
            fixture.BoxCounts.Set(Sublot, boxes);
            JourneyRuntimeRow runtime = await fixture.AdvanceToLoadResultAsync();
            Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
            StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
            return new SkipRig(fixture, load, runtime);
        }

        public Task<JourneyDemandRow> MembershipAsync() => Fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == DemandId, Token);

        /// <summary>
        /// 一行入站报文，走连接自己的上下文。之后清掉引擎那个上下文的跟踪：真宿主每轮新开作用域，两边各读库里的现值；测试共用一个上下文，
        /// 不清的话引擎读到的是它自己先前跟踪的旧行。
        /// </summary>
        public async Task<string> SendAsync(string line)
        {
            string answer = await Processor.ProcessAsync(line, State, Token);
            Fixture.Context.ChangeTracker.Clear();
            return answer;
        }

        public Task<string> SendSnapshotAsync(IReadOnlyList<SlotReading> slots, bool unknownPresent = false) =>
            SendAsync(Envelope(Guid.NewGuid().ToString("D"), "SafetyStateSnapshot", new
            {
                safetyStateVersion = ++_safetyVersion,
                observedAt = Fixture.Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe = !unknownPresent,
                    vehicleStopped = true,
                    allTargetSlotsLocked = slots.All(slot => slot.Lock == "LOCKED"),
                    allUnlockOutputsReset = slots.All(slot => slot.Output == "RESET"),
                    unknownPresent,
                    reasonCodes = Array.Empty<string>()
                },
                slotStates = slots.Select(slot => new
                {
                    slotNo = slot.SlotNo,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = slot.Physical,
                    lockState = slot.Lock,
                    unlockOutputState = slot.Output,
                    reasonCodes = Array.Empty<string>()
                })
            }));

        public Task<string> SendSafetyChangedAsync(params int[] affectedSlots) =>
            SendAsync(Envelope(Guid.NewGuid().ToString("D"), "SafetyStateChanged", new
            {
                safetyStateVersion = ++_safetyVersion,
                observedAt = Fixture.Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                affectedSlots
            }));

        /// <summary>
        /// 车报一条修订号倒退的 <c>SafetyStateChanged</c>：服务端拒收、不进收件箱，本会话的安全基线从此不可信，直到下一份被接受的快照
        /// （control-server#478）。收件箱里此前那份快照仍像是最新、之后没有变化的读数（control-server#837 审查 S2）。
        /// </summary>
        public async Task SendRefusedSafetyChangedAsync()
        {
            string answer = await SendAsync(Envelope(Guid.NewGuid().ToString("D"), "SafetyStateChanged", new
            {
                safetyStateVersion = _safetyVersion - 1,
                observedAt = Fixture.Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                affectedSlots = TargetSlots
            }));
            Assert.Equal("ProtocolProblem", MessageType(answer.Split('\n')[0]));
            Assert.True(State.SafetyBaselineUntrusted);
        }

        public string SkipRequest(
            string skipRequestId,
            int[] loadedSlots,
            string? messageId = null,
            string? demandId = null,
            string? attemptId = null,
            string? operationSessionId = null,
            bool withOperator = true) =>
            Envelope(messageId ?? Guid.NewGuid().ToString("D"), "RemainingBasketsSkipRequested", new
            {
                skipRequestId,
                demandId = demandId ?? DemandId,
                operationSessionId = operationSessionId ?? OperationSessionId,
                slotOperationAttemptId = attemptId ?? AttemptId,
                loadedSlots,
                @operator = withOperator
                    ? new { operatorId = OperatorId, verificationMethod = "BADGE", verifiedAt = Now }
                    : null
            });

        public Task<string> SendSkipRequestAsync(string skipRequestId, int[] loadedSlots) =>
            SendAsync(SkipRequest(skipRequestId, loadedSlots));

        /// <summary>车按授权报的结果：授权的仓 COMPLETED、有货锁闭复位，其余 NOT_STARTED、空着锁闭复位、带 REMAINING_BASKETS_SKIPPED。</summary>
        public Task<string> SendSkippedResultAsync(int[] loaded, int[] remaining, string? messageId = null) =>
            SendAsync(OperationResult(
                "COMPLETED_REMAINING_SKIPPED",
                [
                    .. loaded.Select(slot => SlotResult(slot, "COMPLETED", "OCCUPIED", [])),
                    .. remaining.Select(slot => SlotResult(slot, "NOT_STARTED", "EMPTY", ["REMAINING_BASKETS_SKIPPED"]))
                ],
                messageId));

        public static object SlotResult(int slot, string outcome, string physical, string[] reasonCodes,
            string lockState = "LOCKED", string output = "RESET") => new
            {
                slotNo = slot,
                outcome,
                finalPhysicalState = physical,
                lockState,
                unlockOutputState = output,
                reasonCodes
            };

        public string OperationResult(string overallOutcome, object[] slotResults, string? messageId = null)
        {
            object[] ordered = [.. slotResults.OrderBy(item => JsonSerializer.SerializeToElement(item).GetProperty("slotNo").GetInt32())];
            var withoutHash = new
            {
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                operationType = "LOAD",
                overallOutcome,
                slotResults = ordered,
                observedAt = Fixture.Clock.GetUtcNow(),
                journalCheckpoint = "LOAD_RESULT_RECORDED"
            };
            string resultContentSha256 = Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withoutHash, SerializerOptions))).ToLowerInvariant();
            return Envelope(messageId ?? Guid.NewGuid().ToString("D"), "OperationResult", new
            {
                withoutHash.demandId,
                withoutHash.slotOperationAttemptId,
                withoutHash.operationType,
                withoutHash.overallOutcome,
                withoutHash.slotResults,
                withoutHash.observedAt,
                withoutHash.journalCheckpoint,
                resultContentSha256
            });
        }

        /// <summary>对端收到的全部 <c>RemainingBasketsSkipResult</c> 的载荷，按发送先后。</summary>
        public JsonElement[] SkipResults() =>
        [
            .. Peer.Lines
                .Select(line => JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement)
                .Where(root => root.GetProperty("messageType").GetString() == "RemainingBasketsSkipResult")
                .Select(root => root.GetProperty("payload").Clone())
        ];

        public JsonElement SingleSkipResult() => Assert.Single(SkipResults());

        /// <summary>对端收到的全部 <c>RemainingBasketsSkipResult</c> 整行（信封在内），按发送先后。</summary>
        public JsonElement[] SkipResultLines() =>
        [
            .. Peer.Lines
                .Select(line => JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement.Clone())
                .Where(root => root.GetProperty("messageType").GetString() == "RemainingBasketsSkipResult")
        ];

        public async Task<string[]> UnacknowledgedSkipResultIdsAsync() => await Fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "RemainingBasketsSkipResult" && row.AcknowledgedAt == null)
            .Select(row => row.MessageId)
            .ToArrayAsync(Token);

        /// <summary>车确认了收到的减篮答复（它回的 <c>DurableAck</c>）。</summary>
        public async Task AcknowledgeSkipResultsAsync()
        {
            foreach (ProtocolOutboxRow row in await Fixture.Context.ProtocolOutbox
                         .Where(item => item.MessageType == "RemainingBasketsSkipResult" && item.AcknowledgedAt == null)
                         .ToArrayAsync(Token))
            {
                row.AcknowledgedAt = Fixture.Clock.GetUtcNow();
            }
            await Fixture.Context.SaveChangesAsync(Token);
            Fixture.Context.ChangeTracker.Clear();
        }

        /// <summary>车断线后以新的代次重连、握手做完：会话行与这条连接都换到 <paramref name="generation"/>。</summary>
        public async Task MoveToGenerationAsync(long generation)
        {
            await Fixture.AdvanceSessionAsync(generation);
            Fixture.Context.ChangeTracker.Clear();
            State.SessionGeneration = generation;
            _safetyVersion = 7;
        }

        /// <summary>扫码后给另一条前侧需求分仓时能挑的号（同一个账本，派车也读它）。</summary>
        public async Task<IReadOnlyList<int>> AssignableFrontSlotsAsync()
        {
            Fixture.Context.ChangeTracker.Clear();
            return await new JourneyAwareSlotLedger(Fixture.Context).ReadAssignableSlotsAsync(
                Fixture.Options.AgvId,
                "83000000-0000-4000-8000-0000000000ff",
                new OnboardDispatchFacts(1, [1, 2, 3, 4, 5, 6, 7, 8], true, true, true, true, false),
                new VehicleSlotPositions(
                    Fixture.Options.AgvId,
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
                    }),
                "FRONT",
                Token);
        }

        /// <summary>这辆车开着一个异常处置会话（已转人工恢复）。</summary>
        public async Task OpenExceptionRecoverySessionAsync()
        {
            Fixture.Context.ExceptionRecoverySessions.Add(new ExceptionRecoverySessionRow
            {
                ExceptionRecoverySessionId = "83000000-0000-4000-8000-0000000000e1",
                RequestId = "83000000-0000-4000-8000-0000000000e2",
                RequestContentHash = new string('e', 64),
                AgvId = Fixture.Options.AgvId,
                EventId = "83000000-0000-4000-8000-0000000000e3",
                DemandId = DemandId,
                SlotsJson = "[3]",
                AdministratorId = "ADMIN-001",
                AdministratorRole = "SITE_ADMINISTRATOR",
                Reason = "Slot 3 jammed.",
                State = "OPEN",
                OpenedAt = Fixture.Clock.GetUtcNow(),
                UpdatedAt = Fixture.Clock.GetUtcNow()
            });
            await Fixture.Context.SaveChangesAsync(Token);
            Fixture.Context.ChangeTracker.Clear();
        }

        /// <summary>管理员对这次装货的 <paramref name="slotNo"/> 号仓判了故障，车还没答。</summary>
        public async Task DeclareSlotFaultAsync(int slotNo)
        {
            Fixture.Context.Set<SlotFaultDeclarationRow>().Add(new SlotFaultDeclarationRow
            {
                DeclarationId = "83000000-0000-4000-8000-0000000000f1",
                RequestId = "83000000-0000-4000-8000-0000000000f2",
                RequestContentHash = new string('f', 64),
                AgvId = Fixture.Options.AgvId,
                DemandId = DemandId,
                SlotOperationAttemptId = AttemptId,
                OperationType = "LOAD",
                SlotNo = slotNo,
                FaultCategory = "DOOR_JAMMED",
                Note = "Door does not open.",
                AdministratorId = "ADMIN-001",
                AdministratorRole = "SITE_ADMINISTRATOR",
                DeclaredAt = Fixture.Clock.GetUtcNow(),
                CommandMessageId = "83000000-0000-4000-8000-0000000000f3",
                State = SlotFaultDeclarationStates.Pending
            });
            await Fixture.Context.SaveChangesAsync(Token);
            Fixture.Context.ChangeTracker.Clear();
        }

        /// <summary>这条需求有一条 <paramref name="workflowType"/> 在处理（已授权、等结果）。</summary>
        public async Task OpenWorkflowAsync(string workflowType)
        {
            Fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
            {
                WorkflowId = "83000000-0000-4000-8000-0000000000c1",
                WorkflowType = workflowType,
                AgvId = Fixture.Options.AgvId,
                DemandId = DemandId,
                SlotOperationAttemptId = AttemptId,
                SlotsJson = "[]",
                State = RecoveryWorkflowState.AwaitingResult,
                RequestMessageId = "83000000-0000-4000-8000-0000000000c2",
                RequestContentHash = new string('c', 64),
                CreatedAt = Fixture.Clock.GetUtcNow(),
                UpdatedAt = Fixture.Clock.GetUtcNow()
            });
            await Fixture.Context.SaveChangesAsync(Token);
            Fixture.Context.ChangeTracker.Clear();
        }

        /// <summary>
        /// 同一条需求先前的一次装货：一笔已取消的装货操作，别的 attempt。返回那个 attempt。
        /// </summary>
        public async Task<string> AddEarlierLoadAttemptAsync()
        {
            const string earlier = "83000000-0000-4000-8000-0000000000aa";
            Fixture.Context.StationOperations.Add(new StationOperationRow
            {
                SlotOperationAttemptId = earlier,
                DemandId = DemandId,
                SublotId = Sublot,
                TargetSlotsJson = "[1,2,3]",
                OperationType = SlotOperationType.Load,
                ForcedRecoveryGeneration = 0,
                ContentHash = new string('7', 64),
                Status = StationOperationStatus.Cancelled,
                CreatedAt = Fixture.Clock.GetUtcNow().AddMinutes(-5)
            });
            await Fixture.Context.SaveChangesAsync(Token);
            Fixture.Context.ChangeTracker.Clear();
            return earlier;
        }

        /// <summary>管理员对这次装货授权了复工（RESUME_AFTER_REPAIR，命令已发、等结果），范围是全部目标仓。</summary>
        public async Task AuthorizeResumeAsync()
        {
            StationOperationRow load = await Fixture.OperationAsync(SlotOperationType.Load);
            Fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
            {
                WorkflowId = "83000000-0000-4000-8000-0000000000d1",
                WorkflowType = "RESUME_AFTER_REPAIR",
                AgvId = Fixture.Options.AgvId,
                DemandId = DemandId,
                SlotOperationAttemptId = AttemptId,
                SlotsJson = JsonSerializer.Serialize(TargetSlots),
                ForcedRecoveryGeneration = 0,
                State = RecoveryWorkflowState.AwaitingResult,
                RequestMessageId = "83000000-0000-4000-8000-0000000000d2",
                RequestContentHash = new string('d', 64),
                CommandMessageId = "83000000-0000-4000-8000-0000000000d3",
                CommandMessageType = "SlotOperationResumeCommand",
                CommandContentHash = load.ContentHash,
                CreatedAt = Fixture.Clock.GetUtcNow(),
                UpdatedAt = Fixture.Clock.GetUtcNow()
            });
            await Fixture.Context.SaveChangesAsync(Token);
            Fixture.Context.ChangeTracker.Clear();
        }

        /// <summary>
        /// 装货途中按取消：服务端授权（范围为空），车报 <c>ALL_EMPTY</c>——<paramref name="perSlot"/> 时逐仓报全部目标仓，否则 <c>slotResults</c> 为空。
        /// 返回取消工作流对账后的状态。
        /// </summary>
        public async Task<RecoveryWorkflowState> CancelAndReportAllEmptyAsync(bool perSlot, Func<Task>? afterAuthorization = null)
        {
            const string cancellationId = "83000000-0000-4000-8000-0000000000c9";
            string answer = await SendAsync(Envelope(Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", new
            {
                cancellationId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                @operator = new { operatorId = OperatorId, verificationMethod = "BADGE", verifiedAt = Now },
                reason = "Operator pressed cancel with nothing loaded."
            }));
            using (JsonDocument authorization = JsonDocument.Parse(answer.Split('\n')[0]))
            {
                JsonElement payload = authorization.RootElement.GetProperty("payload");
                Assert.Equal("AUTHORIZED", payload.GetProperty("decision").GetString());
                Assert.Empty(payload.GetProperty("slots").EnumerateArray());
            }
            if (afterAuthorization is not null)
            {
                await afterAuthorization();
            }
            object[] slotResults = perSlot
                ? [.. TargetSlots.Order().Select(slot => SlotResult(slot, "COMPLETED", "EMPTY", []))]
                : [];
            await SendAsync(Envelope(Guid.NewGuid().ToString("D"), "LoadCancellationResult", new
            {
                cancellationId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                overallOutcome = "ALL_EMPTY",
                slotResults,
                observedAt = Now
            }));
            return (await Fixture.Context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == cancellationId, Token)).State;
        }

        /// <summary>这次装货照旧在进行：操作没结算、仓位没改、需求没动、旅程还在等装货结果。</summary>
        public async Task AssertLoadUntouchedAsync()
        {
            Fixture.Context.ChangeTracker.Clear();
            StationOperationRow load = await Fixture.Context.StationOperations.AsNoTracking()
                .SingleAsync(row => row.SlotOperationAttemptId == AttemptId, Token);
            Assert.Equal(StationOperationStatus.Prepared, load.Status);
            Assert.Equal(TargetSlots, Slots(load.TargetSlotsJson));
            JourneyDemandRow membership = await MembershipAsync();
            Assert.Equal(JourneyDemandStatuses.Loading, membership.Status);
            Assert.Equal(TargetSlots, Slots(membership.TargetSlotsJson));
            Assert.Equal(DemandExecutionStatus.Accepted, (await Fixture.DemandRowAsync()).Status);
            Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await Fixture.RuntimeAsync()).Stage);
        }

        /// <summary>离站核验答 SAFE、车开到卸货站：引擎下卸货命令。</summary>
        public async Task DepartAndArriveAtTheGateAsync()
        {
            JourneyRuntimeRow runtime = await Fixture.RuntimeAsync();
            if (runtime.PreDepartureSafetyCheckId is null)
            {
                await Fixture.Engine.ExecuteOnceAsync(Token);
                runtime = await Fixture.RuntimeAsync();
            }
            Assert.NotNull(runtime.PreDepartureSafetyCheckId);
            await Fixture.AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "PreDepartureSafetyCheckResult",
                new
                {
                    preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                    checkPurpose = "DEPARTURE",
                    outcome = "SAFE",
                    observedAt = Fixture.Clock.GetUtcNow(),
                    safetyStateVersion = _safetyVersion,
                    validUntil = Fixture.Clock.GetUtcNow().AddMinutes(1),
                    safety = new
                    {
                        departureSafe = true,
                        vehicleStopped = true,
                        allTargetSlotsLocked = true,
                        allUnlockOutputsReset = true,
                        unknownPresent = false,
                        reasonCodes = Array.Empty<string>()
                    }
                },
                runtime.PreDepartureSafetyCheckMessageId);
            await Fixture.Engine.ExecuteOnceAsync(Token);
            Fixture.Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
            Fixture.Riot.Vehicle = Fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
            await Fixture.Engine.ExecuteOnceAsync(Token);
        }

        public string Envelope(string messageId, string messageType, object payload) =>
            BeforeSublotEnvelope(Fixture, messageId, messageType, State.SessionGeneration ?? 1, payload);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Fixture.DisposeAsync();
        }
    }
}
