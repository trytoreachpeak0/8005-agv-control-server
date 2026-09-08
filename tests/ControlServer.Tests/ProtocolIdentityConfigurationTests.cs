using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// The pinned protocol identity is written down twice: as compile-time constants the wire code
/// actually uses, and as a <c>ProtocolCandidate</c> section in the Host's <c>appsettings.json</c>.
/// Nothing read the second one at runtime and nothing compared them, so they drifted -- on
/// 2026-09-09 the settings still named <c>protocol-v0.1.1</c> while the constants had moved to the
/// 0.2.0 candidate.
/// </summary>
/// <remarks>
/// That gap was not cosmetic. <c>New-WireToGateReleaseCandidate.ps1</c> refuses to cut a package
/// whose <c>ProtocolCandidate.approvalStatus</c> is not <c>APPROVED_RELEASE</c>, and it reads the
/// settings copy -- so the stale 0.1.1 section would have waved through a package whose server then
/// refused every vehicle on the wire, the plaintext era having neither negotiation nor downgrade.
/// The RC gate is only as good as the agreement asserted here.
/// </remarks>
public sealed class ProtocolIdentityConfigurationTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void TheSettingsProtocolIdentityMatchesTheCompiledOne()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"Expected Host appsettings at '{path}'.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement section = document.RootElement.GetProperty("ProtocolCandidate");

        Assert.Equal(ProtocolCandidateIdentity.ReleaseVersion, section.GetProperty("releaseVersion").GetString());
        Assert.Equal(ProtocolCandidateIdentity.Tag, section.GetProperty("tag").GetString());
        Assert.Equal(ProtocolCandidateIdentity.RepositoryCommit, section.GetProperty("repositoryCommit").GetString());
        Assert.Equal(ProtocolCandidateIdentity.ManifestSha256, section.GetProperty("manifestSha256").GetString());
        Assert.Equal(
            ProtocolCandidateIdentity.SchemaBundleSha256, section.GetProperty("schemaBundleSha256").GetString());
        Assert.Equal(ProtocolCandidateIdentity.VectorsSha256, section.GetProperty("vectorsSha256").GetString());
        Assert.Equal(ProtocolCandidateIdentity.ProfileId, section.GetProperty("profileId").GetString());
        Assert.Equal(ProtocolCandidateIdentity.ProtocolVersion, section.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(ProtocolCandidateIdentity.ApprovalStatus, section.GetProperty("approvalStatus").GetString());
    }

    /// <summary>
    /// A released pin names its tag. The candidate era wrote "(unreleased candidate 0.2.0)" there
    /// on purpose, so the shape of this string is what separates "built against a signed release"
    /// from "built against something nobody approved" -- and the RC gate trusts the status beside it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void AnApprovedReleasePinNamesItsTag()
    {
        Assert.Equal("APPROVED_RELEASE", ProtocolCandidateIdentity.ApprovalStatus);
        Assert.Equal(
            $"protocol-v{ProtocolCandidateIdentity.ReleaseVersion}", ProtocolCandidateIdentity.Tag);
        Assert.Matches("^[0-9a-f]{40}$", ProtocolCandidateIdentity.RepositoryCommit);
        Assert.Matches("^[0-9a-f]{64}$", ProtocolCandidateIdentity.ManifestSha256);
    }
}
