using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>
/// Batch 7-04 (control-server#209): the dispatch round moves out of the engine without a single decision changing.
/// </summary>
/// <remarks>
/// <para>
/// Each round below is written down whole -- which vehicle took which demand, every backlog row's reason and times,
/// what the round-end hook was handed, every RIoT order and every log event with its id and arguments -- and
/// compared against a transcript taken on <c>fp/v2-impl@cc8e7992</c>, the commit before the move, where these tests
/// were first run green. The literals are that run's output, not something derived from the code under test.
/// </para>
/// <para>
/// The other tests name the moments the move can get wrong: a demand one vehicle claimed staying claimed for the
/// vehicles behind it, a vehicle cut off by its budget taking what it had staged with it, and vehicles already under
/// way staying out of the round.
/// </para>
/// </remarks>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// The budget of the vehicle a test cuts off. Only that vehicle gets it: the others keep the fixture's 30 seconds,
    /// so a cold first intake -- JIT and EF query compilation -- cannot run them out as well and make the test flaky.
    /// </summary>
    private const int CutOffBudgetMilliseconds = 1000;

    /// <summary>
    /// The budget of a vehicle a test cuts off <em>at</em> intake rather than in the chain. Wider than the one
    /// above because the segment has to get all the way to the acceptance first: a budget that fired before it
    /// would leave no claim to withdraw, and the tests below would pass on a round that never made the claim they
    /// are about. Each of them says so with an assertion rather than trusting the number.
    /// </summary>
    private const int ClaimCutOffBudgetMilliseconds = 3000;

    // ---- round equivalence (control-server#209) -----------------------------------------------------------

    /// <summary>
    /// Two vehicles, three candidates in three AREAs, created in an order that is neither the catalog's nor the
    /// demand ids': the oldest two are taken, oldest first, and the third stays eligible.
    /// </summary>
    [Fact]
    public async Task ARoundWithMoreCandidatesThanVehiclesDecidesAsBeforeTheMove()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2]);
        fixture.Catalog.Set(
        [
            FleetFixture.Demand(0, "N1-1", 3),
            FleetFixture.Demand(1, "N1-2", -4),
            FleetFixture.Demand(2, "N1-3", 1),
        ]);

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        await AssertTranscriptAsync(fixture, """
            journey V1 D1 AwaitingPickupArrival block=- pickup=13 slots=[1] baskets=1
            journey V2 D2 AwaitingPickupArrival block=- pickup=14 slots=[1] baskets=1
            backlog D0 ELIGIBLE first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=-
            backlog D1 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D2 ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            outcome accepted=D1,D2
              V1: D1=ELIGIBLE[1] D2=ELIGIBLE[1] D0=ELIGIBLE[1]
              V2: D1=DEMAND_ALREADY_ACCEPTED[1] D2=ELIGIBLE[1] D0=ELIGIBLE[1]
            riot create BROKERX-0001 W2G-10000001-0000-4000-8000-000000000001-PICKUP-1 -> 13
            riot create BROKERX-0002 W2G-10000002-0000-4000-8000-000000000002-PICKUP-1 -> 14
            catalog reads 3
            """);
    }

    /// <summary>
    /// One demand, three vehicles: the first vehicle takes it, and the two behind it see it as already accepted
    /// rather than trying intake on it again.
    /// </summary>
    [Fact]
    public async Task ADemandTakenByOneVehicleIsNoLongerACandidateForTheVehiclesBehindItInTheSameRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(
            ["ELIGIBLE", "DEMAND_ALREADY_ACCEPTED", "DEMAND_ALREADY_ACCEPTED"],
            outcome.CompletedVehicles.Select(vehicle => Assert.Single(vehicle.Verdicts).ReasonCode).ToArray());
        // The round's decision read and one intake re-read: nobody behind the first vehicle got as far as intake.
        Assert.Equal(2, fixture.Catalog.ReadCount);
        await AssertTranscriptAsync(fixture, """
            journey V1 D0 AwaitingPickupArrival block=- pickup=12 slots=[1] baskets=1
            backlog D0 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            outcome accepted=D0
              V1: D0=ELIGIBLE[1]
              V2: D0=DEMAND_ALREADY_ACCEPTED[1]
              V3: D0=DEMAND_ALREADY_ACCEPTED[1]
            riot create BROKERX-0001 W2G-10000000-0000-4000-8000-000000000000-PICKUP-1 -> 12
            catalog reads 2
            """);
    }

    /// <summary>
    /// A demand stays claimed for the rest of the round even when intake then refuses it: the first vehicle's
    /// final re-read finds its demand gone, and the vehicles behind it still do not try it.
    /// </summary>
    [Fact]
    public async Task ADemandIntakeFoundGoneStaysClaimedForTheRestOfTheRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        fixture.Catalog.GoneOnReread.Add(FleetFixture.Demand(0, "N1-1", 0).DemandId);

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        // Intake refused the first vehicle's pick, and the first vehicle takes nothing else this round.
        Assert.Equal(
            [FleetFixture.AgvIds[1]],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId).ToArrayAsync(
                TestContext.Current.CancellationToken));
        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.All(
            outcome.CompletedVehicles.Skip(1),
            vehicle => Assert.Equal(
                "DEMAND_ALREADY_ACCEPTED",
                vehicle.Verdicts.Single(verdict => verdict.Evaluation.Candidate.DemandId ==
                    FleetFixture.Demand(0, "N1-1", 0).DemandId).ReasonCode));
        await AssertTranscriptAsync(fixture, """
            journey V2 D1 AwaitingPickupArrival block=- pickup=13 slots=[1] baskets=1
            backlog D0 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=-
            backlog D1 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            outcome accepted=D0,D1
              V1: D0=ELIGIBLE[1] D1=ELIGIBLE[1]
              V2: D0=DEMAND_ALREADY_ACCEPTED[1] D1=ELIGIBLE[1]
              V3: D0=DEMAND_ALREADY_ACCEPTED[1] D1=DEMAND_ALREADY_ACCEPTED[1]
            riot create BROKERX-0002 W2G-10000001-0000-4000-8000-000000000001-PICKUP-1 -> 13
            catalog reads 3
            """);
    }

    /// <summary>
    /// The first vehicle runs out its budget in the middle of the admission chain, on its second candidate's box
    /// count; the other two are served.
    /// </summary>
    [Fact]
    public async Task AVehicleCutOffInTheMiddleOfTheChainDecidesAsBeforeTheMove()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet[0].RoundTimeoutMilliseconds = CutOffBudgetMilliseconds);
        // 第四次，不是第二次（批次7-06，control-server#211）。挂起点要落在「第一辆车的第二个候选」上，
        // 而翻转把 box count 的调用序从「一辆车把候选走完再换下一辆」换成了「一条候选问完所有车再换下一条」：
        // 三辆车判第一条候选用掉前三次，第一辆车的第二个候选因此是第四次。这个数字跟着调用序走，
        // 不是断言的一部分——它下面那条对「哪辆车被切断」的断言才是。
        fixture.BoxCounts.HangOnCall = 4;

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        await AssertTranscriptAsync(fixture, """
            journey V1 D0 AwaitingPickupArrival block=- pickup=12 slots=[1] baskets=1
            journey V2 D1 AwaitingPickupArrival block=- pickup=13 slots=[1] baskets=1
            journey V3 D2 AwaitingPickupArrival block=- pickup=14 slots=[1] baskets=1
            backlog D0 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D1 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D2 ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            outcome accepted=D0,D1,D2
              V2: D0=DEMAND_ALREADY_ACCEPTED[1] D1=ELIGIBLE[1] D2=ELIGIBLE[1]
              V3: D0=DEMAND_ALREADY_ACCEPTED[1] D1=DEMAND_ALREADY_ACCEPTED[1] D2=ELIGIBLE[1]
            riot create BROKERX-0001 W2G-10000000-0000-4000-8000-000000000000-PICKUP-1 -> 12
            riot create BROKERX-0002 W2G-10000001-0000-4000-8000-000000000001-PICKUP-1 -> 13
            riot create BROKERX-0003 W2G-10000002-0000-4000-8000-000000000002-PICKUP-1 -> 14
            log 2104 LogVehicleRoundBudgetExhausted Warning: Vehicle V1 exhausted its 1000 ms dispatch budget; the round moved on to the remaining vehicles.
            catalog reads 4
            """);
    }

    /// <summary>A catalog that cannot be read ends the round's dispatch before any backlog row or verdict.</summary>
    [Fact]
    public async Task AFailedCatalogReadDecidesAsBeforeTheMove()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Unreachable = true;

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        Assert.Empty(fixture.RoundOutcomes.Outcomes);
        await AssertTranscriptAsync(fixture, """
            log 2101 LogCatalogPollFailed Warning: MesIngest catalog polling failed closed; no journey was accepted.
            catalog reads 1
            """);
    }

    /// <summary>
    /// What a vehicle cut off by its budget had staged is dropped with it: none of it is written under the next
    /// vehicle's save, and the next vehicle reads the backlog as the database holds it.
    /// </summary>
    /// <remarks>
    /// This is the one place the move can go wrong without any decision changing on the happy path: the round and
    /// the engine have to share one <see cref="ControlServerDbContext"/>, so that <c>ChangeTracker.Clear()</c> in
    /// the round clears the tracker every later save goes through. The backlog rows exist before the round, so the
    /// first vehicle's staged changes are modifications of tracked rows -- the kind a later save would carry out
    /// silently, rather than a duplicate insert that would fail loudly.
    /// </remarks>
    [Fact]
    public async Task WhatAVehicleCutOffMidChainHadStagedIsNotSavedWithTheNextVehicle()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet[0].RoundTimeoutMilliseconds = CutOffBudgetMilliseconds);
        DateTimeOffset earlier = Now.AddMinutes(-30);
        foreach (AcceptedDemandSnapshot demand in (await fixture.Catalog.ReadCatalogAsync(
                     TestContext.Current.CancellationToken)).Items)
        {
            fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
            {
                DemandId = demand.DemandId,
                TransportDemandKey = demand.TransportDemandKey,
                FirstSeenAt = earlier,
                DemandCreatedAt = demand.CreatedAt,
                DecisionFingerprint = "fingerprint-before-the-round",
                ReasonCode = "REASON-BEFORE-THE-ROUND",
                LastSeenAt = earlier,
            });
        }

        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        // Every criterion that writes saves as it goes, so on today's code nothing of the chain's own is left pending
        // at a cut-off. The test stands in for the segment instead: when the first vehicle's second box count hangs,
        // it stages a change on a backlog row the segment is tracking, as a segment cut off between an upsert and
        // its save would leave it.
        const string StagedReason = "STAGED-BY-THE-VEHICLE-CUT-OFF";
        List<object> staged = [];
        // 第四次，不是第二次（批次7-06，control-server#211）。挂起点要落在「第一辆车的第二个候选」上，
        // 而翻转把 box count 的调用序从「一辆车把候选走完再换下一辆」换成了「一条候选问完所有车再换下一条」：
        // 三辆车判第一条候选用掉前三次，第一辆车的第二个候选因此是第四次。这个数字跟着调用序走，
        // 不是断言的一部分——它下面那条对「哪辆车被切断」的断言才是。
        fixture.BoxCounts.HangOnCall = 4;
        fixture.BoxCounts.OnHang = () =>
        {
            foreach (EntityEntry<JourneyBacklogRow> entry in fixture.Context.ChangeTracker.Entries<JourneyBacklogRow>())
            {
                entry.Entity.ReasonCode = StagedReason;
                staged.Add(entry.Entity);
            }
        };
        List<object> savedAfterTheHang = [];
        fixture.Context.SavingChanges += (_, _) =>
        {
            if (staged.Count > 0)
            {
                savedAfterTheHang.AddRange(fixture.Context.ChangeTracker.Entries()
                    .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
                    .Select(entry => entry.Entity));
            }
        };

        await fixture.RunRoundAsync();

        Assert.NotEmpty(staged);
        Assert.NotEmpty(savedAfterTheHang);
        Assert.DoesNotContain(savedAfterTheHang, entity => staged.Contains(entity, ReferenceEqualityComparer.Instance));
        JourneyBacklogRow[] backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.All(backlog, row =>
        {
            Assert.Equal(earlier, row.FirstSeenAt);
            Assert.NotEqual(StagedReason, row.ReasonCode);
        });
        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles.Select(vehicle => vehicle.AgvId).ToArray());
    }

    /// <summary>
    /// The host builds the round, and the Onboard facts reader it shares with the engine, in the engine's own scope:
    /// scoped like the engine and the <see cref="ControlServerDbContext"/>, so all three get the one context the
    /// scope holds -- which is what makes the round's <c>ChangeTracker.Clear()</c> clear the tracker the round's
    /// later saves go through.
    /// </summary>
    [Fact]
    public void TheHostBuildsTheRoundInTheEnginesScope()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DispatchRoundRunner)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(OnboardDispatchFactsReader)).Lifetime);
    }

    /// <summary>
    /// 全车在途不再是「这一轮没什么可做」：目录照读，三辆车照样进轮次结局（REQ-0205；批次7-06，
    /// control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一条以前钉的是相反的事——全车在途时轮次在读目录之前就结束，连孤儿检查都不跑。那时在途车走一条
    /// 一律拒绝的占位路径，问它等于白问，提前退出省下的是纯粹的浪费。本票让在途车与空闲车在同一张候选表上
    /// 竞争，「全车在途」于是成了一种有活可派的局面。
    /// </para>
    /// <para>
    /// <b>单车现场里这不是边角情形，而是常态</b>：车一接单就不再空闲，此后到卸完货为止的每一条新需求都只能
    /// 靠追加接。按空闲车判会让这些需求一条都看不见——同区追加那条 L2 场景第一次跑出来正是这个样子，
    /// 第二条需求连积压行都没有。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WithEveryVehicleUnderWayTheRoundStillReadsTheCatalogAndReports()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.RunRoundAsync();
        Assert.Equal(3, await fixture.Context.JourneyRuntimes.CountAsync(TestContext.Current.CancellationToken));
        int catalogReads = fixture.Catalog.ReadCount;
        fixture.RoundOutcomes.Outcomes.Clear();

        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.True(fixture.Catalog.ReadCount > catalogReads, "The round did not read the catalog.");
        Assert.Equal(
            FleetFixture.AgvIds.Order(StringComparer.Ordinal).ToArray(),
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles
                .Select(vehicle => vehicle.AgvId).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 孤儿检查跟着一起搬到了前面：全车在途时它照跑，一条没有旅程的已受理需求当场就被抓出来。
    /// </summary>
    /// <remarks>
    /// 它守的是受理这道口子，所以它该在「这一轮要不要接活」之前跑。翻转之前全车在途时轮次提早结束，
    /// 这个检查也就一并不跑——孤儿会被推到下一轮有车空出来时才发现。现在不会了。
    /// </remarks>
    [Fact]
    public async Task WithEveryVehicleUnderWayTheOrphanCheckStillGuardsIntake()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.RunRoundAsync();
        await fixture.AcceptOrphanAsync();

        BusinessIdentityConflictException error =
            await Assert.ThrowsAsync<BusinessIdentityConflictException>(
                () => fixture.RunRoundAsync(TimeSpan.FromSeconds(1)));

        Assert.Contains("no production journey runtime", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在途车与空闲车在同一张候选表上竞争（REQ-0205；批次7-06，control-server#211）：它进轮次结局，
    /// 对每条候选都有自己的裁决，身份本身不产生优先级。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一条与它替换掉的那两条是同一件事的两面。</b>翻转之前在途车走一条占位路径
    /// （<c>InTransitAppendNotOpened</c>），一律拒绝、不留痕迹，所以那时的用例断言的是「它不在轮次结局里」
    /// 和「万一那条路径说了 yes，轮次要大声失败而不是静默忽略」。本票把那条路径换成真正的在途资格链，
    /// 于是两条断言各自的前提都不存在了——占位类连同它的接口一起删掉了。
    /// </para>
    /// <para>
    /// 换来的保证写在这里：在途车被问、被记、和空闲车比同一批候选。它接不接得下由链与插位规划决定
    /// （<c>Batch7EnRouteAppendPlannerTests</c> 守那一半），这里只问它有没有被当成车队的一员。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AVehicleUnderWayCompetesForTheSameCandidatesAsTheIdleOnes()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        Assert.Equal(FleetFixture.AgvIds[0], (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).AgvId);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        fixture.RoundOutcomes.Outcomes.Clear();

        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(
            FleetFixture.AgvIds.Order(StringComparer.Ordinal).ToArray(),
            outcome.CompletedVehicles.Select(vehicle => vehicle.AgvId).Order(StringComparer.Ordinal).ToArray());
        DispatchVehicleOutcome underWay = outcome.CompletedVehicles
            .Single(vehicle => vehicle.AgvId == FleetFixture.AgvIds[0]);
        Assert.NotEmpty(underWay.Verdicts);
    }

    // ---- per-vehicle failure isolation (control-server#231) -------------------------------------------------

    /// <summary>
    /// A vehicle in the middle of the roster throwing leaves the vehicles on either side of it served, written down
    /// whole the way the equivalence transcripts above are.
    /// </summary>
    /// <remarks>
    /// Batch 7-04 pinned this case as <c>AVehicleWhoseRiotReadThrowsEndsTheRoundForTheVehiclesBehindIt</c> -- the
    /// round ended, the third vehicle went unserved and the round-end hook never ran -- deliberately as it was
    /// rather than as it should be. control-server#231 is the change that was waiting for, so the transcript is
    /// rewritten here rather than kept.
    /// </remarks>
    [Fact]
    public async Task AVehicleWhoseRiotReadThrowsLeavesTheVehiclesOnEitherSideOfItServed()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.FailOn = FleetFixture.VehicleKeys[1];

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        Assert.Equal(
            [FleetFixture.AgvIds[0], FleetFixture.AgvIds[2]],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId)
                .OrderBy(agvId => agvId)
                .ToArrayAsync(TestContext.Current.CancellationToken));
        await AssertTranscriptAsync(fixture, """
            journey V1 D0 AwaitingPickupArrival block=- pickup=12 slots=[1] baskets=1
            journey V3 D1 AwaitingPickupArrival block=- pickup=13 slots=[1] baskets=1
            backlog D0 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D1 ACCEPTED first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D2 ELIGIBLE first=2026-09-08T06:00:00.0020000+00:00 last=2026-09-08T06:00:00.0020000+00:00 accepted=-
            outcome accepted=D0,D1
              V1: D0=ELIGIBLE[1] D1=ELIGIBLE[1] D2=ELIGIBLE[1]
              V3: D0=DEMAND_ALREADY_ACCEPTED[1] D1=ELIGIBLE[1] D2=ELIGIBLE[1]
            riot create BROKERX-0001 W2G-10000000-0000-4000-8000-000000000000-PICKUP-1 -> 12
            riot create BROKERX-0003 W2G-10000001-0000-4000-8000-000000000001-PICKUP-1 -> 13
            log 2123 LogVehicleRoundFailed Warning: Vehicle V2 could not be served this round: HttpRequestException. The round moved on to the remaining vehicles.
            catalog reads 3
            """);
    }

    /// <summary>
    /// The first vehicle's RIoT read throws, and only that vehicle is skipped: the two behind it are served, the
    /// round-end hook still runs, and the failure is one warning naming the vehicle and what was thrown.
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseRiotReadThrowsIsSkippedWhileTheVehiclesBehindItAreServed()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.FailOn = FleetFixture.VehicleKeys[0];

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId)
                .OrderBy(agvId => agvId)
                .ToArrayAsync(TestContext.Current.CancellationToken));
        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        // The vehicle that threw did not finish deciding, so it is absent the way a budget-exhausted one is.
        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            outcome.CompletedVehicles.Select(vehicle => vehicle.AgvId).ToArray());
        EventRecordingLogger<JourneyRuntimeEngine>.Entry warning =
            Assert.Single(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2123);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(FleetFixture.AgvIds[0], warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a vehicle that threw mid-chain had staged is dropped with it, exactly as a budget cut-off's is: none of
    /// it is written under the next vehicle's save, and the backlog keeps what the database held.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="WhatAVehicleCutOffMidChainHadStagedIsNotSavedWithTheNextVehicle"/>, because the
    /// hazard is the same one: the round and the engine share a <see cref="ControlServerDbContext"/>, so a segment's
    /// abandoned modifications of tracked rows would be carried out silently by whatever saves next.
    /// </remarks>
    [Fact]
    public async Task WhatAVehicleThatThrewMidChainHadStagedIsNotSavedWithTheNextVehicle()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        DateTimeOffset earlier = Now.AddMinutes(-30);
        foreach (AcceptedDemandSnapshot demand in (await fixture.Catalog.ReadCatalogAsync(
                     TestContext.Current.CancellationToken)).Items)
        {
            fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
            {
                DemandId = demand.DemandId,
                TransportDemandKey = demand.TransportDemandKey,
                FirstSeenAt = earlier,
                DemandCreatedAt = demand.CreatedAt,
                DecisionFingerprint = "fingerprint-before-the-round",
                ReasonCode = "REASON-BEFORE-THE-ROUND",
                LastSeenAt = earlier,
            });
        }

        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        // The chain's own criteria save as they go and the box-count reader's failures are caught inside the slot
        // criterion, so nothing of the chain is left pending when a segment throws. The test stands in for the
        // segment instead: when the first vehicle's RIoT read fails, it stages a change on a backlog row the round
        // is tracking, as a segment cut off between an upsert and its save would leave it.
        const string StagedReason = "STAGED-BY-THE-VEHICLE-THAT-THREW";
        List<object> staged = [];
        fixture.Riot.FailOn = FleetFixture.VehicleKeys[0];
        fixture.Riot.OnFail = () =>
        {
            foreach (EntityEntry<JourneyBacklogRow> entry in fixture.Context.ChangeTracker.Entries<JourneyBacklogRow>())
            {
                entry.Entity.ReasonCode = StagedReason;
                staged.Add(entry.Entity);
            }
        };
        List<object> savedAfterTheFailure = [];
        fixture.Context.SavingChanges += (_, _) =>
        {
            if (staged.Count > 0)
            {
                savedAfterTheFailure.AddRange(fixture.Context.ChangeTracker.Entries()
                    .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
                    .Select(entry => entry.Entity));
            }
        };

        await fixture.RunRoundAsync();

        Assert.NotEmpty(staged);
        Assert.NotEmpty(savedAfterTheFailure);
        Assert.DoesNotContain(
            savedAfterTheFailure, entity => staged.Contains(entity, ReferenceEqualityComparer.Instance));
        JourneyBacklogRow[] backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.All(backlog, row =>
        {
            Assert.Equal(earlier, row.FirstSeenAt);
            Assert.NotEqual(StagedReason, row.ReasonCode);
        });
        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles.Select(vehicle => vehicle.AgvId).ToArray());
    }

    /// <summary>
    /// A vehicle that threw is not a vehicle that finished, so a round it was in cannot conclude "no vehicle on the
    /// roster can take this": the other vehicle finding the pickup unreachable raises nothing, and the block already
    /// standing is neither refreshed nor cleared.
    /// </summary>
    /// <remarks>
    /// This is why the exception path counts the vehicle as unfinished rather than finished. A vehicle whose RIoT
    /// read failed said nothing about the demand; counting it as having answered would turn "this one vehicle is
    /// momentarily unreadable" into the fleet-wide alarm REQ-0210 reserves for a demand nothing can ever carry.
    /// </remarks>
    [Fact]
    public async Task AVehicleThatThrewLeavesTheRoundUnableToRaiseAStructuralBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2],
            extraCriterion: new PickupUnreachableForEveryCandidate());
        fixture.Riot.FailOn = FleetFixture.VehicleKeys[0];
        StructuralDispatchBlockStore blocks = new(fixture.Context);
        fixture.RoundOutcomes.Inner = new StructuralDispatchBlockSink(
            blocks,
            fixture.SlotPositions,
            new VehicleRoster(Microsoft.Extensions.Options.Options.Create(fixture.Options)),
            NullLogger<StructuralDispatchBlockSink>.Instance);
        AcceptedDemandSnapshot blocked = FleetFixture.Demand(0, "N1-1", 0);
        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        await blocks.RaiseOrRefreshAsync(
            blocked.DemandId,
            "ROUTE_GRAPH_PICKUP_UNREACHABLE",
            blocked.TransportDemandKey,
            "{}",
            raisedAt,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        await fixture.RunRoundAsync();

        // The round did reach its end: the hook ran, with the one vehicle that finished.
        Assert.Equal(
            [FleetFixture.AgvIds[1]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles
                .Select(vehicle => vehicle.AgvId).ToArray());
        fixture.Context.ChangeTracker.Clear();
        StructuralDispatchBlockRow row = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(blocked.DemandId, row.DemandId);
        Assert.Null(row.ClearedAt);
        // Untouched: neither raised again nor refreshed, because this round proved nothing either way.
        Assert.Equal(raisedAt, row.LastSeenAt);
    }

    /// <summary>
    /// The host shutting down is not one vehicle's failure: its cancellation leaves the round rather than being
    /// caught and logged as a vehicle that could not be served.
    /// </summary>
    [Fact]
    public async Task TheHostsShutdownCancellationLeavesTheRoundRatherThanBeingCaught()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        using CancellationTokenSource shutdown = new();
        fixture.BoxCounts.HangOnCall = 1;
        fixture.BoxCounts.OnHang = shutdown.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Engine.ExecuteOnceAsync(shutdown.Token));

        Assert.Empty(fixture.RoundOutcomes.Outcomes);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2123);
    }

    /// <summary>
    /// 问不动一辆在途车，与问不动一辆空闲车分开记，而且轮末钩子照样跑：在途车这一路是被隔离的。
    /// </summary>
    /// <remarks>
    /// 触发点随本票换了一个（control-server#211）：原来是那条占位在途路径自己抛，而它已经被真正的在途资格链
    /// 取代；现在让这辆在途车的 RIoT 事实读抛，那是同一条链上真实存在的失败方式。<b>两个事件 id 的分工没变</b>——
    /// 2125 是「这辆在途车问不动」，2123 是「这辆空闲车白闲了一轮」。对一辆正跑着自己旅程的车说
    /// 「could not be served this round」是句错话，所以它不该出现。
    /// </remarks>
    [Fact]
    public async Task AnInTransitVehicleThatCannotBeAskedIsLoggedUnderItsOwnEventId()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        Assert.Equal(FleetFixture.AgvIds[0], (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).AgvId);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        fixture.RoundOutcomes.Outcomes.Clear();
        fixture.EngineLog.Entries.Clear();
        fixture.Riot.FailOn = FleetFixture.VehicleKeys[0];

        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles
                .Select(vehicle => vehicle.AgvId).ToArray());
        EventRecordingLogger<JourneyRuntimeEngine>.Entry warning =
            Assert.Single(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2125);
        Assert.Contains(FleetFixture.AgvIds[0], warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2123);
    }

    /// <summary>
    /// A cancellation that is not the host shutting down belongs to the vehicle, not to the round: a store's own
    /// write deadline firing mid-segment skips that vehicle, and the ones behind it are still served.
    /// </summary>
    /// <remarks>
    /// The real one is <c>WireToGateOrchestration</c>'s five-second evidence write timeout, which runs a
    /// <see cref="CancellationTokenSource"/> of its own inside the segment. Its <see cref="OperationCanceledException"/>
    /// answers to neither the budget (that source did not fire) nor the host token (nobody is shutting down), so a
    /// catch written against the exception's type rather than against the host's token would let it end the round —
    /// exactly the failure control-server#231 removes. The fake stands in for it with an already-cancelled token.
    /// </remarks>
    [Fact]
    public async Task AVehicleWhoseOwnDeadlineFiresIsSkippedRatherThanEndingTheRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.CancelOn = FleetFixture.VehicleKeys[0];

        await fixture.RunRoundAsync();

        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId)
                .OrderBy(agvId => agvId)
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles
                .Select(vehicle => vehicle.AgvId).ToArray());
        EventRecordingLogger<JourneyRuntimeEngine>.Entry warning =
            Assert.Single(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2123);
        Assert.Contains(FleetFixture.AgvIds[0], warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OperationCanceledException), warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One of this server's own invariants breaking is reported as the defect it is — Error, under its own event
    /// id — rather than as the weather the isolation was built for.
    /// </summary>
    /// <remarks>
    /// Isolating the vehicle keeps the fleet moving, which is the point of control-server#231, but a
    /// <see cref="BusinessIdentityConflictException"/> or a REQ-0305 freeze that came out incomplete is not an
    /// unreachable peer: it will be there again next round, and every round after. Logged at Warning beside the
    /// unreachable peers, it would be one line a day nobody reads.
    /// </remarks>
    [Fact]
    public async Task AVehicleThatBreaksOneOfTheServersOwnInvariantsIsReportedAsADefect()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Acceptances.ThrowOnFirstAccept =
            new BusinessIdentityConflictException("DemandId is already bound to different content.");

        await fixture.RunRoundAsync();

        EventRecordingLogger<JourneyRuntimeEngine>.Entry fault =
            Assert.Single(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2124);
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Contains(FleetFixture.AgvIds[0], fault.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(BusinessIdentityConflictException), fault.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2123);
        // Still isolated: the vehicles behind it are served, and the round reports.
        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles
                .Select(vehicle => vehicle.AgvId).ToArray());
    }

    /// <summary>
    /// A demand claimed by a segment that then threw before the acceptance was committed does not count as
    /// accepted at the round's end: the structural block standing against it survives the round.
    /// </summary>
    /// <remarks>
    /// The claim is taken before intake on purpose, so the vehicles behind cannot pick the same demand. When the
    /// segment throws instead of reporting, that claim would otherwise outlive the attempt it stood for, and
    /// <see cref="StructuralDispatchBlockSink"/> clears a block for every demand the round says was accepted — so
    /// an alarm that nothing had disproved would be cleared, then raised again as new next round. Whether the
    /// claim was made good on is read from the database rather than guessed from where the exception came from.
    /// </remarks>
    [Fact]
    public async Task AClaimTheSegmentNeverMadeGoodOnDoesNotClearAStructuralBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1]);
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        StructuralDispatchBlockStore blocks = new(fixture.Context);
        fixture.RoundOutcomes.Inner = new StructuralDispatchBlockSink(
            blocks,
            fixture.SlotPositions,
            new VehicleRoster(Microsoft.Extensions.Options.Options.Create(fixture.Options)),
            NullLogger<StructuralDispatchBlockSink>.Instance);
        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        await blocks.RaiseOrRefreshAsync(
            only.DemandId,
            "ROUTE_GRAPH_PICKUP_UNREACHABLE",
            only.TransportDemandKey,
            "{}",
            raisedAt,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        fixture.Acceptances.ThrowOnFirstAccept = new HttpRequestException("The acceptance could not be written.");

        await fixture.RunRoundAsync();

        Assert.Empty(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();
        StructuralDispatchBlockRow row = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(row.ClearedAt);
        Assert.Equal(raisedAt, row.LastSeenAt);
    }

    /// <summary>
    /// A demand claimed by a segment whose budget cut it off before the acceptance committed does not count as
    /// accepted at the round's end either: the structural block standing against it survives the round.
    /// </summary>
    /// <remarks>
    /// The same hole control-server#231 closed in the catch beside this one, left open there because that ticket
    /// was not to change what the budget path does (control-server#239). A budget firing between the claim and the
    /// acceptance leaves the round carrying a demand nothing took, and <see cref="StructuralDispatchBlockSink"/>
    /// clears a block for every demand the round says was accepted -- so an alarm nothing had disproved is cleared
    /// and raised again as new the next round, a 2115/2114 pair per round for as long as the demand is there.
    /// </remarks>
    [Fact]
    public async Task AClaimTheBudgetCutOffBeforeItsAcceptanceDoesNotClearAStructuralBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options =>
            {
                options.Fleet = options.Fleet[..1];
                options.Fleet[0].RoundTimeoutMilliseconds = ClaimCutOffBudgetMilliseconds;
            });
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, only, raisedAt);
        fixture.Acceptances.HangBeforeFirstAccept = true;

        await fixture.RunRoundAsync();

        // The segment reached intake and so did claim the demand; without this a budget that fired earlier would
        // leave nothing claimed and every assertion below would pass on a round this test is not about.
        Assert.False(fixture.Acceptances.HangBeforeFirstAccept);
        // Nothing was accepted, which is what makes the claim a lie.
        Assert.Empty(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();
        // The consequence first: the block is what the operator sees, and clearing it here is what makes the next
        // round raise the same alarm as new.
        StructuralDispatchBlockRow row = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(row.ClearedAt);
        Assert.Equal(raisedAt, row.LastSeenAt);
        // The round proved nothing about this block either way, so it wrote neither of the two lines.
        Assert.Empty(blockLog.Entries);
        Assert.DoesNotContain(
            only.DemandId,
            Assert.Single(fixture.RoundOutcomes.Outcomes).Round.AcceptedDemandIds);
    }

    /// <summary>
    /// A demand whose acceptance did commit before the budget cut the segment off keeps its claim: the round counts
    /// it as accepted, and the block standing against it is cleared as on any other round.
    /// </summary>
    /// <remarks>
    /// This is why the withdrawal reads the database instead of the reason the segment ended. A budget fires
    /// wherever the segment happens to be, the acceptance transaction included, and a segment cut off just after it
    /// committed has made its claim good. Withdrawing on "the budget ended this segment" would take back a true
    /// claim and leave a demand that was accepted carrying an alarm nobody can act on.
    /// </remarks>
    [Fact]
    public async Task AClaimTheBudgetCutOffAfterItsAcceptanceIsNotWithdrawn()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options =>
            {
                options.Fleet = options.Fleet[..1];
                options.Fleet[0].RoundTimeoutMilliseconds = ClaimCutOffBudgetMilliseconds;
            });
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, only, Now.AddMinutes(-30));
        fixture.Acceptances.HangAfterFirstAccept = true;

        await fixture.RunRoundAsync();

        Assert.False(fixture.Acceptances.HangAfterFirstAccept);
        Assert.Single(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            only.DemandId,
            Assert.Single(fixture.RoundOutcomes.Outcomes).Round.AcceptedDemandIds);
        fixture.Context.ChangeTracker.Clear();
        StructuralDispatchBlockRow row = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(row.ClearedAt);
        Assert.Equal(2115, Assert.Single(blockLog.Entries).EventId.Id);
    }

    /// <summary>
    /// The withdrawal lands before the next vehicle starts: a demand the budget cut off ahead of its acceptance is
    /// back in play inside the same round, and the vehicle behind takes it.
    /// </summary>
    /// <remarks>
    /// The claim exists so that the vehicles behind stop considering a demand while one vehicle commits to it.
    /// Taking it back a moment too late -- once the loop had moved on, or at the round's end -- would leave a
    /// demand nobody is working on unserved for a whole round. Nothing here runs in parallel: the round walks its
    /// vehicles in series, and this pins the withdrawal to the gap between one segment ending and the next
    /// starting.
    /// </remarks>
    [Fact]
    public async Task AClaimTheBudgetCutOffIsBackInPlayForTheVehicleBehindInTheSameRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet[0].RoundTimeoutMilliseconds = ClaimCutOffBudgetMilliseconds);
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        fixture.Acceptances.HangBeforeFirstAccept = true;

        await fixture.RunRoundAsync();

        Assert.False(fixture.Acceptances.HangBeforeFirstAccept);
        Assert.Equal(
            [FleetFixture.AgvIds[1]],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId)
                .ToArrayAsync(TestContext.Current.CancellationToken));
        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        // The vehicle behind judged it on its merits rather than finding it still claimed by the one in front.
        DispatchVehicleOutcome behind = outcome.CompletedVehicles
            .Single(vehicle => vehicle.AgvId == FleetFixture.AgvIds[1]);
        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            behind.Verdicts.Single(verdict => verdict.Evaluation.Candidate.DemandId == only.DemandId).ReasonCode);
        Assert.Single(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    // ---- a claim the intake refused (control-server#242) --------------------------------------------------

    /// <summary>
    /// A demand whose decision facts changed under intake does not count as accepted at the round's end: the
    /// structural block standing against it survives the round.
    /// </summary>
    /// <remarks>
    /// The third instance of one defect. control-server#231 closed the throwing path and control-server#239 the
    /// budget one; this is the path where intake ran to its end and said no. The claim taken ahead of the call is
    /// kept on purpose -- the demand stays bound to this attempt, so the vehicles behind must not try it again --
    /// but the round-end hook reads the same set as "accepted this round" and clears the block on the strength of
    /// it. Nothing disproved the block, so the next round raises it again as new: a 2115/2114 pair per round, for
    /// as long as the demand is in the catalog and the refusal repeats.
    /// </remarks>
    [Fact]
    public async Task ACandidateChangedAtIntakeDoesNotClearAStructuralBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1]);
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, only, raisedAt);
        fixture.Catalog.ChangedOnReread.Add(only.DemandId);

        await fixture.RunRoundAsync();

        await AssertIntakeRefusedAsync(fixture, only, "FINAL_CATALOG_DECISION_FACT_CHANGED");
        await AssertBlockSurvivedTheRoundAsync(fixture, blockLog, raisedAt);
    }

    /// <summary>
    /// A demand the final admission gate refused under intake does not count as accepted at the round's end
    /// either.
    /// </summary>
    /// <inheritdoc cref="ACandidateChangedAtIntakeDoesNotClearAStructuralBlock" path="/remarks"/>
    [Fact]
    public async Task AFinalAdmissionRejectedAtIntakeDoesNotClearAStructuralBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1]);
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, only, raisedAt);
        // The vehicle's session stops being Ready between the pre-intake check and the gate inside intake, which
        // is the one difference between the two reads: the gate is what this outcome is about.
        fixture.Catalog.OnReread = () =>
        {
            SessionRecoveryRow session = fixture.Context.SessionRecoveries
                .Single(row => row.AgvId == FleetFixture.AgvIds[0]);
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
            fixture.Context.SaveChanges();
        };

        await fixture.RunRoundAsync();

        // The re-read happened, so the gate below it was really reached; without this the assertions would pass
        // on a round that never got to intake.
        Assert.Null(fixture.Catalog.OnReread);
        await AssertIntakeRefusedAsync(fixture, only, "FINAL_DYNAMIC_FACTS_NOT_READY");
        await AssertBlockSurvivedTheRoundAsync(fixture, blockLog, raisedAt);
    }

    /// <summary>
    /// A demand whose plan the acceptance refused as incomplete (control-server#198) does not count as accepted at
    /// the round's end either.
    /// </summary>
    /// <inheritdoc cref="ACandidateChangedAtIntakeDoesNotClearAStructuralBlock" path="/remarks"/>
    [Fact]
    public async Task AJourneyPlanIncompleteAtIntakeDoesNotClearAStructuralBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1]);
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, only, raisedAt);
        // Intake catches this one and reports it as an outcome rather than letting it throw, which is what makes
        // it this ticket's path rather than control-server#231's.
        fixture.Acceptances.ThrowOnFirstAccept =
            new JourneyPlanFreezeIncompleteException("The plan froze the station versions but no revision.");

        await fixture.RunRoundAsync();

        Assert.Null(fixture.Acceptances.ThrowOnFirstAccept);
        await AssertIntakeRefusedAsync(fixture, only, "FINAL_JOURNEY_PLAN_INCOMPLETE");
        await AssertBlockSurvivedTheRoundAsync(fixture, blockLog, raisedAt);
    }

    /// <summary>
    /// A demand intake found gone still has its structural block cleared, exactly as before: it left the catalog,
    /// so the block is no longer about anything.
    /// </summary>
    /// <remarks>
    /// The one refusal control-server#242 deliberately leaves alone, pinned here so that a later reading of "no
    /// refusal counts as accepted" does not quietly take it with the other three. The round after this one would
    /// clear the block anyway, on the catalog-absence rule -- the demand is missing from the next catalog read --
    /// so withholding it here would only delay the clearing by a round while an operator looks at an alarm about
    /// a demand that is gone.
    /// </remarks>
    [Fact]
    public async Task ADemandIntakeFoundGoneStillClearsItsStructuralBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1]);
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, only, Now.AddMinutes(-30));
        fixture.Catalog.GoneOnReread.Add(only.DemandId);

        await fixture.RunRoundAsync();

        await AssertIntakeRefusedAsync(fixture, only, "FINAL_CATALOG_CANDIDATE_GONE");
        StructuralDispatchBlockRow row = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(row.ClearedAt);
        Assert.Equal(2115, Assert.Single(blockLog.Entries).EventId.Id);
    }

    /// <summary>
    /// Only the refused demand's block is held back: another demand the same round did accept has its block
    /// cleared as on any other round.
    /// </summary>
    /// <remarks>
    /// The guard against subtracting too much, and against subtracting per round rather than per demand. It also
    /// pins when the subtraction takes effect: both vehicles' segments run before the round-end hook, so the
    /// first vehicle's refusal has to be recorded by the time the hook reads the round -- and the second
    /// vehicle's acceptance, which happened after it, must not be caught by it.
    /// </remarks>
    [Fact]
    public async Task ARefusedClaimHoldsBackOnlyItsOwnBlock()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2]);
        AcceptedDemandSnapshot refused = FleetFixture.Demand(0, "N1-1", 0);
        AcceptedDemandSnapshot taken = FleetFixture.Demand(1, "N1-2", 1);
        fixture.Catalog.Set([refused, taken]);
        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, refused, raisedAt);
        await RaiseStandingBlockOnlyAsync(fixture, taken, raisedAt);
        fixture.Catalog.ChangedOnReread.Add(refused.DemandId);

        await fixture.RunRoundAsync();

        // The first vehicle was refused and the second took the other demand: the round really did both.
        fixture.Context.ChangeTracker.Clear();
        AcceptedDemandRow accepted = Assert.Single(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(taken.DemandId, accepted.DemandId);
        StructuralDispatchBlockRow[] rows = await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Length);
        Assert.Null(Assert.Single(rows, row => row.DemandId == refused.DemandId).ClearedAt);
        Assert.NotNull(Assert.Single(rows, row => row.DemandId == taken.DemandId).ClearedAt);
        // One clearing, and it names the accepted demand.
        EventRecordingLogger<StructuralDispatchBlockSink>.Entry cleared = Assert.Single(blockLog.Entries);
        Assert.Equal(2115, cleared.EventId.Id);
        Assert.Contains(taken.DemandId, cleared.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal the segment never finished reporting is withdrawn with the claim it belongs to: the vehicle
    /// behind accepts the demand, and the round's end clears its block as on any other acceptance.
    /// </summary>
    /// <remarks>
    /// The subtraction only means anything while a claim is standing. Between naming a demand in it and writing
    /// the backlog row there is one database write, and a budget or a failed write can end the segment inside that
    /// window — control-server#231's and control-server#239's path, reached from a refusal instead of from the
    /// acceptance. The claim is then withdrawn, the demand is back in play, and a vehicle behind can accept it for
    /// real; an id left behind in the subtraction would hold that demand's block back for a round although
    /// nothing was wrong with it any more.
    /// </remarks>
    [Fact]
    public async Task ARefusalTheSegmentNeverFinishedReportingIsWithdrawnWithItsClaim()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2]);
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog =
            await RaiseStandingBlockAsync(fixture, only, Now.AddMinutes(-30));
        fixture.Catalog.ChangedOnReread.Add(only.DemandId);
        // The first vehicle's backlog write -- the one right after the refusal names the demand in the
        // subtraction -- fails the way a write that cannot be committed does. The same event puts the catalog
        // back in order, so the vehicle behind meets a demand it can really accept.
        bool interrupted = false;
        fixture.Context.SavingChanges += (_, _) =>
        {
            if (interrupted || fixture.Catalog.ReadCount < 2)
            {
                return;
            }
            interrupted = true;
            fixture.Catalog.ChangedOnReread.Clear();
            throw new HttpRequestException("The backlog write could not be committed.");
        };

        await fixture.RunRoundAsync();

        // The window this test is about was really entered: the first vehicle was refused at intake and then lost
        // its segment to the failed write.
        Assert.True(interrupted);
        Assert.Contains(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2123);
        // The vehicle behind took the demand for real.
        fixture.Context.ChangeTracker.Clear();
        AcceptedDemandRow accepted = Assert.Single(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(only.DemandId, accepted.DemandId);
        // So the block is cleared on that acceptance, this round rather than the next one.
        StructuralDispatchBlockRow row = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(row.ClearedAt);
        Assert.Equal(2115, Assert.Single(blockLog.Entries).EventId.Id);
    }

    /// <summary>
    /// A demand intake refused stays claimed for the rest of the round: the vehicles behind do not try it again,
    /// and no second intake is attempted on it.
    /// </summary>
    /// <remarks>
    /// The guard against withdrawing too much. control-server#239's path is the opposite one -- a segment cut off
    /// before intake reported has bound the demand to nothing, so the vehicle behind may take it -- and copying
    /// its withdrawal here would trade a silent defect for a louder one: two vehicles attempting the same demand
    /// in one round. The claim comment says why: every refusal below leaves the demand bound to this attempt.
    /// <para>
    /// It also pins when the claim takes effect. The round walks its vehicles in series, and the catalog read
    /// count below is what says the first vehicle's segment reached intake and the two behind it did not -- so
    /// the claim was in place for the whole gap between one segment ending and the next starting.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADemandIntakeRefusedStaysClaimedForTheRestOfTheRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        AcceptedDemandSnapshot only = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([only]);
        fixture.Catalog.ChangedOnReread.Add(only.DemandId);

        await fixture.RunRoundAsync();

        // The round's own read plus exactly one intake re-read: the first vehicle got to intake, and neither
        // vehicle behind it did. A second attempt would show up here as a third read.
        Assert.Equal(2, fixture.Catalog.ReadCount);
        Assert.Empty(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(
            [DispatchAdmissionChain.Eligible, "DEMAND_ALREADY_ACCEPTED", "DEMAND_ALREADY_ACCEPTED"],
            outcome.CompletedVehicles.Select(vehicle => Assert.Single(vehicle.Verdicts).ReasonCode).ToArray());
    }

    /// <summary>Intake ran to its end and refused: nothing was accepted, and the backlog says why.</summary>
    private static async Task AssertIntakeRefusedAsync(
        FleetFixture fixture,
        AcceptedDemandSnapshot demand,
        string reasonCode)
    {
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.AcceptedDemands.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        JourneyBacklogRow backlog = Assert.Single(await fixture.Context.JourneyBacklog.AsNoTracking()
            .Where(row => row.DemandId == demand.DemandId)
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(reasonCode, backlog.ReasonCode);
    }

    /// <summary>
    /// The block is as the round found it: not cleared, not refreshed, and the round said neither of the two
    /// lines an operator watches for.
    /// </summary>
    private static async Task AssertBlockSurvivedTheRoundAsync(
        FleetFixture fixture,
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog,
        DateTimeOffset raisedAt)
    {
        fixture.Context.ChangeTracker.Clear();
        StructuralDispatchBlockRow row = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(row.ClearedAt);
        Assert.Equal(raisedAt, row.LastSeenAt);
        Assert.Empty(blockLog.Entries);
    }

    /// <summary>
    /// Raises a structural block against one demand and puts the real sink at the round's end, with a logger that
    /// keeps the 2114/2115 lines so a test can say the round wrote neither.
    /// </summary>
    private static async Task<EventRecordingLogger<StructuralDispatchBlockSink>> RaiseStandingBlockAsync(
        FleetFixture fixture,
        AcceptedDemandSnapshot demand,
        DateTimeOffset raisedAt)
    {
        EventRecordingLogger<StructuralDispatchBlockSink> log = new();
        StructuralDispatchBlockStore blocks = new(fixture.Context);
        fixture.RoundOutcomes.Inner = new StructuralDispatchBlockSink(
            blocks,
            fixture.SlotPositions,
            new VehicleRoster(Microsoft.Extensions.Options.Options.Create(fixture.Options)),
            log);
        await blocks.RaiseOrRefreshAsync(
            demand.DemandId,
            "ROUTE_GRAPH_PICKUP_UNREACHABLE",
            demand.TransportDemandKey,
            "{}",
            raisedAt,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        return log;
    }

    /// <summary>
    /// Raises a second standing block, leaving in place the sink and the logger an earlier
    /// <see cref="RaiseStandingBlockAsync"/> call installed.
    /// </summary>
    private static async Task RaiseStandingBlockOnlyAsync(
        FleetFixture fixture,
        AcceptedDemandSnapshot demand,
        DateTimeOffset raisedAt)
    {
        StructuralDispatchBlockStore blocks = new(fixture.Context);
        await blocks.RaiseOrRefreshAsync(
            demand.DemandId,
            "ROUTE_GRAPH_PICKUP_UNREACHABLE",
            demand.TransportDemandKey,
            "{}",
            raisedAt,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>Refuses every candidate for every vehicle with the reason only a whole roster can make structural.</summary>
    private sealed class PickupUnreachableForEveryCandidate : IDispatchAdmissionCriterion
    {
        // At the head of the chain, so nothing below it runs and no verdict carries a basket count: the fleet slot
        // check this test is not about would otherwise ask the fixture's reader for a capacity it does not serve.
        public int Order => int.MinValue;

        public Task<string> EvaluateAsync(
            DispatchCandidateEvaluation evaluation,
            CancellationToken cancellationToken)
        {
            _ = evaluation;
            _ = cancellationToken;
            return Task.FromResult("ROUTE_GRAPH_PICKUP_UNREACHABLE");
        }
    }

    // ---- the transcript ------------------------------------------------------------------------------------

    private static async Task AssertTranscriptAsync(FleetFixture fixture, string expected)
    {
        string actual = await TranscriptAsync(fixture);
        Assert.True(
            string.Equals(Normalize(expected), Normalize(actual), StringComparison.Ordinal),
            $"The round's transcript differs from the one taken before the move.{Environment.NewLine}" +
            $"---- actual ----{Environment.NewLine}{actual}");

        static string Normalize(string text) => text.ReplaceLineEndings("\n").Trim();
    }

    /// <summary>Everything a round decided and said, in a fixed order, one fact per line.</summary>
    private static async Task<string> TranscriptAsync(FleetFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        StringBuilder text = new();
        void Line(string line) => text.Append(line).Append('\n');

        foreach (JourneyRuntimeRow journey in (await fixture.Context.JourneyRuntimes.AsNoTracking()
                     .ToArrayAsync(cancellationToken)).OrderBy(row => row.AgvId, StringComparer.Ordinal))
        {
            Line(
                $"journey {journey.AgvId} {Short(journey.DemandId)} {journey.Stage} " +
                $"block={journey.BlockReasonCode ?? "-"} pickup={journey.PickupStationRiotId} " +
                $"slots={journey.TargetSlotsJson} baskets={journey.ExpectedBasketCount}");
        }

        foreach (JourneyBacklogRow row in (await fixture.Context.JourneyBacklog.AsNoTracking()
                     .ToArrayAsync(cancellationToken)).OrderBy(row => row.DemandId, StringComparer.Ordinal))
        {
            Line(
                $"backlog {Short(row.DemandId)} {row.ReasonCode} first={row.FirstSeenAt:O} last={row.LastSeenAt:O} " +
                $"accepted={row.AcceptedAt?.ToString("O") ?? "-"}");
        }

        foreach (DispatchRoundOutcome outcome in fixture.RoundOutcomes.Outcomes)
        {
            Line(
                "outcome accepted=" +
                string.Join(',', outcome.Round.AcceptedDemandIds.Order(StringComparer.Ordinal).Select(Short)));
            foreach (DispatchVehicleOutcome vehicle in outcome.CompletedVehicles)
            {
                Line(
                    $"  {vehicle.AgvId}: " + string.Join(' ', vehicle.Verdicts.Select(verdict =>
                        $"{Short(verdict.Evaluation.Candidate.DemandId)}={verdict.ReasonCode}" +
                        (verdict.Evaluation.TargetSlots.Length > 0
                            ? $"[{string.Join(',', verdict.Evaluation.TargetSlots)}]"
                            : string.Empty))));
            }
        }

        foreach ((string vehicleKey, string upperId, int destination) in fixture.Riot.Creates)
        {
            Line($"riot create {vehicleKey} {upperId} -> {destination}");
        }

        foreach ((string commandType, string orderId) in fixture.Riot.OrderCommands)
        {
            Line($"riot command {commandType} {orderId}");
        }

        foreach (EventRecordingLogger<JourneyRuntimeEngine>.Entry entry in fixture.EngineLog.Entries)
        {
            Line($"log {entry.EventId.Id} {entry.EventId.Name} {entry.Level}: {entry.Message}");
        }

        Line($"catalog reads {fixture.Catalog.ReadCount}");
        // Vehicles by their place in the fleet: the transcript is compared as text, and V1 reads the same everywhere.
        for (int index = 0; index < FleetFixture.AgvIds.Length; index++)
        {
            text.Replace(FleetFixture.AgvIds[index], $"V{index + 1}");
        }

        return text.ToString();

        static string Short(string demandId) => $"D{demandId[^1]}";
    }
}

/// <summary>A logger that keeps every entry with its event id, so a test can pin both.</summary>
internal sealed class EventRecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(EventId EventId, LogLevel Level, string Message);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add(new Entry(eventId, logLevel, formatter(state, exception)));
        }
    }
}
