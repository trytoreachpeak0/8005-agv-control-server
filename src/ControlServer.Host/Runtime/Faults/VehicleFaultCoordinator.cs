using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>
/// The evidence codes the two-level model recognises, and the closed list REQ-0233 allows to
/// confirm an isolation on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whitelist has one entry.</b> REQ-0233 names it exactly: a fresh
/// <c>emergencyState=CAN_NOT_RECOVER</c>. Everything else it lists as a candidate — <c>sysState</c>,
/// <c>lastErrorCode</c>, <c>hardwareErrorCode</c>, <c>faultCodesList</c> — has to be bound to an
/// environment, a build, a vehicle model and firmware, a meaning, a severity and a clearing
/// condition, and approved again, before it may join. None of that has happened, so none of them
/// are here.
/// </para>
/// <para>
/// <b>The symptoms are named so that they cannot be mistaken for evidence.</b> REQ-0232 lists them
/// — offline, comms loss, navigation failure, a single order FAILED, elapsed time — and says none
/// of them proves a vehicle is faulty. They exist here to be recorded, not to decide anything: the
/// level they produce is <see cref="VehicleFaultLevel.SuspectedBlocked"/> whichever one it is.
/// </para>
/// <para>
/// The codes are open-ended on purpose — a caller may pass a symptom this list does not name, and
/// it will be recorded and will block dispatch like the rest. What is closed is
/// <see cref="IsAutoConfirmable"/>, and a code nobody anticipated fails it.
/// </para>
/// </remarks>
public static class VehicleFaultEvidence
{
    /// <summary>The one fact REQ-0233 lets confirm an isolation automatically.</summary>
    public const string EmergencyCanNotRecover = "RIOT_EMERGENCY_CAN_NOT_RECOVER";

    /// <summary>The vehicle cannot be reached at all.</summary>
    public const string VehicleOffline = "VEHICLE_OFFLINE";

    /// <summary>Communication with the vehicle or with RIoT about it has been lost.</summary>
    public const string CommunicationLost = "VEHICLE_COMMUNICATION_LOST";

    /// <summary>The vehicle failed to navigate.</summary>
    public const string NavigationFailed = "VEHICLE_NAVIGATION_FAILED";

    /// <summary>One order reached FAILED. One order, and REQ-0232 says that is a symptom.</summary>
    public const string OrderFailed = "VEHICLE_ORDER_FAILED";

    /// <summary>
    /// A person with the exception-recovery permission confirmed the isolation from site facts.
    /// </summary>
    /// <remarks>
    /// REQ-0233's second sentence, and deliberately not on <see cref="IsAutoConfirmable"/>: it is
    /// not an automatic confirmation. It reaches the second level through
    /// <see cref="VehicleFaultCoordinator.ConfirmIsolationAsync"/>, which requires the identity and
    /// the session that make it a human decision rather than an inference.
    /// </remarks>
    public const string OperatorConfirmed = "OPERATOR_CONFIRMED_ISOLATION";

    /// <summary>
    /// Whether this fact may take a vehicle to <see cref="VehicleFaultLevel.ConfirmedIsolated"/>
    /// without a person.
    /// </summary>
    public static bool IsAutoConfirmable(string evidenceCode) =>
        string.Equals(evidenceCode, EmergencyCanNotRecover, StringComparison.Ordinal);
}

/// <summary>
/// What a faulted vehicle was carrying and doing, as the caller knows it.
/// </summary>
/// <remarks>
/// Supplied by the caller rather than looked up here, for the same reason the emergency supervisor
/// is driven rather than self-running: the flow that noticed the symptom is the flow that knows
/// which demand this vehicle was executing, and a second lookup path would be a second answer.
/// </remarks>
/// <param name="CurrentOrder">
/// The order this project has in flight on the vehicle, or null when it has none. All three
/// identities are required together — <c>upperId</c> is what the audit is keyed on and
/// <c>orderId</c> is what the RIoT endpoint addresses, and they are not interchangeable.
/// </param>
/// <param name="Cargo">What the vehicle is carrying, or null when it is carrying nothing.</param>
public sealed record FaultedVehicleContext(
    RiotOrderCommandTarget? CurrentOrder,
    FaultedVehicleCargoFacts? Cargo);

