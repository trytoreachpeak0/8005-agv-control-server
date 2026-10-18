namespace ControlServer.Domain;

/// <summary>
/// The protocol release this server is built against, vendored as constants because the server does
/// not read the protocol's files at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This names <c>protocol-v5.0.0</c>, an approved release.</b> Every value below is read off
/// <c>8005-agv-protocol</c> commit <c>18d4fb3495033624cab412a7ee784f675dfe4cc2</c>, the commit the annotated tag
/// <c>protocol-v5.0.0</c> (object <c>7b108ccb</c>) points at. It was released unchanged on 2026-10-18 (tracked by
/// <c>8005-agv-control-server#767</c> and <c>8005-agv-program#195</c>; the released manifest is byte-identical to the
/// candidate's), with one approval in the external attestation (the GitHub Release Asset <c>release-approval.json</c>,
/// SHA-256 <c>31d4d7323cfcf9e922ca65214bb25a978b091771711727fd918f7dc89dbffca5</c>) given by an AI agent the product
/// owner authorized for that release. As a candidate it was the merge of protocol#25 (for <c>8005-agv-control-server#767</c>) into
/// <c>fp/v2-candidate</c> on 2026-10-18, whose tree is the candidate <c>ede2060f</c>'s byte for byte; it is pinned
/// rather than the pull request's head, as <c>protocol-v4.0.0</c> pinned its merge <c>3fb214e5</c>. That third
/// revision corrected <c>docs/wire-notes.md</c> only, so of the vendored files only the manifest changed: the remaining
/// plan rule holds for every plan except a clearance move (<c>CLEARING_MAINTENANCE</c>), which keeps the <c>COMPLETED</c> charger leg it left, and
/// departure condition (4) allows a server to count every basket no longer loaded as having left -- which is what
/// <c>JourneyRuntimeEngine.StagingDeparture</c> does. The second revision, <c>13eca82a</c> (protocol#24 for protocol#23),
/// changed vectors and wording only, no schema, message or error code: a machine-pickup departure request whose
/// worklist revision is stale but carries no material change is accepted
/// (<c>CV-CURRENT-STOP-DEPARTURE-STALE-REVISION-ACCEPTED</c>, behaviour from <c>8005-agv-control-server#742</c>), and the
/// journey plan is described as the remaining plan. The first revision, <c>4e9f3df8</c> (protocol#22 for protocol#21),
/// appends <c>WIRE_TO_FULL_INSPECTION</c> to the closed
/// enum <c>TransportTaskType</c> and otherwise changes nothing but the identity: release <c>5.0.0</c>,
/// protocol version 6, schema <c>$id</c> segment <c>agv-full-product/v6</c>, base release
/// <c>protocol-v4.0.0</c> (<c>3fb214e5</c>, released 2026-10-16, <c>8005-agv-program#184</c>). Appending to
/// a closed enum is breaking -- a v4 peer refuses the new value as <c>PROTOCOL_SCHEMA_INVALID</c> -- which is
/// why the major version moved. <see cref="ApprovalStatus"/> is the field to read before treating this identity as
/// releasable, and the class keeps the name it had while this was a candidate. The
/// manifest's own <c>status</c> is <c>CONTENT_SNAPSHOT</c>, which is what every manifest says, released or
/// not; approval lives only in an external attestation.
/// </para>
/// <para>
/// <b>Bound by <c>8005-agv-control-server#737</c>, to ship in one package with
/// <c>8005-agv-onboard-hmi#360</c>; repinned to the second revision by <c>8005-agv-control-server#748</c> together with
/// <c>8005-agv-onboard-hmi#362</c>, and to the third by <c>8005-agv-control-server#767</c>, again with the onboard HMI in the same package.</b> Neither end may reach the integration branch alone: there is no
/// negotiation, so a server on this identity and a vehicle on <c>protocol-v4.0.0</c> refuse each other.
/// Evidence produced while this identity was still a candidate is <c>UNRELEASED_CANDIDATE</c> and does not count
/// towards a release; evidence bound to <c>protocol-v4.0.0</c> stopped being current when the candidate was bound.
/// Flipped to <c>APPROVED_RELEASE</c> by <c>8005-agv-control-server#767</c> after the release, as
/// <c>8005-agv-control-server#735</c> did for <c>protocol-v4.0.0</c>.
/// </para>
/// <para>
/// <b><see cref="ProtocolVersion"/> is 6, and still monotonic only within one
/// <see cref="ProfileId"/>.</b> <c>WIRE_TO_GATE_MVP 0.3.0</c> and <c>AGV_FULL_PRODUCT 2.0.0</c> both
/// said 3. Nothing may compare identities by that integer alone; the handshake compares the whole
/// release identity, and logs and evidence always write the pair <c>(profileId, protocolVersion)</c>.
/// </para>
/// <para>
/// <b><see cref="Tag"/> names a tag that exists.</b> Until 2026-10-18 it named one that did not, and
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
    public const int ProtocolVersion = 6;
    public const string ProfileId = "AGV_FULL_PRODUCT";
    public const string ReleaseVersion = "5.0.0";
    public const string Tag = "protocol-v5.0.0";
    public const string RepositoryCommit = "18d4fb3495033624cab412a7ee784f675dfe4cc2";
    public const string ManifestSha256 = "dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa";
    public const string SchemaBundleSha256 = "d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933";
    public const string VectorsSha256 = "291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a";
    public const string ApprovalStatus = "APPROVED_RELEASE";
}
