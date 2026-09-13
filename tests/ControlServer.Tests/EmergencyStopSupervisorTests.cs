using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// The software emergency stop: wide to enter, strict to leave (REQ-0249), retried under a
/// controlled backoff until RIoT confirms the latch (REQ-0248), and released automatically only
/// when release has been earned (REQ-0167).
/// </summary>
/// <remarks>
/// <para>
/// The negatives carry most of the weight. A safety capability is not allowed into service with a
/// defect, and every defect this class could have has the same shape — treating something unproven
/// as proven — so each refusal is asserted by the name it reports rather than by "no call went
/// out".
/// </para>
/// <para>
/// The fake RIoT here moves its own latch when a command is issued, rather than answering from a
/// script the test wrote in advance. That is the one behaviour worth modelling: every assertion in
/// this file is about the server reading a state back, and a stand-in that returns the same answer
/// before and after the call cannot tell a confirmation from a coincidence.
/// </para>
/// </remarks>
public sealed class EmergencyStopSupervisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly EmergencyStopSubject Subject = new("agv01", "BROKERX-0001");

    // ---- REQ-0249: stopping is wide ----------------------------------------------------

    [Theory]
    [InlineData(EmergencyStopRequestSource.Automatic, null)]
    [InlineData(EmergencyStopRequestSource.OnboardField, null)]
    [InlineData(EmergencyStopRequestSource.ServerOperator, "operator-7")]
    public async Task EveryApprovedRequestSourceTriggersWithoutFurtherAuthorisation(
        EmergencyStopRequestSource source,
        string? identity)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        EmergencyStopDecision decision = await fixture.Supervisor.RequestStopAsync(
            Request(source, identity), TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.Triggered, decision.Action);
        Assert.Equal(
            RiotCommandTypeNames.TriggerEmergency,
            fixture.Gateway.EmergencyCalls.Single().CommandType);
    }

    /// <summary>
    /// The one bar REQ-0249 sets, and it is an identity rather than an approval: a request nobody
    /// signed cannot satisfy "records 身份", so there would be nothing to record.
    /// </summary>
    [Fact]
    public async Task AnUnidentifiedServerOperatorRequestIsRefusedAndIssuesNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        EmergencyStopDecision decision = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.ServerOperator, identity: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.RequestRefused, decision.Action);
        Assert.Contains("EMERGENCY_REQUESTER_UNIDENTIFIED", decision.Reasons);
        Assert.Empty(fixture.Gateway.EmergencyCalls);
    }

    /// <summary>
    /// An already-latched vehicle is already stopped, so nothing goes out — and no attempt row is
    /// written for a call that never happened.
    /// </summary>
    [Fact]
    public async Task AStopAskedForOnAnAlreadyLatchedVehicleIssuesNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        EmergencyStopDecision second = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.LatchConfirmed, second.Action);
        Assert.Single(fixture.Gateway.EmergencyCalls);
        Assert.Single(await fixture.ReadTriggersAsync());
    }

    // ---- REQ-0248: retry until the latch is confirmed -----------------------------------

    [Theory]
    [InlineData(RiotVehicleEmergencyObservation.CanRecover)]
    [InlineData(RiotVehicleEmergencyObservation.CanNotRecover)]
    public async Task ATriggerIsConfirmedByEitherLatchedState(string latched)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = latched;

        EmergencyStopDecision decision = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, decision.Attempt!.Outcome);
        Assert.Null(decision.AlarmCode);
    }

    [Fact]
    public async Task AnUnreadableLatchLeavesTheTriggerUnconfirmedAndAlarming()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = null;

        EmergencyStopDecision decision = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Unknown, decision.Attempt!.Outcome);
        Assert.Equal(EmergencyStopSupervisor.StopUnconfirmedAlarm, decision.AlarmCode);
    }

    /// <summary>
    /// RIoT accepted the trigger and the latch is still OK. That is not a stopped vehicle.
    /// </summary>
    [Fact]
    public async Task AnAcceptedTriggerThatDidNotEngageTheLatchIsPending()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = RiotVehicleEmergencyObservation.Ok;

        EmergencyStopDecision decision = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Pending, decision.Attempt!.Outcome);
        Assert.Equal(EmergencyStopSupervisor.StopUnconfirmedAlarm, decision.AlarmCode);
    }

    [Fact]
    public async Task ARefusedTriggerCallIsRecordedAsFailedAndAlarms()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.Disposition = RiotCommandCallDisposition.Failed;
        fixture.Gateway.LatchAfterTrigger = RiotVehicleEmergencyObservation.Ok;

        EmergencyStopDecision decision = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Failed, decision.Attempt!.Outcome);
        Assert.Equal(EmergencyStopSupervisor.StopUnconfirmedAlarm, decision.AlarmCode);
    }

    [Fact]
    public async Task AnUnconfirmedTriggerWaitsForTheBackoffBeforeTryingAgain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = null;
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.AwaitingRetry, decision.Action);
        Assert.Equal(EmergencyStopSupervisor.StopUnconfirmedAlarm, decision.AlarmCode);
        Assert.Single(fixture.Gateway.EmergencyCalls);
    }

    [Fact]
    public async Task AnUnconfirmedTriggerRetriesOnceTheBackoffHasElapsed()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = null;
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.Triggered, decision.Action);
        Assert.Equal(2, fixture.Gateway.EmergencyCalls.Count);
        IReadOnlyList<RiotOrderCommandAttempt> attempts = await fixture.ReadTriggersAsync();
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
    }

    /// <summary>
    /// The second retry waits longer than the first — the backoff is a schedule, not a fixed
    /// interval.
    /// </summary>
    [Fact]
    public async Task TheSecondRetryWaitsLongerThanTheFirst()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = null;
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Supervisor.EvaluateAsync(Subject, TestContext.Current.CancellationToken);

        // Three seconds bought the first retry; it does not buy the second.
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        EmergencyStopDecision tooSoon = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        EmergencyStopDecision due = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.AwaitingRetry, tooSoon.Action);
        Assert.Equal(EmergencyStopAction.Triggered, due.Action);
        Assert.Equal(3, fixture.Gateway.EmergencyCalls.Count);
    }

    /// <summary>
    /// Geometric, capped, and never exhausted — REQ-0248 has no attempt limit to encode.
    /// </summary>
    [Fact]
    public void TheBackoffGrowsGeometricallyAndThenHoldsAtTheCeiling()
    {
        RiotCommandOptions options = new()
        {
            EmergencyRetryInitialDelay = TimeSpan.FromSeconds(2),
            EmergencyRetryBackoffFactor = 2.0,
            EmergencyRetryMaxDelay = TimeSpan.FromSeconds(30),
        };

        Assert.Equal(TimeSpan.FromSeconds(2), EmergencyStopSupervisor.RetryDelay(1, options));
        Assert.Equal(TimeSpan.FromSeconds(4), EmergencyStopSupervisor.RetryDelay(2, options));
        Assert.Equal(TimeSpan.FromSeconds(16), EmergencyStopSupervisor.RetryDelay(4, options));
        Assert.Equal(TimeSpan.FromSeconds(30), EmergencyStopSupervisor.RetryDelay(9, options));
        Assert.Equal(TimeSpan.FromSeconds(30), EmergencyStopSupervisor.RetryDelay(500, options));
    }

    /// <summary>
    /// The backoff is configurable, which REQ-0248 requires: the delay that keeps a saturated RIoT
    /// from being made worse is a site fact. This binds the three keys the way the host does.
    /// </summary>
    [Fact]
    public void TheBackoffBindsFromConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RiotCommands:EmergencyRetryInitialDelay"] = "00:00:05",
                ["RiotCommands:EmergencyRetryBackoffFactor"] = "1.5",
                ["RiotCommands:EmergencyRetryMaxDelay"] = "00:02:00",
            })
            .Build();

        RiotCommandOptions options = new();
        configuration.GetSection(RiotCommandOptions.SectionName).Bind(options);

        Assert.Equal(TimeSpan.FromSeconds(5), options.EmergencyRetryInitialDelay);
        Assert.Equal(1.5, options.EmergencyRetryBackoffFactor);
        Assert.Equal(TimeSpan.FromMinutes(2), options.EmergencyRetryMaxDelay);
        Assert.Equal(
            ValidateOptionsResult.Success.Succeeded,
            new RiotCommandOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void TheBackoffValidatorRefusesAFactorThatWouldShortenTheDelay()
    {
        RiotCommandOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new RiotCommandOptions
            {
                EmergencyRetryInitialDelay = TimeSpan.FromSeconds(2),
                EmergencyRetryBackoffFactor = 0.5,
                EmergencyRetryMaxDelay = TimeSpan.FromSeconds(1),
            });

        Assert.True(result.Failed);
        Assert.Contains("EmergencyRetryBackoffFactor", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("EmergencyRetryMaxDelay", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfirmedLatchStopsTheRetrying()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = RiotVehicleEmergencyObservation.CanNotRecover;
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.RecoveryRefused, decision.Action);
        Assert.Contains("EMERGENCY_NOT_CAN_RECOVER", decision.Reasons);
        Assert.Equal(EmergencyStopSupervisor.NotRecoverableAlarm, decision.AlarmCode);
        Assert.Single(fixture.Gateway.EmergencyCalls);
    }

    /// <summary>
    /// REQ-0248's "原因消除前状态意外恢复 OK 时立即重触发并告警" — immediately, without waiting
    /// for the next backoff slot.
    /// </summary>
    [Fact]
    public async Task ALatchThatFallsBackToOkOnItsOwnIsRetriggeredAtOnceAndAlarms()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Riot.Latch = RiotVehicleEmergencyObservation.Ok;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.ReTriggeredAfterUnexpectedRelease, decision.Action);
        Assert.Equal(EmergencyStopSupervisor.UnexpectedReleaseAlarm, decision.AlarmCode);
        Assert.Equal(2, fixture.Gateway.EmergencyCalls.Count);
    }

    [Fact]
    public async Task AVehicleWithNoOpenEpisodeIsLeftAlone()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.None, decision.Action);
        Assert.Empty(fixture.Gateway.EmergencyCalls);
    }

    // ---- REQ-0248: a stop asked for again joins the episode already under way ----------

    /// <summary>
    /// The fault flow asks for the stop on every evaluation that cannot prove the vehicle stopped,
    /// and RIoT's latch engages about a second after the call. A second request inside the backoff
    /// is the same stop asked for again, not a second stop, so nothing goes out.
    /// </summary>
    [Fact]
    public async Task AStopAskedForAgainInsideTheBackoffIssuesNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = RiotVehicleEmergencyObservation.Ok;
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        EmergencyStopDecision second = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.AwaitingRetry, second.Action);
        Assert.Equal(EmergencyStopSupervisor.StopUnconfirmedAlarm, second.AlarmCode);
        Assert.Single(fixture.Gateway.EmergencyCalls);
        Assert.Single(await fixture.ReadTriggersAsync());
    }

    /// <summary>
    /// Joining the episode does not suppress REQ-0248's retry: once the backoff has run out, a
    /// latch that still cannot be read back is tried again.
    /// </summary>
    [Fact]
    public async Task AStopAskedForAgainOnceTheBackoffHasElapsedRetries()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = null;
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        EmergencyStopDecision second = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.Triggered, second.Action);
        Assert.Equal(2, fixture.Gateway.EmergencyCalls.Count);
        Assert.Equal([1, 2], (await fixture.ReadTriggersAsync()).Select(attempt => attempt.AttemptNumber));
    }

    /// <summary>
    /// A latch that engaged after the read-back settles the trigger that engaged it. Without this
    /// the only record of a stop that worked says Pending for ever.
    /// </summary>
    [Fact]
    public async Task AStopAskedForAgainAfterTheLatchEngagedConfirmsTheTrigger()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = RiotVehicleEmergencyObservation.Ok;
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Riot.Latch = RiotVehicleEmergencyObservation.CanRecover;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        EmergencyStopDecision second = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.LatchConfirmed, second.Action);
        Assert.Single(fixture.Gateway.EmergencyCalls);
        Assert.Equal(
            RiotOrderCommandOutcome.Confirmed,
            Assert.Single(await fixture.ReadTriggersAsync()).Outcome);
    }

    /// <summary>
    /// A request that finds a confirmed latch released by something else is REQ-0248's unexpected
    /// release, answered the same way an evaluation answers it: at once, with the alarm.
    /// </summary>
    [Fact]
    public async Task AStopAskedForAgainAfterAnExternalReleaseRetriggersAtOnceAndAlarms()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        fixture.Riot.Latch = RiotVehicleEmergencyObservation.Ok;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        EmergencyStopDecision second = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.ReTriggeredAfterUnexpectedRelease, second.Action);
        Assert.Equal(EmergencyStopSupervisor.UnexpectedReleaseAlarm, second.AlarmCode);
        Assert.Equal(2, fixture.Gateway.EmergencyCalls.Count);
    }

    /// <summary>
    /// "停车宽、恢复严": asking for a stop never earns a release, even when every release fact
    /// holds. Only an evaluation releases.
    /// </summary>
    [Fact]
    public async Task AStopRequestNeverReleasesALatchEvenWhenReleaseIsEarned()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterRelease = RiotVehicleEmergencyObservation.Ok;
        long generation = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);
        await fixture.ProveStopAsync(generation);
        await fixture.ClearFaultAsync(generation);

        EmergencyStopDecision second = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.LatchConfirmed, second.Action);
        Assert.DoesNotContain(
            fixture.Gateway.EmergencyCalls,
            call => call.CommandType == RiotCommandTypeNames.CancelEmergency);
    }

    /// <summary>
    /// A request for a newer fault generation is a new episode rather than a repeat of the old one,
    /// and its trigger carries its own generation. Joined to the old episode, the release rule
    /// would compare the old generation against the new fault for ever.
    /// </summary>
    [Fact]
    public async Task AStopForANewerFaultGenerationStartsItsOwnTrigger()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterTrigger = RiotVehicleEmergencyObservation.Ok;
        long first = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, first),
            TestContext.Current.CancellationToken);
        await fixture.ClearFaultAsync(first);
        long second = await fixture.EnterFaultAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        EmergencyStopDecision decision = await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, second),
            TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.Triggered, decision.Action);
        Assert.Equal(2, fixture.Gateway.EmergencyCalls.Count);
        Assert.Equal(second, (await fixture.ReadTriggersAsync())[^1].FaultGeneration);
    }

    // ---- REQ-0167: releasing is strict --------------------------------------------------

    [Fact]
    public async Task RecoveryIsRefusedWhileTheCauseStillStands()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long generation = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);
        await fixture.ProveStopAsync(generation);

        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.RecoveryRefused, decision.Action);
        Assert.Contains("EMERGENCY_CAUSE_NOT_CLEARED", decision.Reasons);
        Assert.DoesNotContain(
            fixture.Gateway.EmergencyCalls,
            call => call.CommandType == RiotCommandTypeNames.CancelEmergency);
    }

    [Fact]
    public async Task RecoveryIsRefusedWhenTheStopWasNeverProven()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long generation = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);
        await fixture.ClearFaultAsync(generation);

        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.RecoveryRefused, decision.Action);
        Assert.Contains("EMERGENCY_STOP_NOT_PROVEN", decision.Reasons);
    }

    /// <summary>
    /// REQ-0167 keeps manual, external and unattributable stops latched. No fault record is the
    /// unattributable case.
    /// </summary>
    [Fact]
    public async Task RecoveryIsRefusedWhenThereIsNoFaultRecordAtAll()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.ServerOperator, "operator-7"),
            TestContext.Current.CancellationToken);

        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.RecoveryRefused, decision.Action);
        Assert.Contains("EMERGENCY_FAULT_FACT_ABSENT", decision.Reasons);
    }

    /// <summary>
    /// A vehicle that faulted again has a newer generation, and the stop proof attached to the
    /// older one says nothing about the current one.
    /// </summary>
    [Fact]
    public async Task RecoveryIsRefusedWhenTheFaultGenerationHasMovedOn()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long first = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, first),
            TestContext.Current.CancellationToken);
        await fixture.ClearFaultAsync(first);
        long second = await fixture.EnterFaultAsync();
        await fixture.ProveStopAsync(second);
        await fixture.ClearFaultAsync(second);

        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(EmergencyStopAction.RecoveryRefused, decision.Action);
        Assert.Contains("EMERGENCY_FAULT_GENERATION_MOVED", decision.Reasons);
    }

    [Fact]
    public async Task RecoveryReleasesTheLatchWhenEveryFactHolds()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterRelease = RiotVehicleEmergencyObservation.Ok;
        long generation = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);
        await fixture.ProveStopAsync(generation);
        await fixture.ClearFaultAsync(generation);

        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.Recovered, decision.Action);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, decision.Attempt!.Outcome);
        Assert.Contains(
            fixture.Gateway.EmergencyCalls,
            call => call.CommandType == RiotCommandTypeNames.CancelEmergency);
    }

    /// <summary>
    /// REQ-0167 requires the release to be checked back against <c>emergencyState=OK</c>. An
    /// accepted call that did not clear the latch has recovered nothing.
    /// </summary>
    [Fact]
    public async Task AReleaseThatDoesNotReadBackOkIsNotConfirmed()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterRelease = RiotVehicleEmergencyObservation.CanRecover;
        long generation = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);
        await fixture.ProveStopAsync(generation);
        await fixture.ClearFaultAsync(generation);

        EmergencyStopDecision decision = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.RecoveryUnconfirmed, decision.Action);
        Assert.Contains("EMERGENCY_RELEASE_NOT_CONFIRMED", decision.Reasons);
        Assert.Equal(EmergencyStopSupervisor.ReleaseUnconfirmedAlarm, decision.AlarmCode);
    }

    /// <summary>
    /// A release that did not clear the latch is retried under the same backoff as a trigger, not
    /// on every tick.
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedReleaseWaitsForTheBackoffBeforeTryingAgain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterRelease = RiotVehicleEmergencyObservation.CanRecover;
        long generation = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);
        await fixture.ProveStopAsync(generation);
        await fixture.ClearFaultAsync(generation);
        await fixture.Supervisor.EvaluateAsync(Subject, TestContext.Current.CancellationToken);
        int callsAfterFirstRelease = fixture.Gateway.EmergencyCalls.Count;

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        EmergencyStopDecision tooSoon = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);
        int callsWhileWaiting = fixture.Gateway.EmergencyCalls.Count;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        EmergencyStopDecision due = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.AwaitingRetry, tooSoon.Action);
        Assert.Equal(EmergencyStopSupervisor.ReleaseUnconfirmedAlarm, tooSoon.AlarmCode);
        Assert.Equal(callsAfterFirstRelease, callsWhileWaiting);
        Assert.Equal(EmergencyStopAction.RecoveryUnconfirmed, due.Action);
        Assert.Equal(callsAfterFirstRelease + 1, fixture.Gateway.EmergencyCalls.Count);
    }

    /// <summary>A confirmed release closes the episode; an unconfirmed one does not.</summary>
    [Fact]
    public async Task AConfirmedReleaseClosesTheEpisodeAndAnUnconfirmedOneDoesNot()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.LatchAfterRelease = RiotVehicleEmergencyObservation.CanRecover;
        long generation = await fixture.EnterFaultAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null, generation),
            TestContext.Current.CancellationToken);
        await fixture.ProveStopAsync(generation);
        await fixture.ClearFaultAsync(generation);
        await fixture.Supervisor.EvaluateAsync(Subject, TestContext.Current.CancellationToken);

        // Still latched, so the episode is still open and the next evaluation still acts on it.
        EmergencyStopDecision stillOpen = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);
        Assert.NotEqual(EmergencyStopAction.None, stillOpen.Action);

        fixture.Gateway.LatchAfterRelease = RiotVehicleEmergencyObservation.Ok;
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Supervisor.EvaluateAsync(Subject, TestContext.Current.CancellationToken);
        EmergencyStopDecision closed = await fixture.Supervisor.EvaluateAsync(
            Subject, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyStopAction.None, closed.Action);
    }

    // ---- the release rule on its own ----------------------------------------------------

    [Fact]
    public void ReleaseObstaclesNamesEveryMissingFact()
    {
        RiotVehicleEmergencyObservation latchedButUnrecoverable = new(
            Subject.DeviceKey, RiotVehicleEmergencyObservation.CanNotRecover, Now);

        IReadOnlyList<string> obstacles = EmergencyStopSupervisor.ReleaseObstacles(
            latchedButUnrecoverable,
            triggerFaultGeneration: 4,
            fault: Fault(VehicleFaultLevel.SuspectedBlocked, generation: 5, stopProven: false, cleared: false));

        Assert.Equal(
            [
                "EMERGENCY_NOT_CAN_RECOVER",
                "EMERGENCY_FAULT_GENERATION_MOVED",
                "EMERGENCY_CAUSE_NOT_CLEARED",
                "EMERGENCY_STOP_NOT_PROVEN",
            ],
            obstacles);
    }

    [Fact]
    public void ReleaseObstaclesIsEmptyOnlyWhenEveryFactHolds()
    {
        RiotVehicleEmergencyObservation recoverable = new(
            Subject.DeviceKey, RiotVehicleEmergencyObservation.CanRecover, Now);

        Assert.Empty(EmergencyStopSupervisor.ReleaseObstacles(
            recoverable,
            triggerFaultGeneration: 5,
            fault: Fault(VehicleFaultLevel.None, generation: 5, stopProven: true, cleared: true)));
    }

    /// <summary>
    /// The audit target for a vehicle command says what it is, because the column it goes into is
    /// named for order identities.
    /// </summary>
    [Fact]
    public async Task EmergencyAttemptsAreFiledUnderThePrefixedVehicleTarget()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Supervisor.RequestStopAsync(
            Request(EmergencyStopRequestSource.Automatic, null), TestContext.Current.CancellationToken);

        IReadOnlyList<RiotOrderCommandAttempt> attempts = await fixture.ReadTriggersAsync();

        Assert.Equal($"vehicle:{Subject.DeviceKey}", Assert.Single(attempts).TargetUpperId);
        Assert.Equal(Subject.AgvId, attempts[0].AgvId);
        Assert.Null(attempts[0].TargetOrderId);
    }

    private static EmergencyStopRequest Request(
        EmergencyStopRequestSource source,
        string? identity,
        long? faultGeneration = null) =>
        new(Subject, source, identity, "GATE_NOT_PROVEN_LOCKED", faultGeneration);

    private static VehicleFaultFact Fault(
        VehicleFaultLevel level,
        long generation,
        bool stopProven,
        bool cleared) =>
        new(
            Subject.AgvId,
            level,
            generation,
            "RIOT_NAVIGATION_FAILED",
            EvidenceOnAutoConfirmWhitelist: true,
            EnteredAt: Now,
            LastEvaluatedAt: Now,
            StopProven: stopProven,
            StopProvenAt: stopProven ? Now : null,
            EscalatedAt: null,
            ClearedAt: cleared ? Now : null,
            ClearedReason: cleared ? "cause cleared" : null);

    /// <summary>The fake RIoT's latch. A command moves it; a read reports it.</summary>
    private sealed class FakeRiot(TimeProvider clock) : IRiotVehicleEmergencyFacts
    {
        public string? Latch { get; set; } = RiotVehicleEmergencyObservation.Ok;

        public Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
            string deviceKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RiotVehicleEmergencyObservation(deviceKey, Latch, clock.GetUtcNow()));
    }

    private sealed class FakeCommandGateway(FakeRiot riot, TimeProvider clock) : IRiotOrderCommandGateway
    {
        public RiotCommandCallDisposition Disposition { get; set; } = RiotCommandCallDisposition.Accepted;

        /// <summary>What the latch becomes after a <c>triggerEmergency</c>.</summary>
        public string? LatchAfterTrigger { get; set; } = RiotVehicleEmergencyObservation.CanRecover;

        /// <summary>
        /// What the latch becomes after a <c>cancelEmergency</c>. Defaults to still latched, so a
        /// test that means "the release worked" has to say so.
        /// </summary>
        public string? LatchAfterRelease { get; set; } = RiotVehicleEmergencyObservation.CanRecover;

        public List<(string CommandType, string DeviceKey)> EmergencyCalls { get; } = [];

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind,
            string orderId,
            string? reason,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The emergency supervisor issues no order commands.");

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind,
            string deviceKey,
            CancellationToken cancellationToken)
        {
            string commandType = RiotCommandTypeNames.For(kind);
            EmergencyCalls.Add((commandType, deviceKey));
            riot.Latch = kind == RiotEmergencyCommandKind.Trigger ? LatchAfterTrigger : LatchAfterRelease;
            return Task.FromResult(new RiotCommandCallResult(
                Disposition,
                new RiotOrderCallReceipt(
                    commandType,
                    Disposition == RiotCommandCallDisposition.Accepted ? "SdkAccepted" : "SdkFailure",
                    clock.GetUtcNow())));
        }
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;
        private readonly RiotOrderCommandAuditStore _audit;
        private readonly VehicleFaultStore _faults;

        private Fixture(SqliteConnection connection, ControlServerDbContext context)
        {
            _connection = connection;
            _context = context;
            _audit = new RiotOrderCommandAuditStore(context);
            _faults = new VehicleFaultStore(context);
            Riot = new FakeRiot(Clock);
            Gateway = new FakeCommandGateway(Riot, Clock);
            Supervisor = new EmergencyStopSupervisor(
                Gateway,
                Riot,
                _audit,
                _faults,
                Options.Create(new RiotCommandOptions()),
                Clock,
                NullLogger<EmergencyStopSupervisor>.Instance);
        }

        public MovableClock Clock { get; } = new(Now);

        public FakeRiot Riot { get; }

        public FakeCommandGateway Gateway { get; }

        public EmergencyStopSupervisor Supervisor { get; }

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

        public Task<IReadOnlyList<RiotOrderCommandAttempt>> ReadTriggersAsync() =>
            _audit.ReadAttemptsAsync(
                RiotCommandTypeNames.TriggerEmergency,
                $"vehicle:{Subject.DeviceKey}",
                TestContext.Current.CancellationToken);

        public async Task<long> EnterFaultAsync()
        {
            VehicleFaultFact fact = await _faults.RecordLevelAsync(
                Subject.AgvId,
                VehicleFaultLevel.SuspectedBlocked,
                "RIOT_NAVIGATION_FAILED",
                evidenceOnAutoConfirmWhitelist: true,
                Clock.GetUtcNow(),
                TestContext.Current.CancellationToken);
            return fact.FaultGeneration;
        }

        public Task ProveStopAsync(long generation) => _faults.RecordStopProofAsync(
            Subject.AgvId, generation, proven: true, Clock.GetUtcNow(), TestContext.Current.CancellationToken);

        public Task ClearFaultAsync(long generation) => _faults.ClearAsync(
            Subject.AgvId, generation, "cause cleared", Clock.GetUtcNow(), TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
