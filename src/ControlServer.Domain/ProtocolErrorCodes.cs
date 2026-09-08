namespace ControlServer.Domain;

/// <summary>
/// The protocol's <c>ErrorCode</c> enumeration, vendored the way
/// <see cref="ProtocolCandidateIdentity"/> vendors the manifest hash — the server is built against a
/// pinned protocol release and does not read its schemas at runtime.
/// </summary>
/// <remarks>
/// <para>
/// This exists because nothing was checking. <c>SessionReadiness.reasonCodes</c> is typed
/// <c>ErrorCode</c>, a closed 43-value enumeration, and on 2026-09-04 every reason code the server
/// could put there was outside it — seven for seven. Eight gates were green over it: the server has
/// no schema validation of its own, and <c>CONTROL_SERVER_G2</c> verifies the manifest hash rather
/// than each message against its schema.
/// </para>
/// <para>
/// **Re-sync this list whenever <see cref="ProtocolCandidateIdentity.ManifestSha256"/> changes.**
/// The source is <c>schemas/common/types.schema.json</c>, <c>$defs/ErrorCode</c>, in the protocol
/// repository. <c>SessionReadinessReasonCodesTests</c> fails if a reason code the server can emit is
/// not in here, which catches drift in one direction; nothing catches this list drifting away from
/// the protocol except re-syncing it on a release.
/// </para>
/// </remarks>
public static class ProtocolErrorCodes
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "PROTOCOL_ENVELOPE_INVALID",
        "PROTOCOL_SCHEMA_INVALID",
        "UNSUPPORTED_PROTOCOL_VERSION",
        "PROTOCOL_RELEASE_IDENTITY_MISMATCH",
        "UNKNOWN_MESSAGE_TYPE",
        "PROFILE_MESSAGE_NOT_ALLOWED",
        "MESSAGE_ID_CONTENT_CONFLICT",
        "CORRELATION_INVALID",
        "CONTENT_HASH_MISMATCH",
        "VEHICLE_CREDENTIAL_INVALID",
        "AGV_ID_MISMATCH",
        "STALE_SESSION_GENERATION",
        "DUPLICATE_ACTIVE_SESSION",
        "HANDSHAKE_SEQUENCE_INVALID",
        "CAPABILITY_VERSION_GAP",
        "SAFETY_STATE_VERSION_GAP",
        "SNAPSHOT_REVISION_REGRESSION",
        "SNAPSHOT_REVISION_CONTENT_CONFLICT",
        "SESSION_RECOVERY_REQUIRED",
        "BUSINESS_ID_CONTENT_CONFLICT",
        "VEHICLE_NOT_READY",
        "DEMAND_NOT_CURRENT",
        "OPERATION_SESSION_MISMATCH",
        "STATION_MISMATCH",
        "WORKLIST_REVISION_STALE",
        "SUBLOT_MISMATCH",
        "SLOT_SET_INVALID",
        "EXPECTED_BASKET_COUNT_MISMATCH",
        "SLOT_OPERATION_CONFLICT",
        "ACTION_NOT_ALLOWED_IN_STATE",
        "MANUAL_CHARGING_HOLD_ACTIVE",
        "CAPABILITY_UNKNOWN",
        "SLOT_INOPERABLE",
        "SLOT_STATE_UNKNOWN",
        "LOCK_NOT_CLOSED",
        "UNLOCK_OUTPUT_NOT_RESET",
        "DEPARTURE_UNSAFE",
        "PREDEPARTURE_CHECK_EXPIRED",
        "RECOVERY_SESSION_NOT_OPEN",
        "RECOVERY_SCOPE_MISMATCH",
        "RECOVERY_CHECKPOINT_NOT_UNIQUE",
        "RECOVERY_AUTHENTICATION_FAILED",
        "FORCED_RECOVERY_GENERATION_STALE",
        "OPERATOR_TIMEOUT"
    };

    public static bool Contains(string code) => Codes.Contains(code);

    /// <summary>
    /// Maps the server's own session reason code — richer than the protocol's vocabulary, stored on
    /// the session row and asserted by L1 and L2 — onto the wire value the schema permits.
    /// </summary>
    /// <remarks>
    /// The mapping loses detail, and that is deliberate rather than regrettable: three internal
    /// reasons collapse onto <c>SESSION_RECOVERY_REQUIRED</c> because the protocol has one code for
    /// "this session needs reconciliation before it can work". The distinction between them is a
    /// diagnosis for whoever reads the server, not a fact the vehicle acts on differently. Keeping
    /// the internal code on <c>SessionRecoveries.ReasonCode</c> is what preserves it.
    /// </remarks>
    public static string ToSessionReadinessReasonCode(string internalReasonCode) => internalReasonCode switch
    {
        // Exact protocol counterparts.
        "DEPARTURE_SAFETY_NOT_READY" => "DEPARTURE_UNSAFE",
        "FORCED_RECOVERY_GENERATION_MISMATCH" => "FORCED_RECOVERY_GENERATION_STALE",

        // The handshake has not delivered a snapshot the server needs. The protocol's gap codes are
        // the closest true statement: the server does not have the revision it requires.
        "CAPABILITY_SNAPSHOT_REQUIRED" => "CAPABILITY_VERSION_GAP",
        "SAFETY_SNAPSHOT_REQUIRED" => "SAFETY_STATE_VERSION_GAP",

        // Everything that means "reconcile this session before using it".
        "RECOVERY_REPORT_REQUIRED" => "SESSION_RECOVERY_REQUIRED",
        "PENDING_FACT_RECONCILIATION_REQUIRED" => "SESSION_RECOVERY_REQUIRED",
        "OPERATION_RECOVERY_REQUIRED" => "SESSION_RECOVERY_REQUIRED",
        "RECOVERY_REQUIRED" => "SESSION_RECOVERY_REQUIRED",

        // An unmapped code is a bug in this switch, not something to put on the wire. Failing here
        // is the whole point: the previous behaviour was to send whatever string happened to exist.
        _ => throw new InvalidDataException(
            $"Session reason code '{internalReasonCode}' has no protocol ErrorCode mapping.")
    };
}
