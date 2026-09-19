using ControlServer.Application;
using ControlServer.Host.Runtime.TaskTypeStations;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 目录变化按稳定身份分类（REQ-0341、REQ-0342，control-server#162）：基线是绑定记下的 Station id 与名称，不是上一份目录。
/// </summary>
public sealed class CatalogBindingChangeClassifierTests
{
    private static RiotMapStationCatalogSnapshot Catalog(params (int Id, string Name)[] stations) =>
        new(25, Now, "sha", [.. stations.Select(station => new RiotMapStation(station.Id, station.Name))]);

    [Fact]
    public void TheSameIdUnderTheSameNameIsNoChange()
    {
        CatalogBindingChange change = Assert.Single(CatalogBindingChangeClassifier.Classify(
            [GateBinding], Catalog((210, "关卡"), (211, "B-WB-01"))));

        Assert.Equal(CatalogBindingChangeKind.Unchanged, change.Kind);
        Assert.Null(change.HoldReasonCode);
    }

    [Fact]
    public void TheSameIdUnderAnotherNameIsARenameThatNeedsASiteReview()
    {
        CatalogBindingChange change = Assert.Single(CatalogBindingChangeClassifier.Classify(
            [GateBinding], Catalog((210, "关卡-临时堆放"))));

        Assert.Equal(CatalogBindingChangeKind.Renamed, change.Kind);
        Assert.Equal(CatalogBindingHoldReasons.StationRenamed, change.HoldReasonCode);
        Assert.Equal(TransportTaskTypes.WireToGate, change.TaskType);
        Assert.Equal(210, change.StationRiotId);
        Assert.Equal("关卡", change.BoundStationName);
        Assert.Equal("关卡-临时堆放", change.CurrentStationName);
    }

    [Fact]
    public void ABoundIdMissingFromTheCatalogIsRemovedAndInvalidatesTheBinding()
    {
        CatalogBindingChange change = Assert.Single(CatalogBindingChangeClassifier.Classify(
            [GateBinding], Catalog((211, "B-WB-01"))));

        Assert.Equal(CatalogBindingChangeKind.Removed, change.Kind);
        Assert.Equal(CatalogBindingHoldReasons.StationNotInCatalog, change.HoldReasonCode);
        Assert.Null(change.CurrentStationName);
        Assert.Empty(change.SameNameStationIds);
    }

    [Fact]
    public void ANewIdCarryingTheOldNameIsANewStationAndIsNeverReboundByName()
    {
        CatalogBindingChange change = Assert.Single(CatalogBindingChangeClassifier.Classify(
            [GateBinding], Catalog((230, "关卡"), (211, "B-WB-01"))));

        Assert.Equal(CatalogBindingChangeKind.IdReplaced, change.Kind);
        Assert.Equal(CatalogBindingHoldReasons.StationNotInCatalog, change.HoldReasonCode);
        // The binding keeps naming 210; 230 is only reported, never taken as the new binding.
        Assert.Equal(210, change.StationRiotId);
        Assert.Null(change.CurrentStationName);
        Assert.Equal([230], change.SameNameStationIds);
    }

    [Fact]
    public void EachBindingIsJudgedOnItsOwnStation()
    {
        IReadOnlyList<CatalogBindingChange> changes = CatalogBindingChangeClassifier.Classify(
            [GateBinding, StagingBinding], Catalog((210, "关卡"), (305, "派工待送取货-改")));

        Assert.Equal(
            [
                (TransportTaskTypes.StagingToWire, CatalogBindingChangeKind.Renamed),
                (TransportTaskTypes.WireToGate, CatalogBindingChangeKind.Unchanged)
            ],
            changes.Select(change => (change.TaskType, change.Kind)));
    }
}
