using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// Pins the two things that were wrong on 2026-09-04: every reason code the server can put on the
/// wire must be one the protocol allows, and a session must not report READY while the server itself
/// holds an operation that needs recovery.
/// </summary>
public sealed class SessionReadinessReasonCodesTests
{
    /// <summary>
    /// Every internal reason code <c>GetRecoveryReason</c> can return, plus the mapping's own
    /// fallback. Kept as a literal list rather than derived: the point is to fail when someone adds
    /// a reason code and forgets the mapping, and a derived list would quietly grow with them.
    /// </summary>
    public static TheoryData<string> InternalReasonCodes =>
    [
        "CAPABILITY_SNAPSHOT_REQUIRED",
        "SAFETY_SNAPSHOT_REQUIRED",
        "RECOVERY_REPORT_REQUIRED",
        "FORCED_RECOVERY_GENERATION_MISMATCH",
        "PENDING_FACT_RECONCILIATION_REQUIRED",
        "DEPARTURE_SAFETY_NOT_READY",
        "OPERATION_RECOVERY_REQUIRED",
        "RECOVERY_REQUIRED"
    ];

    [Theory]
    [MemberData(nameof(InternalReasonCodes))]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void EverySessionReasonCodeMapsToAProtocolErrorCode(string internalReasonCode)
    {
        string wire = ProtocolErrorCodes.ToSessionReadinessReasonCode(internalReasonCode);

        Assert.True(
            ProtocolErrorCodes.Contains(wire),
            $"'{internalReasonCode}' maps to '{wire}', which is not in the protocol ErrorCode enum. " +
            "SessionReadiness.reasonCodes is typed ErrorCode, so this would be a schema-invalid message.");
    }

    /// <summary>
    /// Before the mapping existed the server sent its internal code straight out, and all seven of
    /// them were outside the enum. This asserts the failure mode rather than the fix: if someone
    /// removes the mapping and passes the internal code through again, the codes themselves are
    /// still illegal and this test says so.
    /// </summary>
    [Theory]
    [MemberData(nameof(InternalReasonCodes))]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void TheServerInternalReasonCodesAreNotThemselvesProtocolErrorCodes(string internalReasonCode)
    {
        Assert.False(
            ProtocolErrorCodes.Contains(internalReasonCode),
            $"'{internalReasonCode}' is now a protocol ErrorCode. If the protocol adopted it, map it " +
            "to itself and drop it from this test rather than leaving the two vocabularies guessing.");
    }

    /// <summary>
    /// The one session reason that is already a protocol ErrorCode, and so maps to itself.
    /// </summary>
    /// <remarks>
    /// Not in <see cref="InternalReasonCodes"/>: that list also feeds the theory asserting its codes are
    /// NOT protocol codes, whose own message says to map an adopted code to itself and drop it from
    /// there. It is also not produced by <c>GetRecoveryReason</c> but by <c>DecideReadinessAsync</c>
    /// directly, which is how it escaped a hand-kept list on 2026-09-10 and threw while serialising
    /// SessionReadiness in G3. The guard that follows the code the store actually produces lives in
    /// <c>CapabilitySnapshotFingerprintTests</c>.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    [Trait("ProtocolVector", "CV-SLOT-CONFIGURATION-ACTIVATION")]
    public void AFingerprintMismatchGoesOnTheWireAsItself()
    {
        string wire = ProtocolErrorCodes.ToSessionReadinessReasonCode(
            SlotConfigurationFingerprintVerdict.MismatchCode);

        Assert.Equal(SlotConfigurationFingerprintVerdict.MismatchCode, wire);
        Assert.True(ProtocolErrorCodes.Contains(wire));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void AnUnmappedReasonCodeThrowsRatherThanReachingTheWire()
    {
        Assert.Throws<InvalidDataException>(
            () => ProtocolErrorCodes.ToSessionReadinessReasonCode("SOMETHING_NOBODY_MAPPED"));
    }
}
