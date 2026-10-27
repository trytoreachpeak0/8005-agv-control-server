using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.RemainingBasketsSkipTests;

namespace ControlServer.Tests;

/// <summary>
/// 装货中取消授权之后车一直不答（control-server#837，用户 10-27 选定的方案 a）：等答复超过 <c>JourneyRuntime:LoadCancellationAnswerTimeout</c>
/// （默认 2 分钟），服务端把旅程挂起（<c>LOAD_CANCELLATION_UNANSWERED</c>）、这次装货标为待恢复、取消工作流收口为
/// <c>LOAD_CANCELLATION_ANSWER_TIMED_OUT</c>，于是异常恢复会话、故障交接与补偿都放得进来。修之前取消挂在 <c>AwaitingResult</c> 没有超时：人工判故障被拒、
/// 会话要旅程挂起才开、装货自己的失败也不终结需求，车载端不答（断网、程序崩溃）就只能改库。
/// </summary>
/// <remarks>
/// 计时从落库的授权时刻（取消工作流的 <c>CreatedAt</c>）起算，重启不重新计；会话未就绪时照样判（判在就绪门之前）。超时之后迟到的取消结果只记证据与日志，
/// 不再改旅程或装货。车载端 onboard-hmi#392 已修好按钮按不了，这是服务端的兜底。
/// </remarks>
public sealed class LoadCancellationAnswerTimeoutTests
{
    private const string CancellationId = "83700000-0000-4000-8000-0000000000a1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>默认 2 分钟：差一秒照旧等，过了就挂起、装货待恢复、工作流收口，记一条事件。需求不动。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnUnansweredCancellationPastTheTimeoutBlocksTheJourneyAndHoldsTheLoadForRecovery()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), new JourneyRuntimeOptions().LoadCancellationAnswerTimeout);
        await using SkipRig rig = await AuthorizedAsync();

