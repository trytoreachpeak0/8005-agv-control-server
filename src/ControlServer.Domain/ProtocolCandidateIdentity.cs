namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names the unreleased <c>6.0.0</c> candidate.</b> Every value below is read off
/// <c>8005-agv-protocol</c> commit <c>98f172642937994ed01668d81fe41e69cf089e10</c>, the merge of protocol PR #27
/// (protocol#26, content commit <c>13f52e67</c>) into <c>fp/v2-candidate</c> on 2026-10-21; it is pinned rather than
/// the pull request's head, as <c>protocol-v4.0.0</c> and <c>protocol-v5.0.0</c> pinned their merges. Its content is
/// CP-0021 (<c>8005-agv-program#213</c>): <c>RemainingBasketsSkipRequested</c>/<c>RemainingBasketsSkipResult</c>
/// (a multi-basket load may end with k of N baskets once the control server authorizes it), the
/// <c>OperationResult</c> outcome <c>COMPLETED_REMAINING_SKIPPED</c>, the slot reason code
/// <c>REMAINING_BASKETS_SKIPPED</c>, and the revised REQ-0211: a load with a basket in any target slot is never
/// cancelled (<c>LOAD_CANCELLATION_BASKET_LOADED</c>), and an <c>AUTHORIZED</c> <c>LoadCancellationAuthorization</c>
/// always carries empty <c>slots</c>. Release <c>6.0.0</c>, protocol version 7, schema <c>$id</c> segment
/// <c>agv-full-product/v7</c>, base release <c>protocol-v5.0.0</c> (<c>18d4fb34</c>, released 2026-10-18).
/// <see cref="Tag"/> names <c>protocol-v6.0.0</c>, which does not exist yet: the user decided on 2026-10-21 to release
/// it only after both ends (<c>8005-agv-control-server#830</c>, <c>8005-agv-onboard-hmi#388</c>) and the real rig have
/// passed on it. <see cref="ApprovalStatus"/> therefore says <c>SUPERSEDING_CANDIDATE</c>, so that the pair tells the
/// truth. The manifest's own <c>status</c> is <c>CONTENT_SNAPSHOT</c>, which is what every manifest says, released or
/// not; approval lives only in an external attestation.
/// </para>
/// <para>
/// <b>Bound by <c>8005-agv-control-server#830</c>, to ship in one package with <c>8005-agv-onboard-hmi#388</c>.</b>
/// Neither end may reach the integration branch alone: there is no negotiation, so a server on this identity and a
/// vehicle on <c>protocol-v5.0.0</c> refuse each other. Both outboxes are drained before the two are replaced together.
/// Evidence produced on this identity is <c>UNRELEASED_CANDIDATE</c> and does not count towards a release; evidence
/// bound to <c>protocol-v5.0.0</c> stops being current when this candidate is bound.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> is 7, and still monotonic only within one
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
/// <c>protocol-v6.0.0</c> is released and this identity is rebound to it, the packager refuses.
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
    public const int ProtocolVersion = 7;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "6.0.0";
    public const string Tag = "protocol-v6.0.0";
    public const string RepositoryCommit = "98f172642937994ed01668d81fe41e69cf089e10";
    public const string ManifestSha256 = "a52876be2ddcc9b8cdd1f1e3b7b7ddbe70d31a307f1d3f017d7a1eb95ffd77b1";
    public const string SchemaBundleSha256 = "0dab682174e2d3e4f931b3c0e87c7fa55bd2cb3395356d7845e595d8bfb1c795";
    public const string VectorsSha256 = "e06ca9ea1d4445cd7d70fc190b0cf606a8e33cb7760d848c0270df24e2594c91";
    public const string ApprovalStatus = "SUPERSEDING_CANDIDATE";
}
