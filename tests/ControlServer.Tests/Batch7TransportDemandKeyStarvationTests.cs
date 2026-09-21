using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-05（control-server#210）与批次7-09（control-server#214）的接缝：业务键被抑制、或已被别的 <c>DemandId</c> 受理过的需求，
/// 越过防饥饿阈值也不告警、不进超时层（独立审查中 1）。
/// </summary>
/// <remarks>
/// <para>
/// REQ-0155 描述的正是 MesIngest 持续可见、给同一个键不断发新 <c>DemandId</c> 的情形，这种新需求的本地建单时刻常常很早。
/// 不排除的话，它一越过阈值就记一条 2161、写下 <c>StarvationEscalatedAt</c>，此后每轮排在超时层最前面——而它永远不会被接走。
/// </para>
/// <para>
/// 两处读的是同一份排除集合 <see cref="StarvationExclusions"/>：告警在轮末读，排序在开轮时读。这里的整轮断言看告警那一处；
/// 「不进超时层」断言同一份集合交给 <see cref="TaskStarvation.Assess"/> 之后不超时，并且先证明不交它时确实超时——
/// 否则「不超时」可能只是年龄或阈值没摆对。
/// </para>
/// </remarks>
public sealed class Batch7TransportDemandKeyStarvationTests
{
    private const string First = "10000000-0000-4000-8000-000000000001";
    private const string Reissued = "10000000-0000-4000-8000-000000000071";
    private const string Sublot = "SUBLOT-001";
    private const string Zone = "MAP-25-WIRE_TO_GATE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASuppressedKeyUnderANewDemandIdIsNeitherEscalatedNorPutInTheTimeoutLayer()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(First, Sublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(Sublot, 4);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", (await fixture.RuntimeAsync()).BlockReasonCode);
        await SuppressionAssertions.AssertTheDemandSuppressedAsync(fixture.Context, "CANCELLED_BY_STATION_TIMEOUT");

        await AssertTheReissuedDemandWaitsWithoutEscalationAsync(fixture, DispatchReasonCodes.TransportDemandKeySuppressed);
    }

    [Fact]
    public async Task AKeyCompletedUnderAnotherDemandIdIsNeitherEscalatedNorPutInTheTimeoutLayer()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(First, Sublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(Sublot, 4);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RunToCompletionAsync()).Stage);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);

        await AssertTheReissuedDemandWaitsWithoutEscalationAsync(fixture, DispatchReasonCodes.TransportDemandKeyAlreadyAccepted);
    }

    /// <summary>
    /// MesIngest 以新 DemandId 重发同一个键，本地建单在一天前，阈值 60 秒；跑三轮。它每轮被判成 <paramref name="reason"/>，
    /// 不告警、不写升级标记，排序拿到的那份集合里有它、交给 <see cref="TaskStarvation.Assess"/> 之后不超时。
    /// </summary>
    private static async Task AssertTheReissuedDemandWaitsWithoutEscalationAsync(RuntimeFixture fixture, string reason)
    {
        DispatchZoneParameterTableVersion thresholds = await fixture.ImportStarvationThresholdsAsync((Zone, 60));
        AcceptedDemandSnapshot reissued = fixture.Demand(Reissued, Sublot, Now.AddDays(-1));
        fixture.Catalog.Set(reissued);
        fixture.BoxCounts.Set(Sublot, 4);

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == Reissued, Token);
        Assert.Equal(reason, backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        Assert.Null(backlog.StarvationEscalatedAt);
        Assert.Null(backlog.StarvationEscalationParameterVersion);
        lock (fixture.StarvationLog.Entries)
        {
            Assert.DoesNotContain(fixture.StarvationLog.Entries, entry =>
                entry.Message.Contains("Starvation escalation", StringComparison.Ordinal));
        }

        HashSet<string> excluded = await StarvationExclusions.ReadAsync(fixture.Context, [reissued], Token);
        Assert.Contains(Reissued, excluded);
        AreaAssignmentTableVersion? areas = await new AreaAssignmentStore(
                fixture.Context, JourneyRuntimeWorkerTestKit.CreateGovernedPublisher(fixture.Context))
            .ReadCurrentAsync(Token);
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Assert.True(TaskStarvation.Assess(reissued, now, areas, thresholds).Overdue,
            "without the exclusion the reissued demand must be overdue, or the next assertion proves nothing");
        Assert.False(TaskStarvation.Assess(reissued, now, areas, thresholds, excluded).Overdue);
    }
}
