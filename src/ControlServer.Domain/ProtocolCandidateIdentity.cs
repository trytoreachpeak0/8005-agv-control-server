namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names <c>protocol-v3.0.0</c>, an approved release.</b> Every value below is read off
/// <c>8005-agv-protocol</c> commit <c>3f091cb2eae7c58cec54a95dd9389c9180bc7b4c</c>, the commit the
/// annotated tag <c>protocol-v3.0.0</c> (object <c>e08c362e</c>) points at. It was frozen as the
/// <c>3.0.0</c> candidate by <c>8005-agv-program#151</c> on 2026-09-29 after G1 passed on it, and
/// released unchanged by <c>8005-agv-program#152</c> on 2026-10-09, with one approval in the
/// external attestation (the GitHub Release Asset <c>release-approval.json</c>, SHA-256
/// <c>64f4036b124eb2393e5a2f747457d3083677036e59347a83f70d897245e63a53</c>) given by an AI agent
/// the product owner authorized for that release. It supersedes the released
/// <c>protocol-v2.0.0</c> (<c>86575456</c>) with the changes of the protocol backlog
/// <c>8005-agv-program#115</c> that batch 8 took on. <see cref="ApprovalStatus"/> is the field to
/// read before treating this identity as releasable, and the class keeps the name it had while this
/// was a candidate. The manifest's own <c>status</c> is <c>CONTENT_SNAPSHOT</c>, which is what
/// every manifest says, released or not; approval lives only in the external attestation.
/// </para>
/// <para>
/// <b>The exit ticket <c>8005-agv-control-server#393</c> bound the release and merged the batch
/// branch <c>batch-p3/v3</c> back into the integration branch.</b> Evidence produced while this
/// identity was still a candidate is <c>UNRELEASED_CANDIDATE</c> and does not count towards a batch
/// exit; evidence bound to <c>protocol-v2.0.0</c> stopped being current when <c>3.0.0</c> was
/// released.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> is 4, and still monotonic only within one
/// <see cref="ProfileId"/>.</b> <c>WIRE_TO_GATE_MVP 0.3.0</c> and <c>AGV_FULL_PRODUCT 2.0.0</c> both
/// said 3. Nothing may compare identities by that integer alone; the handshake compares the whole
/// release identity, and logs and evidence always write the pair <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that exists.</b> Until 2026-10-09 it named one that did not, and
/// <see cref="ApprovalStatus"/> said <c>SUPERSEDING_CANDIDATE</c> so that the pair told the truth.
/// The staged G3 runner reads the pair: it refuses to run when the tag resolves anywhere but
/// <see cref="RepositoryCommit"/>, and when this status claims a release whose tag is absent.
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
    public const int ProtocolVersion = 4;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "3.0.0";
    public const string Tag = "protocol-v3.0.0";
    public const string RepositoryCommit = "3f091cb2eae7c58cec54a95dd9389c9180bc7b4c";
    public const string ManifestSha256 = "d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e";
    public const string SchemaBundleSha256 = "e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43";
    public const string VectorsSha256 = "be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e";
    public const string ApprovalStatus = "APPROVED_RELEASE";
}
