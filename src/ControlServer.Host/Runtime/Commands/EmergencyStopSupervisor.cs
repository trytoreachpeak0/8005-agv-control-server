using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Commands;

/// <summary>The two identities a vehicle has here, which are not the same string.</summary>
/// <param name="AgvId">8005's own identity, and what the audit trail and the fault fact are keyed on.</param>
/// <param name="DeviceKey">
/// RIoT's identity, and what the emergency endpoint addresses. <c>remote-ops/fleet.md</c> is the
/// register that maps one onto the other; neither may be derived from the machine or from a serial.
/// </param>
public sealed record EmergencyStopSubject(string AgvId, string DeviceKey);

/// <summary>Who asked for the stop. REQ-0249 requires this to be recorded, not inferred.</summary>
public enum EmergencyStopRequestSource
{
    /// <summary>
    /// The server decided on its own. REQ-0249's "停车宽": no human authorisation is required, and
    /// no identity exists to record.
    /// </summary>
    Automatic,

    /// <summary>A person at the vehicle, from the onboard HMI. No login is required for their own vehicle.</summary>
    OnboardField,

    /// <summary>
    /// A signed-in server operator acting on an explicitly selected vehicle. No second factor and
    /// no approval — the only thing required is that the person be identified.
    /// </summary>
    ServerOperator,
}

/// <summary>One request to stop a vehicle, with everything REQ-0249 requires recorded.</summary>
public sealed record EmergencyStopRequest(
    EmergencyStopSubject Subject,
    EmergencyStopRequestSource Source,
    string? RequesterIdentity,
    string Reason,
    long? FaultGeneration);

/// <summary>What one evaluation did, or refused to do.</summary>
public enum EmergencyStopAction
{
    /// <summary>No episode is open for this vehicle.</summary>
    None,

    /// <summary>The request was not accepted; see the reasons.</summary>
    RequestRefused,

    /// <summary>A <c>triggerEmergency</c> went out on this evaluation.</summary>
    Triggered,

    /// <summary>Still unconfirmed, and the next retry is not due yet.</summary>
    AwaitingRetry,

    /// <summary>The latch is confirmed. REQ-0248 stops re-triggering here.</summary>
    LatchConfirmed,

    /// <summary>
    /// The latch had been confirmed and RIoT now reports <c>OK</c> while the cause still stands.
    /// REQ-0248 re-triggers immediately and alarms.
    /// </summary>
    ReTriggeredAfterUnexpectedRelease,

    /// <summary>The latch is <c>CAN_RECOVER</c> but the recovery criteria are not all met.</summary>
    RecoveryRefused,

    /// <summary><c>cancelEmergency</c> went out and RIoT read back <c>OK</c>.</summary>
    Recovered,

    /// <summary><c>cancelEmergency</c> went out and <c>OK</c> was not observed.</summary>
    RecoveryUnconfirmed,
}

/// <summary>What one evaluation decided, and why.</summary>
public sealed record EmergencyStopDecision(
    EmergencyStopAction Action,
    RiotVehicleEmergencyObservation Emergency,
    IReadOnlyList<string> Reasons,
    string? AlarmCode = null,
    RiotOrderCommandAttempt? Attempt = null);