        rig.Fixture.Clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);
        rig.Fixture.Context.ChangeTracker.Clear();
        Assert.Equal(RecoveryWorkflowState.AwaitingResult, (await WorkflowAsync(rig)).State);
        Assert.Equal(StationOperationStatus.Prepared, (await LoadAsync(rig)).Status);
        Assert.NotEqual(LoadCancellationAnswerTimeout.BlockReason, (await rig.Fixture.RuntimeAsync()).BlockReasonCode);

        rig.Fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);

        await AssertTimedOutAsync(rig);
        Assert.Equal(DemandExecutionStatus.Accepted, (await rig.Fixture.DemandRowAsync()).Status);
        Assert.Single(rig.Fixture.EngineLog.Entries, entry => entry.Message.Contains(CancellationId, StringComparison.Ordinal));
    }

    /// <summary>重启：计时从落库的授权时刻起算。授权一分钟后重启，再过一分零一秒就到期，不从重启那一刻重新等两分钟。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheTimeoutCountsFromTheAuthorizationOnFileAcrossARestart()
    {
        await using SkipRig rig = await AuthorizedAsync();
        rig.Fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);

        await rig.Fixture.RecreateEngineAsync();
        rig.Fixture.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);

        await AssertTimedOutAsync(rig);
    }

    /// <summary>
    /// 会话未就绪（真车载端挂着本服务端自己的在途单时整段未就绪，车载端不答取消时也常是这样）：照样到期挂起。判在就绪门之前，就绪门后面那一段不跑。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASessionNotReadyStillHasItsUnansweredCancellationTimedOut()
    {
        await using SkipRig rig = await AuthorizedAsync();
        await rig.Fixture.Context.SessionRecoveries
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Readiness, SessionReadiness.RecoveryRequired)
                    .SetProperty(row => row.ReasonCode, "DEPARTURE_SAFETY_NOT_READY"),
                Token);
        rig.Fixture.Context.ChangeTracker.Clear();

        rig.Fixture.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);

        await AssertTimedOutAsync(rig);
    }

    /// <summary>
    /// 超时收口之后迟到的取消结果（授权之后报的 <c>ALL_EMPTY</c>、<c>FAILED</c> 各一形）：确认收下、留证据与日志，不再改旅程、装货、需求，工作流仍是超时收口。
    /// 否则 <c>ALL_EMPTY</c> 会把已经交给人工恢复的这次装货结掉、终结需求，与人手上正在做的恢复打架。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("ALL_EMPTY")]
    [InlineData("FAILED")]
    public async Task ACancellationResultAfterTheTimeoutIsOnlyRecorded(string overallOutcome)
    {
        await using SkipRig rig = await AuthorizedAsync();
        rig.Fixture.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);
        await AssertTimedOutAsync(rig);

        string messageId = Guid.NewGuid().ToString("D");
        string answer = await rig.SendAsync(CancellationResult(rig, messageId, overallOutcome));

        Assert.Equal("DurableAck", MessageType(answer.Split('\n')[0]));
        await AssertTimedOutAsync(rig);
        Assert.Equal(DemandExecutionStatus.Accepted, (await rig.Fixture.DemandRowAsync()).Status);
        RecoveryResultEvidenceRow evidence = await rig.Fixture.Context.RecoveryResultEvidence.AsNoTracking()
            .SingleAsync(row => row.MessageId == messageId, Token);
        Assert.True(evidence.HistoricalOnly);
        Assert.Equal(overallOutcome, evidence.Outcome);

        // And one more round changes nothing either.
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);
        await AssertTimedOutAsync(rig);
    }

    /// <summary>
    /// 答复在到期之前落库（同时到达的另一种先后）：结果照常收口这次取消，之后过了时限，超时判定看不到等答复的取消，什么都不做，不重复处理。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAnswerOnFileBeforeTheTimeoutIsNotTimedOutAgain()
    {
        await using SkipRig rig = await AuthorizedAsync();
        rig.Fixture.Clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await rig.SendAsync(CancellationResult(rig, Guid.NewGuid().ToString("D"), "ALL_EMPTY"));
        rig.Fixture.Context.ChangeTracker.Clear();
        Assert.Equal(RecoveryWorkflowState.Reconciled, (await WorkflowAsync(rig)).State);

        rig.Fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);

        rig.Fixture.Context.ChangeTracker.Clear();
        RecoveryWorkflowRow workflow = await WorkflowAsync(rig);
        Assert.Equal((RecoveryWorkflowState.Reconciled, "ALL_EMPTY"), (workflow.State, workflow.Outcome));
        Assert.Equal(StationOperationStatus.Cancelled, (await LoadAsync(rig)).Status);
        Assert.NotEqual(LoadCancellationAnswerTimeout.BlockReason, (await rig.Fixture.RuntimeAsync()).BlockReasonCode);
        Assert.DoesNotContain(rig.Fixture.EngineLog.Entries, entry => entry.Message.Contains(CancellationId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 授权之后这次装货先完成了（装货自己的 COMPLETED 落了库，旅程已往下走），而取消的答复一直没来：到期只收口取消工作流，装货仍是 Committed，
    /// 旅程不挂起——货装好了，没有要人恢复的东西。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task OverALoadThatCompletedMeanwhileTheTimeoutOnlyClosesTheCancellation()
    {
        await using SkipRig rig = await AuthorizedAsync();
        await rig.SendAsync(rig.OperationResult(
            "COMPLETED", [.. rig.TargetSlots.Select(slot => SkipRig.SlotResult(slot, "COMPLETED", "OCCUPIED", []))]));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);
        rig.Fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeStage movedOnTo = (await rig.Fixture.RuntimeAsync()).Stage;

        rig.Fixture.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await rig.Fixture.Engine.ExecuteOnceAsync(Token);

        rig.Fixture.Context.ChangeTracker.Clear();
        RecoveryWorkflowRow workflow = await WorkflowAsync(rig);
        Assert.Equal(
            (RecoveryWorkflowState.RecoveryRequired, RecoveryWorkflowOutcomes.LoadCancellationAnswerTimedOut),
            (workflow.State, workflow.Outcome));
        Assert.Equal(StationOperationStatus.Committed, (await LoadAsync(rig)).Status);
        JourneyRuntimeRow runtime = await rig.Fixture.RuntimeAsync();
        Assert.NotEqual(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.NotEqual(LoadCancellationAnswerTimeout.BlockReason, runtime.BlockReasonCode);
        Assert.NotEqual(JourneyRuntimeStage.AwaitingLoadResult, movedOnTo);
    }

    private static async Task<SkipRig> AuthorizedAsync()
    {
        SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied());
        string answer = await rig.SendAsync(rig.Envelope(Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", new
        {
            cancellationId = CancellationId,
            demandId = "10000000-0000-4000-8000-000000000001",
            slotOperationAttemptId = rig.AttemptId,
            @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = Now },
            reason = "Operator pressed cancel."
        }));
        using JsonDocument authorization = JsonDocument.Parse(answer.Split('\n')[0]);
        Assert.Equal("AUTHORIZED", authorization.RootElement.GetProperty("payload").GetProperty("decision").GetString());
        rig.Fixture.Context.ChangeTracker.Clear();
        return rig;
    }

    private static string CancellationResult(SkipRig rig, string messageId, string overallOutcome) =>
        rig.Envelope(messageId, "LoadCancellationResult", new
        {
            cancellationId = CancellationId,
            demandId = "10000000-0000-4000-8000-000000000001",
            slotOperationAttemptId = rig.AttemptId,
            overallOutcome,
            slotResults = rig.TargetSlots.Order()
                .Select(slot => overallOutcome == "ALL_EMPTY"
                    ? SkipRig.SlotResult(slot, "COMPLETED", "EMPTY", [])
                    : SkipRig.SlotResult(slot, "FAILED", "UNKNOWN", ["SLOT_STATE_UNKNOWN"], "UNKNOWN", "UNKNOWN"))
                .ToArray(),
            observedAt = rig.Fixture.Clock.GetUtcNow()
        });

    private static async Task AssertTimedOutAsync(SkipRig rig)
    {
        rig.Fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow runtime = await rig.Fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.Blocked, LoadCancellationAnswerTimeout.BlockReason), (runtime.Stage, runtime.BlockReasonCode));
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await LoadAsync(rig)).Status);
        RecoveryWorkflowRow workflow = await WorkflowAsync(rig);
        Assert.Equal(
            (RecoveryWorkflowState.RecoveryRequired, RecoveryWorkflowOutcomes.LoadCancellationAnswerTimedOut),
            (workflow.State, workflow.Outcome));
    }

    private static Task<RecoveryWorkflowRow> WorkflowAsync(SkipRig rig) =>
        rig.Fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(row => row.WorkflowId == CancellationId, Token);

    private static Task<StationOperationRow> LoadAsync(SkipRig rig) =>
        rig.Fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == rig.AttemptId, Token);
}
