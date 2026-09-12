namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names <c>protocol-v1.0.0</c>, an approved release.</b> Every value below is read off
/// <c>8005-agv-protocol</c> commit <c>9f22db825d52ad86c1d803bd0c1925dcc58d6793</c>, the commit the
/// annotated tag <c>protocol-v1.0.0</c> points at. It was released on 2026-09-12 with one approval
/// in its external attestation (the GitHub Release Asset <c>release-approval.json</c>, SHA-256
/// <c>545fba1c6d67be0cf2b834142340001e36ab9b3245ec1fc1eabaf9b28ccf22e0</c>), given by an AI agent the
/// product owner authorized, as the protocol's governance allows since that day.
/// <see cref="ApprovalStatus"/> is the field to read before treating this identity as releasable.
/// The class keeps the name it had while this was a candidate.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that exists.</b> Until 2026-09-12 it named one that did not, and
/// <see cref="ApprovalStatus"/> said <c>SUPERSEDING_CANDIDATE</c> so that the pair told the truth. The
/// staged G3 runner still refuses to run when the tag points anywhere but
/// <see cref="RepositoryCommit"/>, and now also when this status claims a release whose tag is absent.
/// </para>
/// <para>
/// <b>What follows from the status.</b> <c>scripts/New-WireToGateReleaseCandidate.ps1</c> refuses to
/// package a release candidate unless <c>approvalStatus</c> is <c>APPROVED_RELEASE</c>. With the
/// release approved and tagged it no longer refuses on that ground.
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
    public const string RepositoryCommit = "9f22db825d52ad86c1d803bd0c1925dcc58d6793";
    public const string ManifestSha256 = "a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23";
    public const string SchemaBundleSha256 = "885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685";
    public const string VectorsSha256 = "51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df";
    public const string ApprovalStatus = "APPROVED_RELEASE";
}
