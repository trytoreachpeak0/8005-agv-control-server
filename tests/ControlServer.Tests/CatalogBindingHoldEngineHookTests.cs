using ControlServer.Application;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 引擎在每次目录完整确认之后跑目录变化收敛（control-server#162 的挂接）：一轮里读到绑定站点改名，这一轮结束时该任务类型就有
/// 一条「目录变化」暂停，下一轮的准入读得到它。
/// </summary>
public sealed class CatalogBindingHoldEngineHookTests
{
    [Fact]
    public async Task ARoundThatConfirmsACatalogWithTheBoundGateRenamedLeavesWireToGateHeld()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, "关卡-临时堆放"),
            new RiotMapStation(300, "等待点"));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        TaskTypeStationHold hold = Assert.Single(
            await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
        Assert.Equal(TransportTaskTypes.WireToGate, hold.TaskType);
        Assert.Equal(TaskTypeStationHoldSource.CatalogChange, hold.Source);
        Assert.Equal(CatalogBindingHoldReasons.StationRenamed, hold.ReasonCode);
    }

    [Fact]
    public async Task ARoundOverAnUnchangedCatalogRaisesNoHold()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(
            await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
    }
}
