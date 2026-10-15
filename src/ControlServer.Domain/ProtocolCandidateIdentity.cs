namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names the <c>4.0.0</c> candidate, and the candidate is not an approved release.</b> Every
/// value below is read off <c>8005-agv-protocol</c> commit
/// <c>4d67655c4da5120d20cd06d99f67f14c4f2dfb08</c> on branch <c>fp/v2-candidate</c> (protocol#14 added
/// <c>CurrentStopDepartureRequested</c>/<c>CurrentStopDepartureResult</c> and the closed-reason values
/// <c>OPERATOR_DEPARTED</c>/<c>CHARGING_REQUIRED</c>; protocol#15 changed staging charging to
/// "deliver first, charge afterwards"). It supersedes the released <c>protocol-v3.0.0</c>
/// (<c>3f091cb2</c>) for CP-0012 and CP-0013. <see cref="ApprovalStatus"/> says
/// <c>SUPERSEDING_CANDIDATE</c> rather than <c>APPROVED_RELEASE</c> for exactly that reason, and it is
/// the field to read before treating this identity as releasable. The manifest's own <c>status</c> is
/// <c>CONTENT_SNAPSHOT</c>, which is what every manifest says, released or not; approval lives only in
/// the external attestation.
/// </para>
/// <para>
/// <b>Bound by <c>8005-agv-control-server#713</c>, merged back to back with
/// <c>8005-agv-onboard-hmi#349</c>.</b> Neither end may reach the integration branch alone: there is no
/// negotiation, so a server on this identity and a vehicle on <c>protocol-v3.0.0</c> refuse each other.
/// Evidence produced against this candidate is <c>UNRELEASED_CANDIDATE</c> and does not count towards a
/// release; evidence bound to <c>protocol-v3.0.0</c> stops being current once this is merged.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> is 5, and still monotonic only within one
/// <see cref="ProfileId"/>.</b> <c>WIRE_TO_GATE_MVP 0.3.0</c> and <c>AGV_FULL_PRODUCT 2.0.0</c> both
/// said 3. Nothing may compare identities by that integer alone; the handshake compares the whole
/// release identity, and logs and evidence always write the pair <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that does not exist yet.</b> <c>protocol-v4.0.0</c> is cut on this
/// same commit once the release is authorized. The constant still carries the name because
/// <c>$defs/ProtocolReleaseIdentity</c> requires <c>tag</c>, constrains it to <c>minLength: 1</c> and
/// <c>^protocol-v</c>, and forbids additional properties. The pair is what tells the truth: this build
/// targets <c>protocol-v4.0.0</c>, and that release is not approved. The staged G3 runner reads the
/// pair: it refuses to run when the tag resolves anywhere but <see cref="RepositoryCommit"/>, and when
/// this status claims a release whose tag is absent.
/// </para>
/// <para>
/// <b>What follows from the status.</b> <c>scripts/New-WireToGateReleaseCandidate.ps1</c> refuses to
/// package a release candidate unless <c>approvalStatus</c> is <c>APPROVED_RELEASE</c>, so it refuses
/// until the release is tagged and this identity is rebound. That is the intended consequence.
/// </para>
/// <para>
/// <c>src/ControlServer.Host/appsettings.json</c> carries the same nine values under
/// <c>ProtocolCandidate</c> for that packaging script, which reads the published settings file
/// rather than this assembly. <c>ProtocolIdentityArchitectureTests</c> is what keeps the two from
/// drifting.
/// </para>
/// </remarks>
public static class ProtocolCandidateIdentity
{
    public const int ProtocolVersion = 5;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "4.0.0";
    public const string Tag = "protocol-v4.0.0";
    public const string RepositoryCommit = "4d67655c4da5120d20cd06d99f67f14c4f2dfb08";
    public const string ManifestSha256 = "38a5d2d53e1e235f7f844d991c7258ed51910d542f235ebae6768455c5511fc4";
    public const string SchemaBundleSha256 = "dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628";
    public const string VectorsSha256 = "290b9260df46cd484f993a5a5022712fd54e043c9777da78caa0f9aabf5aa300";
    public const string ApprovalStatus = "SUPERSEDING_CANDIDATE";
}