/// <summary>The cargo facts REQ-0238 decides the two paths from.</summary>
/// <param name="LoadingWitnessed">Whether loading was actually observed.</param>
/// <param name="CargoStateKnown">
/// Whether the cargo state is known at all. Unknown counts the same as loaded — REQ-0238 puts both
/// into <c>FaultedVehicleCargo</c>, because a vehicle that might be carrying a product must not
/// have its binding released on the assumption that it is not.
/// </param>
public sealed record FaultedVehicleCargoFacts(
    string DemandId,
    string? MovementLegId,
    string TransportDemandKey,
    bool LoadingWitnessed,
    bool CargoStateKnown);

/// <summary>What one fault evaluation established and did.</summary>
public sealed record VehicleFaultDecision(
    VehicleFaultLevel Level,
    long FaultGeneration,
    string EvidenceCode,
    RiotOrderCommandOutcome? HoldOutcome,
    FaultedCargoBinding? Cargo,
    StopProofVerdict StopProof,
    bool Escalated,
    IReadOnlyList<string> Reasons,
    string? AlarmCode = null)
{
    /// <summary>
    /// True whenever a fault fact is held. New dispatch is blocked at either level — REQ-0234 does
    /// not distinguish them for this — and the block is the fact's existence rather than an action
    /// taken here.
    /// </summary>
    public bool NewDispatchBlocked => Level != VehicleFaultLevel.None;
}

/// <summary>Why a repair resumption was refused, or that it was not.</summary>
public sealed record VehicleFaultResumeDecision(
    bool Resumed,
    RiotOrderCommandOutcome? Outcome,
    IReadOnlyList<string> Refusals);

/// <summary>The original task a repaired vehicle would continue.</summary>
public sealed record VehicleFaultResumption(
    RiotOrderCommandTarget Order,
    string DemandId,
    string TransportDemandKey,
    string Reason);

