namespace ControlServer.Domain;

/// <summary>
/// The protocol's <c>ErrorCode</c> enumeration, vendored the way
/// <see cref="ProtocolCandidateIdentity"/> vendors the manifest hash — the server is built against a
/// pinned protocol release and does not read its schemas at runtime.
/// </summary>
/// <remarks>
/// <para>
/// This exists because nothing was checking. <c>SessionReadiness.reasonCodes</c> is typed
/// <c>ErrorCode</c>, and on 2026-09-04 every reason code the server could put there was outside the
/// 43 values the enumeration then held — seven for seven. Eight gates were green over it: the server
/// has no schema validation of its own, and <c>CONTROL_SERVER_G2</c> verifies the manifest hash
/// rather than each message against its schema.
/// </para>
/// <para>
/// **This list and the identity beside it now name the same candidate.** The 54 values below are
/// <c>$defs/ErrorCode</c> of <c>schemas/common/types.schema.json</c> at protocol candidate manifest
/// <c>a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23</c>
/// (<c>status: CONTENT_SNAPSHOT</c>, profile <c>AGV_FULL_PRODUCT</c>, <c>protocolVersion: 2</c>),
/// and <see cref="ProtocolCandidateIdentity"/> names that same commit. They were deliberately out
/// of step for four days: this list moved to v2 first because eleven codes were appended and none
/// removed, so re-syncing it could only let the reason-code guard tell the truth about the nine
/// codes this server already emits, without touching wire behaviour. Moving the identity was the
/// other half, and it is done.
/// </para>
/// <para>
/// **Re-sync this list whenever the protocol's error surface moves**, which from here means either a
/// new candidate manifest or <see cref="ProtocolCandidateIdentity"/> catching up to this one. The
/// registry is <c>appendOnly</c>, so a re-sync adds and never removes; a code that disappears is a
/// governance breach rather than a merge to resolve here.
/// <c>SessionReadinessReasonCodesTests</c> fails if a reason code the server can emit is not in
/// here, and <c>ProtocolReasonCodeArchitectureTests</c> fails if any of them is, which catches drift
/// in one direction; nothing catches this list drifting away from the protocol except re-syncing it.
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
        "SLOT_CONFIGURATION_VERIFICATION_FAILED",
        "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH",
        "RECOVERY_DEMAND_NOT_BLOCKED",
        "RECOVERY_EVENT_MISMATCH",
        "RECOVERY_DEMAND_MISMATCH",
        "RECOVERY_OPERATOR_MISMATCH",
        "RECOVERY_ACTION_ALREADY_SELECTED",
        "RECOVERY_OPERATION_NOT_FOUND",
        "PROVEN_RECOVERY_CHECKPOINT_REQUIRED",
        "RECOVERY_ACTION_REQUIRED",
        "RECOVERY_RESULT_REQUIRED"
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
    /// the internal code on <c>SessionRecoveries.ReasonCode</c> is what preserves it. v2 appending
    /// eleven codes did not change that: none of them is one of these internal reasons, so every arm
    /// below still maps onto the same wire value it did against the 43.
    /// </remarks>
    public static string ToSessionReadinessReasonCode(string internalReasonCode) => internalReasonCode switch
    {
        // Exact protocol counterparts.
        "DEPARTURE_SAFETY_NOT_READY" => "DEPARTURE_UNSAFE",
        "FORCED_RECOVERY_GENERATION_MISMATCH" => "FORCED_RECOVERY_GENERATION_STALE",
        // REQ-0316. Already a protocol ErrorCode, so it goes on the wire as itself -- and it has to go
        // as itself, not collapse onto SESSION_RECOVERY_REQUIRED: the vehicle's operator needs to know
        // the fix is an activation, not a recovery session. Added 2026-09-10 when a fingerprint
        // disagreement stopped refusing the session and became a readiness reason instead; without
        // this arm the server threw while serialising SessionReadiness and dropped the connection,
        // which is how G3 found it (evidence/g3/20260910-fp-is-14-fingerprint-mismatch-unready).
        SlotConfigurationFingerprintVerdict.MismatchCode => SlotConfigurationFingerprintVerdict.MismatchCode,

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
