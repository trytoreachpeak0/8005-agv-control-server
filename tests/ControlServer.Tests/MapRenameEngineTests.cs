using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 引擎每一轮开头读 RIoT 的地图列表、比地图名基线（control-server#186）。判据落在派车结果上：改名那一轮起该图生效绑定的任务类型
/// 不派车，接受新名并解除暂停后恢复；读失败不挡这一轮、也不加暂停；<c>JourneyRuntime:mapIdentity</c> 与地图名基线互不比较。
/// </summary>
public sealed class MapRenameEngineTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000186";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARenameStopsDispatchFromItsRoundAndAcceptingTheNameThenReleasingTheHoldResumesIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Riot.MapNames = [new RiotMapName(25, "老厂前线new_wk2")];
        OfferDemand(fixture);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(0, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        TaskTypeStationHold hold = Assert.Single(await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, Token));
        Assert.Equal(
            (TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, MapNameHoldReasons.MapRenamed),
            (hold.TaskType, hold.Source, hold.ReasonCode));

        await AcceptAndReleaseAsync(fixture, "老厂前线new_wk2");
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>读失败或超时：这一轮照常派车，不加暂停，不建基线；只记一条日志，交给目录新鲜度门禁。</summary>
    [Theory]
    [InlineData("invalid")]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task AFailedMapListReadNeitherStopsTheRoundNorHolds(string failure)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.FailMapNameReads = failure switch
        {
            "invalid" => new InvalidDataException("RIoT Map list response was not valid."),
            "http" => new HttpRequestException("connection refused"),
            _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
        };
        OfferDemand(fixture);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Empty(await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, Token));
        Assert.Null(await Baselines(fixture).ReadAsync(25, Token));
        Assert.Contains(fixture.EngineLog.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("Map name", StringComparison.Ordinal));
    }

    /// <summary>
    /// 调度 2026-09-28 要求钉住的两个前提，都断在派车结果上：
    /// (b) 生效版本里没有绑定的任务类型今天就派不了车；
    /// (a) 改名待接受期间它被绑定上，这一轮就被加上暂停、派不出车，直到改名被接受、暂停被解除。
    /// </summary>
    [Fact]
    public async Task AnUnboundTaskTypeDoesNotDispatchAndBoundDuringAPendingRenameItIsHeldUntilTheRenameIsAccepted()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TaskTypeStationBinding staging = new(TransportTaskTypes.StagingToWire, 300, "等待点", "SITE-CHECK-300");
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests, Now.AddMinutes(-30), [TransportTaskTypes.StagingToWire], [staging]);
        OfferDemand(fixture);

        // (b): WIRE_TO_GATE has no binding in the active version, and nothing is dispatched.
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(0, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        Assert.Equal(DispatchReasonCodes.TaskTypeBindingMissing, (await fixture.BacklogAsync(DemandId)).ReasonCode);

        // The rename holds what is bound -- STAGING_TO_WIRE -- and there is no WIRE_TO_GATE binding to hold.
        fixture.Riot.MapNames = [new RiotMapName(25, "老厂前线new_wk2")];
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            [TransportTaskTypes.StagingToWire],
            (await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, Token)).Select(hold => hold.TaskType));
        Assert.Equal(0, await fixture.Context.JourneyRuntimes.CountAsync(Token));

        // (a): bound while the rename is pending -- held in the very round that first sees the binding, and not dispatched.
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now.AddMinutes(-20),
            [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            [TaskTypeStationRuntimeSeed.GateBinding, staging]);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.True(await new TaskTypeStationHoldStore(fixture.Context).IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Equal(0, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(DispatchReasonCodes.TaskTypeHeld, (await fixture.BacklogAsync(DemandId)).ReasonCode);

        await AcceptAndReleaseAsync(fixture, "老厂前线new_wk2");
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 审查必修（PR #378 第二路 M1）：改名挂上暂停之后地图列表读不到的那几轮，暂停照旧挡着——断在派车结果上，不只断在存储层。
    /// 反向验证是变异 MX2：读失败时顺手把 MAP_RENAMED 暂停放掉，这一条红。
    /// </summary>
    [Fact]
    public async Task AfterARenameAFailedMapListReadDoesNotLetTheHeldTaskTypeDispatch()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Riot.MapNames = [new RiotMapName(25, "老厂前线new_wk2")];
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.True(await new TaskTypeStationHoldStore(fixture.Context).IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));

        fixture.Riot.FailMapNameReads = new InvalidDataException("RIoT Map list response was not valid.");
        OfferDemand(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(0, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        Assert.Equal(DispatchReasonCodes.TaskTypeHeld, (await fixture.BacklogAsync(DemandId)).ReasonCode);
    }

    /// <summary>
    /// <c>mapIdentity</c>（这里是 <c>MAP-25</c>）不是基线也不是初值：RIoT 名字与它不同，首轮照常派车、基线记的是 RIoT 的名字；
    /// RIoT 名字改成恰好等于它，照样是改名。
    /// </summary>
    [Fact]
    public async Task MapIdentityIsNeitherTheBaselineNorComparedWithIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Assert.Equal("MAP-25", fixture.Options.MapIdentity);
        OfferDemand(fixture);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal("老厂前线new_wk", (await Baselines(fixture).ReadAsync(25, Token))?.Name);
        Assert.Empty(await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, Token));

        fixture.Riot.MapNames = [new RiotMapName(25, fixture.Options.MapIdentity)];
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            MapNameHoldReasons.MapRenamed,
            Assert.Single(await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, Token)).ReasonCode);
    }

    private static void OfferDemand(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-186", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-186", 7);
    }

    private static MapNameBaselineStore Baselines(RuntimeFixture fixture)
    {
        GovernanceStore governance = new(
            fixture.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
        return new MapNameBaselineStore(fixture.Context, governance);
    }

    /// <summary>What the field does: accepts the name in FieldOps, then releases each hold (the release verb is batch 6-05's).</summary>
    private static async Task AcceptAndReleaseAsync(RuntimeFixture fixture, string name)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        GovernanceStore governance = new(
            context, new GovernanceDeploymentIdentity("deployment:fieldops@test"), AuditRetentionPolicy.Default);
        MapNameBaselineAcceptResult accepted = await new MapNameBaselineAcceptanceService(
                new MapNameBaselineStore(context, governance), governance)
            .AcceptAsync(25, name, new TaskTypeStationChangeRequest("现场核对改名"), Now, Token);
        Assert.True(accepted.Accepted, string.Join("; ", accepted.Violations.Select(violation => violation.ReasonCode)));
        TaskTypeStationHoldStore holds = new(context);
        foreach (TaskTypeStationHold hold in await holds.ListUnreleasedAsync(25, Token))
        {
            await holds.ReleaseAsync(hold.HoldId, "fieldops:test", Now, Token);
        }
        fixture.Context.ChangeTracker.Clear();
    }
}
