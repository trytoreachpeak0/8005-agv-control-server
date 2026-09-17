namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names <c>protocol-v2.0.0</c>, an approved release.</b> Every value below is read off
/// <c>8005-agv-protocol</c> commit <c>86575456c847041515b7b75e8851a00e0d939804</c>, the commit the
/// annotated tag <c>protocol-v2.0.0</c> points at. It was frozen as the <c>2.0.0</c> candidate by
/// <c>8005-agv-program#96</c> on 2026-09-16 after G1 passed on it, and released by
/// <c>8005-agv-program#97</c> the same day, with one approval in the external attestation (the
/// GitHub Release Asset <c>release-approval.json</c>, SHA-256
/// <c>db745d0dffd6fa4c206003d7d4b49d771327cc6fcc6276ff19de97c01e3631f6</c>) given by an AI agent
/// the product owner authorized, which the protocol's governance has allowed since 2026-09-12. It
/// supersedes the released <c>protocol-v1.0.0</c> (<c>9f22db8</c>) with the seven field changes of
/// section 6.3 of the full-product scope specification. <see cref="ApprovalStatus"/> is the field
/// to read before treating this identity as releasable, and the class keeps the name it had while
/// this was a candidate.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> stays 3 against <c>protocol-v0.3.0</c>'s 3.</b> The integer is
/// monotonic only within one <see cref="ProfileId"/>: <c>WIRE_TO_GATE_MVP 0.3.0</c> and
/// <c>AGV_FULL_PRODUCT 2.0.0</c> both say 3. Nothing may compare identities by that integer alone;
/// the handshake compares the whole release identity, and logs and evidence always write the pair
/// <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that exists.</b> Until 2026-09-16 it named one that did not, and
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
    public const int ProtocolVersion = 3;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "2.0.0";
    public const string Tag = "protocol-v2.0.0";
    public const string RepositoryCommit = "86575456c847041515b7b75e8851a00e0d939804";
    public const string ManifestSha256 = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";
    public const string SchemaBundleSha256 = "9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221";
    public const string VectorsSha256 = "391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a";
    public const string ApprovalStatus = "APPROVED_RELEASE";
}
