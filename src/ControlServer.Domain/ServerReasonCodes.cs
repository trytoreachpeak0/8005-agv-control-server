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
/// <para>
/// **Four were registered here before anything sent them.** The protocol <c>2.0.0</c> candidate added
/// the three sublot rejection reasons, which <c>SublotRejected</c> starts carrying in
/// <c>8005-agv-control-server#82</c>, and <c>OPERATOR_TIMEOUT</c>, which the v2 onboard never
/// produces and this server only has to settle defensively when it arrives
/// (<c>8005-agv-control-server#81</c>). Naming them first is what put them under the registry guard
/// before their first use rather than after; <c>EXPECTED_BASKET_COUNT_MISMATCH</c> joined the
/// <c>SublotRejected</c> surface in the same ticket, having until then only ever been a dispatch
/// reason code the peer never saw.
/// </para>
/// <para>
/// **<c>SLOT_FAULT_DECLARED</c> is recognised, not produced.** The protocol <c>3.0.0</c> candidate added it for the
/// vehicle to put on the slot an administrator declared faulty, in that slot's <c>SlotResult.reasonCodes</c> (REQ-0359,
/// <c>8005-agv-control-server#383</c>). The server settles such a result as it settles every <c>UNKNOWN</c> slot and sends
/// the code nowhere; it is named here, as <c>OPERATOR_TIMEOUT</c> is, so the one place the server spells it stays under
/// the registry guard.
/// </para>
/// </remarks>
public static class ServerReasonCodes
{
    public const string ActionNotAllowedInState = "ACTION_NOT_ALLOWED_IN_STATE";
    public const string BusinessIdContentConflict = "BUSINESS_ID_CONTENT_CONFLICT";
    public const string ContentHashMismatch = "CONTENT_HASH_MISMATCH";
    public const string ExpectedBasketCountMismatch = "EXPECTED_BASKET_COUNT_MISMATCH";
    public const string ForcedRecoveryGenerationStale = "FORCED_RECOVERY_GENERATION_STALE";
    public const string OperatorTimeout = "OPERATOR_TIMEOUT";
    public const string MessageIdContentConflict = "MESSAGE_ID_CONTENT_CONFLICT";
    public const string PackageCapacityUnresolved = "PACKAGE_CAPACITY_UNRESOLVED";
    public const string ProtocolReleaseIdentityMismatch = "PROTOCOL_RELEASE_IDENTITY_MISMATCH";
    public const string ProtocolSchemaInvalid = "PROTOCOL_SCHEMA_INVALID";
    public const string ProvenRecoveryCheckpointRequired = "PROVEN_RECOVERY_CHECKPOINT_REQUIRED";
    public const string RecoveryActionAlreadySelected = "RECOVERY_ACTION_ALREADY_SELECTED";
    public const string RecoveryActionRequired = "RECOVERY_ACTION_REQUIRED";

    /// <summary>
    /// Why an exception recovery session closed without its action reconciling (control-server#169, #187, #385): a result
    /// that reported FAILED or UNKNOWN or a success its slot results do not bear out, or a resume command the vehicle
    /// refused. It is the session snapshot's <c>closedReason</c>, the one position the registry allows it in.
    /// </summary>
    public const string RecoveryActionResultNotReconciled = "RECOVERY_ACTION_RESULT_NOT_RECONCILED";
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

    /// <summary>
    /// A slot a cancellation or compensation settled as empty while its door lock or unlock output was not proven
    /// (REQ-0364, CP-0009, control-server#385). It stands in <c>VehicleBusinessStateSnapshot.blockingFacts</c>, one fact
    /// per held slot, for as long as the vehicle is held for its repair release.
    /// </summary>
    public const string SlotDoorLockUnprovenAfterEmpty = "SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY";
    public const string SlotFaultDeclared = "SLOT_FAULT_DECLARED";
    public const string SnapshotRevisionContentConflict = "SNAPSHOT_REVISION_CONTENT_CONFLICT";
    public const string SlotSetInvalid = "SLOT_SET_INVALID";
    public const string SnapshotRevisionRegression = "SNAPSHOT_REVISION_REGRESSION";
    public const string SublotBoxCountUnavailable = "SUBLOT_BOX_COUNT_UNAVAILABLE";
    public const string SublotNotInDispatchScope = "SUBLOT_NOT_IN_DISPATCH_SCOPE";
    public const string WorklistRevisionStale = "WORKLIST_REVISION_STALE";
}
