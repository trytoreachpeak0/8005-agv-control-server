using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// B2: the server drives a fleet. Sessions, journeys and dispatch decisions stay per vehicle, the
/// round stays one worker serving vehicles in series, and one vehicle's trouble stays its own.
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 6, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // ---- session isolation ---------------------------------------------------------------

    /// <summary>
    /// Three vehicles, three demands, one round: each vehicle ends up on its own journey, and no
    /// two journeys share a vehicle or a demand.
    /// </summary>
    [Fact]
    public async Task ThreeVehiclesEachTakeTheirOwnDemandInOneRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow[] journeys = await fixture.Context.JourneyRuntimes
            .OrderBy(row => row.AgvId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, journeys.Length);
        Assert.Equal(
            FleetFixture.AgvIds,
            journeys.Select(row => row.AgvId).ToArray());
        Assert.Equal(
            FleetFixture.VehicleKeys,
            journeys.Select(row => row.VehicleKey).ToArray());
        Assert.Equal(3, journeys.Select(row => row.DemandId).Distinct(StringComparer.Ordinal).Count());
        // Every journey is at its own vehicle's pickup station, which is the visible half of the
        // decisions not having been made against one shared vehicle observation.
        Assert.Equal(3, journeys.Select(row => row.PickupStationRiotId).Distinct().Count());
    }

    /// <summary>
    /// A vehicle whose Onboard session is not ready takes nothing, and that says nothing about the
    /// others: reading a session per vehicle rather than off the configured one is what keeps a
    /// dead peer from either blocking or admitting its neighbours.
    /// </summary>
    [Fact]
    public async Task AVehicleWithNoSessionTakesNothingAndDoesNotStopTheOthers()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.DropSessionAsync(FleetFixture.AgvIds[1]);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        string[] served = await fixture.Context.JourneyRuntimes
            .Select(row => row.AgvId)
            .OrderBy(agvId => agvId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([FleetFixture.AgvIds[0], FleetFixture.AgvIds[2]], served);
    }

    /// <summary>
    /// One vehicle's safety snapshot does not admit another. The second vehicle's session carries
    /// facts that would block it; the first vehicle's identical-looking session must not be read in
    /// its place.
    /// </summary>
    [Fact]
    public async Task OneVehiclesOnboardFactsDoNotAdmitAnother()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.MakeDepartureUnsafeAsync(FleetFixture.AgvIds[2]);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        string[] served = await fixture.Context.JourneyRuntimes
            .Select(row => row.AgvId)
            .OrderBy(agvId => agvId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([FleetFixture.AgvIds[0], FleetFixture.AgvIds[1]], served);
    }

    /// <summary>
    /// Two vehicles each on their own journey is normal; two journeys on one vehicle is still the
    /// conflict it always was. The invariant moved from the server to the vehicle, and this pins
    /// both halves of that move.
    /// </summary>
    [Fact]
    public async Task TwoJourneysOnOneVehicleStillConflictWhileTwoVehiclesDoNot()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, await fixture.Context.JourneyRuntimes.CountAsync(
            TestContext.Current.CancellationToken));

        await fixture.DuplicateJourneyOntoAsync(FleetFixture.AgvIds[0]);

        BusinessIdentityConflictException error =
            await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
                fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains(FleetFixture.AgvIds[0], error.Message, StringComparison.Ordinal);
    }

    // ---- one worker, vehicles in series --------------------------------------------------

    /// <summary>
    /// The round reads the catalog once and serves the vehicles one after another, with no
    /// vehicle's reads interleaved into another's segment.
    /// </summary>
    /// <remarks>
    /// What this protects is snapshot freshness: the reason B2 is a single worker iterating rather
    /// than one worker per vehicle is that two workers would each decide against their own read of
    /// the same catalog and could accept the same demand twice. Interleaving is the observable
    /// symptom of that, so it is what the test looks at.
    /// </remarks>
    [Fact]
    public async Task OneWorkerServesTheVehiclesInSeriesAgainstOneCatalogRead()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // One read for the round's decision, plus the one final re-read intake does for each
        // demand it is about to accept. Nothing is read per candidate, and nothing is read per
        // vehicle: three vehicles judging three candidates against a per-candidate read would be
        // nine, and against a per-vehicle read would be three decision reads rather than one.
        Assert.Equal(1 + 3, fixture.Catalog.ReadCount);
        string[] segments = fixture.Riot.VehicleReads
            .Where((key, index) => index == 0 || fixture.Riot.VehicleReads[index - 1] != key)
            .ToArray();
        Assert.Equal(segments, segments.Distinct(StringComparer.Ordinal).ToArray());
        Assert.Equal(FleetFixture.VehicleKeys, segments.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Each vehicle in a round takes a different demand: the same catalog ranked by the same total
    /// order would otherwise hand every vehicle the same one, and every vehicle after the first
    /// would collide on it in intake.
    /// </summary>
    [Fact]
    public async Task ADemandTakenEarlierInTheRoundIsNoLongerACandidateLaterInIt()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        string[] demands = await fixture.Context.JourneyRuntimes
            .Select(row => row.DemandId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, demands.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, await fixture.Context.AcceptedDemands.CountAsync(
            TestContext.Current.CancellationToken));
    }

    // ---- per-vehicle timeout budget -------------------------------------------------------

    /// <summary>
    /// A vehicle whose reads never come back loses its own segment and nothing else: the vehicles
    /// behind it in the round are still served.
    /// </summary>
    /// <remarks>
    /// The hang is placed on the RIoT vehicle read because that is the shape the budget exists for
    /// — a call that neither answers nor fails. Without a per-vehicle budget the round would sit in
    /// it until the poll interval's own cancellation, and every vehicle behind it would go
    /// unserved for as long as the hang lasted.
    /// </remarks>
    [Fact]
    public async Task AVehicleThatExhaustsItsBudgetDoesNotCostTheOthersTheirRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(budgetMilliseconds: 1000);
        fixture.Riot.HangOn = FleetFixture.VehicleKeys[0];

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        string[] served = await fixture.Context.JourneyRuntimes
            .Select(row => row.AgvId)
            .OrderBy(agvId => agvId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]], served);
    }

    /// <summary>
    /// The budget belongs to the vehicle, not to the round: a vehicle that timed out this round is
    /// served normally in the next one, because nothing about the timeout is remembered.
    /// </summary>
    [Fact]
    public async Task AVehicleThatTimedOutIsServedAgainInTheNextRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(budgetMilliseconds: 1000);
        fixture.Riot.HangOn = FleetFixture.VehicleKeys[0];
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        fixture.Riot.HangOn = null;
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            FleetFixture.AgvIds[0],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId)
                .ToArrayAsync(TestContext.Current.CancellationToken),
            StringComparer.Ordinal);
    }

    // ---- batch 4 dispatch chain seams (control-server#69) ------------------------------------

    /// <summary>
    /// The area assignment table is read once per round, however many vehicles and candidates the round
    /// judges, and every plan of the round carries the version that one read returned.
    /// </summary>
    /// <remarks>
    /// Three vehicles judging three candidates would be nine reads if a criterion read the table for itself,
    /// and nine reads can straddle an import: two candidates of one round judged against two tables.
    /// </remarks>
    [Fact]
    public async Task TheRoundReadsTheAreaAssignmentTableOnceForAllItsVehiclesAndCandidates()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.AreaAssignments.CurrentReads);
        Assert.Equal(3, fixture.AcceptedPlans.Count);
        Assert.All(fixture.AcceptedPlans, plan =>
        {
            Assert.Equal(1, plan.AreaAssignmentVersion);
            Assert.Equal("FRONT", plan.RequiredSlotPosition);
        });
    }

    /// <summary>
    /// Each vehicle's slot groups are read once in its own segment of the round, not once per candidate, and
    /// what was read reaches every candidate judged for that vehicle.
    /// </summary>
    [Fact]
    public async Task EachVehiclesSlotGroupsAreReadOncePerRoundAndReachEveryCandidateItJudges()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            FleetFixture.AgvIds.Order(StringComparer.Ordinal).ToArray(),
            fixture.SlotPositions.Reads.Order(StringComparer.Ordinal).ToArray());
        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.All(outcome.CompletedVehicles, vehicle =>
        {
            Assert.NotEmpty(vehicle.Verdicts);
            Assert.All(vehicle.Verdicts, verdict =>
                Assert.Equal(vehicle.AgvId, Assert.IsType<VehicleSlotPositions>(verdict.Evaluation.Vehicle.SlotPositions).AgvId));
        });
    }

    /// <summary>
    /// The version a candidate was looked up in and the slot group its AREA is assigned travel through the
    /// eligible candidate into the plan intake accepts, candidate by candidate.
    /// </summary>
    [Fact]
    public async Task ThePlanCarriesTheVersionAndSlotGroupItsCandidateWasLookedUpIn()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        AreaAssignmentTableVersion table = await fixture.AreaAssignments.ImportAsync(
            [
                new("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"),
                new("N1-2", "MAP-25-WIRE_TO_GATE", "REAR"),
                new("N1-3", "MAP-25-WIRE_TO_GATE", "FRONT"),
            ],
            Now);

        await fixture.RunRoundAsync();

        Assert.Equal(2, table.Version);
        Assert.Equal(3, fixture.AcceptedPlans.Count);
        Assert.All(fixture.AcceptedPlans, plan => Assert.Equal(table.Version, plan.AreaAssignmentVersion));
        Assert.Equal(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["N1-1"] = "FRONT",
                ["N1-2"] = "REAR",
                ["N1-3"] = "FRONT",
            },
            fixture.AcceptedPlans.ToDictionary(plan => plan.PickupStationId, plan => plan.RequiredSlotPosition, StringComparer.Ordinal));
    }

    /// <summary>
    /// A route's dispatch zone is the one the area assignment table gives its AREA, not the one zone the server
    /// is configured with, and in one round each candidate is judged against its own zone's vehicles
    /// (control-server#72).
    /// </summary>
    /// <remarks>
    /// The first vehicle serves only zone A and the other two only zone B. Were the route zone still
    /// <c>JourneyRuntime:dispatchZone</c> (zone A), the first vehicle would be admitted to every candidate and
    /// the other two to none.
    /// </remarks>
    [Fact]
    public async Task EachCandidateOfOneRoundIsRoutedIntoItsAssignedZoneAndJudgedAgainstThatZonesVehicles()
    {
        const string ZoneA = "MAP-25-ZONE-A";
        const string ZoneB = "MAP-25-ZONE-B";
        await using FleetFixture fixture = await FleetFixture.CreateAsync(configure: options =>
        {
            options.DispatchZone = ZoneA;
            options.AllowedDispatchZones = [ZoneA, ZoneB];
            options.Fleet[0].Zones = [ZoneA];
            options.Fleet[1].Zones = [ZoneB];
            options.Fleet[2].Zones = [ZoneB];
        });
        await fixture.AreaAssignments.ImportAsync(
            [
                new("N1-1", ZoneA, "FRONT"),
                new("N1-2", ZoneB, "FRONT"),
                new("N1-3", ZoneB, "FRONT"),
            ],
            Now);

        await fixture.RunRoundAsync();

        Assert.Equal(3, fixture.AcceptedPlans.Count);
        JourneyExecutionPlan zoneAPlan = Assert.Single(fixture.AcceptedPlans, plan => plan.PickupStationId == "N1-1");
        Assert.Equal(ZoneA, zoneAPlan.DispatchZone);
        Assert.Equal(FleetFixture.AgvIds[0], zoneAPlan.AgvId);
        JourneyExecutionPlan[] zoneBPlans = [.. fixture.AcceptedPlans.Where(plan => plan.PickupStationId != "N1-1")];
        Assert.All(zoneBPlans, plan => Assert.Equal(ZoneB, plan.DispatchZone));
        Assert.Equal(
            FleetFixture.AgvIds[1..].Order(StringComparer.Ordinal).ToArray(),
            zoneBPlans.Select(plan => plan.AgvId).Order(StringComparer.Ordinal).ToArray());

        DispatchVehicleOutcome first = Assert.Single(
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles,
            vehicle => vehicle.AgvId == FleetFixture.AgvIds[0]);
        Assert.All(
            first.Verdicts.Where(verdict => verdict.Evaluation.Candidate.LiveMesFields!.Area != "N1-1"),
            verdict =>
            {
                Assert.Equal(ZoneB, verdict.Evaluation.Route!.DispatchZone);
                Assert.Equal(DispatchZoneVehicleCriterion.VehicleNotInZoneReason, verdict.ReasonCode);
            });
    }

    /// <summary>
    /// Once the round has served every vehicle, the round-end hook is called once with every vehicle's
    /// verdict on every candidate — the refusals included, since those are what a structural judgement reads.
    /// </summary>
    /// <remarks>
    /// <c>JourneyBacklog</c> keeps one reason per demand and each vehicle overwrites it, so after a round it
    /// holds the last vehicle's reason. "No vehicle could take this" can only be concluded from all of them,
    /// which is why #74 needs this hook rather than the backlog.
    /// </remarks>
    [Fact]
    public async Task AtTheEndOfTheRoundTheHookIsCalledOnceWithEveryVehiclesVerdictOnEveryCandidate()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(FleetFixture.AgvIds, outcome.CompletedVehicles.Select(vehicle => vehicle.AgvId).ToArray());
        string[] demandIds =
        [
            .. (await fixture.Context.AcceptedDemands.Select(row => row.DemandId)
                    .ToArrayAsync(TestContext.Current.CancellationToken))
                .Order(StringComparer.Ordinal)
        ];
        Assert.Equal(3, demandIds.Length);
        Assert.All(outcome.CompletedVehicles, vehicle => Assert.Equal(
            demandIds,
            vehicle.Verdicts.Select(verdict => verdict.Evaluation.Candidate.DemandId).Order(StringComparer.Ordinal).ToArray()));
        string takenByFirst = (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).DemandId;
        Assert.Equal(
            "DEMAND_ALREADY_ACCEPTED",
            outcome.CompletedVehicles[1].Verdicts
                .Single(verdict => verdict.Evaluation.Candidate.DemandId == takenByFirst)
                .ReasonCode);
    }

    /// <summary>
    /// A vehicle that runs out its budget does not cost the round its hook: the hook is still called once,
    /// with only the vehicles that finished.
    /// </summary>
    /// <remarks>
    /// The hang is placed after the first vehicle has judged every candidate — on intake's final catalog
    /// re-read — so its verdicts exist and have to be left out. A vehicle cut off mid-segment has not
    /// finished deciding, and counting its verdicts would let a structural judgement rest on a vehicle the
    /// round never finished asking.
    /// </remarks>
    [Fact]
    public async Task WhenAVehicleExhaustsItsBudgetTheHookIsStillCalledOnceWithOnlyTheVehiclesThatFinished()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(budgetMilliseconds: 1000);
        // Read 1 is the round's decision read; read 2 is the first vehicle's intake re-read.
        fixture.Catalog.HangOnRead = 2;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        DispatchRoundOutcome outcome = Assert.Single(fixture.RoundOutcomes.Outcomes);
        Assert.Equal(
            [FleetFixture.AgvIds[1], FleetFixture.AgvIds[2]],
            outcome.CompletedVehicles.Select(vehicle => vehicle.AgvId).ToArray());
        Assert.DoesNotContain(
            FleetFixture.AgvIds[0],
            await fixture.Context.JourneyRuntimes.Select(row => row.AgvId).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    // ---- MT_WAIT_FOR_CHECKPOINT ------------------------------------------------------------

    /// <summary>
    /// A vehicle holding at a traffic checkpoint says so, and keeps saying so until it stops.
    /// </summary>
    [Fact]
    public async Task AVehicleHoldingAtACheckpointNamesTheWaitAndClearsItWhenItMovesOn()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        fixture.Riot.MovementState = RiotMovementStates.WaitForCheckpoint;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.All(
            await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken),
            row => Assert.Equal(JourneyRuntimeEngine.CheckpointWaitReason, row.BlockReasonCode));

        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.All(
            await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken),
            row => Assert.Null(row.BlockReasonCode));
    }

    /// <summary>
    /// Past the budget the wait stops being ordinary and the journey says a different thing.
    /// </summary>
    [Fact]
    public async Task ACheckpointWaitPastItsBudgetIsReportedDifferently()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        fixture.Riot.MovementState = RiotMovementStates.WaitForCheckpoint;
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        fixture.Clock.Advance(fixture.Options.CheckpointWaitBudget + TimeSpan.FromSeconds(1));
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.All(
            await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken),
            row => Assert.Equal(JourneyRuntimeEngine.CheckpointWaitExceededReason, row.BlockReasonCode));
    }

    // ---- the command surface, reached from the loop ------------------------------------------

    /// <summary>
    /// RIoT reporting a leg's order FAILED is REQ-0232's symptom, and it reaches the fault model
    /// with the order this project has in flight, so REQ-0234's OrderHold has a target.
    /// </summary>
    [Fact]
    public async Task AFailedLegOrderIsRecordedAsASymptomAndHoldsThatOrder()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow journey = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);
        fixture.Riot.FailOrder(journey.PickupUpperId);

        await fixture.RunRoundAsync();

        RiotOrderCommandAuditRow[] holds = await fixture.HoldAttemptsAsync();
        RiotOrderCommandAuditRow hold = Assert.Single(holds);
        Assert.Equal(RiotCommandTypeNames.OrderHold, hold.CommandType);
        Assert.Equal(FleetFixture.AgvIds[0], hold.AgvId);
        Assert.Equal(journey.PickupUpperId, hold.TargetUpperId);
        Assert.Equal(1, hold.AttemptNumber);

        VehicleFaultStateRow fault = Assert.Single(
            await fixture.Context.VehicleFaultStates.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(FleetFixture.AgvIds[0], fault.AgvId);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, fault.Level);
        Assert.Equal(VehicleFaultEvidence.OrderFailed, fault.EvidenceCode);
        Assert.Equal(
            VehicleFaultEvidence.OrderFailed,
            (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).BlockReasonCode);
    }

    /// <summary>
    /// The hold goes out once, however many rounds run over it.
    /// </summary>
    /// <remarks>
    /// This is specification 8.3's "called once" for the command surface, and it is not an accident
    /// of the loop's rate: reconciliation reads the order back and finds it terminal in a state the
    /// hold was not for, which is <c>Failed</c> — the command did not achieve what it was for and
    /// no longer can. Re-issuing that is a repeated dispatch, so the coordinator does not. Counted
    /// off the audit table rather than off the double's call list, because a retry is a new attempt
    /// row and that is what makes "once" and "three times" different facts rather than a matter of
    /// trust.
    /// </remarks>
    [Fact]
    public async Task AFailedLegOrderIsHeldOnceHoweverManyRoundsRun()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow journey = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);
        fixture.Riot.FailOrder(journey.PickupUpperId);

        for (int round = 0; round < 6; round++)
        {
            await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Single(await fixture.HoldAttemptsAsync());
        Assert.Single(
            fixture.Riot.OrderCommands,
            call => call.CommandType == RiotCommandTypeNames.OrderHold);
        // A vehicle standing still at a known station has proven it stopped, so REQ-0246's
        // escalation does not fire and no emergency stop is issued. The two halves are asserted
        // together because "held once" would also be true of a vehicle that was emergency-stopped
        // on the first round, and that is a different event entirely.
        Assert.Empty(fixture.Riot.EmergencyCommands);
    }

    /// <summary>
    /// One vehicle's failed order is one vehicle's business: the other two keep their journeys,
    /// their reasons and their absence from the command audit.
    /// </summary>
    [Fact]
    public async Task AFailedLegOrderDoesNotReachTheOtherVehicles()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow journey = await fixture.JourneyOfAsync(FleetFixture.AgvIds[1]);
        fixture.Riot.FailOrder(journey.PickupUpperId);

        await fixture.RunRoundAsync();
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        RiotOrderCommandAuditRow hold = Assert.Single(await fixture.HoldAttemptsAsync());
        Assert.Equal(FleetFixture.AgvIds[1], hold.AgvId);
        Assert.Equal(
            FleetFixture.AgvIds[1],
            Assert.Single(await fixture.Context.VehicleFaultStates.ToArrayAsync(
                TestContext.Current.CancellationToken)).AgvId);
        foreach (string agvId in new[] { FleetFixture.AgvIds[0], FleetFixture.AgvIds[2] })
        {
            Assert.Null((await fixture.JourneyOfAsync(agvId)).BlockReasonCode);
        }
    }

    /// <summary>
    /// A leg that is merely still running issues nothing. "Called when it was due" needs the
    /// negative half as much as the positive one.
    /// </summary>
    [Fact]
    public async Task AHealthyLegIssuesNoCommandAtAll()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(await fixture.Context.RiotOrderCommandAudit.ToArrayAsync(
            TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Empty(await fixture.Context.VehicleFaultStates.ToArrayAsync(
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Recognising the state does not make it evidence of a stop.
    /// </summary>
    /// <remarks>
    /// REQ-0247's proof rests on RIoT positively reporting a non-moving state. A vehicle waiting at
    /// a checkpoint has not been shown to have stopped rolling — it has been shown to be in the
    /// middle of something — so the reading stays Unknown, which blocks the proof. Naming the wait
    /// for an operator and admitting it as safety evidence are different things, and this pins the
    /// second one down.
    /// </remarks>
    [Fact]
    public void RecognisingTheCheckpointStateDoesNotMakeItEvidenceOfAStop() =>
        Assert.Equal(
            VehicleMotionReading.Unknown,
            HttpRiotMovementGateway.ReadMotion(RiotMovementStates.WaitForCheckpoint, 0));

    // ---- the two vehicle-filtering criteria -------------------------------------------------

    [Fact]
    public void AVehicleTheDispatchPolicyDoesNotMentionTakesNothing() =>
        Assert.Equal(
            VehicleTaskTypeAdmissionCriterion.VehicleNotInPolicyReason,
            VehicleTaskTypeAdmissionCriterion.Evaluate(Policy("AGV-1", "WIRE_TO_GATE"), "AGV-9", "WIRE_TO_GATE"));

    [Fact]
    public void AVehicleTakesOnlyTheTaskTypesItIsAdmittedFor()
    {
        VehicleDispatchPolicy policy = Policy("AGV-1", "WIRE_TO_GATE");
        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            VehicleTaskTypeAdmissionCriterion.Evaluate(policy, "AGV-1", "WIRE_TO_GATE"));
        Assert.Equal(
            VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason,
            VehicleTaskTypeAdmissionCriterion.Evaluate(policy, "AGV-1", "SOMETHING_ELSE"));
    }

    /// <summary>
    /// An empty allowed set is a vehicle that may take nothing, not a vehicle with no restriction.
    /// </summary>
    [Fact]
    public void AVehicleWithAnEmptyAllowedSetTakesNothing() =>
        Assert.Equal(
            VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason,
            VehicleTaskTypeAdmissionCriterion.Evaluate(Policy("AGV-1"), "AGV-1", "WIRE_TO_GATE"));

    [Theory]
    [InlineData("ZONE-A", "AGV-1", DispatchAdmissionChain.Eligible)]
    [InlineData("ZONE-A", "AGV-2", DispatchZoneVehicleCriterion.VehicleNotInZoneReason)]
    [InlineData("ZONE-B", "AGV-1", DispatchZoneVehicleCriterion.ZoneNotConfiguredReason)]
    public async Task AZoneIsServedOnlyByTheVehiclesConfiguredForIt(string zone, string agvId, string expected)
    {
        VehicleDispatchPolicy policy = new(
            [new VehicleDispatchProfile("AGV-1", new HashSet<string>(StringComparer.Ordinal), 1000)],
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["ZONE-A"] = new HashSet<string>(StringComparer.Ordinal) { "AGV-1" },
            },
            "TEST-POLICY");

        Assert.Equal(
            expected,
            await new DispatchZoneVehicleCriterion().EvaluateAsync(
                Evaluation(policy, agvId, zone), TestContext.Current.CancellationToken));
    }

    // ---- policy application ------------------------------------------------------------------

    /// <summary>
    /// The configured roster reaches the three tables, and applying it again writes nothing: the
    /// stored content fingerprint is what decides, so an unchanged configuration is a no-op and a
    /// changed one is always applied without anyone having to bump a version by hand.
    /// </summary>
    [Fact]
    public async Task TheConfiguredFleetIsWrittenOnceAndReappliedOnlyWhenItChanges()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        VehicleDispatchPolicyStore store = new(fixture.Context);
        VehicleDispatchPolicyAccess access = new(store, Options.Create(fixture.Options), fixture.Clock);

        VehicleDispatchPolicy first = await access.EnsureCurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, first.Vehicles.Count);
        Assert.Equal(
            FleetFixture.AgvIds.ToHashSet(StringComparer.Ordinal),
            first.ZoneVehicles[fixture.Options.DispatchZone]);

        VehicleDispatchPolicy again = await access.EnsureCurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.ConfigurationVersion, again.ConfigurationVersion);

        fixture.Options.Fleet = fixture.Options.Fleet.Take(2).ToArray();
        VehicleDispatchPolicyAccess smaller = new(store, Options.Create(fixture.Options), fixture.Clock);
        VehicleDispatchPolicy shrunk = await smaller.EnsureCurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, shrunk.Vehicles.Count);
        Assert.NotEqual(first.ConfigurationVersion, shrunk.ConfigurationVersion);
    }

    /// <summary>
    /// An empty roster is the single-vehicle deployment, read off the fields that already describe
    /// it. That is what keeps a server upgraded to this build behaving exactly as it did.
    /// </summary>
    [Fact]
    public void AnEmptyRosterIsTheOneConfiguredVehicle()
    {
        JourneyRuntimeOptions options = new()
        {
            AgvId = "AGV-ONLY",
            VehicleKey = "BROKERX-ONLY",
            AgvLifecycleGeneration = 4,
            DispatchZone = "ZONE-A",
            AllowedWorkTypes = ["WIRE_TO_GATE"],
        };
        VehicleRoster roster = new(Options.Create(options));

        FleetVehicle only = Assert.Single(roster.Vehicles);
        Assert.Equal("AGV-ONLY", only.AgvId);
        Assert.Equal("BROKERX-ONLY", only.VehicleKey);
        Assert.Equal(4, only.AgvLifecycleGeneration);
        Assert.Same(only, roster.ByVehicleKey("BROKERX-ONLY"));
        Assert.Same(only, roster.ByAgvId("AGV-ONLY"));
        Assert.Null(roster.ByVehicleKey("BROKERX-SOMEONE-ELSE"));

        VehicleDispatchPolicy policy =
            new VehicleDispatchPolicyAccess(new ThrowingPolicyStore(), Options.Create(options), TimeProvider.System)
                .FromConfiguration();
        Assert.Equal(["WIRE_TO_GATE"], Assert.Single(policy.Vehicles).AllowedTaskTypes.ToArray());
        Assert.Equal(["AGV-ONLY"], policy.ZoneVehicles["ZONE-A"].ToArray());
    }

    // ---- occupancy uniqueness ------------------------------------------------------------------

    /// <summary>
    /// A dispatched vehicle holds its occupancy claim for the whole journey, and a second claim for
    /// the same vehicle is refused by the index rather than by a read.
    /// </summary>
    /// <remarks>
    /// This is where the uniqueness ticket 06 moved down onto <c>OrderIntents</c> starts being
    /// enforced: the columns and the filtered unique index existed already, and until something
    /// wrote them every row fell outside the index. The ticket's own acceptance names a
    /// <c>DispatchUniquenessGuard</c>, which does not exist in this repository — see the
    /// resolution — so what is pinned here is the invariant that name stood for.
    /// </remarks>
    [Fact]
    public async Task ADispatchedVehicleHoldsItsOccupancyUntilTheJourneyEnds()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        OrderIntentRow[] claimed = await fixture.Context.OrderIntents
            .Where(row => row.VehicleOccupancyClaimedAt != null && row.VehicleOccupancyReleasedAt == null)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, claimed.Length);
        Assert.Equal(
            FleetFixture.VehicleKeys,
            claimed.Select(row => row.VehicleKey).Order(StringComparer.Ordinal).ToArray());

        // A second in-flight order for a vehicle that already holds one is refused by the index.
        VehicleDispatchPolicyStore store = new(fixture.Context);
        string second = await fixture.AddSecondIntentAsync(FleetFixture.VehicleKeys[0]);
        Assert.False(await store.TryClaimVehicleOccupancyAsync(
            second, Now, TestContext.Current.CancellationToken));
    }

    // ---- N sessions on the peer -----------------------------------------------------------------

    /// <summary>
    /// Two vehicles are attached at once and each one's traffic reaches only its own socket.
    /// </summary>
    [Fact]
    public async Task ThePeerRoutesEachEnvelopeToTheVehicleItIsAddressedTo()
    {
        OnboardPeer peer = new();
        using MemoryStream first = new();
        using MemoryStream second = new();
        await using OnboardPeerConnection firstConnection = new(first);
        await using OnboardPeerConnection secondConnection = new(second);
        peer.Attach("AGV-1", firstConnection);
        peer.Attach("AGV-2", secondConnection);

        await peer.SendAsync(Envelope("AGV-2"), TestContext.Current.CancellationToken);

        Assert.Empty(first.ToArray());
        Assert.Contains("AGV-2", Encoding.UTF8.GetString(second.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A vehicle with no attached connection is a failure, not a broadcast and not a silent drop.
    /// </summary>
    [Fact]
    public async Task AnEnvelopeForAVehicleWithNoSessionFails()
    {
        OnboardPeer peer = new();
        using MemoryStream stream = new();
        await using OnboardPeerConnection connection = new(stream);
        peer.Attach("AGV-1", connection);

        await Assert.ThrowsAsync<IOException>(() =>
            peer.SendAsync(Envelope("AGV-2"), TestContext.Current.CancellationToken));
        Assert.Empty(stream.ToArray());
    }

    /// <summary>
    /// One vehicle, one live connection. Two sockets claiming the same vehicle is the ambiguity the
    /// single-connection peer refused, and it is still refused — per vehicle now.
    /// </summary>
    [Fact]
    public async Task TwoConnectionsForOneVehicleAreStillRefused()
    {
        OnboardPeer peer = new();
        using MemoryStream first = new();
        using MemoryStream second = new();
        await using OnboardPeerConnection firstConnection = new(first);
        await using OnboardPeerConnection secondConnection = new(second);
        peer.Attach("AGV-1", firstConnection);

        Assert.Throws<InvalidOperationException>(() => peer.Attach("AGV-1", secondConnection));

        // Detaching the one that is attached frees the vehicle for the next connection; detaching a
        // stale one must not, or a reconnect would evict the session that replaced it.
        peer.Detach("AGV-1", secondConnection);
        await peer.SendAsync(Envelope("AGV-1"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(first.ToArray());

        peer.Detach("AGV-1", firstConnection);
        await Assert.ThrowsAsync<IOException>(() =>
            peer.SendAsync(Envelope("AGV-1"), TestContext.Current.CancellationToken));
    }

    private static ReadOnlyMemory<byte> Envelope(string agvId) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            messageType = "VehicleBusinessStateSnapshot",
            messageId = Guid.NewGuid().ToString("D"),
            agvId,
            sessionGeneration = 1,
        }, SerializerOptions) + "\n");

    private static VehicleDispatchPolicy Policy(string agvId, params string[] taskTypes) => new(
        [new VehicleDispatchProfile(agvId, taskTypes.ToHashSet(StringComparer.Ordinal), 30_000)],
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
        "TEST-POLICY");

    private static DispatchCandidateEvaluation Evaluation(
        VehicleDispatchPolicy policy,
        string agvId,
        string zone)
    {
        AcceptedDemandSnapshot candidate = new("demand-1", "tdk-1", 1, "epoch-1", 1, Now);
        DispatchCandidateEvaluation evaluation = new(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot("epoch-1", 1, [candidate]),
                new RiotMapStationCatalogSnapshot(25, Now, new string('a', 64), []),
                new SingleStationView(new RiotMapStation(210, "关卡")),
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                policy),
            new DispatchVehicleFacts(
                "BROKERX-1",
                agvId,
                null,
                new RiotVehicleObservation("BROKERX-1", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now))
        {
            Route = new ResolvedJourneyRoute(zone, "MAPCAT-1", "N1-1", 12, "关卡", 210, FixedTaskStationResolution.Resolved("WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
        };
        return evaluation;
    }

    /// <summary>A store no test may reach: the roster cases below read configuration only.</summary>
    private sealed class ThrowingPolicyStore : IVehicleDispatchPolicyStore
    {
        public Task<VehicleDispatchPolicy> ReadPolicyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplacePolicyAsync(
            VehicleDispatchPolicy policy,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryClaimVehicleOccupancyAsync(
            string upperId,
            DateTimeOffset claimedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseVehicleOccupancyAsync(
            string upperId,
            DateTimeOffset releasedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// A commissioned three-vehicle server: three Onboard sessions, three open demands in three
    /// areas, and a RIoT that answers for whichever vehicle it is asked about.
    /// </summary>
    private sealed class FleetFixture : IAsyncDisposable
    {
        public static readonly string[] AgvIds = ["老厂前线新多仓位1", "老厂前线新多仓位2", "老厂前线新多仓位3"];
        public static readonly string[] VehicleKeys = ["BROKERX-0001", "BROKERX-0002", "BROKERX-0003"];
        private static readonly string[] Areas = ["N1-1", "N1-2", "N1-3"];
        private static readonly int[] PickupStations = [12, 13, 14];

        private readonly SqliteConnection _connection;
        private readonly Dictionary<string, List<string>> _safetyMessageIds = new(StringComparer.Ordinal);

        /// <summary>A criterion put at the head of the chain, so a test can state what a vehicle concludes.</summary>
        private readonly IDispatchAdmissionCriterion? _extraCriterion;

        private FleetFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            JourneyRuntimeOptions options,
            MovableClock clock,
            IDispatchAdmissionCriterion? extraCriterion)
        {
            _connection = connection;
            _extraCriterion = extraCriterion;
            Context = context;
            Options = options;
            Clock = clock;
            Riot = new FleetRiot(clock, options);
            Acceptances = new RecordingAcceptances(new WireToGateStore(context), AcceptedPlans);
            GovernanceStore governance = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            AreaAssignments = new CountingAreaAssignments(
                new AreaAssignmentStore(context, new GovernedConfigurationPublisher(governance, governance)));
            Engine = CreateEngine();
        }

        public ControlServerDbContext Context { get; }
        public JourneyRuntimeOptions Options { get; }
        public MovableClock Clock { get; }
        public FleetRiot Riot { get; }
        public FleetCatalog Catalog { get; } = new();
        public CheckpointWaitLedger CheckpointWaits { get; } = new();
        public CountingAreaAssignments AreaAssignments { get; }
        public CountingSlotPositions SlotPositions { get; } = new();
        public RecordingRoundOutcomes RoundOutcomes { get; } = new();
        public List<JourneyExecutionPlan> AcceptedPlans { get; } = [];
        public RecordingAcceptances Acceptances { get; }
        public FleetBoxCounts BoxCounts { get; } = new();
        public RecordingInTransitQualification InTransit { get; } = new();
        public EventRecordingLogger<JourneyRuntimeEngine> EngineLog { get; } = new();
        public JourneyRuntimeEngine Engine { get; private set; }

        public static async Task<FleetFixture> CreateAsync(
            int budgetMilliseconds = 30_000,
            Action<JourneyRuntimeOptions>? configure = null,
            IDispatchAdmissionCriterion? extraCriterion = null)
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> dbOptions =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(dbOptions);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await TaskTypeStationRuntimeSeed.ActivateAsync(dbOptions, Now);
            JourneyRuntimeOptions options = FleetOptions(budgetMilliseconds);
            configure?.Invoke(options);
            FleetFixture fixture = new(connection, context, options, new MovableClock(Now), extraCriterion);
            await fixture.SeedAsync();
            return fixture;
        }

        /// <summary>One more round, with the clock moved on first so samples are spaced.</summary>
        /// <remarks>
        /// The tracker is cleared between rounds because each round is a fresh scope in the worker
        /// and these tests share one context; without it a row this test read stays attached and
        /// the next round's read comes back from memory rather than from the database.
        /// </remarks>
        public async Task RunRoundAsync(TimeSpan? advance = null)
        {
            if (advance is TimeSpan elapsed)
            {
                Clock.Advance(elapsed);
            }

            Context.ChangeTracker.Clear();
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        public async Task<JourneyRuntimeRow> JourneyOfAsync(string agvId) =>
            await Context.JourneyRuntimes.AsNoTracking()
                .SingleAsync(row => row.AgvId == agvId, TestContext.Current.CancellationToken);

        public async Task<RiotOrderCommandAuditRow[]> HoldAttemptsAsync() =>
            await Context.RiotOrderCommandAudit.AsNoTracking()
                .Where(row => row.CommandType == RiotCommandTypeNames.OrderHold)
                .OrderBy(row => row.AttemptNumber)
                .ToArrayAsync(TestContext.Current.CancellationToken);

        public async Task RecreateEngineAsync()
        {
            Context.ChangeTracker.Clear();
            Engine = CreateEngine();
            await Task.CompletedTask;
        }

        /// <summary>Powers a vehicle down the way a repair does: its session stops being Ready.</summary>
        public async Task DropSessionAsync(string agvId)
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                row => row.AgvId == agvId, TestContext.Current.CancellationToken);
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Replaces one vehicle's safety summary with one that refuses departure.</summary>
        /// <remarks>
        /// The old snapshot is removed rather than superseded. Both carry the session's current
        /// safetyStateVersion, and which of two same-version messages wins is decided by arrival
        /// time — leaving the safe one in place would make the test depend on a tie.
        /// </remarks>
        public async Task MakeDepartureUnsafeAsync(string agvId)
        {
            string[] messageIds = [.. _safetyMessageIds[agvId]];
            ProtocolInboxRow[] existing = await Context.ProtocolInbox
                .Where(row => messageIds.Contains(row.MessageId))
                .ToArrayAsync(TestContext.Current.CancellationToken);
            Context.ProtocolInbox.RemoveRange(existing);
            _safetyMessageIds[agvId].Clear();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddSafetySnapshotAsync(agvId, 1, departureSafe: false);
        }

        /// <summary>Puts a second unresolved journey on one vehicle, which must not be allowed.</summary>
        public async Task DuplicateJourneyOntoAsync(string agvId)
        {
            JourneyRuntimeRow existing = await Context.JourneyRuntimes
                .AsNoTracking()
                .SingleAsync(row => row.AgvId == agvId, TestContext.Current.CancellationToken);
            JourneyRuntimeRow copy = existing;
            copy.DemandId = existing.DemandId + "-SECOND";
            copy.JourneyId = ControlServer.Application.JourneyIdentity.ForAnchorDemand(copy.DemandId);
            copy.CreatedAt = existing.CreatedAt.AddSeconds(1);
            Context.JourneyRuntimes.Add(copy);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        /// <summary>
        /// Accepts a demand no journey was built for -- what the orphan check refuses whenever it runs.
        /// </summary>
        public async Task AcceptOrphanAsync()
        {
            AcceptedDemandSnapshot orphan = Demand(9, "N1-1", 0);
            await new WireToGateStore(Context).AcceptWithOrderIntentAsync(
                orphan,
                new OrderIntent(
                    "ORPHAN-PICKUP-LEG",
                    orphan.DemandId,
                    "W2G-ORPHAN-PICKUP-1",
                    "TO_PICKUP",
                    "N1-1",
                    Now,
                    "BROKERX-ORPHAN",
                    Options.MapId,
                    12,
                    1,
                    Options.DispatchGeneration),
                TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        /// <summary>Adds another in-flight order intent for one vehicle and returns its upperId.</summary>
        public async Task<string> AddSecondIntentAsync(string vehicleKey)
        {
            string upperId = $"W2G-SECOND-{vehicleKey}";
            Context.OrderIntents.Add(new OrderIntentRow
            {
                UpperId = upperId,
                MovementLegId = Guid.NewGuid().ToString("D"),
                DemandId = "demand-second",
                Purpose = "TO_PICKUP",
                TargetStationId = "N1-1",
                VehicleKey = vehicleKey,
                MapId = 25,
                DestinationStationId = 12,
                AgvLifecycleGeneration = 1,
                DispatchGeneration = 1,
                CreatedAt = Now,
                Status = "PENDING",
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return upperId;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private JourneyRuntimeEngine CreateEngine()
        {
            WireToGateStore store = new(Context);
            IOptions<JourneyRuntimeOptions> options =
                Microsoft.Extensions.Options.Options.Create(Options);
            MovementDispatchService movement = new(store, Riot);
            CatalogAvailabilityAccess catalogAccess = new(
                new CatalogAvailabilityStore(Context),
                Microsoft.Extensions.Options.Options.Create(new MapStationCatalogOptions
                {
                    ApprovedSyncPeriod = TimeSpan.FromSeconds(30),
                    ApprovedMaxUnconfirmed = TimeSpan.FromMinutes(5),
                }),
                new CatalogAlarmLedger(),
                Clock,
                NullLogger<CatalogAvailabilityAccess>.Instance);
            PreCreateGate gate = new(
                Riot, new CatalogAvailabilityStore(Context), Clock, NullLogger<PreCreateGate>.Instance);
            // One of each, shared by the round and the engine, the way the host's scope shares them.
            VehicleDispatchPolicyAccess dispatchPolicy =
                new(new VehicleDispatchPolicyStore(Context), options, Clock);
            OnboardDispatchFactsReader onboardFacts = new(Context, options, Clock);
            DispatchRoundRunner dispatchRound = new(
                Context,
                Catalog,
                Riot,
                new JourneyIntakeCoordinator(
                    new DemandIntakeService(Catalog, Acceptances),
                    movement),
                new DispatchAdmissionChain(
                [
                    .. DispatchAdmissionCriteria.Default(
                        options,
                        new MapStationResolver(),
                        new PackageCapacityStore(Context),
                        store,
                        new VehicleFaultStore(Context),
                        BoxCounts,
                        NullLogger<SlotCapacityCriterion>.Instance,
                        routeGraph: null,
                        catalog: catalogAccess,
                        createGate: gate),
                    .. _extraCriterion is null ? Array.Empty<IDispatchAdmissionCriterion>() : [_extraCriterion],
                ]),
                DispatchCandidateOrdering.Ranker(),
                dispatchPolicy,
                AreaAssignments,
                SlotPositions,
                RoundOutcomes,
                onboardFacts,
                InTransit,
                options,
                Clock,
                EngineLog);
            return new JourneyRuntimeEngine(
                Context,
                Riot,
                Riot,
                new MapStationResolver(),
                new BoundFixedTaskStationResolver(TaskTypeStationRuntimeSeed.Access(Context), options),
                TaskTypeStationRuntimeSeed.Access(Context),
                TaskTypeStationRuntimeSeed.CatalogBindingHolds(Context, Clock),
                movement,
                store,
                new OnboardJourneyPublisher(store, new SilentPeer(), Clock),
                BoxCounts,
                new PackageCapacityStore(Context),
                catalogAccess,
                new CatalogAvailabilityStore(Context),
                gate,
                new VehicleRoster(options),
                dispatchPolicy,
                Riot,
                CheckpointWaits,
                CreateFaultCoordinator(),
                dispatchRound,
                onboardFacts,
                options,
                Clock,
                EngineLog);
        }

        private VehicleFaultCoordinator CreateFaultCoordinator()
        {
            VehicleFaultStore faults = new(Context);
            RiotOrderCommandAuditStore audit = new(Context);
            IOptions<VehicleFaultOptions> faultOptions =
                Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions());
            return new VehicleFaultCoordinator(
                faults,
                Riot,
                Riot,
                Riot,
                audit,
                new RiotOrderCommandService(Riot, audit, Riot, Clock),
                new EmergencyStopSupervisor(
                    Riot,
                    Riot,
                    Riot,
                    audit,
                    faults,
                    Microsoft.Extensions.Options.Options.Create(new RiotCommandOptions()),
                    Clock,
                    NullLogger<EmergencyStopSupervisor>.Instance),
                new VehicleMotionLedger(faultOptions),
                faultOptions,
                Clock,
                NullLogger<VehicleFaultCoordinator>.Instance);
        }

        private async Task SeedAsync()
        {
            for (int index = 0; index < AgvIds.Length; index++)
            {
                Context.SessionRecoveries.Add(new SessionRecoveryRow
                {
                    AgvId = AgvIds[index],
                    SessionGeneration = 1,
                    ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
                    ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    ProfileId = ProtocolCandidateIdentity.ProfileId,
                    ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    CapabilityRevision = 1,
                    CapabilityHash = new string('1', 64),
                    SafetyRevision = 7,
                    SafetyHash = new string('2', 64),
                    DepartureSafe = true,
                    RecoveryReportId = Guid.NewGuid().ToString("D"),
                    Readiness = SessionReadiness.Ready,
                    ReasonCode = "READY",
                    UpdatedAt = Now,
                });
            }

            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            foreach (string agvId in AgvIds)
            {
                await AddCapabilitySnapshotAsync(agvId, 1);
                await AddSafetySnapshotAsync(agvId, 1, departureSafe: true);
            }

            // Every demand's AREA in the configured zone: since control-server#72 the table is the whole
            // execution whitelist, so without one no vehicle takes anything.
            await AreaAssignments.ImportAsync(
                [.. Areas.Select(area => new AreaAssignment(area, Options.DispatchZone, "FRONT"))], Now);
            Context.ChangeTracker.Clear();
            Catalog.Set([.. Enumerable.Range(0, AgvIds.Length).Select(Demand)]);
        }

        private static AcceptedDemandSnapshot Demand(int index) => Demand(index, Areas[index], index);

        /// <summary>
        /// A demand like the seeded ones, in any AREA and at any age: <paramref name="age"/> moves its
        /// creation time, so a round's ranking can be arranged.
        /// </summary>
        public static AcceptedDemandSnapshot Demand(int index, string area, int age) => new(
            $"1000000{index}-0000-4000-8000-00000000000{index}",
            $"SUBLOT-00{index}|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            $"SERIES-{index}",
            "WIRE_TO_GATE",
            $"SUBLOT-00{index}",
            1,
            Now.AddMinutes(-10 + age),
            Now.AddMinutes(-9 + age),
            $"TRACE-{index}",
            $"COMMIT-{index}",
            new LiveMesFieldSet(area, $"EQP-0{index}", "STEP-01", Now, "PDFN5×6-8L(12R)"));

        private Task AddCapabilitySnapshotAsync(string agvId, long generation) => AddInboxAsync(
            agvId,
            "CapabilitySnapshot",
            generation,
            new
            {
                capabilityVersion = 1,
                observedAt = Now,
                slotModelVersion = "SLOT-MODEL-1",
                activeSlotConfigurationVersion = "SLOT-CONFIG-1",
                activeSlotConfigurationFingerprint = new string('0', 64),
                slotStates = Slots(),
                supportsBatchUnlock = true,
                onboardJournalFormatVersion = 1,
            });

        private Task AddSafetySnapshotAsync(string agvId, long generation, bool departureSafe) => AddInboxAsync(
            agvId,
            "SafetyStateSnapshot",
            generation,
            new
            {
                safetyStateVersion = 7,
                observedAt = Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>(),
                },
                slotStates = Slots(),
            });

        private static object[] Slots() => [.. Enumerable.Range(1, 8).Select(slot => new
        {
            slotNo = slot,
            operability = "OPERABLE",
            administrativeAvailability = "ENABLED",
            physicalState = "EMPTY",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = Array.Empty<string>(),
        })];

        private async Task AddInboxAsync(string agvId, string messageType, long generation, object payload)
        {
            string messageId = Guid.NewGuid().ToString("D");
            if (messageType == "SafetyStateSnapshot")
            {
                if (!_safetyMessageIds.TryGetValue(agvId, out List<string>? ids))
                {
                    ids = [];
                    _safetyMessageIds[agvId] = ids;
                }

                ids.Add(messageId);
            }

            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = messageType,
                RequestJson = JsonSerializer.Serialize(new
                {
                    messageType,
                    messageId,
                    agvId,
                    sessionGeneration = generation,
                    sentAt = Clock.GetUtcNow(),
                    payload,
                }, SerializerOptions),
                ContentHash = new string('f', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Clock.GetUtcNow(),
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        private static JourneyRuntimeOptions FleetOptions(int budgetMilliseconds) => new()
        {
            Enabled = true,
            PollInterval = TimeSpan.FromSeconds(1),
            AgvId = AgvIds[0],
            VehicleKey = VehicleKeys[0],
            AgvLifecycleGeneration = 1,
            MapId = 25,
            MapIdentity = "MAP-25",
            DispatchZone = "MAP-25-WIRE_TO_GATE",
            DispatchGeneration = 1,
            MinimumBatteryPercent = 40,
            MaximumEvidenceAge = TimeSpan.FromMinutes(2),
            SublotBoxCountPath = "/api/v2/sublot-box-count",
            AllowedWorkTypes = ["WIRE_TO_GATE"],
            AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
            AdmissionPolicyVersion = 1,
            AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1",
            // Off: these tests are about several vehicles sharing a map, not about the pickup wait.
            StationDepartureWaitTimeout = TimeSpan.Zero,
            Fleet = [.. Enumerable.Range(0, AgvIds.Length).Select(index => new FleetVehicleOptions
            {
                AgvId = AgvIds[index],
                VehicleKey = VehicleKeys[index],
                AgvLifecycleGeneration = 1,
                AllowedTaskTypes = ["WIRE_TO_GATE"],
                Zones = ["MAP-25-WIRE_TO_GATE"],
                RoundTimeoutMilliseconds = budgetMilliseconds,
            })],
        };

        public static int PickupStationFor(int index) => PickupStations[index];
    }

    private sealed class FleetCatalog : IMesIngestCatalog
    {
        private AcceptedDemandSnapshot[] _items = [];

        public int ReadCount { get; private set; }

        /// <summary>The read, counted from 1, that never comes back; null when every read answers.</summary>
        public int? HangOnRead { get; set; }

        /// <summary>Whether every catalog read fails the way an unreachable MesIngest does.</summary>
        public bool Unreachable { get; set; }

        /// <summary>
        /// Demands every read after the round's first no longer lists -- what intake's final re-read meets when MES
        /// closed the demand while the round was deciding.
        /// </summary>
        public HashSet<string> GoneOnReread { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Demands every read after the round's first lists with a later revision -- what intake's final re-read
        /// meets when MES revised the demand while the round was deciding, which it reports as
        /// <see cref="DemandIntakeOutcome.CandidateChanged"/>.
        /// </summary>
        public HashSet<string> ChangedOnReread { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Runs once, just before the round's second read answers, and clears itself -- the moment intake re-reads
        /// the catalog, which is where a test stages a change to the facts the final admission gate reads next.
        /// </summary>
        /// <remarks>
        /// It is a callback rather than a delay: nothing here waits, so no test can wedge the process on it. Its
        /// self-clearing is what lets a test assert that the re-read really happened (control-server#239's habit),
        /// instead of passing on a round that never reached intake.
        /// </remarks>
        public Action? OnReread { get; set; }

        public void Set(AcceptedDemandSnapshot[] items) => _items = items;

        public async Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            if (ReadCount == HangOnRead)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            if (Unreachable)
            {
                throw new HttpRequestException("MesIngest is unreachable.");
            }

            if (ReadCount > 1 && OnReread is Action staged)
            {
                OnReread = null;
                staged();
            }

            AcceptedDemandSnapshot[] items = ReadCount == 1
                ? _items
                : [.. _items
                    .Where(item => !GoneOnReread.Contains(item.DemandId))
                    .Select(item => ChangedOnReread.Contains(item.DemandId)
                        ? item with { DemandRevision = item.DemandRevision + 1 }
                        : item)];
            return new DemandCatalogSnapshot(
                _items.FirstOrDefault()?.HistoryEpoch ?? "11111111-1111-4111-8111-111111111111",
                21,
                items);
        }

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_items.SingleOrDefault(item => item.DemandId == demandId));
        }
    }

    private sealed class FleetBoxCounts : ISublotBoxCountReader
    {
        public int Calls { get; private set; }

        /// <summary>The call, counted from 1, that never comes back; null when every call answers.</summary>
        public int? HangOnCall { get; set; }

        /// <summary>Runs just before the hanging call starts to wait, so a test can see the state it hangs in.</summary>
        public Action? OnHang { get; set; }

        public async Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken)
        {
            _ = sublot;
            Calls++;
            if (Calls == HangOnCall)
            {
                OnHang?.Invoke();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return 4;
        }
    }

    /// <summary>
    /// The real area assignment store, counting how often a round asks for the current version.
    /// </summary>
    /// <remarks>
    /// Real rather than a table held in memory: acceptance freezes the version a plan carries, and a version
    /// that was never written cannot be frozen.
    /// </remarks>
    private sealed class CountingAreaAssignments(IAreaAssignmentStore inner) : IAreaAssignmentStore
    {
        public int CurrentReads { get; private set; }

        public Task<AreaAssignmentTableVersion> ImportAsync(IReadOnlyList<AreaAssignment> assignments, DateTimeOffset at) =>
            inner.WriteVersionAsync(assignments, at, TestContext.Current.CancellationToken);

        public Task<AreaAssignmentTableVersion?> ReadCurrentAsync(CancellationToken cancellationToken)
        {
            CurrentReads++;
            return inner.ReadCurrentAsync(cancellationToken);
        }

        public Task<AreaAssignmentTableVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken) =>
            inner.ReadVersionAsync(version, cancellationToken);

        public Task<AreaAssignmentTableVersion> WriteVersionAsync(
            IReadOnlyList<AreaAssignment> assignments,
            DateTimeOffset importedAt,
            CancellationToken cancellationToken) => inner.WriteVersionAsync(assignments, importedAt, cancellationToken);
    }

    /// <summary>Every vehicle on an eight-slot model, front four and rear four, with each read written down.</summary>
    private sealed class CountingSlotPositions : IVehicleSlotPositionReader
    {
        public List<string> Reads { get; } = [];

        public Task<VehicleSlotPositions?> ReadAsync(string agvId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Reads.Add(agvId);
            return Task.FromResult<VehicleSlotPositions?>(new VehicleSlotPositions(
                agvId,
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
                }));
        }

        public Task<SlotPositionGroupCapacity> ReadLargestGroupCapacityAsync(
            IReadOnlyCollection<string> agvIds,
            string slotPosition,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// The host's in-transit path, with every vehicle it was asked about written down; <see cref="Answer"/> overrides
    /// it for the one test about a yes.
    /// </summary>
    private sealed class RecordingInTransitQualification : IInTransitDispatchQualification
    {
        private readonly InTransitAppendNotOpened _host = new();

        public List<(DispatchRoundFacts Round, FleetVehicle Vehicle)> Asked { get; } = [];

        public bool? Answer { get; set; }

        /// <summary>Whether the path fails the way a read of its own would; what control-server#211 puts here can throw.</summary>
        public bool Throws { get; set; }

        public async Task<bool> QualifiesAsync(
            DispatchRoundFacts round,
            FleetVehicle vehicle,
            CancellationToken cancellationToken)
        {
            Asked.Add((round, vehicle));
            if (Throws)
            {
                throw new HttpRequestException($"The in-transit path did not answer for {vehicle.AgvId}.");
            }

            bool host = await _host.QualifiesAsync(round, vehicle, cancellationToken);
            return Answer ?? host;
        }
    }

    private sealed class RecordingRoundOutcomes : IDispatchRoundOutcomeSink
    {
        public List<DispatchRoundOutcome> Outcomes { get; } = [];

        /// <summary>
        /// The real hook this forwards to, for a test about what the round end concludes; null for none. Typed as the
        /// structural block sink because that is the conclusion a round's own tests can be about.
        /// </summary>
        public StructuralDispatchBlockSink? Inner { get; set; }

        public async Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            if (Inner is not null)
            {
                await Inner.RecordAsync(outcome, cancellationToken);
            }
        }
    }

    /// <summary>The real store, with every journey plan intake hands it written down first.</summary>
    private sealed class RecordingAcceptances(WireToGateStore inner, List<JourneyExecutionPlan> plans)
        : IJourneyAcceptanceStore
    {
        /// <summary>
        /// Thrown instead of the first acceptance, and only that one, the way the real store refuses one it cannot
        /// make good on. Nothing is written when it throws, so the round claimed a demand it never accepted.
        /// </summary>
        public Exception? ThrowOnFirstAccept { get; set; }

        /// <summary>
        /// Hangs in place of the first acceptance, and only that one, so the vehicle's budget cuts the segment off
        /// before anything is committed. The claim the segment took ahead of intake then stands for nothing.
        /// </summary>
        /// <remarks>
        /// The caller must give that vehicle a <c>RoundTimeoutMilliseconds</c> shorter than
        /// <see cref="HangCeiling"/>, which is what ends the wait in the test this hook is for.
        /// </remarks>
        public bool HangBeforeFirstAccept { get; set; }

        /// <summary>
        /// Hangs once the first acceptance has committed, so the budget cuts the segment off with the demand
        /// genuinely accepted -- the timing that tells a claim read from the database from one guessed at.
        /// </summary>
        /// <inheritdoc cref="HangBeforeFirstAccept" path="/remarks"/>
        public bool HangAfterFirstAccept { get; set; }

        /// <summary>
        /// How long either hook waits before giving up, rather than waiting forever.
        /// </summary>
        /// <remarks>
        /// Longer than any budget a test sets, so it never ends a wait the test meant the budget to end: the
        /// semantics are those of an unanswered call either way. What it rules out is a caller that forgets the
        /// short budget, or a refactor that carries <see cref="CancellationToken.None"/> down to this layer --
        /// either would hang the whole test process here. A wedged run is worse than one red test: the job's
        /// timeout cancels it, and cancelling a job on a self-hosted runner wedges the runner session, and the
        /// server's <c>test</c> workflow has one runner.
        /// </remarks>
        private static readonly TimeSpan HangCeiling = TimeSpan.FromSeconds(60);

        public Task AcceptWithOrderIntentAsync(
            AcceptedDemandSnapshot snapshot,
            OrderIntent orderIntent,
            CancellationToken cancellationToken) =>
            inner.AcceptWithOrderIntentAsync(snapshot, orderIntent, cancellationToken);

        public async Task AcceptWithOrderIntentAsync(
            AcceptedDemandSnapshot snapshot,
            OrderIntent orderIntent,
            JourneyExecutionPlan journey,
            CancellationToken cancellationToken)
        {
            if (ThrowOnFirstAccept is Exception refusal)
            {
                ThrowOnFirstAccept = null;
                throw refusal;
            }

            if (HangBeforeFirstAccept)
            {
                HangBeforeFirstAccept = false;
                await Task.Delay(HangCeiling, cancellationToken).ConfigureAwait(false);
            }

            plans.Add(journey);
            await inner.AcceptWithOrderIntentAsync(snapshot, orderIntent, journey, cancellationToken)
                .ConfigureAwait(false);

            if (HangAfterFirstAccept)
            {
                HangAfterFirstAccept = false;
                await Task.Delay(HangCeiling, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class SilentPeer : IOnboardPeer
    {
        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            _ = ndjsonLine;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A RIoT that answers per vehicle, records the order it was asked in, and can be made to hang
    /// on one vehicle the way an unanswered call does.
    /// </summary>
    private sealed class FleetRiot(MovableClock clock, JourneyRuntimeOptions options)
        : IRiotMovementGateway, IRiotVehicleFacts, IRiotMapStationCatalog, IVehicleMotionFacts,
          IRiotRouteCostProbe, IRiotOrderCommandGateway, IRiotVehicleEmergencyFacts, IRiotVehicleOrderFacts
    {
        private readonly Dictionary<string, RiotOrderObservation> _orders = new(StringComparer.Ordinal);

        /// <summary>The vehicle key whose reads never come back, or null.</summary>
        public string? HangOn { get; set; }

        /// <summary>What RIoT reports in movementState; null reads as Unknown.</summary>
        public string? MovementState { get; set; }

        /// <summary>The vehicle key whose reads fail the way an unreachable RIoT does, or null.</summary>
        public string? FailOn { get; set; }

        /// <summary>Runs just before the failing read throws, so a test can stage what the segment leaves behind.</summary>
        public Action? OnFail { get; set; }

        /// <summary>
        /// The vehicle key whose reads end in a cancellation that is not the host's, the way a store's own
        /// write timeout does, or null.
        /// </summary>
        public string? CancelOn { get; set; }

        /// <summary>Every vehicle read, in the order it was asked, so a test can see the segments.</summary>
        public List<string> VehicleReads { get; } = [];

        /// <summary>Every order created, oldest first, as (vehicleKey, upperId, destination station).</summary>
        public List<(string VehicleKey, string UpperId, int DestinationStationId)> Creates { get; } = [];

        public async Task<RiotVehicleObservation> ReadVehicleAsync(
            string vehicleKey,
            CancellationToken cancellationToken)
        {
            VehicleReads.Add(vehicleKey);
            if (string.Equals(HangOn, vehicleKey, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(FailOn, vehicleKey, StringComparison.Ordinal))
            {
                OnFail?.Invoke();
                throw new HttpRequestException($"RIoT did not answer for {vehicleKey}.");
            }

            if (string.Equals(CancelOn, vehicleKey, StringComparison.Ordinal))
            {
                // A token of its own, already cancelled: neither the round's budget nor the host's shutdown.
                throw new OperationCanceledException(
                    $"A deadline of this vehicle's own fired for {vehicleKey}.", new CancellationToken(true));
            }

            return new RiotVehicleObservation(
                vehicleKey,
                Connected: true,
                Enabled: true,
                ProcState: "IDLE",
                CurrentMap: options.MapIdentity,
                CurrentStationId: 300,
                BatteryPercent: 80,
                BatteryState: "NO_CHARGE",
                Speed: 0,
                ObservedAt: clock.GetUtcNow(),
                LockStatus: 0,
                OrderTaskId: null);
        }

        public Task<VehicleMotionSample> SampleMotionAsync(string deviceKey, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new VehicleMotionSample(
                deviceKey,
                HttpRiotMovementGateway.ReadMotion(MovementState, 0),
                MovementState,
                0,
                options.MapIdentity,
                300,
                clock.GetUtcNow()));
        }

        public Task<RiotMapStationCatalogSnapshot> ReadMapStationsAsync(
            int mapId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new RiotMapStationCatalogSnapshot(
                mapId,
                clock.GetUtcNow(),
                new string('c', 64),
                [
                    new RiotMapStation(12, "N1-1"),
                    new RiotMapStation(13, "N1-2"),
                    new RiotMapStation(14, "N1-3"),
                    new RiotMapStation(210, "关卡"),
                ]));
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_orders.TryGetValue(upperId, out RiotOrderObservation? order)
                ? order
                : new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Creates.Add((intent.VehicleKey, intent.UpperId, intent.DestinationStationId));
            RiotOrderObservation active = new(
                intent.UpperId,
                RiotOrderObservationKind.Active,
                $"ORDER-{intent.UpperId}",
                OrderState: 3,
                VehicleKey: intent.VehicleKey,
                MapId: intent.MapId,
                DestinationStationId: intent.DestinationStationId);
            _orders[intent.UpperId] = active;
            return Task.FromResult(active);
        }

        public Task<RiotRouteCost?> ReadRouteCostAsync(
            int mapId,
            int stationId,
            string vehicleKey,
            CancellationToken cancellationToken)
        {
            _ = mapId;
            _ = stationId;
            _ = vehicleKey;
            _ = cancellationToken;
            return Task.FromResult<RiotRouteCost?>(new RiotRouteCost(12_000));
        }

        /// <summary>Every order command issued, oldest first, as (commandType, orderId).</summary>
        public List<(string CommandType, string OrderId)> OrderCommands { get; } = [];

        /// <summary>Every emergency command issued, oldest first, as (commandType, deviceKey).</summary>
        public List<(string CommandType, string DeviceKey)> EmergencyCommands { get; } = [];

        /// <summary>
        /// Moves an order to RIoT's terminal FAILED, the way RIoT reports a move order that could
        /// not be carried out. The fake never applies a command's consequence, so a hold issued
        /// against this order leaves it here -- which is what makes the retry rule observable.
        /// </summary>
        public void FailOrder(string upperId)
        {
            RiotOrderObservation order = _orders[upperId];
            _orders[upperId] = order with
            {
                Kind = RiotOrderObservationKind.Terminal,
                OrderState = RiotOrderState.Failed,
            };
        }

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind,
            string orderId,
            string? reason,
            CancellationToken cancellationToken)
        {
            _ = reason;
            _ = cancellationToken;
            string commandType = RiotCommandTypeNames.For(kind);
            OrderCommands.Add((commandType, orderId));
            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(commandType, "SdkAccepted", clock.GetUtcNow(), ResultPresent: true)));
        }

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind,
            string deviceKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            string commandType = RiotCommandTypeNames.For(kind);
            EmergencyCommands.Add((commandType, deviceKey));
            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(commandType, "SdkAccepted", clock.GetUtcNow(), ResultPresent: true)));
        }

        public Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
            string deviceKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new RiotVehicleEmergencyObservation(
                deviceKey, RiotVehicleEmergencyObservation.Ok, clock.GetUtcNow()));
        }

        public Task<RiotVehicleOrderObservation> ReadUnfinishedOrdersAsync(
            string deviceKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new RiotVehicleOrderObservation(deviceKey, false, [], clock.GetUtcNow()));
        }
    }

    private sealed class MovableClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        /// <summary>
        /// How far every read moves the clock on; zero, the default, keeps it still. A transcript test sets it so that
        /// each read of the clock answers differently, and a timestamp shows which read it came from.
        /// </summary>
        public TimeSpan Tick { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            DateTimeOffset now = _utcNow;
            _utcNow += Tick;
            return now;
        }

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
