using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    [Trait("IntegrationSlice", "FP-IS-10")]
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

    /// <summary>
    /// 缺绑定的三种样子都是 <c>TASK_TYPE_BINDING_MISSING</c>：该图没有生效绑定集、任务类型不在需求集、在需求集却没绑定
    /// （最后一种静态校验会拒绝，这里防的是读到这种数据时不猜）。地图上明明有站点 210「关卡」，也不回落到它。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData("no-active-set")]
    [InlineData("not-required")]
    [InlineData("required-but-unbound")]
    public async Task ATaskTypeWithoutAnEffectiveBindingIsRefusedAndNeverFallsBackToADefaultStation(string shape)
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        switch (shape)
        {
            case "no-active-set":
                await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
                fixture.Context.ChangeTracker.Clear();
                break;
            case "not-required":
                await ActivateAsync(fixture, [TransportTaskTypes.StagingToWire], [StagingBinding]);
                break;
            default:
                // No validated path writes a set that requires a task type it does not bind: since control-server#161
                // (review S6) the store refuses it. The shape can still reach the database by hand, so it is made here by
                // activating a whole set and removing its binding row around the store; the resolver must still refuse.
                (_, long version) = await ActivateAsync(fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
                await using (Microsoft.Data.Sqlite.SqliteCommand unbind = fixture.Connection.CreateCommand())
                {
                    unbind.CommandText = FormattableString.Invariant(
                        $"DELETE FROM TaskTypeStationBindings WHERE MapId = 25 AND Version = {version}");
                    await unbind.ExecuteNonQueryAsync(Token);
                }
                break;
        }

        FixedTaskStationResolution resolution = (await ReadAsync(fixture, Map)).Resolve(TransportTaskTypes.WireToGate);

        Assert.Equal(DispatchReasonCodes.TaskTypeBindingMissing, resolution.RefusalReasonCode);
        Assert.Null(resolution.Station);
        Assert.Equal(FixedStationEnd.Destination, resolution.FixedEnd);
    }

    /// <summary>
    /// 绑定站点不在本轮目录里（id 没了、同 id 改了名、目录不是本图）：该任务类型得到「绑定站点缺失」，与缺绑定区分；
    /// 同一轮里另一个绑定站点还在的任务类型照常解析（REQ-0342，不连带）。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData("absent")]
    [InlineData("renamed")]
    [InlineData("other-map")]
    public async Task ABoundStationMissingFromTheCatalogRefusesOnlyItsOwnTaskType(string shape)
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await ActivateAsync(
            fixture,
            [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            [GateBinding, StagingBinding]);
        RiotMapStation staging = new(305, "派工待送取货");
        RiotMapStationCatalogSnapshot map = shape switch
        {
            "absent" => Map with { Stations = [new RiotMapStation(12, "N1-1"), staging] },
            "renamed" => Map with { Stations = [new RiotMapStation(12, "N1-1"), new RiotMapStation(210, "关卡-旧"), staging] },
            _ => Map with { MapId = 26, Stations = [new RiotMapStation(12, "N1-1"), Gate, staging] },
        };

        IFixedTaskStationView view = await ReadAsync(fixture, map);
        FixedTaskStationResolution gate = view.Resolve(TransportTaskTypes.WireToGate);

        Assert.Equal(TaskTypeStationReasonCodes.BindingStationNotInCatalog, gate.RefusalReasonCode);
        Assert.Null(gate.Station);
        if (shape != "other-map")
        {
            FixedTaskStationResolution other = view.Resolve(TransportTaskTypes.StagingToWire);
            Assert.Null(other.RefusalReasonCode);
            Assert.Equal(staging, other.Station);
            Assert.Equal(FixedStationEnd.Origin, other.FixedEnd);
        }
    }

    /// <summary>
    /// 本图该任务类型处于暂停，任何来源都算：看板人工、目录变化、激活结果未知（REQ-0340、REQ-0342、REQ-0347）。
    /// 人工与目录变化只挡被暂停的那个任务类型；已解除的暂停不再挡。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData(TaskTypeStationHoldSource.Manual)]
    [InlineData(TaskTypeStationHoldSource.CatalogChange)]
    public async Task AHeldTaskTypeIsRefusedWhateverRaisedTheHoldAndOnlyThatTaskType(string source)
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await ActivateAsync(
            fixture,
            [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            [GateBinding, StagingBinding]);
        RiotMapStationCatalogSnapshot map = Map with { Stations = [.. Map.Stations, new RiotMapStation(305, "派工待送取货")] };
        TaskTypeStationHold released = await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.StagingToWire, source, "TEST_HOLD", "{}", "test", Now, Token);
        await fixture.Holds.ReleaseAsync(released.HoldId, "test", Now, Token);
        await fixture.Holds.RaiseAsync(25, TransportTaskTypes.WireToGate, source, "TEST_HOLD", "{}", "test", Now, Token);
        fixture.Context.ChangeTracker.Clear();

        IFixedTaskStationView view = await ReadAsync(fixture, map);

        Assert.Equal(DispatchReasonCodes.TaskTypeHeld, view.Resolve(TransportTaskTypes.WireToGate).RefusalReasonCode);
        Assert.Null(view.Resolve(TransportTaskTypes.WireToGate).Station);
        Assert.Null(view.Resolve(TransportTaskTypes.StagingToWire).RefusalReasonCode);
    }

    /// <summary>激活结果未知时整张图的绑定都不可信，本图每个任务类型都算暂停（REQ-0347）。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AnActivationWhoseOutcomeIsUnknownHoldsEveryTaskTypeOfTheMap()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await ActivateAsync(fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        TaskTypeStationActiveBindingSetRow pointer = await fixture.Context.Set<TaskTypeStationActiveBindingSetRow>()
            .SingleAsync(row => row.MapId == 25, Token);
        pointer.State = TaskTypeStationActivationState.ActivationUnknown;
        pointer.PendingVersion = pointer.ActiveVersion + 1;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            DispatchReasonCodes.TaskTypeHeld,
            (await ReadAsync(fixture, Map)).Resolve(TransportTaskTypes.WireToGate).RefusalReasonCode);
    }

    /// <summary>
    /// 顺序是缺绑定 → 绑定站点缺失 → 暂停：一个既暂停又缺绑定的任务类型报缺绑定，现场先要补的是绑定。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AMissingBindingIsNamedAheadOfAHold()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await ActivateAsync(fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.StagingToWire, TaskTypeStationHoldSource.Manual, "TEST_HOLD", "{}", "test", Now, Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            DispatchReasonCodes.TaskTypeBindingMissing,
            (await ReadAsync(fixture, Map)).Resolve(TransportTaskTypes.StagingToWire).RefusalReasonCode);
    }

    /// <summary>
    /// 规则表不认识的任务类型（或者根本还没有规则表）仍是 <c>OUT_OF_SCOPE_WORK_TYPE</c>：没有规则就不知道固定端在哪头，
    /// 更谈不上绑定。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATaskTypeTheRulesDoNotKnowIsOutOfScope(bool withRules)
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        if (withRules)
        {
            await ActivateAsync(fixture, [TransportTaskTypes.WireToGate], [GateBinding]);
        }

        IFixedTaskStationView view = await ReadAsync(fixture, Map);

        Assert.Equal(DispatchReasonCodes.OutOfScopeWorkType, view.Resolve("NOT_A_TASK_TYPE").RefusalReasonCode);
        Assert.Null(view.Resolve("NOT_A_TASK_TYPE").Station);
        if (!withRules)
        {
            Assert.Equal(DispatchReasonCodes.OutOfScopeWorkType, view.Resolve(TransportTaskTypes.WireToGate).RefusalReasonCode);
        }
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
