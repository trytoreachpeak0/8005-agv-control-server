using ControlServer.Application;
using ControlServer.Host.Runtime.CreateGate;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 判「这份目录就是服务端刚确认过的那一份」靠的是与服务端同一个指纹与修订算法（control-server#161）。两边各写了一份，
/// 这里把它们钉在一起：任何一边改了，这里红。
/// </summary>
public sealed class TaskTypeStationCatalogEvidenceTests
{
    [Fact]
    public void FingerprintMatchesTheOneTheGatewayComputesForTheSameCatalog()
    {
        // The same three stations HttpRiotMovementGatewayTests.StrictMapCatalogIsCanonicalAndObservedAfterSdkReadCompletes
        // reads from RIoT, in RIoT's own order, and the hash that test pins.
        string fingerprint = TaskTypeStationCatalogEvidence.Fingerprint(
            25, [new(210, "关卡"), new(12, "N1-3_N1-7"), new(11, "C15-13")]);

        Assert.Equal("8e1df56b366705969098327145588a67612a05b172c8958aa1f0aec64f3e6d30", fingerprint);
    }

    [Theory]
    [InlineData("8e1df56b366705969098327145588a67612a05b172c8958aa1f0aec64f3e6d30")]
    [InlineData("ffffffffffffffff000000000000000000000000000000000000000000000000")]
    [InlineData("0000000000000001ffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void RevisionMatchesTheOneTheServerRecordsOnConfirmation(string contentSha256) =>
        Assert.Equal(
            CatalogAvailabilityAccess.RevisionOf(contentSha256),
            TaskTypeStationCatalogEvidence.RevisionOf(contentSha256));

    [Fact]
    public void ACatalogConfirmedByTheServerInsideItsWindowIsFreshAndAnyOtherIsNot()
    {
        DateTimeOffset now = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);
        RiotMapStationCatalogSnapshot supplied = TaskTypeStationCatalogEvidence.Supplied(25, [new(210, "关卡")], now);
        long revision = TaskTypeStationCatalogEvidence.RevisionOf(supplied.ContentSha256);
        MapStationCatalogAvailability confirmed = new(
            25, ControlServer.Domain.MapStationCatalogState.Fresh, now.AddSeconds(-299), now.AddSeconds(-299), null, revision,
            60, 300, now);

        Assert.Empty(TaskTypeStationCatalogEvidence.JudgeFreshness(25, supplied, confirmed, now));
        Assert.Single(TaskTypeStationCatalogEvidence.JudgeFreshness(
            25, supplied, confirmed with { LastCompleteConfirmationAt = now.AddSeconds(-301) }, now));
        Assert.Single(TaskTypeStationCatalogEvidence.JudgeFreshness(
            25, supplied, confirmed with { CatalogRevision = revision + 1 }, now));
        Assert.Single(TaskTypeStationCatalogEvidence.JudgeFreshness(
            25, supplied, confirmed with { State = ControlServer.Domain.MapStationCatalogState.BuildIncompatible }, now));
        Assert.Single(TaskTypeStationCatalogEvidence.JudgeFreshness(
            25, supplied, confirmed with { ApprovedMaxUnconfirmedSeconds = null }, now));
        Assert.Single(TaskTypeStationCatalogEvidence.JudgeFreshness(25, supplied, null, now));
        Assert.Single(TaskTypeStationCatalogEvidence.JudgeFreshness(25, null, confirmed, now));
    }
}
