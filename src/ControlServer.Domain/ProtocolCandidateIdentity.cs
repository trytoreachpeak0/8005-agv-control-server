namespace ControlServer.Domain;

public static class ProtocolCandidateIdentity
{
    public const int ProtocolVersion = 3;
    public const string ProfileId = "WIRE_TO_GATE_MVP";
    public const string ReleaseVersion = "0.3.0";

    /// <summary>
    /// Pinned to the released <c>protocol-v0.3.0</c>: approved on 2026-09-08 by the single product
    /// owner, whose attestation is published as a release asset and stays out of git. The class name
    /// still says "candidate" because every call site is written against it; what it holds now is a
    /// released identity, and <see cref="ApprovalStatus"/> is what says so.
    /// </summary>
    public const string Tag = "protocol-v0.3.0";
    public const string RepositoryCommit = "345c53c58517968192c87c3e7777ed08ddb48726";
    public const string ManifestSha256 = "b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138";
    public const string SchemaBundleSha256 = "68bfd531c4b9c08bc80f6d9c5a67264891efa200acdb154eb18e1d083bf4ed98";
    public const string VectorsSha256 = "bd272b63a1d0663d61c4a38d6e8633d7e7d4f7b561a7915c3df51c7a93bd4576";

    /// <summary>
    /// The peer compares the whole identity, so a mismatch shows up as a refused session rather
    /// than a silent divergence -- the plaintext era has no negotiation and no downgrade.
    /// </summary>
    /// <remarks>
    /// <c>New-WireToGateReleaseCandidate.ps1</c> gates on this too, but it reads the copy in
    /// <c>appsettings.json</c> rather than this constant, and until 2026-09-09 nothing checked that
    /// the two agreed. They had drifted a whole release apart -- the settings still said 0.1.1 while
    /// this said 0.2.0 -- so the RC gate would have passed a package whose server then refused every
    /// 0.1.1 vehicle on the wire. <c>ProtocolIdentityConfigurationTests</c> is what keeps them in
    /// step now.
    /// </remarks>
    public const string ApprovalStatus = "APPROVED_RELEASE";
}