/// <summary>
/// REQ-0232's two-level fault model, the protection REQ-0234 requires on entering it, and the
/// stop proof REQ-0247 defines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two levels, and only one fact may cross between them on its own.</b> A symptom — offline,
/// comms lost, navigation failed, one order FAILED — enters
/// <see cref="VehicleFaultLevel.SuspectedBlocked"/> and stays there however long it lasts, because
/// REQ-0232 says time raises urgency and never escalates the fault fact. The second level is
/// reached automatically by exactly one observation, a fresh <c>CAN_NOT_RECOVER</c> latch, and
/// otherwise only by a person through <see cref="ConfirmIsolationAsync"/>.
/// </para>
/// <para>
/// <b>The whitelist is checked against what this class read, not against what it was told.</b> The
/// caller passes a symptom code; whether the vehicle reaches the second level is decided by an
/// emergency-state read taken during this evaluation. That is what makes REQ-0233's "fresh" true
/// by construction — a caller cannot claim a stale <c>CAN_NOT_RECOVER</c>, because its own claim is
/// never the input to that branch.
/// </para>
/// <para>
/// <b>Nothing here downgrades.</b> A confirmed isolation stays confirmed until the fault is cleared
/// outright; a later evaluation that only sees a symptom does not walk it back. Isolation is a
/// statement that hard evidence was seen once, and the evidence not being visible on a second look
/// is not evidence that it was wrong.
/// </para>
/// <para>
/// <b>Driven, like the command surface it calls.</b> There is no hosted service: the flow that
/// detects symptoms drives this, and each call does one round of what is due. Calling it more often
/// samples motion more often, which is the only thing that changes.
/// </para>
/// </remarks>
public sealed class VehicleFaultCoordinator(
    IVehicleFaultStore faults,
    IRiotVehicleEmergencyFacts emergencyFacts,
    IVehicleMotionFacts motionFacts,
    IRiotMovementGateway movement,
    IRiotOrderCommandAuditStore audit,
    RiotOrderCommandService commands,
    EmergencyStopSupervisor emergencyStop,
    VehicleMotionLedger ledger,
    IOptions<VehicleFaultOptions> options,
    TimeProvider timeProvider,
    ILogger<VehicleFaultCoordinator> logger)
{
    /// <summary>
    /// Raised while a fault is held and the current order is not demonstrably protected.
    /// </summary>
    /// <remarks>
    /// REQ-0234's "形成高优先级事件". It covers all three of the cases that entry names — OrderHold
    /// unavailable, its result unknown, and the vehicle not stopping — because they call for the
    /// same thing from a person: go and look, the block stays on until you do.
    /// </remarks>
    public const string OrderNotProtectedAlarm = "VEHICLE_FAULT_ORDER_NOT_PROTECTED";

    /// <summary>Raised when a vehicle reaches the second level.</summary>
    public const string IsolatedAlarm = "VEHICLE_FAULT_ISOLATED";

    private static readonly Action<ILogger, string, string, string, long, Exception?> LogFaultLevel =
        LoggerMessage.Define<string, string, string, long>(
            LogLevel.Warning,
            new EventId(9200, "VehicleFaultLevel"),
            "Vehicle {AgvId} is at fault level {Level} on evidence {EvidenceCode} (generation {FaultGeneration}).");

    private static readonly Action<ILogger, string, string, string, Exception?> LogAlarm =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Critical,
            new EventId(9201, "VehicleFaultAlarm"),
            "Vehicle fault alarm {AlarmCode} for {AgvId}: {Reasons}.");

    /// <summary>
    /// The human confirmation itself, with the two things that make it one.
    /// </summary>
    /// <remarks>
    /// It goes to the log because there is no first-class column for either: <c>VehicleFaultStates</c>
    /// records the evidence code and this batch adds no migration. The same residual gap ticket 10
    /// recorded for the command audit, and the same disposition — visible, not queryable.
    /// </remarks>
    private static readonly Action<ILogger, string, string, string, Exception?> LogOperatorConfirmation =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(9202, "VehicleFaultOperatorConfirmation"),
            "Isolation of {AgvId} confirmed by {RequesterIdentity} in exception-recovery session {SessionId}.");

    private readonly VehicleFaultOptions faultOptions = options.Value;

    /// <summary>
    /// Records a symptom, protects the order and the cargo, and either proves the vehicle stopped
    /// or escalates.
    /// </summary>
    public async Task<VehicleFaultDecision> ObserveAsync(
        EmergencyStopSubject subject,
        string symptomCode,
        FaultedVehicleContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(symptomCode);

        RiotVehicleEmergencyObservation emergency = await emergencyFacts
            .ReadEmergencyStateAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        bool hardEvidence = string.Equals(
            emergency.EmergencyState,
            RiotVehicleEmergencyObservation.CanNotRecover,
            StringComparison.Ordinal);
        string evidenceCode = hardEvidence ? VehicleFaultEvidence.EmergencyCanNotRecover : symptomCode;

        return await ApplyAsync(
            subject,
            hardEvidence ? VehicleFaultLevel.ConfirmedIsolated : VehicleFaultLevel.SuspectedBlocked,
            evidenceCode,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// REQ-0233's second route to isolation: a person with the exception-recovery permission,
    /// deciding from site facts inside an exception-recovery session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arguments are required, and a missing one refuses rather than defaults. This is the
    /// only path by which a fact outside the whitelist reaches the second level, so what makes it
    /// legitimate is precisely that a named person took it inside a recorded session; without
    /// either, what would be recorded is an automatic confirmation wearing a person's name.
    /// </para>
    /// <para>
    /// <b>There is no transport for this yet.</b> The permission model REQ-0253 describes does not
    /// exist in this batch, so nothing calls this from outside a test — the same shape as the two
    /// human-initiated emergency-stop sources ticket 10 left with a policy and no entry point.
    /// </para>
    /// </remarks>
    public async Task<VehicleFaultDecision> ConfirmIsolationAsync(
        EmergencyStopSubject subject,
        string requesterIdentity,
        string exceptionRecoverySessionId,
        FaultedVehicleContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(exceptionRecoverySessionId);

        LogOperatorConfirmation(logger, subject.AgvId, requesterIdentity, exceptionRecoverySessionId, null);
        return await ApplyAsync(
            subject,
            VehicleFaultLevel.ConfirmedIsolated,
            VehicleFaultEvidence.OperatorConfirmed,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// REQ-0239's repair resumption: the same business task, on the same loaded vehicle, through
    /// the existing <c>OrderContinue</c> path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things must agree and all three are read rather than assumed: the identity (this
    /// vehicle, this <c>upperId</c>, this <c>orderId</c>), the target (RIoT still reports the order
    /// HELD), and the cargo binding (the demand and the transport key still match the live one).
    /// Any disagreement refuses, and refusing means the cargo stays bound and the fault stays held
    /// — REQ-0239 forbids working around a mismatch by renumbering or reassigning.
    /// </para>
    /// <para>
    /// <b>The fault is cleared only on a confirmed continue.</b> That ordering is what makes the
    /// vehicle recoverable at all: the emergency supervisor releases a latch only when the fault
    /// fact has actually been cleared — level None with a clearing timestamp — so clearing before
    /// the order resumed would offer release on a task that had not.
    /// </para>
    /// </remarks>
    public async Task<VehicleFaultResumeDecision> ResumeAsync(
        EmergencyStopSubject subject,
        VehicleFaultResumption resumption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(resumption);

        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken)
            .ConfigureAwait(false);
        FaultedCargoBinding? cargo = await faults.ReadLiveCargoAsync(subject.AgvId, cancellationToken)
            .ConfigureAwait(false);
        RiotOrderObservation observation = await movement
            .ReconcileByUpperIdAsync(resumption.Order.UpperId, cancellationToken).ConfigureAwait(false);

        List<string> refusals = ResumeRefusals(subject, resumption, fault, cargo, observation);
        if (refusals.Count > 0)
        {
            Alarm(OrderNotProtectedAlarm, subject.AgvId, refusals);
            return new VehicleFaultResumeDecision(false, null, refusals);
        }

        RiotOrderCommandRecord record = await commands.IssueAsync(
            RiotOrderCommandKind.ContinueFromHeld,
            resumption.Order,
            resumption.Reason,
            fault!.FaultGeneration,
            cancellationToken).ConfigureAwait(false);
        if (!record.Succeeded)
        {
            // Not cleared, not released, still blocked. An unconfirmed continue is exactly the
            // state where the order may or may not be running, and REQ-0239 keeps the cargo
            // protected through it.
            Alarm(OrderNotProtectedAlarm, subject.AgvId, ["RESUME_CONTINUE_NOT_CONFIRMED"]);
            return new VehicleFaultResumeDecision(
                false, record.Outcome, ["RESUME_CONTINUE_NOT_CONFIRMED"]);
        }

        if (cargo is not null)
        {
            await faults.ReleaseCargoAsync(
                cargo.CargoBindingId,
                "RESUMED_ON_ORIGINAL_VEHICLE",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        await faults.ClearAsync(
            subject.AgvId,
            fault.FaultGeneration,
            resumption.Reason,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        ledger.Forget(subject.DeviceKey);
        return new VehicleFaultResumeDecision(true, record.Outcome, []);
    }

    /// <summary>
    /// Everything REQ-0239 requires to agree before the original task may continue.
    /// </summary>
    /// <remarks>
    /// Pure, and returns every mismatch rather than the first: whoever has to decide what to do
    /// with a vehicle that cannot resume needs to know whether one fact disagrees or three do.
    /// </remarks>
    internal static List<string> ResumeRefusals(
        EmergencyStopSubject subject,
        VehicleFaultResumption resumption,
        VehicleFaultFact? fault,
        FaultedCargoBinding? cargo,
        RiotOrderObservation observation)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(resumption);
        ArgumentNullException.ThrowIfNull(observation);

        List<string> refusals = [];
        if (fault is null || fault.Level == VehicleFaultLevel.None)
        {
            // Nothing to resume from. Continuing an order on a vehicle with no live fault is not
            // this class's decision; the ordinary dispatch path owns a vehicle that is not blocked.
            refusals.Add("RESUME_NO_LIVE_FAULT");
        }

        if (!string.Equals(resumption.Order.AgvId, subject.AgvId, StringComparison.Ordinal))
        {
            refusals.Add("RESUME_VEHICLE_MISMATCH");
        }

        if (observation.OrderState != RiotOrderState.Paused)
        {
            // Covers "not HELD" and "we could not read it" alike, and deliberately: REQ-0239 asks
            // for the order to be *confirmed* HELD, so an unreadable order fails it the same way a
            // running one does.
            refusals.Add("RESUME_ORDER_NOT_HELD");
        }

        if (observation.OrderId is not null &&
            !string.Equals(observation.OrderId, resumption.Order.OrderId, StringComparison.Ordinal))
        {
            refusals.Add("RESUME_ORDER_IDENTITY_MISMATCH");
        }

        if (cargo is not null &&
            (!string.Equals(cargo.DemandId, resumption.DemandId, StringComparison.Ordinal) ||
             !string.Equals(cargo.TransportDemandKey, resumption.TransportDemandKey, StringComparison.Ordinal) ||
             !string.Equals(cargo.AgvId, subject.AgvId, StringComparison.Ordinal)))
        {
            refusals.Add("RESUME_CARGO_BINDING_MISMATCH");
        }

        return refusals;
    }

    /// <summary>
    /// Whether REQ-0246 requires an immediate escalation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry is three conditions in series, and all three are here. The block has already asked
    /// the vehicle to stop — that is what having a fault fact means. The second is that OrderHold
    /// and the stop are not both proven. The third is that the vehicle is still moving, or that
    /// monitoring failed in a way that cannot rule out continued movement.
    /// </para>
    /// <para>
    /// <b>The third condition is what keeps this from firing on every first evaluation.</b> A
    /// vehicle that reads as positively not moving, at a known station, with a fresh sample and no
    /// position change, is not escalated merely because the window has not filled yet — nothing
    /// about it says it is moving, and the requirement asks for an escalation when something does.
    /// A window that is short, or spaced wrongly, withholds the proof without asserting motion.
    /// </para>
    /// <para>
    /// <b>Everything else escalates, including silence.</b> An unreadable movement state, a vehicle
    /// between stations, a sample from a clock that disagrees — each is "monitoring failed and
    /// continued movement cannot be excluded", and each escalates at once. That is deliberately
    /// expensive: an offline vehicle will escalate, its emergency stop will not confirm, and a
    /// person will be sent. A vehicle nobody can see is a vehicle nobody can say is stopped.
    /// </para>
    /// </remarks>
    internal static bool RequiresEscalation(
        bool holdConfirmed,
        StopProofVerdict proof,
        VehicleMotionSample latest)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(latest);

        if (holdConfirmed && proof.Proven)
        {
            return false;
        }

        return latest.Reading != VehicleMotionReading.NotMoving ||
            !latest.HasKnownPosition ||
            proof.MissingFacts.Contains(StopProof.PositionChanged) ||
            proof.MissingFacts.Contains(StopProof.EvidenceStale);
    }

    private async Task<VehicleFaultDecision> ApplyAsync(
        EmergencyStopSubject subject,
        VehicleFaultLevel level,
        string evidenceCode,
        FaultedVehicleContext context,
        CancellationToken cancellationToken)
    {
        VehicleFaultFact? existing = await faults.ReadAsync(subject.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.Level == VehicleFaultLevel.ConfirmedIsolated)
        {
            // No downgrade. Hard evidence seen once is not undone by a later look that missed it.
            level = VehicleFaultLevel.ConfirmedIsolated;
            evidenceCode = existing.EvidenceCode ?? evidenceCode;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        VehicleFaultFact fault = await faults.RecordLevelAsync(
            subject.AgvId,
            level,
            evidenceCode,
            VehicleFaultEvidence.IsAutoConfirmable(evidenceCode),
            now,
            cancellationToken).ConfigureAwait(false);
        LogFaultLevel(logger, subject.AgvId, level.ToString(), evidenceCode, fault.FaultGeneration, null);

        // A new episode starts from an empty window. Samples taken during the previous fault say
        // nothing about this one, and a resumption is not the only way an episode can end -- the
        // store's ClearAsync is reachable from elsewhere, so the generation moving is what this
        // keys on rather than the path that moved it.
        if (existing?.FaultGeneration != fault.FaultGeneration)
        {
            ledger.Forget(subject.DeviceKey);
        }

        FaultedCargoBinding? cargo = await ProtectCargoAsync(subject, fault, context, cancellationToken)
            .ConfigureAwait(false);
        RiotOrderCommandOutcome? hold = await HoldCurrentOrderAsync(fault, context, cancellationToken)
            .ConfigureAwait(false);
        (StopProofVerdict proof, VehicleMotionSample latest) = await ProveStopAsync(
            subject, fault, cancellationToken).ConfigureAwait(false);

        bool escalated = RequiresEscalation(hold == RiotOrderCommandOutcome.Confirmed, proof, latest);
        if (escalated)
        {
            await EscalateAsync(subject, fault, proof, cancellationToken).ConfigureAwait(false);
        }

        List<string> reasons = [.. proof.MissingFacts];
        if (hold is not null && hold != RiotOrderCommandOutcome.Confirmed)
        {
            reasons.Add($"ORDER_HOLD_{hold.ToString()!.ToUpperInvariant()}");
        }

        string? alarm = level == VehicleFaultLevel.ConfirmedIsolated
            ? IsolatedAlarm
            : reasons.Count > 0 ? OrderNotProtectedAlarm : null;
        Alarm(alarm, subject.AgvId, reasons);

        return new VehicleFaultDecision(
            fault.Level, fault.FaultGeneration, evidenceCode, hold, cargo, proof, escalated, reasons, alarm);
    }

    /// <summary>
    /// REQ-0238: a vehicle that is, or might be, carrying something keeps its binding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unknown counts as loaded. The alternative reading — release the binding unless loading was
    /// witnessed — throws away the association between a product and its demand exactly when
    /// nobody can see which is true, and REQ-0238 puts both cases into the same hold for that
    /// reason.
    /// </para>
    /// <para>
    /// One binding per episode: a live binding is left alone rather than duplicated, so re-observing
    /// the same fault does not accumulate rows against one cargo.
    /// </para>
    /// </remarks>
    private async Task<FaultedCargoBinding?> ProtectCargoAsync(
        EmergencyStopSubject subject,
        VehicleFaultFact fault,
        FaultedVehicleContext context,
        CancellationToken cancellationToken)
    {
        FaultedCargoBinding? live = await faults.ReadLiveCargoAsync(subject.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (live is not null || context.Cargo is null)
        {
            return live;
        }

        FaultedVehicleCargoFacts facts = context.Cargo;
        if (!facts.LoadingWitnessed && facts.CargoStateKnown)
        {
            return null;
        }

        return await faults.BindCargoAsync(
            subject.AgvId,
            fault.FaultGeneration,
            facts.DemandId,
            facts.MovementLegId,
            facts.TransportDemandKey,
            facts.LoadingWitnessed,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// REQ-0234: hold the order this project has in flight, and read HELD back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never Cancel.</b> The entry says so in as many words, and the reason is that cancelling
    /// releases the vehicle to be dispatched again by RIoT, which is the opposite of stopping it.
    /// </para>
    /// <para>
    /// A hold already confirmed is not re-issued — the order is where the command wanted it. A hold
    /// that is not confirmed is re-issued on the next evaluation, until the vehicle escalates: once
    /// an emergency stop has been triggered the vehicle is held by something stronger than an order
    /// state, and REQ-0234 asks for the block and a high-priority event from there on, not for a
    /// command repeated at whatever rate the caller happens to evaluate.
    /// </para>
    /// </remarks>
    private async Task<RiotOrderCommandOutcome?> HoldCurrentOrderAsync(
        VehicleFaultFact fault,
        FaultedVehicleContext context,
        CancellationToken cancellationToken)
    {
        if (context.CurrentOrder is not RiotOrderCommandTarget target)
        {
            return null;
        }

        IReadOnlyList<RiotOrderCommandAttempt> attempts = await audit.ReadAttemptsAsync(
            RiotCommandTypeNames.OrderHold, target.UpperId, cancellationToken).ConfigureAwait(false);
        RiotOrderCommandAttempt[] thisEpisode =
            [.. attempts.Where(attempt => attempt.FaultGeneration == fault.FaultGeneration)];
        if (thisEpisode.Any(attempt => attempt.Outcome == RiotOrderCommandOutcome.Confirmed))
        {
            return RiotOrderCommandOutcome.Confirmed;
        }

        if (thisEpisode.Length > 0 && fault.EscalatedAt is not null)
        {
            return thisEpisode[^1].Outcome;
        }

        RiotOrderCommandRecord record = await commands.IssueAsync(
            RiotOrderCommandKind.Hold,
            target,
            fault.EvidenceCode ?? "VEHICLE_FAULT",
            fault.FaultGeneration,
            cancellationToken).ConfigureAwait(false);
        return record.Outcome;
    }

    /// <summary>
    /// Takes one motion sample, adds it to the window, and records what the combined evidence says.
    /// </summary>
    /// <remarks>
    /// The verdict is written either way. A recorded <c>false</c> is not nothing: it is the fact
    /// that this server looked and could not prove the vehicle had stopped, at a time that is now
    /// on the record, and it is what keeps a stale <c>true</c> from an earlier moment standing.
    /// </remarks>
    private async Task<(StopProofVerdict Proof, VehicleMotionSample Latest)> ProveStopAsync(
        EmergencyStopSubject subject,
        VehicleFaultFact fault,
        CancellationToken cancellationToken)
    {
        VehicleMotionSample sample = await motionFacts
            .SampleMotionAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        VehicleMotionSample[] window = ledger.Record(sample);
        StopProofVerdict proof = StopProof.Evaluate(window, timeProvider.GetUtcNow(), faultOptions);

        await faults.RecordStopProofAsync(
            subject.AgvId,
            fault.FaultGeneration,
            proof.Proven,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return (proof, sample);
    }

    /// <summary>
    /// REQ-0246's escalation: recorded on the fault, then asked of the emergency supervisor.
    /// </summary>
    /// <remarks>
    /// The supervisor owns everything after this — the controlled backoff, the retry until the
    /// latch confirms, the alarms. Asking it again while an episode is open is harmless: it issues
    /// a trigger only when one is due, and refuses to re-trigger a confirmed latch.
    /// </remarks>
    private async Task EscalateAsync(
        EmergencyStopSubject subject,
        VehicleFaultFact fault,
        StopProofVerdict proof,
        CancellationToken cancellationToken)
    {
        await faults.RecordEscalationAsync(
            subject.AgvId, fault.FaultGeneration, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        await emergencyStop.RequestStopAsync(
            new EmergencyStopRequest(
                subject,
                EmergencyStopRequestSource.Automatic,
                RequesterIdentity: null,
                Reason: proof.MissingFacts.Count > 0
                    ? string.Join(',', proof.MissingFacts)
                    : "STOP_NOT_PROVEN",
                fault.FaultGeneration),
            cancellationToken).ConfigureAwait(false);
    }

    private void Alarm(string? alarmCode, string agvId, IReadOnlyList<string> reasons)
    {
        if (alarmCode is not null)
        {
            LogAlarm(logger, alarmCode, agvId, string.Join(',', reasons), null);
        }
    }
}