/// <summary>
/// The software emergency stop: triggering it, retrying it until RIoT confirms the latch, and
/// releasing it only when release has been earned.
/// </summary>
/// <remarks>
/// <para>
/// <b>"停车宽、恢复严" is the whole shape of this class</b> (REQ-0249). Stopping is never refused
/// for want of authority: an automatic trigger needs no human at all, a person at the vehicle needs
/// no login, and a server operator needs only to be identified. Releasing is refused by default and
/// has to clear four separate facts, each of which is read rather than assumed.
/// </para>
/// <para>
/// <b>The backoff is a due time computed from the audit trail, not a sleep.</b> REQ-0248 requires
/// the retry to continue until the state is confirmed, which can outlive a process; a delay loop
/// holding the interval in memory would restart the schedule on every restart and would be
/// untestable without waiting in real time. Each evaluation asks "has enough elapsed since attempt
/// N", so the schedule survives a crash and a test can move a clock.
/// </para>
/// <para>
/// <b>Nothing here decides whether the vehicle is stopped.</b> That is REQ-0247's combined-evidence
/// rule and it belongs to ticket 11, which writes its verdict onto the fault fact. Re-deriving it
/// here from the safety facts would put two rules in the server for one question, and the obvious
/// second rule is circular anyway — <see cref="IRiotVehicleSafetyFacts"/> reports
/// <c>RIOT_EMERGENCY_NOT_OK</c> for every vehicle whose emergency stop is latched, so a vehicle
/// held by this class could never satisfy it.
/// </para>
/// <para>
/// The field fallback REQ-0248 requires — what the alarms mean, who may press the physical stop,
/// and why none of it counts as electronic stop proof — is written for the people who act on it in
/// <c>docs/emergency-stop-field-fallback.md</c>.
/// </para>
/// <para>
/// <b>It is driven, not self-running.</b> Ticket 11 owns the fault flow that decides when a vehicle
/// must stop and calls in here; this ticket owns what happens once it has. There is deliberately no
/// hosted service, because a second scheduler for the same episodes is how two components come to
/// disagree about which one is live.
/// </para>
/// </remarks>
public sealed class EmergencyStopSupervisor(
    IRiotOrderCommandGateway gateway,
    IRiotVehicleEmergencyFacts emergencyFacts,
    IRiotOrderCommandAuditStore audit,
    IVehicleFaultStore faults,
    IOptions<RiotCommandOptions> options,
    TimeProvider timeProvider,
    ILogger<EmergencyStopSupervisor> logger)
{
    /// <summary>Alarm raised while a stop has been asked for and no latch is confirmed (REQ-0248).</summary>
    public const string StopUnconfirmedAlarm = "EMERGENCY_STOP_UNCONFIRMED";

    /// <summary>Alarm raised when the latch falls back to OK on its own while the cause stands.</summary>
    public const string UnexpectedReleaseAlarm = "EMERGENCY_LATCH_RELEASED_EXTERNALLY";

    /// <summary>Alarm raised once RIoT reports the latch cannot be recovered from.</summary>
    public const string NotRecoverableAlarm = "EMERGENCY_STOP_NOT_RECOVERABLE";

    /// <summary>
    /// Alarm raised when an earned release did not clear the latch.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StopUnconfirmedAlarm"/> on purpose, because the two send people to
    /// do opposite things. A stop that cannot be confirmed is a vehicle that may still be moving;
    /// a release that cannot be confirmed is a vehicle that is definitely stopped and will not
    /// come back into service. Raising the first for the second would have the plant running at a
    /// vehicle that is standing still.
    /// </remarks>
    public const string ReleaseUnconfirmedAlarm = "EMERGENCY_RELEASE_UNCONFIRMED";

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogRequest =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Information,
            new EventId(9100, "EmergencyStopRequest"),
            "Emergency stop request from {Source} by {RequesterIdentity} for {AgvId}: {Reason} -> {Action}.");

    private static readonly Action<ILogger, string, string, Exception?> LogAlarm =
        LoggerMessage.Define<string, string>(
            LogLevel.Critical,
            new EventId(9101, "EmergencyStopAlarm"),
            "Emergency stop alarm {AlarmCode} for {AgvId}; field confirmation and area isolation required.");

    private readonly RiotCommandOptions commandOptions = options.Value;

    /// <summary>
    /// Asks for a stop and, unless the latch is already engaged or the stop is already under way,
    /// issues the first <c>triggerEmergency</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A stop asked for again is the same stop, not a second one.</b> The fault flow asks on every
    /// evaluation that cannot prove the vehicle stopped, and RIoT's latch engages about a second
    /// after the call (Round 19), so the next evaluation routinely finds a vehicle still moving and a
    /// latch not yet engaged. A request for the same fault generation while its episode is open
    /// therefore joins the episode under the rules <see cref="EvaluateAsync"/> applies — settle a
    /// latch that has engaged, retry only when the backoff is due, re-trigger at once after an
    /// unexpected release — instead of issuing a trigger per evaluation. Until 2026-09-13 it issued
    /// one per evaluation, which is two real calls for one stop.
    /// </para>
    /// <para>
    /// <b>A request never releases.</b> Joining the episode takes only the stopping half of the
    /// evaluation; a release is earned by an evaluation and by nothing that asks for a stop.
    /// </para>
    /// </remarks>
    public async Task<EmergencyStopDecision> RequestStopAsync(
        EmergencyStopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        if (Authorize(request) is string refusal)
        {
            // Reached only by an unidentified server operator. Nothing else can land here, which
            // is REQ-0249's "停车宽" expressed as code rather than as a comment.
            return Record(
                request,
                new EmergencyStopDecision(
                    EmergencyStopAction.RequestRefused,
                    new RiotVehicleEmergencyObservation(
                        request.Subject.DeviceKey, null, timeProvider.GetUtcNow()),
                    [refusal]));
        }

        RiotVehicleEmergencyObservation emergency = await emergencyFacts
            .ReadEmergencyStateAsync(request.Subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        (RiotOrderCommandAttempt Trigger, int AttemptsInEpisode)? episode =
            await SameEpisodeAsync(request, cancellationToken).ConfigureAwait(false);
        if (emergency.IsLatched)
        {
            // Already stopped. REQ-0248 forbids re-triggering a confirmed latch, so this request
            // issues nothing and therefore writes no attempt row -- the audit table counts calls,
            // and a call that never happened must not appear in it as one. The trigger that
            // engaged the latch is settled, so a stop that worked is not left recorded as Pending.
            if (episode is (RiotOrderCommandAttempt engaged, _))
            {
                await SettleAsync(engaged, cancellationToken).ConfigureAwait(false);
            }

            return Record(request, Latched(emergency, request.Subject));
        }

        if (episode is (RiotOrderCommandAttempt trigger, int attempts))
        {
            return Record(
                request,
                await AdvanceUnlatchedAsync(request.Subject, trigger, attempts, emergency, cancellationToken)
                    .ConfigureAwait(false));
        }

        return Record(
            request,
            await TriggerAsync(request, EmergencyStopAction.Triggered, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// The open episode this request is a repeat of, or null when it starts a new one.
    /// </summary>
    /// <remarks>
    /// Same episode means the same fault generation as the open trigger. A request for a newer
    /// generation is a new episode and gets its own trigger carrying its own generation: joined to
    /// the old one, the release rule would compare the old generation against the new fault for
    /// ever and the vehicle could never be released.
    /// </remarks>
    private async Task<(RiotOrderCommandAttempt Trigger, int AttemptsInEpisode)?> SameEpisodeAsync(
        EmergencyStopRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RiotOrderCommandAttempt> triggers = await audit.ReadAttemptsAsync(
            RiotCommandTypeNames.TriggerEmergency,
            VehicleTarget(request.Subject.DeviceKey),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RiotOrderCommandAttempt> releases = await audit.ReadAttemptsAsync(
            RiotCommandTypeNames.CancelEmergency,
            VehicleTarget(request.Subject.DeviceKey),
            cancellationToken).ConfigureAwait(false);

        return OpenEpisode(triggers, releases) is (RiotOrderCommandAttempt trigger, int attempts) &&
            trigger.FaultGeneration == request.FaultGeneration
            ? (trigger, attempts)
            : null;
    }

    /// <summary>
    /// Writes the request itself to the log, whatever came of it.
    /// </summary>
    /// <remarks>
    /// REQ-0249 requires every request to record its source, identity, vehicle, reason and result.
    /// The attempt row carries the last three for requests that issued a call; this carries all
    /// five for every request, including the two that issue nothing -- a refusal, and a stop asked
    /// for on a vehicle whose latch is already engaged.
    /// </remarks>
    private EmergencyStopDecision Record(EmergencyStopRequest request, EmergencyStopDecision decision)
    {
        LogRequest(
            logger,
            request.Source.ToString(),
            request.RequesterIdentity ?? "-",
            request.Subject.AgvId,
            request.Reason,
            decision.Action.ToString(),
            null);
        return decision;
    }

    /// <summary>
    /// Advances an open episode: retries an unconfirmed trigger when due, re-triggers an
    /// unexpectedly released latch, and releases the latch only once release is earned.
    /// </summary>
    public async Task<EmergencyStopDecision> EvaluateAsync(
        EmergencyStopSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        IReadOnlyList<RiotOrderCommandAttempt> triggers = await audit.ReadAttemptsAsync(
            RiotCommandTypeNames.TriggerEmergency,
            VehicleTarget(subject.DeviceKey),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RiotOrderCommandAttempt> releases = await audit.ReadAttemptsAsync(
            RiotCommandTypeNames.CancelEmergency,
            VehicleTarget(subject.DeviceKey),
            cancellationToken).ConfigureAwait(false);

        RiotVehicleEmergencyObservation emergency = await emergencyFacts
            .ReadEmergencyStateAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        if (OpenEpisode(triggers, releases) is not (RiotOrderCommandAttempt trigger, int attempts))
        {
            return new EmergencyStopDecision(EmergencyStopAction.None, emergency, []);
        }

        if (emergency.IsLatched)
        {
            await SettleAsync(trigger, cancellationToken).ConfigureAwait(false);
            return await ConsiderReleaseAsync(
                subject, trigger, ReleasesSince(releases, trigger), emergency, cancellationToken)
                .ConfigureAwait(false);
        }

        return await AdvanceUnlatchedAsync(subject, trigger, attempts, emergency, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The stopping half of an evaluation, for an open episode whose latch is not engaged.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="EvaluateAsync"/> and by a repeated <see cref="RequestStopAsync"/>, so the
    /// two cannot come to disagree about when a trigger may go out again.
    /// </remarks>
    private async Task<EmergencyStopDecision> AdvanceUnlatchedAsync(
        EmergencyStopSubject subject,
        RiotOrderCommandAttempt trigger,
        int attempts,
        RiotVehicleEmergencyObservation emergency,
        CancellationToken cancellationToken)
    {
        // RIoT says OK. Either the latch never engaged — keep retrying — or it engaged and has
        // since been released by something that is not this server, which REQ-0248 answers by
        // re-triggering at once rather than by waiting for the next backoff slot. An unreadable
        // latch is neither, and waits for the backoff like an unengaged one.
        if (emergency.IsKnown && trigger.Outcome == RiotOrderCommandOutcome.Confirmed)
        {
            EmergencyStopRequest reTrigger = new(
                subject,
                EmergencyStopRequestSource.Automatic,
                RequesterIdentity: null,
                Reason: UnexpectedReleaseAlarm,
                trigger.FaultGeneration);
            return await TriggerAsync(
                reTrigger,
                EmergencyStopAction.ReTriggeredAfterUnexpectedRelease,
                cancellationToken,
                UnexpectedReleaseAlarm).ConfigureAwait(false);
        }

        return await RetryIfDueAsync(subject, trigger, attempts, emergency, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the request may proceed, and why not when it may not.
    /// </summary>
    /// <remarks>
    /// REQ-0249 sets no bar at all for the two paths that matter in an emergency, and one bar for
    /// the third: a server operator must be someone. It is not an approval and not a second
    /// factor — an unidentified request simply cannot satisfy "records 身份", so there would be
    /// nothing to record.
    /// </remarks>
    internal static string? Authorize(EmergencyStopRequest request) => request.Source switch
    {
        EmergencyStopRequestSource.Automatic => null,
        EmergencyStopRequestSource.OnboardField => null,
        EmergencyStopRequestSource.ServerOperator when
            string.IsNullOrWhiteSpace(request.RequesterIdentity) => "EMERGENCY_REQUESTER_UNIDENTIFIED",
        EmergencyStopRequestSource.ServerOperator => null,
        _ => "EMERGENCY_REQUEST_SOURCE_UNKNOWN"
    };

    /// <summary>
    /// How long after attempt <paramref name="attemptNumber"/> the next one may go out.
    /// </summary>
    /// <remarks>
    /// Geometric from the first delay, capped, and never exhausted. The cap is where it stays: a
    /// vehicle whose stop has not been confirmed after an hour is still a vehicle whose stop has
    /// not been confirmed.
    /// </remarks>
    internal static TimeSpan RetryDelay(int attemptNumber, RiotCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);

        double scaled = options.EmergencyRetryInitialDelay.TotalMilliseconds *
            Math.Pow(options.EmergencyRetryBackoffFactor, attemptNumber - 1);
        return TimeSpan.FromMilliseconds(
            Math.Min(scaled, options.EmergencyRetryMaxDelay.TotalMilliseconds));
    }

    /// <summary>
    /// What the latch says about an emergency command that was aimed at it.
    /// </summary>
    /// <remarks>
    /// Same shape as the order commands' reconciliation and for the same reason: the observation
    /// outranks the call. A trigger whose call timed out but whose latch is now engaged did stop
    /// the vehicle.
    /// </remarks>
    internal static RiotOrderCommandOutcome ReconcileEmergency(
        RiotEmergencyCommandKind kind,
        RiotCommandCallDisposition disposition,
        RiotVehicleEmergencyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsKnown)
        {
            return RiotOrderCommandOutcome.Unknown;
        }

        bool achieved = kind == RiotEmergencyCommandKind.Trigger
            ? observation.IsLatched
            : string.Equals(observation.EmergencyState, RiotVehicleEmergencyObservation.Ok, StringComparison.Ordinal);
        if (achieved)
        {
            return RiotOrderCommandOutcome.Confirmed;
        }

        return disposition switch
        {
            RiotCommandCallDisposition.Accepted => RiotOrderCommandOutcome.Pending,
            RiotCommandCallDisposition.Failed => RiotOrderCommandOutcome.Failed,
            _ => RiotOrderCommandOutcome.Unknown
        };
    }

    /// <summary>
    /// The four facts REQ-0167 requires before a latch this server set may be released, each named
    /// so that a refusal says which one is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default-deny throughout. A vehicle with no fault fact at all is one whose stop this server
    /// cannot show it caused or cleared — REQ-0167 keeps manual, external and unattributable stops
    /// latched and hands them to a person, and "we have no record" is the unattributable case.
    /// </para>
    /// <para>
    /// The generation check is what stops a release earned by one episode from being spent on the
    /// next: a vehicle that faulted again after the original cause cleared has a newer generation,
    /// and the stop proof attached to the older one says nothing about the current one.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> ReleaseObstacles(
        RiotVehicleEmergencyObservation emergency,
        long? triggerFaultGeneration,
        VehicleFaultFact? fault)
    {
        ArgumentNullException.ThrowIfNull(emergency);

        List<string> obstacles = [];
        if (!string.Equals(
                emergency.EmergencyState,
                RiotVehicleEmergencyObservation.CanRecover,
                StringComparison.Ordinal))
        {
            // CAN_NOT_RECOVER forbids the call outright (allowlist 1.5); anything else is not a
            // recoverable latch either.
            obstacles.Add("EMERGENCY_NOT_CAN_RECOVER");
        }

        if (fault is null)
        {
            obstacles.Add("EMERGENCY_FAULT_FACT_ABSENT");
            return obstacles;
        }

        if (triggerFaultGeneration is long generation && fault.FaultGeneration != generation)
        {
            obstacles.Add("EMERGENCY_FAULT_GENERATION_MOVED");
        }

        if (fault.Level != VehicleFaultLevel.None || fault.ClearedAt is null)
        {
            obstacles.Add("EMERGENCY_CAUSE_NOT_CLEARED");
        }

        if (!fault.StopProven)
        {
            obstacles.Add("EMERGENCY_STOP_NOT_PROVEN");
        }

        return obstacles;
    }

    /// <summary>
    /// The audit target for a vehicle-scoped command.
    /// </summary>
    /// <remarks>
    /// The column is named <c>TargetUpperId</c> and an emergency command has no order, so the
    /// prefix says plainly that what follows is a <c>deviceKey</c> rather than an order identity.
    /// Collision is impossible either way — the unique index includes the command type — so the
    /// prefix is there for whoever reads the table, not for the database.
    /// </remarks>
    internal static string VehicleTarget(string deviceKey) => $"vehicle:{deviceKey}";

    private async Task<EmergencyStopDecision> RetryIfDueAsync(
        EmergencyStopSubject subject,
        RiotOrderCommandAttempt trigger,
        int attemptsSoFar,
        RiotVehicleEmergencyObservation emergency,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = RetryDelay(attemptsSoFar, commandOptions);
        if (timeProvider.GetUtcNow() < trigger.IssuedAt + delay)
        {
            Alarm(StopUnconfirmedAlarm, subject.AgvId);
            return new EmergencyStopDecision(
                EmergencyStopAction.AwaitingRetry,
                emergency,
                ["EMERGENCY_STATE_UNCONFIRMED"],
                StopUnconfirmedAlarm,
                trigger);
        }

        EmergencyStopRequest retry = new(
            subject,
            EmergencyStopRequestSource.Automatic,
            RequesterIdentity: null,
            Reason: StopUnconfirmedAlarm,
            trigger.FaultGeneration);
        return await TriggerAsync(retry, EmergencyStopAction.Triggered, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<EmergencyStopDecision> TriggerAsync(
        EmergencyStopRequest request,
        EmergencyStopAction action,
        CancellationToken cancellationToken,
        string? alarmRegardlessOfOutcome = null)
    {
        RiotOrderCommandAttempt attempt = await ArmAsync(
            RiotEmergencyCommandKind.Trigger, request, cancellationToken).ConfigureAwait(false);
        RiotCommandCallResult call = await gateway.IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind.Trigger, request.Subject.DeviceKey, cancellationToken)
            .ConfigureAwait(false);
        RiotVehicleEmergencyObservation after = await emergencyFacts
            .ReadEmergencyStateAsync(request.Subject.DeviceKey, cancellationToken).ConfigureAwait(false);

        RiotOrderCommandOutcome outcome = ReconcileEmergency(
            RiotEmergencyCommandKind.Trigger, call.Disposition, after);
        await audit.RecordOutcomeAsync(
            attempt.CommandAuditId,
            outcome,
            ReceiptJson(request, call, after),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        // An unconfirmed trigger always alarms. So does an unexpected release, whether or not
        // the re-trigger that follows it works -- REQ-0248 alarms on the release itself, and a
        // latch that came off on its own is news even when the server put it straight back.
        string? alarm = alarmRegardlessOfOutcome
            ?? (outcome == RiotOrderCommandOutcome.Confirmed ? null : StopUnconfirmedAlarm);
        Alarm(alarm, request.Subject.AgvId);
        return new EmergencyStopDecision(
            action,
            after,
            [$"EMERGENCY_TRIGGER_{outcome.ToString().ToUpperInvariant()}"],
            alarm,
            attempt with { Outcome = outcome });
    }

    private async Task<EmergencyStopDecision> ConsiderReleaseAsync(
        EmergencyStopSubject subject,
        RiotOrderCommandAttempt trigger,
        IReadOnlyList<RiotOrderCommandAttempt> releasesSoFar,
        RiotVehicleEmergencyObservation emergency,
        CancellationToken cancellationToken)
    {
        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> obstacles = ReleaseObstacles(
            emergency, trigger.FaultGeneration, fault);
        if (obstacles.Count > 0)
        {
            string? alarm = string.Equals(
                emergency.EmergencyState,
                RiotVehicleEmergencyObservation.CanNotRecover,
                StringComparison.Ordinal)
                ? NotRecoverableAlarm
                : null;
            Alarm(alarm, subject.AgvId);
            return new EmergencyStopDecision(
                EmergencyStopAction.RecoveryRefused, emergency, obstacles, alarm, trigger);
        }

        // An earned release that did not clear the latch is retried under the same controlled
        // backoff as the trigger, rather than on every tick. Hammering cancelEmergency would be
        // the opposite of "恢复严", and refusing to retry at all would strand a vehicle on one
        // transient failure.
        if (releasesSoFar.Count > 0 &&
            timeProvider.GetUtcNow() < releasesSoFar[^1].IssuedAt + RetryDelay(releasesSoFar.Count, commandOptions))
        {
            Alarm(ReleaseUnconfirmedAlarm, subject.AgvId);
            return new EmergencyStopDecision(
                EmergencyStopAction.AwaitingRetry,
                emergency,
                ["EMERGENCY_RELEASE_NOT_CONFIRMED"],
                ReleaseUnconfirmedAlarm,
                releasesSoFar[^1]);
        }

        EmergencyStopRequest release = new(
            subject,
            EmergencyStopRequestSource.Automatic,
            RequesterIdentity: null,
            Reason: fault?.ClearedReason ?? "EMERGENCY_CAUSE_CLEARED",
            trigger.FaultGeneration);
        RiotOrderCommandAttempt attempt = await ArmAsync(
            RiotEmergencyCommandKind.Cancel, release, cancellationToken).ConfigureAwait(false);
        RiotCommandCallResult call = await gateway.IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind.Cancel, subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        RiotVehicleEmergencyObservation after = await emergencyFacts
            .ReadEmergencyStateAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);

        RiotOrderCommandOutcome outcome = ReconcileEmergency(
            RiotEmergencyCommandKind.Cancel, call.Disposition, after);
        await audit.RecordOutcomeAsync(
            attempt.CommandAuditId,
            outcome,
            ReceiptJson(release, call, after),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        return outcome == RiotOrderCommandOutcome.Confirmed
            ? new EmergencyStopDecision(
                EmergencyStopAction.Recovered, after, [], null, attempt with { Outcome = outcome })
            : Alarmed(new EmergencyStopDecision(
                EmergencyStopAction.RecoveryUnconfirmed,
                after,
                ["EMERGENCY_RELEASE_NOT_CONFIRMED"],
                ReleaseUnconfirmedAlarm,
                attempt with { Outcome = outcome }), subject.AgvId);
    }

    /// <summary>
    /// Marks the trigger confirmed once the latch has been observed, whichever attempt engaged it.
    /// </summary>
    private Task SettleAsync(RiotOrderCommandAttempt trigger, CancellationToken cancellationToken) =>
        trigger.Outcome == RiotOrderCommandOutcome.Confirmed
            ? Task.CompletedTask
            : audit.RecordOutcomeAsync(
                trigger.CommandAuditId,
                RiotOrderCommandOutcome.Confirmed,
                trigger.ReceiptJson,
                timeProvider.GetUtcNow(),
                cancellationToken);

    private EmergencyStopDecision Latched(
        RiotVehicleEmergencyObservation emergency,
        EmergencyStopSubject subject)
    {
        string? alarm = string.Equals(
            emergency.EmergencyState,
            RiotVehicleEmergencyObservation.CanNotRecover,
            StringComparison.Ordinal)
            ? NotRecoverableAlarm
            : null;
        Alarm(alarm, subject.AgvId);
        return new EmergencyStopDecision(
            EmergencyStopAction.LatchConfirmed, emergency, ["EMERGENCY_ALREADY_LATCHED"], alarm);
    }

    private Task<RiotOrderCommandAttempt> ArmAsync(
        RiotEmergencyCommandKind kind,
        EmergencyStopRequest request,
        CancellationToken cancellationToken) =>
        audit.ArmAttemptAsync(
            RiotCommandTypeNames.For(kind),
            request.Subject.AgvId,
            VehicleTarget(request.Subject.DeviceKey),
            targetOrderId: null,
            SemanticHash(kind, request),
            request.FaultGeneration,
            timeProvider.GetUtcNow(),
            cancellationToken);

    /// <summary>
    /// The latest trigger whose episode has not been closed by a confirmed release.
    /// </summary>
    /// <remarks>
    /// The audit trail is the episode record. A release that was issued but never confirmed does
    /// not close anything — that is the case where the vehicle may still be latched, and treating
    /// it as closed is how a stop gets forgotten.
    /// </remarks>
    /// <summary>The release attempts made since the episode's trigger, oldest first.</summary>
    private static IReadOnlyList<RiotOrderCommandAttempt> ReleasesSince(
        IReadOnlyList<RiotOrderCommandAttempt> releases,
        RiotOrderCommandAttempt trigger) =>
        [.. releases.Where(release => release.IssuedAt >= trigger.IssuedAt)];

    internal static (RiotOrderCommandAttempt Trigger, int AttemptsInEpisode)? OpenEpisode(
        IReadOnlyList<RiotOrderCommandAttempt> triggers,
        IReadOnlyList<RiotOrderCommandAttempt> releases)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(releases);

        DateTimeOffset? closedAt = releases
            .Where(release => release.Outcome == RiotOrderCommandOutcome.Confirmed)
            .Select(release => (DateTimeOffset?)release.IssuedAt)
            .Max();
        List<RiotOrderCommandAttempt> episode = triggers
            .Where(trigger => closedAt is null || trigger.IssuedAt > closedAt)
            .ToList();
        return episode.Count == 0 ? null : (episode[^1], episode.Count);
    }

    private EmergencyStopDecision Alarmed(EmergencyStopDecision decision, string agvId)
    {
        Alarm(decision.AlarmCode, agvId);
        return decision;
    }

    private void Alarm(string? alarmCode, string agvId)
    {
        if (alarmCode is not null)
        {
            LogAlarm(logger, alarmCode, agvId, null);
        }
    }

    private static string SemanticHash(RiotEmergencyCommandKind kind, EmergencyStopRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\u001f',
            RiotCommandTypeNames.For(kind),
            request.Subject.AgvId,
            request.Subject.DeviceKey,
            request.Source.ToString(),
            request.RequesterIdentity ?? string.Empty,
            request.Reason,
            request.FaultGeneration?.ToString(CultureInfo.InvariantCulture) ?? string.Empty))))
            .ToLowerInvariant();

    /// <summary>
    /// The request as REQ-0249 requires it recorded: source, identity if any, vehicle, reason and
    /// result, alongside the call's receipt.
    /// </summary>
    /// <remarks>
    /// It goes into the audit row's one free-form column because the table has no first-class
    /// columns for the first three, and adding them is a migration this batch does not take. The
    /// residual gap is small and real: between arming and recording, the request context exists
    /// only inside the semantic hash, which makes a changed request visible without making it
    /// readable.
    /// </remarks>
    private static string ReceiptJson(
        EmergencyStopRequest request,
        RiotCommandCallResult call,
        RiotVehicleEmergencyObservation emergency) =>
        JsonSerializer.Serialize(new
        {
            source = request.Source.ToString(),
            requesterIdentity = request.RequesterIdentity,
            agvId = request.Subject.AgvId,
            deviceKey = request.Subject.DeviceKey,
            request.Reason,
            call = new
            {
                disposition = call.Disposition.ToString(),
                call.Receipt.Operation,
                call.Receipt.Classification,
                call.Receipt.HttpStatusCode,
                call.Receipt.BusinessCode,
                call.Receipt.FailureCategory,
                observedAt = call.Receipt.ObservedAt,
            },
            emergencyState = emergency.EmergencyState,
        });
}
