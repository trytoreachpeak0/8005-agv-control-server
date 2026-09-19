using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.TaskTypeStations;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 终点按「本轮绑定集快照中本图本任务类型的绑定」解析（control-server#160，规格 5.3，REQ-0334、REQ-0335）：
/// 给出绑定的站点与两个版本号；缺什么就给该任务类型一条原因，不回落到任何默认站点。
/// </summary>
public sealed class BoundFixedTaskStationResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly RiotMapStation Gate = new(210, "关卡");

    private static readonly RiotMapStation Gate2 = new(220, "关卡2");

    private static readonly RiotMapStationCatalogSnapshot Map = new(
        25, Now, new string('c', 64), [new RiotMapStation(12, "N1-1"), Gate, Gate2]);

    private static readonly TaskTypeStationBinding Gate2Binding =
        new(TransportTaskTypes.WireToGate, 220, "关卡2", "SITE-CHECK-GATE-2");

    [Fact]
    public async Task ABoundTaskTypeResolvesToItsBoundStationUnderTheActiveVersions()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await ActivateAsync(fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        (long ruleVersion, long bindingSetVersion) = await ActivateAsync(fixture, [TransportTaskTypes.WireToGate], [Gate2Binding]);

        FixedTaskStationResolution resolution = (await ReadAsync(fixture, Map)).Resolve(TransportTaskTypes.WireToGate);

        Assert.Null(resolution.RefusalReasonCode);
        Assert.Equal(Gate2, resolution.Station);
        Assert.Equal(FixedStationEnd.Destination, resolution.FixedEnd);
        Assert.Equal(ruleVersion, resolution.RuleVersion);
        Assert.Equal(bindingSetVersion, resolution.BindingSetVersion);
        Assert.Equal(2, bindingSetVersion);
    }

    internal static async Task<(long RuleVersion, long BindingSetVersion)> ActivateAsync(
        TaskTypeStationPersistenceFixture fixture,
        IReadOnlyList<string> required,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        int mapId = 25)
    {
        TaskTypeStationRuleVersion rules = (await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token)).Version;
        TaskTypeStationBindingSetVersion set = (await fixture.Bindings.WriteVersionAsync(
            mapId, rules.Version, required, bindings, null, Source, Now, Token)).Version;
        await fixture.Bindings.SetActiveAsync(mapId, set.Version, Now, Token);
        fixture.Context.ChangeTracker.Clear();
        return (rules.Version, set.Version);
    }

    private static Task<IFixedTaskStationView> ReadAsync(
        TaskTypeStationPersistenceFixture fixture,
        RiotMapStationCatalogSnapshot map) =>
        new BoundFixedTaskStationResolver(
                new TaskTypeStationAccess(fixture.Rules, fixture.Bindings, fixture.Holds, fixture.Freezes),
                Options.Create(new JourneyRuntimeOptions { MapId = 25 }))
            .ReadForRoundAsync(map, Token);
}
