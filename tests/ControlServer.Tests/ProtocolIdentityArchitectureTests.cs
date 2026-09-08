using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the protocol release identity this server claims: the nine constants are
/// the vendored manifest's own fields, and the copy of them that ships in <c>appsettings.json</c>
/// says the same thing.
/// </summary>
/// <remarks>
/// <para>
/// Before this class, nothing checked any of it. Switching all nine constants from
/// <c>protocol-v0.1.1</c> to the v2 candidate left the suite at 569 passed / 0 failed -- the
/// identity is on every envelope the server writes and in the handshake it validates the peer
/// against, and not one test read it. A typo in a SHA-256 would have shipped, and the peer would
/// have been rejected with an expected identity nobody could match.
/// </para>
/// <para>
/// <b>The pin is the constant, not a second hash.</b>
/// <see cref="ProtocolCandidateIdentity.ManifestSha256"/> is by definition the SHA-256 of
/// <c>manifest/release.json</c>, so hashing the vendored copy against it needs no new approved
/// digest -- the constant the server puts on the wire is what makes the copy honest, and the copy
/// is what makes the constant checkable. The same file then answers every other identity field,
/// which is why the rest of this class compares against a parse rather than a literal.
/// </para>
/// <para>
/// Refreshing the copy is <c>vendor/8005-agv-protocol/README.md</c>: whole-file <c>cp</c>, never a
/// hand edit.
/// </para>
/// </remarks>
public sealed class ProtocolIdentityArchitectureTests
{
    [Fact]
    public void TheVendoredManifestIsTheProtocolManifestByteForByte()
    {
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ManifestPath())))
            .ToLowerInvariant();

        Assert.Equal(ProtocolCandidateIdentity.ManifestSha256, actual);
    }

    /// <summary>
    /// Every identity constant the manifest also states, compared field by field against it.
    /// </summary>
    /// <remarks>
    /// <see cref="ProtocolCandidateIdentity.RepositoryCommit"/> is not here and cannot be: a commit
    /// hash is not a fact the file it commits can carry. It is pinned transitively instead -- the
    /// manifest is bound byte for byte by the test above, and that byte sequence exists at exactly
    /// one commit in the protocol repository.
    /// </remarks>
    [Fact]
    public void TheIdentityConstantsAreTheVendoredManifestsOwnFields()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(ManifestPath()));
        JsonElement root = manifest.RootElement;

        Assert.Equal("8005-agv-protocol", root.GetProperty("repository").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.ReleaseVersion,
            root.GetProperty("releaseVersion").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.ProtocolVersion,
            root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(ProtocolCandidateIdentity.ProfileId, root.GetProperty("profileId").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.SchemaBundleSha256,
            root.GetProperty("schemaBundleSha256").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.VectorsSha256,
            root.GetProperty("vectorsSha256").GetString());
    }

    /// <summary>
    /// The tag satisfies what <c>$defs/ProtocolReleaseIdentity</c> requires of it on the wire.
    /// </summary>
    /// <remarks>
    /// The schema makes <c>tag</c> required, <c>minLength: 1</c> and <c>^protocol-v</c>, and forbids
    /// additional properties; <c>SessionHello</c>, <c>SessionAccepted</c> and <c>SessionRejected</c>
    /// all carry it. Neither end validates against the schemas at runtime, so an empty or misshapen
    /// tag would travel unremarked -- the same silence that let seven reason codes outside the
    /// registry survive eight green gates. This is the check that would have spoken.
    /// </remarks>
    [Fact]
    public void TheTagIsWellFormedForTheReleaseVersionItNames()
    {
        Assert.StartsWith("protocol-v", ProtocolCandidateIdentity.Tag, StringComparison.Ordinal);
        Assert.Equal(
            $"protocol-v{ProtocolCandidateIdentity.ReleaseVersion}",
            ProtocolCandidateIdentity.Tag);
    }

    /// <summary>
    /// While the identity names a candidate, packaging a release candidate over it stays refused.
    /// </summary>
    /// <remarks>
    /// <c>scripts/New-WireToGateReleaseCandidate.ps1</c> gates on <c>APPROVED_RELEASE</c> and this
    /// is the other half of that gate. Section 6.6 of the full-product scope specification lists
    /// what protocol v2 still owes -- item 6 is two product owners' external attestation plus the
    /// annotated tag <see cref="ProtocolCandidateIdentity.Tag"/> names, and <c>git tag --list</c> in
    /// the protocol repository does not show that tag. When that lands, this test is where the
    /// change has to be made deliberately rather than noticed afterwards.
    /// </remarks>
    [Fact]
    public void ThisIdentityIsACandidateAndDoesNotClaimAnApprovedRelease()
    {
        Assert.NotEqual("APPROVED_RELEASE", ProtocolCandidateIdentity.ApprovalStatus);
        Assert.Equal("SUPERSEDING_CANDIDATE", ProtocolCandidateIdentity.ApprovalStatus);
    }

    /// <summary>
    /// The shipped settings say exactly what the constants say.
    /// </summary>
    /// <remarks>
    /// <c>appsettings.json</c> carries the identity a second time because the release-candidate
    /// packager and <c>scripts/test-wire-to-gate.ps1</c> read the published settings file rather
    /// than this assembly. Two copies of nine values kept by hand is how a gate certifies a protocol
    /// nobody is running; this is what keeps the second copy from being a second truth.
    /// </remarks>
    [Fact]
    public void TheShippedSettingsMirrorTheIdentityConstants()
    {
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json")));
        JsonElement mirror = settings.RootElement.GetProperty("ProtocolCandidate");

        Assert.Equal(
            ProtocolCandidateIdentity.ReleaseVersion,
            mirror.GetProperty("releaseVersion").GetString());
        Assert.Equal(ProtocolCandidateIdentity.Tag, mirror.GetProperty("tag").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.RepositoryCommit,
            mirror.GetProperty("repositoryCommit").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.ManifestSha256,
            mirror.GetProperty("manifestSha256").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.SchemaBundleSha256,
            mirror.GetProperty("schemaBundleSha256").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.VectorsSha256,
            mirror.GetProperty("vectorsSha256").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.ProtocolVersion,
            mirror.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(ProtocolCandidateIdentity.ProfileId, mirror.GetProperty("profileId").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.ApprovalStatus,
            mirror.GetProperty("approvalStatus").GetString());
    }

    internal static string ManifestPath() => Path.Combine(
        RepositoryRoot(), "vendor", "8005-agv-protocol", "manifest", "release.json");

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ControlServer repository root.");
    }
}
