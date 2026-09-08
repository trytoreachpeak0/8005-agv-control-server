namespace ControlServer.Domain;

public static class ProtocolCandidateIdentity
{
    public const int ProtocolVersion = 2;
    public const string ProfileId = "WIRE_TO_GATE_MVP";
    public const string ReleaseVersion = "0.2.0";

    /// <summary>
    /// There is no tag yet: 0.2.0 is a frozen content candidate awaiting the two-owner approval
    /// attestation, and a tag cannot be invented before it is signed. The commit below is what the
    /// identity is actually pinned to, and it is exact -- the candidate content is frozen at
    /// 952b49c in 8005-agv-protocol.
    /// </summary>
    public const string Tag = "(unreleased candidate 0.2.0)";
    public const string RepositoryCommit = "952b49c7e585662dcb5632a7cdc1a5cc11af4ee7";
    public const string ManifestSha256 = "2ea94f1b97a73725e9d4a66404a6d26a0cc0cc4516f46cc978c77942c0e482fa";
    public const string SchemaBundleSha256 = "f4c39f55ed8a3319259ee277df06359f1e51c8b9997271baf61d071e70e15984";
    public const string VectorsSha256 = "bd272b63a1d0663d61c4a38d6e8633d7e7d4f7b561a7915c3df51c7a93bd4576";

    /// <summary>
    /// Not <c>APPROVED_RELEASE</c> until both product owners sign. The server states what it is
    /// built against rather than claiming an approval that does not exist; the peer compares the
    /// whole identity, so a mismatch shows up as a refused session instead of a silent divergence.
    /// </summary>
    public const string ApprovalStatus = "PENDING_CANDIDATE";
}
