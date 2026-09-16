namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names the <c>2.0.0</c> candidate, and the candidate is not an approved release.</b> Every
/// value below is read off <c>8005-agv-protocol</c> commit
/// <c>86575456c847041515b7b75e8851a00e0d939804</c> (branch <c>fp/v2-candidate</c>), the candidate
/// frozen by <c>8005-agv-program#96</c> on 2026-09-16 after G1 passed on it. It supersedes the
/// released <c>protocol-v1.0.0</c> (<c>9f22db8</c>) with the seven field changes of section 6.3 of
/// the full-product scope specification. <see cref="ApprovalStatus"/> says
/// <c>SUPERSEDING_CANDIDATE</c> rather than <c>APPROVED_RELEASE</c> for exactly that reason, and it
/// is the field to read before treating this identity as releasable.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> stays 3 against <c>protocol-v0.3.0</c>'s 3.</b> The integer is
/// monotonic only within one <see cref="ProfileId"/>: <c>WIRE_TO_GATE_MVP 0.3.0</c> and
/// <c>AGV_FULL_PRODUCT 2.0.0</c> both say 3. Nothing may compare identities by that integer alone;
/// the handshake compares the whole release identity, and logs and evidence always write the pair
/// <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that does not exist yet.</b> <c>8005-agv-program#97</c> cuts
/// <c>protocol-v2.0.0</c> on this same commit once both ends pass L1 and development-mode G2 on the
/// candidate. The constant still carries the name because <c>$defs/ProtocolReleaseIdentity</c>
/// requires <c>tag</c>, constrains it to <c>minLength: 1</c> and <c>^protocol-v</c>, and forbids
/// additional properties -- an empty string would put a schema-invalid value on
/// <c>SessionHello</c>, <c>SessionAccepted</c> and <c>SessionRejected</c>. The pair is what tells the
/// truth: this build targets <c>protocol-v2.0.0</c>, and that release is not approved. Binding the
/// released identity afterwards is <c>8005-agv-control-server#89</c>.
/// </para>
/// <para>
/// <b>What follows from the status.</b> <c>scripts/New-WireToGateReleaseCandidate.ps1</c> refuses to
/// package a release candidate unless <c>approvalStatus</c> is <c>APPROVED_RELEASE</c>, so it now
/// refuses again. That is the intended consequence rather than a regression to work around.
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
    public const int ProtocolVersion = 3;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "2.0.0";
    public const string Tag = "protocol-v2.0.0";
    public const string RepositoryCommit = "86575456c847041515b7b75e8851a00e0d939804";
    public const string ManifestSha256 = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";
    public const string SchemaBundleSha256 = "9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221";
    public const string VectorsSha256 = "391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a";
    public const string ApprovalStatus = "SUPERSEDING_CANDIDATE";
}
