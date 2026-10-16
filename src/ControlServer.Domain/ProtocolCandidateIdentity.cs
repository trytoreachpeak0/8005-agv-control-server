namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names the unreleased <c>5.0.0</c> candidate.</b> Every value below is read off
/// <c>8005-agv-protocol</c> commit <c>9d7915786b736a6fa8d0c8737bfb115ac08a30b3</c> (protocol#22 for
/// protocol#21, branch <c>fp/v2-candidate</c>), which appends <c>WIRE_TO_FULL_INSPECTION</c> to the closed
/// enum <c>TransportTaskType</c> and otherwise changes nothing but the identity: release <c>5.0.0</c>,
/// protocol version 6, schema <c>$id</c> segment <c>agv-full-product/v6</c>, base release
/// <c>protocol-v4.0.0</c> (<c>3fb214e5</c>, released 2026-10-16, <c>8005-agv-program#184</c>). Appending to
/// a closed enum is breaking -- a v4 peer refuses the new value as <c>PROTOCOL_SCHEMA_INVALID</c> -- which is
/// why the major version moved. <see cref="Tag"/> names <c>protocol-v5.0.0</c>, which does not exist yet,
/// and <see cref="ApprovalStatus"/> says <c>SUPERSEDING_CANDIDATE</c> so that the pair tells the truth.
/// <see cref="ApprovalStatus"/> is the field to read before treating this identity as releasable. The
/// manifest's own <c>status</c> is <c>CONTENT_SNAPSHOT</c>, which is what every manifest says, released or
/// not; approval lives only in an external attestation.
/// </para>
/// <para>
/// <b>Bound by <c>8005-agv-control-server#737</c>, to ship in one package with
/// <c>8005-agv-onboard-hmi#360</c>.</b> Neither end may reach the integration branch alone: there is no
/// negotiation, so a server on this identity and a vehicle on <c>protocol-v4.0.0</c> refuse each other.
/// Evidence produced on this identity is <c>UNRELEASED_CANDIDATE</c> and does not count towards a release;
/// evidence bound to <c>protocol-v4.0.0</c> stops being current when this candidate is bound.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> is 6, and still monotonic only within one
/// <see cref="ProfileId"/>.</b> <c>WIRE_TO_GATE_MVP 0.3.0</c> and <c>AGV_FULL_PRODUCT 2.0.0</c> both
/// said 3. Nothing may compare identities by that integer alone; the handshake compares the whole
/// release identity, and logs and evidence always write the pair <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that does not exist yet.</b> The staged G3 runner reads the pair: it
/// refuses to run when the tag resolves anywhere but <see cref="RepositoryCommit"/>, and when this status
/// claims a release whose tag is absent.
/// </para>
/// <para>
/// <b>What follows from the status.</b> <c>scripts/New-WireToGateReleaseCandidate.ps1</c> refuses to
/// package a release candidate unless <c>approvalStatus</c> is <c>APPROVED_RELEASE</c>. Until
/// <c>protocol-v5.0.0</c> is released and this identity is rebound to it, the packager refuses.
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
    public const int ProtocolVersion = 6;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "5.0.0";
    public const string Tag = "protocol-v5.0.0";
    public const string RepositoryCommit = "9d7915786b736a6fa8d0c8737bfb115ac08a30b3";
    public const string ManifestSha256 = "743b886d99194038b893e98aed2a1dd01409cd7f80d39c734e02d860e0e5827c";
    public const string SchemaBundleSha256 = "d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933";
    public const string VectorsSha256 = "24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7";
    public const string ApprovalStatus = "SUPERSEDING_CANDIDATE";
}
