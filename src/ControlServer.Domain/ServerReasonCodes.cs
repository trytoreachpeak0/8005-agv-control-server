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
/// **There is no longer a divider here.** This class was in two halves until the v2 candidate:
/// above, the codes the protocol's closed <c>ErrorCode</c> enumeration contained; below, nine it
/// had no vocabulary for -- the enum offered one <c>RECOVERY_SCOPE_MISMATCH</c> where the server
/// separates the event, the demand and the operator, and one <c>ACTION_NOT_ALLOWED_IN_STATE</c>
/// where it separates "already chose an action", "no operation found" and "no proven checkpoint".
/// Collapsing them would have cost what the onboard shows the operator, so v2 appended them
/// instead and the error surface went from 43 codes to 54. All seventeen below are now registry
/// codes, which is why they are in one alphabetical list: there is no second category left for a
/// new one to be sorted into, and <c>ProtocolReasonCodeArchitectureTests</c> now requires that --
/// its pinned-deviation set is empty, so a code outside the registry fails rather than being
/// filed under the divider.
/// </para>
/// </remarks>
public static class ServerReasonCodes
{
    public const string ActionNotAllowedInState = "ACTION_NOT_ALLOWED_IN_STATE";
    public const string ForcedRecoveryGenerationStale = "FORCED_RECOVERY_GENERATION_STALE";
    public const string ProtocolReleaseIdentityMismatch = "PROTOCOL_RELEASE_IDENTITY_MISMATCH";
    public const string ProtocolSchemaInvalid = "PROTOCOL_SCHEMA_INVALID";
    public const string ProvenRecoveryCheckpointRequired = "PROVEN_RECOVERY_CHECKPOINT_REQUIRED";
    public const string RecoveryActionAlreadySelected = "RECOVERY_ACTION_ALREADY_SELECTED";
    public const string RecoveryActionRequired = "RECOVERY_ACTION_REQUIRED";
    public const string RecoveryAuthenticationFailed = "RECOVERY_AUTHENTICATION_FAILED";
    public const string RecoveryDemandMismatch = "RECOVERY_DEMAND_MISMATCH";
    public const string RecoveryDemandNotBlocked = "RECOVERY_DEMAND_NOT_BLOCKED";
    public const string RecoveryEventMismatch = "RECOVERY_EVENT_MISMATCH";
    public const string RecoveryOperationNotFound = "RECOVERY_OPERATION_NOT_FOUND";
    public const string RecoveryOperatorMismatch = "RECOVERY_OPERATOR_MISMATCH";
    public const string RecoveryResultRequired = "RECOVERY_RESULT_REQUIRED";
    public const string RecoveryScopeMismatch = "RECOVERY_SCOPE_MISMATCH";
    public const string RecoverySessionNotOpen = "RECOVERY_SESSION_NOT_OPEN";
    public const string SessionRecoveryRequired = "SESSION_RECOVERY_REQUIRED";
}
