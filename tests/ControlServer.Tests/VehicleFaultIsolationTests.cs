using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// REQ-0232's two levels, REQ-0233's closed whitelist, REQ-0234's order protection, REQ-0238's
/// cargo hold, REQ-0239's repair resumption, and the two entries that carry the safety weight —
/// REQ-0246's immediate escalation and REQ-0247's combined stop proof.
/// </summary>
/// <remarks>
/// <para>
/// <b>The negatives are the tests that matter.</b> Specification 8.7 does not allow a safety
/// capability into service with a defect, and every defect this code could have is the same defect:
/// treating something unproven as proven. So each refusal is asserted by the name it reports, not
/// by "nothing happened" — a refusal for the wrong reason passes that weaker assertion and would
/// send a person to the wrong place.
/// </para>
/// <para>
/// The fake RIoT holds state and moves it when commanded, rather than replaying a script. Every
/// assertion here is about the server reading something back, and a stand-in that answers the same
/// before and after a call cannot tell a confirmation from a coincidence.
/// </para>
/// </remarks>
public sealed class VehicleFaultIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);

    private static readonly EmergencyStopSubject Subject = new("agv01", "BROKERX-0001");

    private static readonly RiotOrderCommandTarget Order =
        new(Subject.AgvId, "upper-4401", "order-9901");

    // ---- REQ-0232: symptoms reach the first level and stay there ------------------------

    [Theory]
    [InlineData(VehicleFaultEvidence.VehicleOffline)]
    [InlineData(VehicleFaultEvidence.CommunicationLost)]
    [InlineData(VehicleFaultEvidence.NavigationFailed)]
    [InlineData(VehicleFaultEvidence.OrderFailed)]
    public async Task EverySymptomEntersTheSuspectedLevelAndNoFurther(string symptom)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        VehicleFaultDecision decision = await fixture.ObserveAsync(symptom);

        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, decision.Level);
        Assert.Equal(symptom, decision.EvidenceCode);
        Assert.True(decision.NewDispatchBlocked);
    }

    /// <summary>
    /// REQ-0232's last sentence: time raises urgency and never escalates the fact. Ten evaluations
    /// over an hour are still one suspected block, on one generation.
    /// </summary>
    [Fact]
    public async Task TimeAloneNeverEscalatesTheFaultFact()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleFaultDecision first = await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed);

        for (int round = 0; round < 10; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(6));
            VehicleFaultDecision later = await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed);

            Assert.Equal(VehicleFaultLevel.SuspectedBlocked, later.Level);
            Assert.Equal(first.FaultGeneration, later.FaultGeneration);
        }
    }

    // ---- REQ-0233: the closed whitelist -------------------------------------------------

    /// <summary>The one fact that confirms an isolation without a person.</summary>
    [Fact]
    public async Task AFreshCanNotRecoverLatchConfirmsTheIsolation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.Latch = RiotVehicleEmergencyObservation.CanNotRecover;

        VehicleFaultDecision decision = await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed);

        Assert.Equal(VehicleFaultLevel.ConfirmedIsolated, decision.Level);
        Assert.Equal(VehicleFaultEvidence.EmergencyCanNotRecover, decision.EvidenceCode);
        Assert.Equal(VehicleFaultCoordinator.IsolatedAlarm, decision.AlarmCode);
    }

    /// <summary>
    /// The whitelist is checked against what this evaluation read, never against what the caller
    /// said. A caller naming the hard evidence does not thereby produce it.
    /// </summary>
    [Fact]
    public async Task ACallerClaimingTheHardEvidenceWithoutTheLatchStaysSuspected()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.Latch = RiotVehicleEmergencyObservation.Ok;

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.EmergencyCanNotRecover);

        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, decision.Level);
    }

    /// <summary>
    /// A recoverable latch is not the whitelisted fact. Only CAN_NOT_RECOVER is.
    /// </summary>
    [Fact]
    public async Task ARecoverableLatchDoesNotConfirmTheIsolation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.Latch = RiotVehicleEmergencyObservation.CanRecover;

        VehicleFaultDecision decision = await fixture.ObserveAsync(VehicleFaultEvidence.VehicleOffline);

        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, decision.Level);
    }

    [Theory]
    [InlineData(VehicleFaultEvidence.EmergencyCanNotRecover, true)]
    [InlineData(VehicleFaultEvidence.OperatorConfirmed, false)]
    [InlineData(VehicleFaultEvidence.VehicleOffline, false)]
    [InlineData(VehicleFaultEvidence.CommunicationLost, false)]
    [InlineData(VehicleFaultEvidence.NavigationFailed, false)]
    [InlineData(VehicleFaultEvidence.OrderFailed, false)]
    [InlineData("sysState", false)]
    [InlineData("lastErrorCode", false)]
    [InlineData("hardwareErrorCode", false)]
    [InlineData("faultCodesList", false)]
    public void TheAutoConfirmWhitelistHasExactlyOneEntry(string evidenceCode, bool expected) =>
        Assert.Equal(expected, VehicleFaultEvidence.IsAutoConfirmable(evidenceCode));

    /// <summary>
    /// REQ-0233's second sentence. A person may confirm from site facts, and the record says it was
    /// a person: the evidence is not on the automatic whitelist.
    /// </summary>
    [Fact]
    public async Task APersonMayConfirmTheIsolationAndTheRecordSaysItWasNotAutomatic()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        VehicleFaultDecision decision = await fixture.Coordinator.ConfirmIsolationAsync(
            Subject,
            "maint-3",
            "6b3a6f2e-0000-4000-8000-000000000001",
            new FaultedVehicleContext(null, null),
            TestContext.Current.CancellationToken);
        VehicleFaultFact fact = await fixture.ReadFaultAsync();

        Assert.Equal(VehicleFaultLevel.ConfirmedIsolated, decision.Level);
        Assert.Equal(VehicleFaultEvidence.OperatorConfirmed, fact.EvidenceCode);
        Assert.False(fact.EvidenceOnAutoConfirmWhitelist);
    }

    [Theory]
    [InlineData("", "session-1")]
    [InlineData("  ", "session-1")]
    [InlineData("maint-3", "")]
    [InlineData("maint-3", "  ")]
    public async Task AnOperatorConfirmationWithoutBothIdentityAndSessionIsRefused(
        string identity,
        string session)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Coordinator.ConfirmIsolationAsync(
            Subject, identity, session, new FaultedVehicleContext(null, null),
            TestContext.Current.CancellationToken));
        Assert.Null(await fixture.ReadFaultOrNullAsync());
    }

    /// <summary>
    /// Hard evidence seen once is not undone by a later look that missed it.
    /// </summary>
    [Fact]
    public async Task AConfirmedIsolationIsNeverWalkedBackToSuspected()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.Latch = RiotVehicleEmergencyObservation.CanNotRecover;
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed);

        fixture.Riot.Latch = RiotVehicleEmergencyObservation.Ok;
        VehicleFaultDecision later = await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed);

        Assert.Equal(VehicleFaultLevel.ConfirmedIsolated, later.Level);
        Assert.Equal(VehicleFaultEvidence.EmergencyCanNotRecover, later.EvidenceCode);
    }

    /// <summary>
    /// Escalating within a live fault keeps the generation, so the cargo binding and the command
    /// audit stay attached to the same episode.
    /// </summary>
    [Fact]
    public async Task EscalatingFromSuspectedToIsolatedKeepsTheSameGeneration()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleFaultDecision suspected = await fixture.ObserveAsync(VehicleFaultEvidence.VehicleOffline);

        fixture.Riot.Latch = RiotVehicleEmergencyObservation.CanNotRecover;
        VehicleFaultDecision isolated = await fixture.ObserveAsync(VehicleFaultEvidence.VehicleOffline);

        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, suspected.Level);
        Assert.Equal(VehicleFaultLevel.ConfirmedIsolated, isolated.Level);
        Assert.Equal(suspected.FaultGeneration, isolated.FaultGeneration);
    }

    // ---- REQ-0234: new dispatch is blocked at both levels --------------------------------

    [Theory]
    [InlineData(VehicleFaultLevel.SuspectedBlocked, VehicleFaultBlockCriterion.SuspectedReason)]
    [InlineData(VehicleFaultLevel.ConfirmedIsolated, VehicleFaultBlockCriterion.IsolatedReason)]
    public async Task ABlockedVehicleTakesNoNewWorkAndSaysWhichLevelBlockedIt(
        VehicleFaultLevel level,
        string expected)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Faults.RecordLevelAsync(
            Subject.AgvId, level, VehicleFaultEvidence.NavigationFailed, false, Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, await fixture.AdmitAsync(Subject.DeviceKey));
    }

    [Fact]
    public async Task AVehicleWithNoFaultFactIsAdmitted() =>
        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await (await Fixture.CreateAsync()).AdmitAsync(Subject.DeviceKey));

    /// <summary>
    /// A cleared fault stops blocking. Level None with a clearing timestamp is what "cleared"
    /// means, and it is the same fact the emergency supervisor requires before it releases a latch.
    /// </summary>
    [Fact]
    public async Task AClearedFaultStopsBlockingDispatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleFaultFact fact = await fixture.Faults.RecordLevelAsync(
            Subject.AgvId, VehicleFaultLevel.SuspectedBlocked, VehicleFaultEvidence.OrderFailed,
            false, Now, TestContext.Current.CancellationToken);
        await fixture.Faults.ClearAsync(
            Subject.AgvId, fact.FaultGeneration, "repaired", Now, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.AdmitAsync(Subject.DeviceKey));
    }

    /// <summary>
    /// A vehicle key this server cannot map onto an agvId is a vehicle whose fault state cannot be
    /// read, and dispatching one of those is what the criterion exists to prevent.
    /// </summary>
    [Fact]
    public async Task AVehicleKeyThatResolvesToNoAgvIdIsBlocked()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        Assert.Equal(
            VehicleFaultBlockCriterion.IdentityUnresolvedReason,
            await fixture.AdmitAsync("BROKERX-SOMEONE-ELSE"));
    }

    // ---- REQ-0234: the current order is held, never cancelled ---------------------------

    [Fact]
    public async Task EnteringAFaultHoldsTheCurrentOrderAndReadsHeldBack()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, decision.HoldOutcome);
        Assert.Equal(RiotCommandTypeNames.OrderHold, Assert.Single(fixture.Riot.OrderCalls).CommandType);
    }

    /// <summary>
    /// REQ-0234 says it in as many words: Cancel does not stand in for stopping. Cancelling frees
    /// the vehicle for RIoT to dispatch again, which is the opposite of what a block is for.
    /// </summary>
    [Fact]
    public async Task NoFaultPathEverIssuesACancel()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = false;
        fixture.Motion.Script = [Moving(Now)];

        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrder());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.DoesNotContain(
            fixture.Riot.OrderCalls,
            call => call.CommandType == RiotCommandTypeNames.CancelOrder);
    }

    /// <summary>
    /// An unconfirmed hold keeps the block on and raises the high-priority event REQ-0234 requires,
    /// naming the outcome rather than saying only that something is wrong.
    /// </summary>
    [Fact]
    public async Task AHoldThatDidNotReachHeldAlarmsAndKeepsTheBlock()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = false;

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.Equal(RiotOrderCommandOutcome.Pending, decision.HoldOutcome);
        Assert.Equal(VehicleFaultCoordinator.OrderNotProtectedAlarm, decision.AlarmCode);
        Assert.Contains("ORDER_HOLD_PENDING", decision.Reasons);
        Assert.True(decision.NewDispatchBlocked);
    }

    /// <summary>A hold already confirmed is not issued again; the order is where it was sent.</summary>
    [Fact]
    public async Task AConfirmedHoldIsNotReissuedOnTheNextEvaluation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;

        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrder());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.Single(fixture.Riot.OrderCalls);
    }

    /// <summary>A vehicle with no order in flight has nothing to hold, and nothing is sent.</summary>
    [Fact]
    public async Task AVehicleWithNoOrderInFlightIssuesNoOrderCommand()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        VehicleFaultDecision decision = await fixture.ObserveAsync(VehicleFaultEvidence.VehicleOffline);

        Assert.Null(decision.HoldOutcome);
        Assert.Empty(fixture.Riot.OrderCalls);
    }

    // ---- REQ-0238: the cargo keeps its binding ------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ALoadedOrPossiblyLoadedVehicleKeepsItsBinding(
        bool loadingWitnessed,
        bool cargoStateKnown)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithCargo(loadingWitnessed, cargoStateKnown));

        Assert.NotNull(decision.Cargo);
        Assert.Equal("demand-77", decision.Cargo.DemandId);
        Assert.Equal("tdk-77", decision.Cargo.TransportDemandKey);
        Assert.Equal(Subject.AgvId, decision.Cargo.AgvId);
        Assert.Equal(decision.FaultGeneration, decision.Cargo.FaultGeneration);
    }

    /// <summary>
    /// The only case that does not bind: loading was not witnessed and the cargo state is known.
    /// That is a vehicle known to be empty.
    /// </summary>
    [Fact]
    public async Task AVehicleKnownToBeEmptyBindsNoCargo()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed,
            WithCargo(loadingWitnessed: false, cargoStateKnown: true));

        Assert.Null(decision.Cargo);
    }

    [Fact]
    public async Task ReObservingTheSameFaultDoesNotBindTheCargoTwice()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleFaultDecision first = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithCargo(true, true));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        VehicleFaultDecision second = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithCargo(true, true));

        Assert.NotNull(first.Cargo);
        Assert.NotNull(second.Cargo);
        Assert.Equal(first.Cargo.CargoBindingId, second.Cargo.CargoBindingId);
    }

    // ---- REQ-0247: the combined stop proof, positive ------------------------------------

    /// <summary>
    /// The only shape that proves it: several consecutive samples, each positively non-moving, all
    /// at the same known station, spaced within the configured window, the newest one fresh.
    /// </summary>
    [Fact]
    public void ThreeAgreeingFreshSamplesAtOneStationProveTheStop()
    {
        VehicleMotionSample[] samples =
        [
            Stopped(Now, station: 4),
            Stopped(Now.AddSeconds(1), station: 4),
            Stopped(Now.AddSeconds(2), station: 4),
        ];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(2), ProofOptions());

        Assert.True(verdict.Proven);
        Assert.Empty(verdict.MissingFacts);
    }

    // ---- REQ-0247: the three facts that do not count on their own -----------------------

    /// <summary>
    /// REQ-0247's first negative. One query is one query, whatever it says.
    /// </summary>
    [Fact]
    public void ASingleQueryIsNeverAStopProof()
    {
        StopProofVerdict verdict = StopProof.Evaluate([Stopped(Now, 4)], Now, ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Equal([StopProof.TooFewSamples], verdict.MissingFacts);
    }

    /// <summary>
    /// REQ-0247's second and third negatives, together. An order in HELD and an engaged emergency
    /// latch are both true here and neither is read: the movement state is unknown, so the proof
    /// fails. A stop proof that could be satisfied by those two would be satisfied by every vehicle
    /// this server has just commanded.
    /// </summary>
    [Fact]
    public async Task AHeldOrderAndAnEngagedLatchDoNotProveTheStop()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        fixture.Riot.Latch = RiotVehicleEmergencyObservation.CanRecover;
        fixture.Motion.Script = [Unreadable(Now)];

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, decision.HoldOutcome);
        Assert.False(decision.StopProof.Proven);
        Assert.Contains(StopProof.MotionUnknown, decision.StopProof.MissingFacts);
        Assert.False((await fixture.ReadFaultAsync()).StopProven);
    }

    // ---- REQ-0247: every way the proof fails, by name -----------------------------------

    [Fact]
    public void OneMovingSampleInTheWindowDefeatsTheProof()
    {
        VehicleMotionSample[] samples =
            [Stopped(Now, 4), Moving(Now.AddSeconds(1)), Stopped(Now.AddSeconds(2), 4)];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(2), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.MotionObserved, verdict.MissingFacts);
    }

    /// <summary>
    /// A vehicle between stations has no position at all — Round 10 recorded exactly that through a
    /// whole move. Unknown is a missing fact, not an unchanged one.
    /// </summary>
    [Fact]
    public void AVehicleBetweenStationsHasNoPositionAndCannotBeProvenStopped()
    {
        VehicleMotionSample[] samples =
        [
            Stopped(Now, station: null),
            Stopped(Now.AddSeconds(1), station: null),
            Stopped(Now.AddSeconds(2), station: null),
        ];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(2), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.PositionUnknown, verdict.MissingFacts);
        Assert.DoesNotContain(StopProof.PositionChanged, verdict.MissingFacts);
    }

    [Fact]
    public void APositionThatChangedIsMovementUnderAnotherName()
    {
        VehicleMotionSample[] samples =
            [Stopped(Now, 4), Stopped(Now.AddSeconds(1), 4), Stopped(Now.AddSeconds(2), 5)];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(2), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.PositionChanged, verdict.MissingFacts);
    }

    /// <summary>A Map change is a position change even when the station number matches.</summary>
    [Fact]
    public void AStationNumberOnAnotherMapIsNotTheSamePlace()
    {
        VehicleMotionSample[] samples =
        [
            Stopped(Now, 4),
            Stopped(Now.AddSeconds(1), 4),
            Stopped(Now.AddSeconds(2), 4) with { CurrentMap = "map19" },
        ];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(2), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.PositionChanged, verdict.MissingFacts);
    }

    /// <summary>Three reads in one instant are one observation, not three moments.</summary>
    [Fact]
    public void SamplesTakenTooCloseTogetherAreOneObservation()
    {
        VehicleMotionSample[] samples =
            [Stopped(Now, 4), Stopped(Now.AddMilliseconds(10), 4), Stopped(Now.AddMilliseconds(20), 4)];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddMilliseconds(20), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.SamplesTooClose, verdict.MissingFacts);
    }

    /// <summary>
    /// "No sign of movement in between" is a claim about the whole interval. A vehicle can leave a
    /// station and return to it in a gap nobody watched, and every sample would still agree.
    /// </summary>
    [Fact]
    public void AGapNobodyWatchedIsNotAPeriodWithNoSignOfMovement()
    {
        VehicleMotionSample[] samples =
        [
            Stopped(Now, 4),
            Stopped(Now.AddMinutes(30), 4),
            Stopped(Now.AddMinutes(30).AddSeconds(1), 4),
        ];

        StopProofVerdict verdict = StopProof.Evaluate(
            samples, Now.AddMinutes(30).AddSeconds(1), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.ObservationGap, verdict.MissingFacts);
    }

    [Fact]
    public void EvidenceOlderThanTheConfiguredMaximumIsNotFresh()
    {
        VehicleMotionSample[] samples =
            [Stopped(Now, 4), Stopped(Now.AddSeconds(1), 4), Stopped(Now.AddSeconds(2), 4)];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(20), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.EvidenceStale, verdict.MissingFacts);
    }

    /// <summary>
    /// A future timestamp means two clocks disagree, and an age computed from disagreeing clocks
    /// proves nothing about freshness.
    /// </summary>
    [Fact]
    public void ASampleFromTheFutureIsNotFreshEither()
    {
        VehicleMotionSample[] samples =
            [Stopped(Now, 4), Stopped(Now.AddSeconds(1), 4), Stopped(Now.AddSeconds(2), 4)];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(1), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.EvidenceStale, verdict.MissingFacts);
    }

    /// <summary>Every missing fact is named, not only the first one found.</summary>
    [Fact]
    public void AProofThatFailsSeveralWaysNamesAllOfThem()
    {
        VehicleMotionSample[] samples =
        [
            Stopped(Now, 4),
            Moving(Now.AddSeconds(1)),
            Stopped(Now.AddSeconds(2), station: null),
        ];

        StopProofVerdict verdict = StopProof.Evaluate(samples, Now.AddSeconds(2), ProofOptions());

        Assert.False(verdict.Proven);
        Assert.Contains(StopProof.MotionObserved, verdict.MissingFacts);
        Assert.Contains(StopProof.PositionUnknown, verdict.MissingFacts);
    }

    // ---- The closed movementState list --------------------------------------------------

    [Theory]
    [InlineData("MT_FINISHED", 0d, VehicleMotionReading.NotMoving)]
    [InlineData("MT_PAUSED", 0d, VehicleMotionReading.NotMoving)]
    [InlineData("MT_RUNNING", 0d, VehicleMotionReading.Moving)]
    [InlineData("MT_FINISHED", 0.4d, VehicleMotionReading.Moving)]
    [InlineData("MT_NA", 0d, VehicleMotionReading.Unknown)]
    [InlineData("MT_WAIT_FOR_CHECKPOINT", 0d, VehicleMotionReading.Unknown)]
    [InlineData("MT_WAIT_FOR_START", 0d, VehicleMotionReading.Unknown)]
    [InlineData("MT_IN_CANCEL", 0d, VehicleMotionReading.Unknown)]
    [InlineData("", 0d, VehicleMotionReading.Unknown)]
    [InlineData(null, 0d, VehicleMotionReading.Unknown)]
    [InlineData("MT_FINISHED", null, VehicleMotionReading.Unknown)]
    public void OnlyTwoMovementStatesReadAsNotMoving(
        string? movementState,
        double? speed,
        VehicleMotionReading expected) =>
        Assert.Equal(expected, HttpRiotMovementGateway.ReadMotion(movementState, speed));

    // ---- REQ-0246: escalate at once, and only when motion cannot be excluded ------------

    /// <summary>
    /// The whole point of the entry: no fixed timeout is waited out. The escalation happens on the
    /// evaluation that saw the vehicle moving, with the clock never advanced.
    /// </summary>
    [Fact]
    public async Task AVehicleStillMovingEscalatesOnTheFirstEvaluation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Motion.Script = [Moving(Now)];

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.True(decision.Escalated);
        Assert.Equal(
            RiotCommandTypeNames.TriggerEmergency,
            Assert.Single(fixture.Riot.EmergencyCalls).CommandType);
        Assert.NotNull((await fixture.ReadFaultAsync()).EscalatedAt);
    }

    /// <summary>
    /// "Monitoring failed and continued movement cannot be excluded" — an unreadable vehicle is the
    /// clearest case of it, and it escalates like a moving one.
    /// </summary>
    [Fact]
    public async Task AVehicleNobodyCanReadEscalates()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Motion.Script = [Unreadable(Now)];

        VehicleFaultDecision decision = await fixture.ObserveAsync(VehicleFaultEvidence.VehicleOffline);

        Assert.True(decision.Escalated);
        Assert.Single(fixture.Riot.EmergencyCalls);
    }

    /// <summary>A vehicle stopped between stations cannot be placed, so it escalates too.</summary>
    [Fact]
    public async Task AVehicleStoppedBetweenStationsEscalates()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Motion.Script = [Stopped(Now, station: null)];

        VehicleFaultDecision decision = await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed);

        Assert.True(decision.Escalated);
        Assert.Contains(StopProof.PositionUnknown, decision.StopProof.MissingFacts);
    }

    /// <summary>
    /// The negative that keeps REQ-0246 from firing on every first evaluation. Nothing about a
    /// vehicle reading as positively stopped at a known station says it is moving, so a window that
    /// has not filled yet withholds the proof without asserting motion.
    /// </summary>
    [Fact]
    public async Task AVehicleReadingAsStoppedDoesNotEscalateWhileTheWindowFills()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        fixture.Motion.Script = [Stopped(Now, 4)];

        VehicleFaultDecision decision = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.False(decision.Escalated);
        Assert.False(decision.StopProof.Proven);
        Assert.Equal([StopProof.TooFewSamples], decision.StopProof.MissingFacts);
        Assert.Empty(fixture.Riot.EmergencyCalls);
    }

    /// <summary>
    /// Three evaluations later the window is full and the proof is recorded on the fault fact,
    /// where the emergency supervisor reads it.
    /// </summary>
    [Fact]
    public async Task ThreeStoppedEvaluationsProveTheStopAndRecordIt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;

        VehicleFaultDecision decision = await fixture.ObserveStoppedRoundsAsync(3);
        VehicleFaultFact fact = await fixture.ReadFaultAsync();

        Assert.True(decision.StopProof.Proven);
        Assert.False(decision.Escalated);
        Assert.True(fact.StopProven);
        Assert.NotNull(fact.StopProvenAt);
        Assert.Empty(fixture.Riot.EmergencyCalls);
    }

    /// <summary>
    /// A stop that was proven and then lost is recorded as lost. A stale <c>true</c> from an
    /// earlier moment must not be what a release is later earned on.
    /// </summary>
    [Fact]
    public async Task AProvenStopIsWithdrawnWhenTheVehicleMovesAgain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        await fixture.ObserveStoppedRoundsAsync(3);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Motion.Script = [Moving(fixture.Clock.GetUtcNow())];
        VehicleFaultDecision moved = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.False(moved.StopProof.Proven);
        Assert.True(moved.Escalated);
        Assert.False((await fixture.ReadFaultAsync()).StopProven);
    }

    /// <summary>
    /// A second fault starts from an empty window, so the stop it proved last time is not what
    /// this one is decided on. The fault was cleared through the store rather than through a
    /// resumption, which is the path that would otherwise carry samples across.
    /// </summary>
    [Fact]
    public async Task ANewFaultEpisodeStartsFromAnEmptyObservationWindow()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        await fixture.ObserveStoppedRoundsAsync(3);
        VehicleFaultFact proven = await fixture.ReadFaultAsync();
        await fixture.Faults.ClearAsync(
            Subject.AgvId, proven.FaultGeneration, "cleared elsewhere", fixture.Clock.GetUtcNow(),
            TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        VehicleFaultDecision second = await fixture.ObserveAsync(
            VehicleFaultEvidence.NavigationFailed, WithOrder());

        Assert.True(proven.StopProven);
        Assert.False(second.StopProof.Proven);
        Assert.Contains(StopProof.TooFewSamples, second.StopProof.MissingFacts);
        Assert.NotEqual(proven.FaultGeneration, second.FaultGeneration);
    }

    // ---- REQ-0239: repair resumption ----------------------------------------------------

    /// <summary>
    /// Identity, target and cargo binding all agree, so the original task continues on the original
    /// vehicle — and only then are the cargo released and the fault cleared.
    /// </summary>
    [Fact]
    public async Task AResumptionWithEverythingInAgreementContinuesTheOriginalOrder()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());

        VehicleFaultResumeDecision decision = await fixture.ResumeAsync();
        VehicleFaultFact fact = await fixture.ReadFaultAsync();

        Assert.True(decision.Resumed);
        Assert.Empty(decision.Refusals);
        Assert.Contains(
            fixture.Riot.OrderCalls,
            call => call.CommandType == RiotCommandTypeNames.OrderContinue);
        Assert.Equal(VehicleFaultLevel.None, fact.Level);
        Assert.NotNull(fact.ClearedAt);
        Assert.Null(await fixture.Faults.ReadLiveCargoAsync(
            Subject.AgvId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The order must be confirmed HELD. An order RIoT is still executing has not been protected,
    /// and continuing it would be a command aimed at a state that is not there.
    /// </summary>
    [Fact]
    public async Task AnOrderThatIsNotHeldRefusesTheResumption()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = false;
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());

        VehicleFaultResumeDecision decision = await fixture.ResumeAsync();

        Assert.False(decision.Resumed);
        Assert.Contains("RESUME_ORDER_NOT_HELD", decision.Refusals);
    }

    [Fact]
    public async Task ACargoBindingThatDoesNotMatchRefusesTheResumption()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());

        VehicleFaultResumeDecision decision = await fixture.ResumeAsync(demandId: "demand-99");

        Assert.False(decision.Resumed);
        Assert.Contains("RESUME_CARGO_BINDING_MISMATCH", decision.Refusals);
    }

    /// <summary>
    /// REQ-0239 forbids renumbering around a mismatch, so an orderId that is not the one RIoT
    /// reports refuses rather than continues whatever is there.
    /// </summary>
    [Fact]
    public async Task AnOrderIdentityThatDoesNotMatchRefusesTheResumption()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());

        VehicleFaultResumeDecision decision = await fixture.ResumeAsync(
            order: Order with { OrderId = "order-0000" });

        Assert.False(decision.Resumed);
        Assert.Contains("RESUME_ORDER_IDENTITY_MISMATCH", decision.Refusals);
    }

    [Fact]
    public async Task AResumptionForAnotherVehicleIsRefused()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());

        VehicleFaultResumeDecision decision = await fixture.ResumeAsync(
            order: Order with { AgvId = "agv02" });

        Assert.False(decision.Resumed);
        Assert.Contains("RESUME_VEHICLE_MISMATCH", decision.Refusals);
    }

    [Fact]
    public async Task AResumptionOnAVehicleWithNoLiveFaultIsRefused()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.OrderState = RiotOrderState.Paused;

        VehicleFaultResumeDecision decision = await fixture.ResumeAsync();

        Assert.False(decision.Resumed);
        Assert.Contains("RESUME_NO_LIVE_FAULT", decision.Refusals);
    }

    /// <summary>
    /// An unconfirmed continue leaves everything protected. It is exactly the state where the order
    /// may or may not be running, and REQ-0239 keeps the cargo bound through it.
    /// </summary>
    [Fact]
    public async Task AContinueThatWasNotConfirmedClearsNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());
        fixture.Riot.ContinueWorks = false;

        VehicleFaultResumeDecision decision = await fixture.ResumeAsync();
        VehicleFaultFact fact = await fixture.ReadFaultAsync();

        Assert.False(decision.Resumed);
        Assert.Contains("RESUME_CONTINUE_NOT_CONFIRMED", decision.Refusals);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, fact.Level);
        Assert.NotNull(await fixture.Faults.ReadLiveCargoAsync(
            Subject.AgvId, TestContext.Current.CancellationToken));
    }

    // ---- REQ-0167: the cleared fault is what lets the latch come off --------------------

    /// <summary>
    /// The join between this ticket and ticket 10. Only a fault that was really cleared — level
    /// None with a clearing timestamp — plus a proven stop lets the supervisor release the latch;
    /// a resumption that clears both is what makes the vehicle recoverable without a person.
    /// </summary>
    [Fact]
    public async Task AResumptionThatClearsTheFaultLetsTheEmergencyLatchBeReleased()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Riot.HoldWorks = true;
        fixture.Motion.Script = [Moving(Now)];
        await fixture.ObserveAsync(VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());

        fixture.Riot.Latch = RiotVehicleEmergencyObservation.CanRecover;
        EmergencyStopDecision refused = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.ObserveStoppedRoundsAsync(3);
        await fixture.ResumeAsync();
        fixture.Riot.LatchAfterRelease = RiotVehicleEmergencyObservation.Ok;
        EmergencyStopDecision released = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.RecoveryRefused, refused.Action);
        Assert.Contains("EMERGENCY_CAUSE_NOT_CLEARED", refused.Reasons);
        Assert.Equal(EmergencyStopAction.Recovered, released.Action);
    }

    // ---- Configuration ------------------------------------------------------------------

    [Theory]
    [InlineData(1, 500, 5000, 3000, "StopProofSampleCount")]
    [InlineData(3, 0, 5000, 3000, "MinimumSampleInterval")]
    [InlineData(3, 5000, 500, 3000, "MaximumSampleInterval")]
    [InlineData(3, 500, 5000, 0, "MaximumEvidenceAge")]
    public void TheValidatorRefusesAConfigurationThatWouldWeakenTheProof(
        int count,
        int minimumMs,
        int maximumMs,
        int ageMs,
        string expectedKey)
    {
        VehicleFaultOptions options = new()
        {
            StopProofSampleCount = count,
            MinimumSampleInterval = TimeSpan.FromMilliseconds(minimumMs),
            MaximumSampleInterval = TimeSpan.FromMilliseconds(maximumMs),
            MaximumEvidenceAge = TimeSpan.FromMilliseconds(ageMs),
        };

        ValidateOptionsResult result = new VehicleFaultOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(expectedKey, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultConfigurationIsValid() =>
        Assert.True(new VehicleFaultOptionsValidator().Validate(null, new VehicleFaultOptions()).Succeeded);

    // ---- Fixtures and fakes -------------------------------------------------------------

    private static VehicleFaultOptions ProofOptions() => new();

    private static VehicleMotionSample Stopped(DateTimeOffset at, int? station) => new(
        Subject.DeviceKey, VehicleMotionReading.NotMoving, "MT_FINISHED", 0, "map14", station, at);

    private static VehicleMotionSample Moving(DateTimeOffset at) => new(
        Subject.DeviceKey, VehicleMotionReading.Moving, "MT_RUNNING", 0.6, "map14", null, at);

    private static VehicleMotionSample Unreadable(DateTimeOffset at) => new(
        Subject.DeviceKey, VehicleMotionReading.Unknown, null, null, null, null, at);

    private static FaultedVehicleContext WithOrder() => new(Order, null);

    private static FaultedVehicleContext WithCargo(bool loadingWitnessed, bool cargoStateKnown) =>
        new(null, new FaultedVehicleCargoFacts(
            "demand-77", "leg-1", "tdk-77", loadingWitnessed, cargoStateKnown));

    private static FaultedVehicleContext WithOrderAndCargo() =>
        new(Order, new FaultedVehicleCargoFacts("demand-77", "leg-1", "tdk-77", true, true));

    /// <summary>
    /// RIoT's order state, emergency latch and issued calls, held together because the tests are
    /// about reading them back after commanding them.
    /// </summary>
    private sealed class FakeRiot(TimeProvider clock)
        : IRiotOrderCommandGateway, IRiotVehicleEmergencyFacts, IRiotMovementGateway
    {
        public string? Latch { get; set; } = RiotVehicleEmergencyObservation.Ok;

        public string? LatchAfterTrigger { get; set; } = RiotVehicleEmergencyObservation.CanRecover;

        public string? LatchAfterRelease { get; set; } = RiotVehicleEmergencyObservation.CanRecover;

        public int OrderState { get; set; } = RiotOrderState.Executing;

        /// <summary>Whether an OrderHold actually moves the order to PAUSED.</summary>
        public bool HoldWorks { get; set; }

        /// <summary>Whether an OrderContinue actually moves the order back to EXECUTING.</summary>
        public bool ContinueWorks { get; set; } = true;

        public List<(string CommandType, string Target)> OrderCalls { get; } = [];

        public List<(string CommandType, string DeviceKey)> EmergencyCalls { get; } = [];

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind,
            string orderId,
            string? reason,
            CancellationToken cancellationToken)
        {
            string commandType = RiotCommandTypeNames.For(kind);
            OrderCalls.Add((commandType, orderId));
            switch (kind)
            {
                case RiotOrderCommandKind.Hold when HoldWorks:
                    OrderState = RiotOrderState.Paused;
                    break;
                case RiotOrderCommandKind.ContinueFromHeld when ContinueWorks:
                    OrderState = RiotOrderState.Executing;
                    break;
                default:
                    break;
            }

            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(commandType, "SdkAccepted", clock.GetUtcNow())));
        }

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind,
            string deviceKey,
            CancellationToken cancellationToken)
        {
            string commandType = RiotCommandTypeNames.For(kind);
            EmergencyCalls.Add((commandType, deviceKey));
            Latch = kind == RiotEmergencyCommandKind.Trigger ? LatchAfterTrigger : LatchAfterRelease;
            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(commandType, "SdkAccepted", clock.GetUtcNow())));
        }

        public Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
            string deviceKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RiotVehicleEmergencyObservation(deviceKey, Latch, clock.GetUtcNow()));

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RiotOrderObservation(
                upperId,
                RiotOrderObservationKind.Active,
                Order.OrderId,
                OrderState));

        public Task<RiotOrderObservation> CreateAsync(
            OrderIntent intent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fault coordinator creates no orders.");
    }

    /// <summary>
    /// The motion samples the coordinator will read, one per evaluation.
    /// </summary>
    /// <remarks>
    /// A one-element script repeats, which is what "the vehicle stayed like that" looks like; a
    /// longer one is consumed in order and then holds on its last entry. Each sample is restamped
    /// with the clock as it is handed out, so a test controls spacing by moving the clock rather
    /// than by writing timestamps into the script.
    /// </remarks>
    private sealed class FakeMotion(TimeProvider clock) : IVehicleMotionFacts
    {
        private int taken;

        public VehicleMotionSample[] Script { get; set; } = [Stopped(Now, 4)];

        public Task<VehicleMotionSample> SampleMotionAsync(
            string deviceKey,
            CancellationToken cancellationToken)
        {
            VehicleMotionSample sample = Script[Math.Min(taken, Script.Length - 1)];
            taken++;
            return Task.FromResult(sample with { ObservedAt = clock.GetUtcNow() });
        }
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ControlServerDbContext context;
        private readonly VehicleFaultBlockCriterion criterion;

        private Fixture(SqliteConnection connection, ControlServerDbContext context)
        {
            this.connection = connection;
            this.context = context;
            Faults = new VehicleFaultStore(context);
            Riot = new FakeRiot(Clock);
            Motion = new FakeMotion(Clock);
            RiotOrderCommandAuditStore audit = new(context);
            RiotOrderCommandService commands = new(Riot, audit, Riot, Clock);
            Supervisor = new EmergencyStopSupervisor(
                Riot, Riot, audit, Faults, Options.Create(new RiotCommandOptions()), Clock,
                NullLogger<EmergencyStopSupervisor>.Instance);
            IOptions<VehicleFaultOptions> faultOptions = Options.Create(new VehicleFaultOptions());
            Coordinator = new VehicleFaultCoordinator(
                Faults, Riot, Motion, Riot, audit, commands, Supervisor,
                new VehicleMotionLedger(faultOptions), faultOptions, Clock,
                NullLogger<VehicleFaultCoordinator>.Instance);
            criterion = new VehicleFaultBlockCriterion(
                Faults,
                Options.Create(new JourneyRuntimeOptions
                {
                    AgvId = Subject.AgvId,
                    VehicleKey = Subject.DeviceKey,
                }));
        }

        public MovableClock Clock { get; } = new(Now);

        public FakeRiot Riot { get; }

        public FakeMotion Motion { get; }

        public VehicleFaultStore Faults { get; }

        public EmergencyStopSupervisor Supervisor { get; }

        public VehicleFaultCoordinator Coordinator { get; }

        public static async Task<Fixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return new Fixture(connection, context);
        }

        public Task<VehicleFaultDecision> ObserveAsync(
            string evidenceCode,
            FaultedVehicleContext? context = null) =>
            Coordinator.ObserveAsync(
                Subject,
                evidenceCode,
                context ?? new FaultedVehicleContext(null, null),
                TestContext.Current.CancellationToken);

        /// <summary>
        /// Evaluates <paramref name="rounds"/> times, a second apart, with the vehicle reading as
        /// stopped at one station throughout.
        /// </summary>
        public async Task<VehicleFaultDecision> ObserveStoppedRoundsAsync(int rounds)
        {
            Motion.Script = [Stopped(Clock.GetUtcNow(), 4)];
            VehicleFaultDecision decision = await ObserveAsync(
                VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());
            for (int round = 1; round < rounds; round++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                decision = await ObserveAsync(
                    VehicleFaultEvidence.NavigationFailed, WithOrderAndCargo());
            }

            return decision;
        }

        public Task<VehicleFaultResumeDecision> ResumeAsync(
            RiotOrderCommandTarget? order = null,
            string? demandId = null) =>
            Coordinator.ResumeAsync(
                Subject,
                new VehicleFaultResumption(
                    order ?? Order, demandId ?? "demand-77", "tdk-77", "repaired on site"),
                TestContext.Current.CancellationToken);

        public Task<string> AdmitAsync(string vehicleKey) =>
            criterion.EvaluateAsync(Evaluation(vehicleKey), TestContext.Current.CancellationToken);

        public async Task<VehicleFaultFact> ReadFaultAsync() =>
            await ReadFaultOrNullAsync() ?? throw new InvalidOperationException("No fault fact.");

        public Task<VehicleFaultFact?> ReadFaultOrNullAsync() =>
            Faults.ReadAsync(Subject.AgvId, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await connection.DisposeAsync();
        }

        /// <summary>
        /// The least evaluation the criterion reads from: it looks only at the vehicle's key.
        /// </summary>
        private static DispatchCandidateEvaluation Evaluation(string vehicleKey) => new(
            new AcceptedDemandSnapshot("demand-77", "tdk-77", 1, "epoch-1", 1, Now),
            new DispatchRoundFacts(
                new DemandCatalogSnapshot("epoch-1", 1, []),
                new RiotMapStationCatalogSnapshot(14, Now, "fingerprint", [new RiotMapStation(4, "站 4")]),
                new RiotMapStation(4, "站 4"),
                new HashSet<string>(StringComparer.Ordinal),
                Now),
            new DispatchVehicleFacts(
                vehicleKey,
                null,
                new RiotVehicleObservation(
                    vehicleKey, true, true, "IDLE", "map14", 4, 90, "IDLE", 0, Now, 0, null),
                Now));
    }
}
