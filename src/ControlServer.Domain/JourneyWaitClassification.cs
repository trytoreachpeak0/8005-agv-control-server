namespace ControlServer.Domain;

/// <summary>
/// What a vehicle on a journey is doing in each <see cref="JourneyRuntimeStage"/>, from the one question the waiting
/// journey watch asks (control-server#273): is it standing still until a person does something?
/// </summary>
/// <remarks>
/// <para>
/// <b>This table is the only place that answers it.</b> The database context that stamps when a wait began, the watch
/// that logs a long wait and the dashboard card that shows it all read <see cref="IsWaiting"/>; none of them branches on
/// stages of its own. It lives in the domain so that the persistence layer can use it too. A stage added to the enum and
/// not to this table throws here on the first journey that reaches it, and
/// <c>WaitingJourneyBatteryWatchTests.EveryStageIsClassifiedAndTheTableIsTheOneThatWasReviewed</c> fails before that: it
/// pins the whole table against every value of the enum.
/// </para>
/// <para>
/// The classification is from reading <c>JourneyRuntimeEngine.AdvanceAsync</c> at <c>fp/v2-impl@8ec088b1</c>. The
/// stationary stages are the ones where the engine does nothing until a person or the vehicle answers: a sublot entry, a
/// load or unload closing, a departure wait or cargo holding, a departure safety check, a recovery. Some of them are
/// bounded (the station departure wait, cargo holding) and some are not (the gate's unload, a block); the watch does not
/// care which, because a bounded wait still costs battery until its bound.
/// </para>
/// <para>
/// A travelling stage is a wait only while the engine names a reason the leg is not arriving -- a checkpoint wait, an
/// order RIoT suspended or failed, a session gone quiet, an AREA machine that no longer admits the stop. A leg that is
/// simply under way is not a wait, however long it takes, and its wait begins when the reason appears, not when the leg
/// did.
/// </para>
/// <para>
/// <b>Except <see cref="SessionNotReadyReason"/>, which on a leg is never a wait</b> (incremental review of #320). A real
/// onboard carrying this server's own order in flight reports RecoveryRequired by design until the order ends
/// (control-server#314, #316), so the engine writes that code on the first round of nearly every leg and clears it only on
/// arrival. Counted, it logged a twelve-minute drive as "waited 10 min" and carried the drive into the stop that followed.
/// All of it is left out rather than only the own-order case <c>OwnMovementOrderExplanation</c> recognises: that judgement
/// needs the session and order rows, and this table is read where there are none (the database context that stamps the
/// wait). What it gives up is small: a leg that has really stopped is named on the RIoT side -- an order failed or hung, a
/// checkpoint wait past its budget, a session gone silent -- and those still count. At a stop the code changes nothing: the
/// stage is stationary and waits anyway.
/// </para>
/// </remarks>
public static class JourneyWaitClassification
{
    public static JourneyStageActivity Of(JourneyRuntimeStage stage) => stage switch
    {
        JourneyRuntimeStage.AwaitingPickupArrival => JourneyStageActivity.Travelling,
        JourneyRuntimeStage.AwaitingSublot => JourneyStageActivity.Stationary,
        JourneyRuntimeStage.AwaitingLoadResult => JourneyStageActivity.Stationary,
        JourneyRuntimeStage.AwaitingStationDeparture => JourneyStageActivity.Stationary,
        JourneyRuntimeStage.AwaitingDepartureSafety => JourneyStageActivity.Stationary,
        JourneyRuntimeStage.AwaitingGateArrival => JourneyStageActivity.Travelling,
        JourneyRuntimeStage.AwaitingUnloadResult => JourneyStageActivity.Stationary,
        JourneyRuntimeStage.Blocked => JourneyStageActivity.Stationary,
        JourneyRuntimeStage.Completed => JourneyStageActivity.Finished,
        _ => throw new InvalidDataException(
            $"Journey stage '{stage}' has no waiting classification; add it to {nameof(JourneyWaitClassification)}.")
    };

    /// <summary>
    /// Whether a journey in <paramref name="stage"/> carrying <paramref name="blockReasonCode"/> is waiting right now: its
    /// vehicle stands still in a stationary stage, or a travelling stage names why it is not arriving.
    /// </summary>
    public static bool IsWaiting(JourneyRuntimeStage stage, string? blockReasonCode) => Of(stage) switch
    {
        JourneyStageActivity.Stationary => true,
        JourneyStageActivity.Travelling => blockReasonCode is not null &&
            !string.Equals(blockReasonCode, SessionNotReadyReason, StringComparison.Ordinal),
        _ => false
    };

    /// <summary>
    /// The code <c>JourneyRuntimeEngine.AdvanceAsync</c> writes while the vehicle's session is not Ready. Spelled out here
    /// because the domain cannot see the host; <c>WaitingJourneyBatteryWatchTests.ADriveWithTheSessionNotReadyIsNotAWaitAndTheStopWaitsFromArrival</c>
    /// runs the engine's own write through this table, so a rename on either side turns it red.
    /// </summary>
    public const string SessionNotReadyReason = "ONBOARD_SESSION_NOT_READY";
}

public enum JourneyStageActivity
{
    /// <summary>The vehicle stands at a stop, or wherever it was stopped, and nothing moves it until someone answers.</summary>
    Stationary,

    /// <summary>The vehicle is on a leg this server sent it on.</summary>
    Travelling,

    /// <summary>The journey has ended.</summary>
    Finished
}
