using System.Text.Json;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// Whether a session's unknown departure-safety evidence is fully explained by a move order this server itself has in flight
/// for the journey (control-server#139). Only then does the blocked-journey dashboard let an <c>ONBOARD_SESSION_NOT_READY</c>
/// block wait for the escalation lines instead of going straight to the maintenance administrator.
/// </summary>
/// <remarks>
/// <para>
/// Why the unknown arises at all (control-server#138): while the vehicle carries a non-final RIoT order this server created,
/// <c>ReadVehicleSafetyAsync</c> adds <c>RIOT_NONFINAL_ORDER_PRESENT</c> and returns <c>motionState=Unknown</c>; the onboard side
/// reports <c>VEHICLE_NOT_READY</c> with <c>unknownPresent=true</c>, and the session drops to <c>DEPARTURE_SAFETY_NOT_READY</c>.
/// That gate is the release candidate's section 8 and stays as it is. What changes is only who the dashboard sends: a vehicle
/// queueing or giving way on its own order is not a maintenance call.
/// </para>
/// <para>
/// Every condition has to hold, and each one fails closed. The session reason codes must name the vehicle and nothing but the
/// vehicle — an allow-list, not a deny-list, so a slot-side code this file has never heard of keeps the block at the top. The
/// own-order fact is decided by the caller from this server's own records, because the dashboard reads the database and never
/// calls RIoT.
/// </para>
/// <para>
/// <b>The journey runtime reads this too</b> (control-server#314): <c>JourneyRuntimeEngine.PublishPickupDispatchPlanPastOwnOrderAsync</c>
/// lets the pickup dispatch plan through the closed readiness gate only when this returns true. Widening the allow-list
/// therefore also widens which unready sessions receive that plan; <c>PickupDispatchPlanPastOwnOrderTests</c> pins one
/// refusing case per condition.
/// </para>
/// </remarks>
internal static class OwnMovementOrderExplanation
{
    internal const string SessionNotReadyBlock = "ONBOARD_SESSION_NOT_READY";
    internal const string DepartureSafetyNotReady = "DEPARTURE_SAFETY_NOT_READY";
    internal const string VehicleNotReady = "VEHICLE_NOT_READY";

    /// <summary>
    /// The only session safety reason codes that leave the unknown with no source but the vehicle's own signals.
    /// </summary>
    private static readonly HashSet<string> VehicleOnlyReasons =
        new([VehicleNotReady, "ACTION_NOT_ALLOWED_IN_STATE"], StringComparer.Ordinal);

    internal static bool Explains(
        string? blockReasonCode,
        string? sessionReasonCode,
        string? safetyReasonCodesJson,
        bool? safetyUnknownPresent,
        bool ownMovementOrderInFlight)
    {
        if (!ownMovementOrderInFlight ||
            !string.Equals(blockReasonCode, SessionNotReadyBlock, StringComparison.Ordinal))
        {
            return false;
        }
        return OnlyTheVehicleKeepsItNotReady(sessionReasonCode, safetyReasonCodesJson, safetyUnknownPresent);
    }

    /// <summary>
    /// Whether the session is not ready for the vehicle's own signals and nothing else: the departure-safety reason, an
    /// unknown in the safety evidence, and only vehicle-side safety codes. The same allow-list as <see cref="Explains"/>, without
    /// asking why the vehicle reads that way.
    /// </summary>
    /// <remarks>
    /// control-server#318 reads it behind the readiness gate: an order of this server's that ended mid-leg is rebuilt there
    /// only when the session is held back by the vehicle alone, which the rebuild's own read of the vehicle then judges. A
    /// session held back for anything slot-side, an operation to recover or a handshake not finished keeps it waiting.
    /// </remarks>
    internal static bool OnlyTheVehicleKeepsItNotReady(
        string? sessionReasonCode,
        string? safetyReasonCodesJson,
        bool? safetyUnknownPresent)
    {
        if (safetyUnknownPresent != true ||
            !string.Equals(sessionReasonCode, DepartureSafetyNotReady, StringComparison.Ordinal))
        {
            return false;
        }
        string[]? reasons = ReadReasons(safetyReasonCodesJson);
        return reasons is not null &&
               reasons.Contains(VehicleNotReady, StringComparer.Ordinal) &&
               reasons.All(VehicleOnlyReasons.Contains);
    }

    private static string[]? ReadReasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
