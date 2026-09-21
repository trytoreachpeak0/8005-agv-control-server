using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Tests;

/// <summary>
/// 同一份完整 MES 快照里，一个 Sublot 命中了多于一种任务类型：该 Sublot 的<b>全部</b>候选都挡，别的 Sublot 不受影响
/// （REQ-0189；批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// <b>挡整个 Sublot 而不是「多出来的那一条」，是这一条判据全部的内容。</b>矛盾的是 MES 给的事实本身——同一批料
/// 同时说要走两种工艺，服务端没有判断哪一种对的依据。挑一条执行等于替 MES 做决定，而那批料一旦装上车就改不回来。
/// 所以下面那条「两条候选都挡」的用例才是主判据；只挡一条的实现能通过「有东西被挡了」这种断言，却正好做错了事。
/// </para>
/// <para>
/// <b>归普通积压，不是结构性告警</b>（<c>StructuralDispatchBlockTests</c> 那张表里登记着）：这是 MES 那一侧的数据
/// 自相矛盾，下一份快照就能改掉，而结构性告警说的是「整个车队都接不了」——换一辆车、等一等都没用。两者要人做的
/// 事也不同：这一条要去看 MES，不是去看车队。
/// </para>
/// </remarks>
public sealed class Batch7SublotTaskTypeConflictTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 一个 Sublot 上两种任务类型：这个 Sublot 的两条候选都被挡，一条都不放过去。
    /// </summary>
    [Fact]
    public async Task EveryCandidateOfAConflictedSublotIsRefused()
    {
        AcceptedDemandSnapshot wireToGate = Demand("D-1", "SUBLOT-A", "WIRE_TO_GATE");
        AcceptedDemandSnapshot other = Demand("D-2", "SUBLOT-A", "OTHER_PROCESS");
        SublotTaskTypeConflictCriterion criterion = new();

        Assert.Equal(
            DispatchReasonCodes.SublotTaskTypeConflict,
            await criterion.EvaluateAsync(
                Evaluation(wireToGate, wireToGate, other), TestContext.Current.CancellationToken));
        Assert.Equal(
            DispatchReasonCodes.SublotTaskTypeConflict,
            await criterion.EvaluateAsync(
                Evaluation(other, wireToGate, other), TestContext.Current.CancellationToken));
    }

    /// <summary>一个 Sublot 上只有一种任务类型：照常放行，哪怕它有好几条候选。</summary>
    [Fact]
    public async Task ASublotWithOneTaskTypeIsNotRefusedHoweverManyCandidatesItHas()
    {
        AcceptedDemandSnapshot first = Demand("D-1", "SUBLOT-A", "WIRE_TO_GATE");
        AcceptedDemandSnapshot second = Demand("D-2", "SUBLOT-A", "WIRE_TO_GATE");

        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await new SublotTaskTypeConflictCriterion().EvaluateAsync(
                Evaluation(first, first, second), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 别的 Sublot 自相矛盾，不牵连这一条：边界是按 Sublot 划的。
    /// </summary>
    /// <remarks>
    /// 这正是「一辆车承载多条独立需求，各自保留任务类型、端点、状态、取消、仓位与审计边界」的另一面——
    /// 边界独立，所以一条的数据出问题不该让别的跟着停。按「本轮快照里存在冲突」去判会让整轮停摆，
    /// 而那种实现在只有一个 Sublot 的测试里看不出来。
    /// </remarks>
    [Fact]
    public async Task AConflictInAnotherSublotDoesNotTouchThisOne()
    {
        AcceptedDemandSnapshot clean = Demand("D-1", "SUBLOT-A", "WIRE_TO_GATE");
        AcceptedDemandSnapshot conflictedOne = Demand("D-2", "SUBLOT-B", "WIRE_TO_GATE");
        AcceptedDemandSnapshot conflictedTwo = Demand("D-3", "SUBLOT-B", "OTHER_PROCESS");

        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await new SublotTaskTypeConflictCriterion().EvaluateAsync(
                Evaluation(clean, clean, conflictedOne, conflictedTwo), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 判的是<b>本轮那一份完整快照</b>，不是历史：快照里只剩一条，就没有矛盾可言。
    /// </summary>
    /// <remarks>
    /// 同一个 Sublot 上一轮走 A 工艺、这一轮走 B 工艺，是普通的需求改写，不是自相矛盾。把判据写成跨快照比较，
    /// 每一次正常的工艺变更都会被挡住，而现场看到的是一批料莫名其妙派不出去。
    /// </remarks>
    [Fact]
    public async Task TheJudgementIsAboutThisRoundsSnapshotNotHistory()
    {
        AcceptedDemandSnapshot only = Demand("D-1", "SUBLOT-A", "OTHER_PROCESS");

        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await new SublotTaskTypeConflictCriterion().EvaluateAsync(
                Evaluation(only, only), TestContext.Current.CancellationToken));
    }

    private static AcceptedDemandSnapshot Demand(string demandId, string sublot, string workType) =>
        new(
            demandId,
            $"{sublot}|{workType}",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            WorkType: workType,
            Sublot: sublot);

    /// <summary>判 <paramref name="candidate"/>，而本轮快照里有 <paramref name="catalog"/> 这几条。</summary>
    private static DispatchCandidateEvaluation Evaluation(
        AcceptedDemandSnapshot candidate,
        params AcceptedDemandSnapshot[] catalog) =>
        new(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, catalog),
                new RiotMapStationCatalogSnapshot(25, Now, new string('c', 64), []),
                null!,
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                new VehicleDispatchPolicy(
                    [], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY")),
            new DispatchVehicleFacts(
                "BROKERX-1",
                "AGV-1",
                new OnboardDispatchFacts(1, [1, 2], true, true, true, true, false),
                new RiotVehicleObservation("BROKERX-1", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now));
}
