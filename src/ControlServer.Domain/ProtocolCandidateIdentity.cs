namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names the <c>3.0.0</c> candidate, and the candidate is not an approved release.</b> Every
/// value below is read off <c>8005-agv-protocol</c> commit
/// <c>3f091cb2eae7c58cec54a95dd9389c9180bc7b4c</c> (branch
/// <c>batch-p3/protocol-v3.0.0-candidate</c>), the candidate frozen by <c>8005-agv-program#151</c> on
/// 2026-09-29 after G1 passed on it. It supersedes the released <c>protocol-v2.0.0</c>
/// (<c>86575456</c>) with the changes of the protocol backlog <c>8005-agv-program#115</c> that
/// batch 8 took on. <see cref="ApprovalStatus"/> says <c>SUPERSEDING_CANDIDATE</c> rather than
/// <c>APPROVED_RELEASE</c> for exactly that reason, and it is the field to read before treating this
/// identity as releasable. The manifest's own <c>status</c> is <c>CONTENT_SNAPSHOT</c>, which is
/// what every manifest says, released or not; approval lives only in the external attestation.
/// </para>
/// <para>
/// <b>This identity lives only on the batch branch <c>batch-p3/v3</c>.</b> The integration branch
/// stays on <c>protocol-v2.0.0</c> until the candidate is released on a real vehicle's evidence
/// (<c>8005-agv-program#152</c>); the exit ticket <c>8005-agv-control-server#393</c> then binds the
/// released identity. Evidence produced against this candidate is <c>UNRELEASED_CANDIDATE</c> and
/// does not count towards a batch exit.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> is 4, and still monotonic only within one
/// <see cref="ProfileId"/>.</b> <c>WIRE_TO_GATE_MVP 0.3.0</c> and <c>AGV_FULL_PRODUCT 2.0.0</c> both
/// said 3. Nothing may compare identities by that integer alone; the handshake compares the whole
/// release identity, and logs and evidence always write the pair <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that does not exist yet.</b> <c>8005-agv-program#152</c> cuts
/// <c>protocol-v3.0.0</c> on this same commit once the release is authorized. The constant still
/// carries the name because <c>$defs/ProtocolReleaseIdentity</c> requires <c>tag</c>, constrains it
/// to <c>minLength: 1</c> and <c>^protocol-v</c>, and forbids additional properties -- an empty
/// string would put a schema-invalid value on <c>SessionHello</c>, <c>SessionAccepted</c> and
/// <c>SessionRejected</c>. The pair is what tells the truth: this build targets
/// <c>protocol-v3.0.0</c>, and that release is not approved. The staged G3 runner reads the pair: it
/// refuses to run when the tag resolves anywhere but <see cref="RepositoryCommit"/>, and when this
/// status claims a release whose tag is absent.
/// </para>
/// <para>
/// <b>What follows from the status.</b> <c>scripts/New-WireToGateReleaseCandidate.ps1</c> refuses to
/// package a release candidate unless <c>approvalStatus</c> is <c>APPROVED_RELEASE</c>, so it
/// refuses on this branch. That is the intended consequence rather than a regression to work around:
/// the vehicle deployment package is built from the integration branch, which keeps its released
/// identity.
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
    public const string ApprovalStatus = "SUPERSEDING_CANDIDATE";
}
