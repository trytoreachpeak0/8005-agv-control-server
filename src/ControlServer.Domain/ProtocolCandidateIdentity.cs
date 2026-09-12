namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names the v2 candidate, and the candidate is not an approved release.</b> Every value
/// below is read off <c>8005-agv-protocol</c> commit
/// <c>16e2567a7033883f00fc999f7fa08f954dd13a26</c> (branch <c>fp/v2-candidate</c>), the candidate
/// G1 passed on 2026-09-12 once the single-owner release rule was carried over.
/// <see cref="ApprovalStatus"/> says <c>SUPERSEDING_CANDIDATE</c> rather than
/// <c>APPROVED_RELEASE</c> for exactly that reason, and it is the field to read before treating
/// this identity as releasable.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that does not exist yet.</b> Section 6.6 of the full-product
/// scope specification lists what a <c>ProtocolRelease</c> still needs, and item 6 is two product
/// owners' external attestation plus the annotated tag <c>protocol-v1.0.0</c>; neither has
/// happened, and <c>git tag --list</c> in the protocol repository shows <c>protocol-v0.1.0</c>,
/// <c>protocol-v0.1.1</c> and <c>protocol-v0.2.0</c> and nothing else. The constant still carries
/// the name because <c>$defs/ProtocolReleaseIdentity</c> requires <c>tag</c>, constrains it to
/// <c>minLength: 1</c> and <c>^protocol-v</c>, and forbids additional properties -- an empty string
/// would put a schema-invalid value on <c>SessionHello</c>, <c>SessionAccepted</c> and
/// <c>SessionRejected</c>, and neither end validates against the schemas at runtime, so nothing
/// would catch it. The pair is what tells the truth: this build targets <c>protocol-v1.0.0</c>, and
/// that release is not approved.
/// </para>
/// <para>
/// <b>What follows from the status.</b> <c>scripts/New-WireToGateReleaseCandidate.ps1</c> refuses to
/// package a release candidate unless <c>approvalStatus</c> is <c>APPROVED_RELEASE</c>, so it now
/// refuses. That is the intended consequence rather than a regression to work around: a release
/// candidate cut against an unapproved protocol candidate is the thing section 6.6 exists to
/// prevent.
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
    public const int ProtocolVersion = 2;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "1.0.0";
    public const string Tag = "protocol-v1.0.0";
    public const string RepositoryCommit = "16e2567a7033883f00fc999f7fa08f954dd13a26";
    public const string ManifestSha256 = "25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0";
    public const string SchemaBundleSha256 = "225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff";
    public const string VectorsSha256 = "51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df";
    public const string ApprovalStatus = "SUPERSEDING_CANDIDATE";
}
