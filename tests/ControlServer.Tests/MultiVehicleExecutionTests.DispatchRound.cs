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

        await fixture.RunRoundAsync();

        await AssertTranscriptAsync(fixture, """
            journey V1 D1 AwaitingPickupArrival block=- pickup=13 slots=[1] baskets=1
            journey V2 D2 AwaitingPickupArrival block=- pickup=14 slots=[1] baskets=1
            backlog D0 ELIGIBLE first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=-
            backlog D1 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D2 ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            outcome accepted=D1,D2
              V1: D0=ELIGIBLE[1] D1=ELIGIBLE[1] D2=ELIGIBLE[1]
              V2: D0=ELIGIBLE[1] D1=DEMAND_ALREADY_ACCEPTED D2=ELIGIBLE[1]
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

        await fixture.RunRoundAsync();

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(
            ["ELIGIBLE", "DEMAND_ALREADY_ACCEPTED", "DEMAND_ALREADY_ACCEPTED"],
            outcome.CompletedVehicles.Select(vehicle => Assert.Single(vehicle.Verdicts).ReasonCode).ToArray());
        // The round's decision read and one intake re-read: nobody behind the first vehicle got as far as intake.
        Assert.Equal(2, fixture.Catalog.ReadCount);
        await AssertTranscriptAsync(fixture, """
            journey V1 D0 AwaitingPickupArrival block=- pickup=12 slots=[1] baskets=1
            backlog D0 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            outcome accepted=D0
              V1: D0=ELIGIBLE[1]
              V2: D0=DEMAND_ALREADY_ACCEPTED
              V3: D0=DEMAND_ALREADY_ACCEPTED
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
            backlog D0 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=-
            backlog D1 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            outcome accepted=D0,D1
              V1: D0=ELIGIBLE[1] D1=ELIGIBLE[1]
              V2: D0=DEMAND_ALREADY_ACCEPTED D1=ELIGIBLE[1]
              V3: D0=DEMAND_ALREADY_ACCEPTED D1=DEMAND_ALREADY_ACCEPTED
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
        await using FleetFixture fixture = await FleetFixture.CreateAsync(budgetMilliseconds: 1000);
        fixture.BoxCounts.HangOnCall = 2;

        await fixture.RunRoundAsync();

        await AssertTranscriptAsync(fixture, """
            journey V2 D0 AwaitingPickupArrival block=- pickup=12 slots=[1] baskets=1
            journey V3 D1 AwaitingPickupArrival block=- pickup=13 slots=[1] baskets=1
            backlog D0 DEMAND_ALREADY_ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D1 ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D2 ELIGIBLE first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=-
            outcome accepted=D0,D1
              V2: D0=ELIGIBLE[1] D1=ELIGIBLE[1] D2=ELIGIBLE[1]
              V3: D0=DEMAND_ALREADY_ACCEPTED D1=ELIGIBLE[1] D2=ELIGIBLE[1]
            riot create BROKERX-0002 W2G-10000000-0000-4000-8000-000000000000-PICKUP-1 -> 12
            riot create BROKERX-0003 W2G-10000001-0000-4000-8000-000000000001-PICKUP-1 -> 13
            log 2104 LogVehicleRoundBudgetExhausted Warning: Vehicle V1 exhausted its 1000 ms dispatch budget; the round moved on to the remaining vehicles.
            catalog reads 3
            """);
    }

    /// <summary>A catalog that cannot be read ends the round's dispatch before any backlog row or verdict.</summary>
    [Fact]
    public async Task AFailedCatalogReadDecidesAsBeforeTheMove()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Unreachable = true;

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
        await using FleetFixture fixture = await FleetFixture.CreateAsync(budgetMilliseconds: 1000);
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
        fixture.BoxCounts.HangOnCall = 2;
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
    /// With every vehicle under way the round ends before dispatch: the catalog is not read and the orphan check
    /// does not run -- an accepted demand without a journey, which that check refuses, goes unnoticed this round.
    /// </summary>
    [Fact]
    public async Task WithEveryVehicleUnderWayTheRoundReadsNoCatalogAndRunsNoOrphanCheck()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.RunRoundAsync();
        Assert.Equal(3, await fixture.Context.JourneyRuntimes.CountAsync(TestContext.Current.CancellationToken));
        await fixture.AcceptOrphanAsync();
        int catalogReads = fixture.Catalog.ReadCount;
        fixture.RoundOutcomes.Outcomes.Clear();

        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(catalogReads, fixture.Catalog.ReadCount);
        Assert.Empty(fixture.RoundOutcomes.Outcomes);
        // Not even the in-transit path is asked: the round ended before there was a round to qualify for.
        Assert.Empty(fixture.InTransit.Asked);
    }

    /// <summary>
    /// A vehicle under way goes down the in-transit path, which refuses it with nothing to show for it: no verdict,
    /// no slot group read, no backlog write, nothing handed to the round-end hook -- while the idle vehicles beside it
    /// are served as before.
    /// </summary>
    /// <remarks>
    /// The path is asked once per vehicle under way, with the round's own facts, and says no; appending to a journey
    /// under way is control-server#211's to open. The host's path is handed no database context at all, so its refusal
    /// cannot write a backlog row; what this test watches is that the round adds nothing for that vehicle either.
    /// </remarks>
    [Fact]
    public async Task AVehicleUnderWayIsLeftOutOfTheRoundItsIdleNeighboursAreServedIn()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        Assert.Equal(FleetFixture.AgvIds[0], (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).AgvId);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        fixture.RoundOutcomes.Outcomes.Clear();
        fixture.SlotPositions.Reads.Clear();

        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles.Select(vehicle => vehicle.AgvId).ToArray());
        Assert.DoesNotContain(FleetFixture.AgvIds[0], fixture.SlotPositions.Reads);
        (DispatchRoundFacts round, FleetVehicle asked) = Assert.Single(fixture.InTransit.Asked);
        Assert.Equal(FleetFixture.AgvIds[0], asked.AgvId);
        Assert.Same(Assert.Single(fixture.RoundOutcomes.Outcomes).Round, round);
    }

    /// <summary>
    /// The in-transit path saying yes is not something this round can act on yet: it fails loudly rather than
    /// quietly ignoring a vehicle it was told may take work.
    /// </summary>
    [Fact]
    public async Task AnInTransitVehicleTheRoundCannotYetServeIsNotSilentlyDropped()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        fixture.InTransit.Answer = true;

        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.RunRoundAsync(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Today a vehicle whose RIoT read throws ends the whole round: the vehicles behind it are not served and the
    /// round-end hook is not called. Pinned as it is, not as it should be -- the move keeps behaviour, and isolating
    /// one vehicle's failure is its own change (it also decides whether that vehicle counts as having finished for
    /// the structural block).
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseRiotReadThrowsEndsTheRoundForTheVehiclesBehindIt()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.FailOn = FleetFixture.VehicleKeys[1];

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.RunRoundAsync());

        Assert.Equal(
            [FleetFixture.AgvIds[0]],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId).ToArrayAsync(
                TestContext.Current.CancellationToken));
        Assert.Empty(fixture.RoundOutcomes.Outcomes);
        await AssertTranscriptAsync(fixture, """
            journey V1 D0 AwaitingPickupArrival block=- pickup=12 slots=[1] baskets=1
            backlog D0 ACCEPTED first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=2026-09-08T06:00:00.0000000+00:00
            backlog D1 ELIGIBLE first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=-
            backlog D2 ELIGIBLE first=2026-09-08T06:00:00.0000000+00:00 last=2026-09-08T06:00:00.0000000+00:00 accepted=-
            riot create BROKERX-0001 W2G-10000000-0000-4000-8000-000000000000-PICKUP-1 -> 12
            catalog reads 2
            """);
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
