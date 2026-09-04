namespace ControlServer.Domain;

/// <summary>
/// Every reason code this server can put on the wire, as named constants.
/// </summary>
/// <remarks>
/// <para>
/// Reason codes reach the peer through three schema positions: <c>Problem.reasonCode</c> (nine
/// message types), <c>BlockingFact.reasonCode</c> (two snapshots) and
/// <c>SessionReadiness.reasonCodes</c>. The last has its own mapping in
/// <see cref="ProtocolErrorCodes.ToSessionReadinessReasonCode"/>; the other two are named here.
/// </para>
/// <para>
/// They used to be bare string literals, and most of them never appeared at a <c>reasonCode =</c>
/// or <c>Problem(</c> position at all -- they were returned from a <c>Validate*</c> helper and
/// arrived through a local, so scanning those positions alone reported nothing. That is exactly
/// the shape of "not actually scanning" that <c>ProtocolReasonCodeArchitectureTests</c> exists to
/// rule out, and naming them is what gives it one place to read.
/// </para>
/// <para>
/// Members below the divider are **not** in the protocol's closed <c>ErrorCode</c> enumeration.
/// The architecture test pins that set exactly, so a tenth one cannot appear quietly. They are
/// not typos -- unlike the three renamed on 2026-09-04 -- and they are not near-misses either:
/// <c>RECOVERY_SESSION_CLOSED</c> and <c>FORCED_RECOVERY_GENERATION_MISMATCH</c> did have exact
/// counterparts in the enumeration, so they became <c>RECOVERY_SESSION_NOT_OPEN</c> and
/// <c>FORCED_RECOVERY_GENERATION_STALE</c> and moved above the divider. What is left is nine
/// distinctions this server draws that the protocol has no vocabulary for: the enum has one
/// <c>RECOVERY_SCOPE_MISMATCH</c> where the server separates the event, the demand and the
/// operator. Collapsing those would lose what the onboard shows the operator, so they go into the
/// v2 error surface rather than onto an existing code.
/// </para>
/// </remarks>
public static class ServerReasonCodes
{
    // --- In the protocol's ErrorCode enumeration ----------------------------
    public const string ActionNotAllowedInState = "ACTION_NOT_ALLOWED_IN_STATE";
    public const string ForcedRecoveryGenerationStale = "FORCED_RECOVERY_GENERATION_STALE";
    public const string ProtocolReleaseIdentityMismatch = "PROTOCOL_RELEASE_IDENTITY_MISMATCH";
    public const string ProtocolSchemaInvalid = "PROTOCOL_SCHEMA_INVALID";
    public const string RecoveryAuthenticationFailed = "RECOVERY_AUTHENTICATION_FAILED";
    public const string RecoveryScopeMismatch = "RECOVERY_SCOPE_MISMATCH";
    public const string RecoverySessionNotOpen = "RECOVERY_SESSION_NOT_OPEN";
    public const string SessionRecoveryRequired = "SESSION_RECOVERY_REQUIRED";

    // --- Not in the enumeration; pinned by the architecture test ------------
    public const string RecoveryDemandNotBlocked = "RECOVERY_DEMAND_NOT_BLOCKED";
    public const string RecoveryEventMismatch = "RECOVERY_EVENT_MISMATCH";
    public const string RecoveryDemandMismatch = "RECOVERY_DEMAND_MISMATCH";
    public const string RecoveryOperatorMismatch = "RECOVERY_OPERATOR_MISMATCH";
    public const string RecoveryActionAlreadySelected = "RECOVERY_ACTION_ALREADY_SELECTED";
    public const string RecoveryOperationNotFound = "RECOVERY_OPERATION_NOT_FOUND";
    public const string ProvenRecoveryCheckpointRequired = "PROVEN_RECOVERY_CHECKPOINT_REQUIRED";
    public const string RecoveryActionRequired = "RECOVERY_ACTION_REQUIRED";
    public const string RecoveryResultRequired = "RECOVERY_RESULT_REQUIRED";
}
