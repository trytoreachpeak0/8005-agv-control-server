using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 探针（control-server#643 第 1 步）：一站两条需求、离站等待开着，第一批闭环之后离站等待的起点与期限。
/// </summary>
/// <remarks>断言写的是 ADR-cross-0055 要求的行为；红即确认偏离。观测值打到输出里。</remarks>
public sealed class MiddleBatchDepartureWaitProbeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Loading = TimeSpan.FromSeconds(40);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMiddleBatchRestartsTheDepartureWait()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        DateTimeOffset atArrival = runtime.StationDepartureWaitStartedAt!.Value;
        (long arrivalRevision, DateTimeOffset? arrivalDeadline) = await LatestWorklistAsync(fixture);

        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync(FirstDemandId)).Stage);

        fixture.Clock.Advance(Loading);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset closedAt = fixture.Clock.GetUtcNow();
        JourneyRuntimeRow afterFirst = await fixture.RuntimeAsync(FirstDemandId);
        (long r1, DateTimeOffset? d1) = await LatestWorklistAsync(fixture);

        await TickAndRunAsync(fixture);
        JourneyRuntimeRow afterNext = await fixture.RuntimeAsync(FirstDemandId);
        (long r2, DateTimeOffset? d2) = await LatestWorklistAsync(fixture);

        // 到站起算的期限已过、按闭环起算的还没过：门证明关着，再跑一轮，看本站是否被期限结束。
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(atArrival + Wait - fixture.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset judgedAt = fixture.Clock.GetUtcNow();
        JourneyRuntimeRow afterDeadline = await fixture.RuntimeAsync(FirstDemandId);
        JourneyDemandRow first = await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId);
        JourneyDemandRow second = await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId);

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine,
            $"arrival start {atArrival:O}; worklist r{arrivalRevision} deadline {arrivalDeadline:O}",
            $"first batch closed at {closedAt:O}; stage {afterFirst.Stage}; start {afterFirst.StationDepartureWaitStartedAt:O}; worklist r{r1} deadline {d1:O}",
            $"next round: stage {afterNext.Stage}; start {afterNext.StationDepartureWaitStartedAt:O}; worklist r{r2} deadline {d2:O}",
            $"at {judgedAt:O} (arrival+wait+1s, closure+{judgedAt - closedAt}): stage {afterDeadline.Stage} / {afterDeadline.BlockReasonCode}; " +
            $"first {first.Status}, second {second.Status}"));

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, afterNext.Stage);
        Assert.Equal(closedAt, afterNext.StationDepartureWaitStartedAt);
        Assert.Equal(closedAt + Wait, d2);
        Assert.Equal(JourneyDemandStatuses.PendingLoad, first.Status);
    }

    /// <summary>
    /// 票外探针：车装完停在取货站等离站（离站等待在走），同一站追加一条需求。只观测、不判对错，看离站等待起点与车上期限怎么变。
    /// </summary>
    [Fact]
    public async Task ProbeAnAppendAtTheStationTheVehicleStandsAt()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset closure = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyRuntimeRow held = await JourneyOfAsync(fixture, FirstDemandId);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        (long r0, DateTimeOffset? d0) = await LatestWorklistAsync(fixture);
        List<string> log =
        [
            $"loaded at {closure:O}; stage {held.Stage}/{held.LoadingPhaseState}; start {held.StationDepartureWaitStartedAt:O}; " +
            $"stop {firstPickup.StopId} @ {firstPickup.StationId}; worklist r{r0} deadline {d0:O}"
        ];

        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.BoxCounts.Set(SecondSublot, 7);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(
            fixture, SecondDemandId, SecondSublot, firstPickup.StationId, firstPickup.StationRiotId);
        DateTimeOffset appendedAt = fixture.Clock.GetUtcNow();
        JourneyStopRow[] stops = [.. (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == held.JourneyId).ToArrayAsync(Token))
            .OrderBy(row => row.Sequence)];
        log.Add($"appended at {appendedAt:O}; stops: {string.Join(" | ", stops.Select(s => $"{s.Sequence}:{s.StopRole}@{s.StationId}/{s.Status}"))}");

        for (int round = 0; round < 3; round++)
        {
            await fixture.HearFromPeerAsync();
            await TickAndRunAsync(fixture);
            JourneyRuntimeRow now = await JourneyOfAsync(fixture, FirstDemandId);
            (long r, DateTimeOffset? d) = await LatestWorklistAsync(fixture);
            log.Add($"round {round} at {fixture.Clock.GetUtcNow():O}: stage {now.Stage}/{now.LoadingPhaseState}; " +
                    $"start {now.StationDepartureWaitStartedAt:O}; current {(await CurrentStopAsync(fixture, FirstDemandId)).StopId}; " +
                    $"worklist r{r} deadline {d:O}");
        }

        // 原站离站期限到，车被放走去「下一站」（同一站的新停靠），到站后看新起点。
        fixture.Clock.Advance(held.StationDepartureWaitStartedAt!.Value + Wait - fixture.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow leaving = await JourneyOfAsync(fixture, FirstDemandId);
        log.Add($"past the first stop's deadline at {fixture.Clock.GetUtcNow():O}: stage {leaving.Stage}; start {leaving.StationDepartureWaitStartedAt:O}");
        if (leaving.Stage == JourneyRuntimeStage.AwaitingDepartureSafety)
        {
            await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
            await fixture.HearFromPeerAsync();
            JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
            for (int round = 0; round < 4 && arrived.Stage == JourneyRuntimeStage.AwaitingPickupArrival; round++)
            {
                log.Add($"  not yet arrived: {arrived.BlockReasonCode}");
                await fixture.HearFromPeerAsync();
                arrived = await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
            }
            (long r, DateTimeOffset? d) = await LatestWorklistAsync(fixture);
            log.Add($"arrived at the appended stop {(await CurrentStopAsync(fixture, SecondDemandId)).StopId} at {fixture.Clock.GetUtcNow():O}: " +
                    $"stage {arrived.Stage}; start {arrived.StationDepartureWaitStartedAt:O}; worklist r{r} deadline {d:O}");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, log));
    }

    /// <summary>修订号最高的那一版清单（即车此刻应持有的那一版）的修订号与期限。</summary>
    private static async Task<(long Revision, DateTimeOffset? Deadline)> LatestWorklistAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        long best = long.MinValue;
        DateTimeOffset? deadline = null;
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            long revision = payload.GetProperty("worklistRevision").GetInt64();
            if (revision <= best) continue;
            best = revision;
            JsonElement at = payload.GetProperty("stationDepartureDeadlineAt");
            deadline = at.ValueKind == JsonValueKind.Null ? null : at.GetDateTimeOffset();
        }
        return (best, deadline);
    }
}
