namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names <c>protocol-v7.0.0</c>, an approved release.</b> Every value below is read off
/// <c>8005-agv-protocol</c> commit <c>a1facf8674b05c67bfbddc14db4a163e8da2782a</c>, the commit the annotated tag
/// <c>protocol-v7.0.0</c> (object <c>250c4d86</c>) points at. It was released unchanged on 2026-11-21 for the deployment
/// prepared under <c>8005-agv-program#210</c> (the released manifest is byte-identical to the candidate's), with one
/// approval in the external attestation (<c>release-approval.json</c>, SHA-256
/// <c>b5240f97714647cea8852eb56d010269e79857646037b4216c8d3bd1ad156f59</c>) given by an AI agent the product owner
/// authorized for that release. As a candidate it was the merge of protocol PR #30
/// (protocol#29, content commit <c>2d26048f</c>) into <c>fp/v2-candidate</c> on 2026-11-16; it is pinned rather than
/// the pull request's head, as every earlier candidate pinned its merge. Its content is one field: each
/// <c>CurrentStopWorklistSnapshot</c> item carries <c>eqp</c>, required and nullable, the MES <c>EQP</c> recorded when
/// the demand was accepted, for display beside the sublot on the onboard pickup scan list and unload list only
/// (<c>CV-WORKLIST-EQP-DISPLAY-ONLY</c>, <c>FP-IS-09</c>); neither end may decide anything by it. The item is a closed
/// object, so a <c>6.0.0</c> vehicle refuses the field and this is a major release. Release <c>7.0.0</c>, protocol
/// version 8, schema <c>$id</c> segment <c>agv-full-product/v8</c>, base release <c>protocol-v6.0.0</c>
/// (<c>98f17264</c>). <see cref="ApprovalStatus"/> is the field to read before treating this identity as releasable,
/// and the class keeps the name it had while this was a candidate. The manifest's own <c>status</c> is <c>CONTENT_SNAPSHOT</c>, which is what every manifest says, released
/// or not; approval lives only in an external attestation.
/// </para>
/// <para>
/// <b>Bound by <c>8005-agv-control-server#896</c>, to ship in one package with <c>8005-agv-onboard-hmi#417</c>.</b>
/// Neither end may reach the field alone: there is no negotiation, so a server on this identity and a vehicle on
/// <c>protocol-v6.0.0</c> refuse each other (<c>INCOMPATIBLE_EXACT_IDENTITY_REQUIRED</c>). Both outboxes are drained
/// before the two are replaced together, and the switch is made with the dispatch gate closed and every journey
/// <c>Completed</c>, because the vehicle keys an applied worklist by its <c>worklistRevision</c> and refuses the same
/// revision with different content. Evidence produced while this identity was still a candidate
/// is <c>UNRELEASED_CANDIDATE</c> and does not count towards a release; evidence bound to <c>protocol-v6.0.0</c> stopped
/// being current when the candidate was bound. Flipped to <c>APPROVED_RELEASE</c> on the deployment branch
/// <c>release/v7-20261121</c> after the release, as <c>release/interim-20261107</c> (<c>cb5fb376</c>) did for
/// <c>protocol-v6.0.0</c>.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> is 8, and still monotonic only within one
/// <see cref="ProfileId"/>.</b> <c>WIRE_TO_GATE_MVP 0.3.0</c> and <c>AGV_FULL_PRODUCT 2.0.0</c> both
/// said 3. Nothing may compare identities by that integer alone; the handshake compares the whole
/// release identity, and logs and evidence always write the pair <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that exists.</b> Until 2026-11-21 it named one that did not, and
/// <see cref="ApprovalStatus"/> said <c>SUPERSEDING_CANDIDATE</c> so that the pair told the truth.
/// The staged G3 runner reads the pair: it refuses to run when the tag resolves anywhere but <see cref="RepositoryCommit"/>, and when this status
/// claims a release whose tag is absent.
/// </para>
/// <para>
/// <b>What follows from the status.</b> <c>scripts/New-WireToGateReleaseCandidate.ps1</c> refuses to
/// package a release candidate unless <c>approvalStatus</c> is <c>APPROVED_RELEASE</c>. The release
/// is approved and tagged, the status says <c>APPROVED_RELEASE</c>, and the packager therefore
/// accepts packaging again.
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
    public const int ProtocolVersion = 8;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "7.0.0";
    public const string Tag = "protocol-v7.0.0";
    public const string RepositoryCommit = "a1facf8674b05c67bfbddc14db4a163e8da2782a";
    public const string ManifestSha256 = "44fa0b273810fb2d5326068c03f9647bfd8fdc1c5f01e27a5177b0e0473fe7cb";
    public const string SchemaBundleSha256 = "6361d734f9b1a4f81178c79c0bc18b8287fd747622e60c99073e7fd5a5cf4c88";
    public const string VectorsSha256 = "6295840ec27422dc024a24f6deae9a7379f9b3047a163e903ac862c8b0274eec";
    public const string ApprovalStatus = "APPROVED_RELEASE";
}
