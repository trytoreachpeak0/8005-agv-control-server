using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// Map 级改名检测的公开入口 <see cref="MapRenameHoldConvergence.ObserveAsync"/>（REQ-0341，control-server#186）：
/// 首次读到只记基线；同一 <c>mapId</c> 下改名，该图生效绑定集里的每个任务类型各一条「目录变化」暂停，别的图不动；读失败、
/// 缺图、空名都不算改名、不动基线也不动暂停。
/// </summary>
public sealed class MapRenameHoldConvergenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstNameSeenForAMapBecomesItsBaselineAndHoldsNothing()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding, StagingBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));

        IReadOnlyList<MapRenameObservation> observed = await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        MapNameBaseline? baseline = await Baselines(fixture).ReadAsync(26, Token);
        Assert.Equal(
            ("老厂前线new_wk", (DateTimeOffset?)Now, (string?)null),
            (baseline?.Name, baseline?.EstablishedAt, baseline?.PendingName));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(26, Token));
        Assert.Equal(MapRenameObservationKind.BaselineEstablished, Assert.Single(observed).Kind);
    }

    [Fact]
    public async Task TheSameNameAgainChangesNothing()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding, StagingBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);

        IReadOnlyList<MapRenameObservation> observed =
            await Convergence(fixture, riot, Now.AddMinutes(5)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(Now, (await Baselines(fixture).ReadAsync(26, Token))?.EstablishedAt);
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(26, Token));
        Assert.Equal(MapRenameObservationKind.Unchanged, Assert.Single(observed).Kind);
    }

    [Fact]
    public async Task ARenameUnderTheSameMapIdHoldsEveryBoundTaskTypeOfThatMapAndNothingOnAnotherMap()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding, StagingBinding);
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding);
        ScriptedMapNames riot = new((25, "老厂前线new"), (26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);

        riot.Set((25, "老厂前线new"), (26, "老厂前线new_wk2"));
        IReadOnlyList<MapRenameObservation> observed =
            await Convergence(fixture, riot, Now.AddMinutes(1)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        IReadOnlyList<TaskTypeStationHold> holds = await fixture.Holds.ListUnreleasedAsync(26, Token);
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            holds.Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.All(holds, hold =>
        {
            Assert.Equal(TaskTypeStationHoldSource.CatalogChange, hold.Source);
            Assert.Equal(MapNameHoldReasons.MapRenamed, hold.ReasonCode);
        });
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));

        // The baseline keeps the old name until someone accepts the new one; the new one is what is pending.
        MapNameBaseline? baseline = await Baselines(fixture).ReadAsync(26, Token);
        Assert.Equal(("老厂前线new_wk", "老厂前线new_wk2", (DateTimeOffset?)Now.AddMinutes(1)),
            (baseline?.Name, baseline?.PendingName, baseline?.PendingSince));
        Assert.Equal(
            MapRenameObservationKind.Renamed,
            Assert.Single(observed, observation => observation.MapId == 26).Kind);
    }

    [Fact]
    public async Task NamesAreComparedByteForByteSoAWidthOrCaseDifferenceIsARename()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);

        riot.Set((26, "老厂前线NEW_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            MapNameHoldReasons.MapRenamed,
            Assert.Single(await fixture.Holds.ListUnreleasedAsync(26, Token)).ReasonCode);
    }

    [Fact]
    public async Task ARenameWritesOneDetectionAuditAndOneHoldAuditPerTaskTypeAndARepeatWritesNothingMore()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        long version = await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding, StagingBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set((26, "老厂前线new_wk2"));

        await Convergence(fixture, riot).ObserveAsync(Token);
        await Convergence(fixture, riot, Now.AddMinutes(1)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        BusinessAuditRecordRow detected = Assert.Single(await AuditsAsync(fixture, MapNameBaselineAuditActions.RenameDetected));
        Assert.Equal(GovernedObjectKind.PublicStationBinding, detected.ObjectKind);
        Assert.Equal("map-26", detected.ObjectId);
        using (JsonDocument detail = JsonDocument.Parse(detected.DetailJson))
        {
            Assert.Equal(26, detail.RootElement.GetProperty("mapId").GetInt32());
            Assert.Equal("老厂前线new_wk", detail.RootElement.GetProperty("baselineName").GetString());
            Assert.Equal("老厂前线new_wk2", detail.RootElement.GetProperty("observedName").GetString());
        }

        BusinessAuditRecordRow[] raised = await AuditsAsync(fixture, CatalogBindingHoldConvergence.HoldRaisedAction);
        Assert.Equal(2, raised.Length);
        Assert.All(raised, audit => Assert.Equal(version, audit.Version));
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            raised.Select(audit =>
            {
                using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
                Assert.Equal(MapNameHoldReasons.MapRenamed, detail.RootElement.GetProperty("reasonCode").GetString());
                Assert.Equal("MAP_RENAMED", detail.RootElement.GetProperty("classification").GetString());
                Assert.Equal("老厂前线new_wk", detail.RootElement.GetProperty("before").GetProperty("mapName").GetString());
                Assert.Equal("老厂前线new_wk2", detail.RootElement.GetProperty("after").GetProperty("mapName").GetString());
                return detail.RootElement.GetProperty("taskType").GetString();
            }).Order(StringComparer.Ordinal));
        Assert.Equal(2, (await fixture.Holds.ListUnreleasedAsync(26, Token)).Count);
    }

    [Fact]
    public async Task AFailedReadThrowsAndLeavesBaselineAndHoldsExactlyAsTheyWere()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Failure = new InvalidDataException("RIoT Map list response was not valid.");

        Exception? failure = await Record.ExceptionAsync(() => Convergence(fixture, riot).ObserveAsync(Token));
        fixture.Context.ChangeTracker.Clear();

        MapNameBaseline? baseline = await Baselines(fixture).ReadAsync(26, Token);
        Assert.Equal(("老厂前线new_wk", (string?)null), (baseline?.Name, baseline?.PendingName));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(26, Token));
        Assert.IsType<InvalidDataException>(failure);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("duplicate")]
    public async Task AMapTheListingDoesNotNameUsablyIsNotARename(string shape)
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set(shape switch
        {
            "missing" => [(25, "老厂前线new")],
            "empty" => [(26, "")],
            "blank" => [(26, "  ")],
            _ => [(26, "老厂前线new_wk"), (26, "老厂前线new_wk2")]
        });

        IReadOnlyList<MapRenameObservation> observed = await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        MapNameBaseline? baseline = await Baselines(fixture).ReadAsync(26, Token);
        Assert.Equal(("老厂前线new_wk", (string?)null), (baseline?.Name, baseline?.PendingName));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(26, Token));
        Assert.DoesNotContain(observed, observation =>
            observation.MapId == 26 && observation.Kind != MapRenameObservationKind.NotObserved);
    }

    /// <summary>
    /// 调度 2026-09-28 要求钉住：改名挂上暂停之后，读失败或该图缺失的那几轮，暂停不动、基线不变；恢复读取后，没接受的改名仍挡着。
    /// </summary>
    [Fact]
    public async Task HoldsAndBaselineStandThroughFailedAndMissingReadsAndTheUnacceptedRenameStillHoldsAfter()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding, StagingBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set((26, "老厂前线new_wk2"));
        await Convergence(fixture, riot, Now.AddMinutes(1)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        string before = await DescribeAsync(fixture);
        Assert.Equal(2, (await fixture.Holds.ListUnreleasedAsync(26, Token)).Count);

        riot.Failure = new InvalidDataException("RIoT Map list response was not valid.");
        Exception? failure = await Record.ExceptionAsync(
            () => Convergence(fixture, riot, Now.AddMinutes(2)).ObserveAsync(Token));
        riot.Failure = null;
        riot.Set((25, "老厂前线new"));
        await Convergence(fixture, riot, Now.AddMinutes(3)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(before, await DescribeAsync(fixture));
        Assert.IsType<InvalidDataException>(failure);

        riot.Set((26, "老厂前线new_wk2"));
        IReadOnlyList<MapRenameObservation> back = await Convergence(fixture, riot, Now.AddMinutes(4)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(await fixture.Holds.IsHeldAsync(26, TransportTaskTypes.WireToGate, Token));
        Assert.True(await fixture.Holds.IsHeldAsync(26, TransportTaskTypes.StagingToWire, Token));
        Assert.Equal(before, await DescribeAsync(fixture));
        Assert.Equal(MapRenameObservationKind.Renamed, Assert.Single(back, observation => observation.MapId == 26).Kind);
    }

    [Fact]
    public async Task ARenameBackToTheBaselineClearsThePendingNameButLeavesTheHoldsForTheField()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set((26, "老厂前线new_wk2"));
        await Convergence(fixture, riot).ObserveAsync(Token);

        riot.Set((26, "老厂前线new_wk"));
        IReadOnlyList<MapRenameObservation> observed = await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(await fixture.Holds.IsHeldAsync(26, TransportTaskTypes.WireToGate, Token));
        MapNameBaseline? baseline = await Baselines(fixture).ReadAsync(26, Token);
        Assert.Equal(("老厂前线new_wk", (string?)null, (DateTimeOffset?)null),
            (baseline?.Name, baseline?.PendingName, baseline?.PendingSince));
        Assert.Equal(MapRenameObservationKind.RenamedBack, Assert.Single(observed).Kind);
    }

    /// <summary>
    /// 调度 2026-09-28 要求钉住的前提 (a) 的存储一半：改名待接受期间某个任务类型新绑定到该图，下一次观察就给它加暂停。
    /// 派车一半在 <see cref="MapRenameEngineTests"/>。
    /// </summary>
    [Fact]
    public async Task ATaskTypeBoundWhileTheRenameIsPendingIsHeldOnTheNextObservation()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, StagingBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set((26, "老厂前线new_wk2"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.False(await fixture.Holds.IsHeldAsync(26, TransportTaskTypes.WireToGate, Token));

        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding, StagingBinding);
        await Convergence(fixture, riot, Now.AddMinutes(1)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        TaskTypeStationHold gate = Assert.Single(
            await fixture.Holds.ListUnreleasedAsync(26, Token), hold => hold.TaskType == TransportTaskTypes.WireToGate);
        Assert.Equal(MapNameHoldReasons.MapRenamed, gate.ReasonCode);
        // One detection, not two: the pending name did not change.
        Assert.Single(await AuditsAsync(fixture, MapNameBaselineAuditActions.RenameDetected));
    }

    [Fact]
    public async Task AfterTheNewNameIsAcceptedAndTheHoldsReleasedReadingItAgainHoldsNothing()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding);
        ScriptedMapNames riot = new((26, "老厂前线new_wk"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set((26, "老厂前线new_wk2"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        MapNameBaselineAcceptResult accepted = await new MapNameBaselineAcceptanceService(Baselines(fixture), fixture.Governance)
            .AcceptAsync(26, "老厂前线new_wk2", new TaskTypeStationChangeRequest("现场核对 26 号图改名"), Now.AddMinutes(2), Token);
        Assert.True(accepted.Accepted);
        foreach (TaskTypeStationHold hold in await fixture.Holds.ListUnreleasedAsync(26, Token))
        {
            await fixture.Holds.ReleaseAsync(hold.HoldId, "test", Now.AddMinutes(3), Token);
        }

        IReadOnlyList<MapRenameObservation> observed =
            await Convergence(fixture, riot, Now.AddMinutes(4)).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(26, Token));
        Assert.Equal("老厂前线new_wk2", (await Baselines(fixture).ReadAsync(26, Token))?.Name);
        Assert.Equal(MapRenameObservationKind.Unchanged, Assert.Single(observed).Kind);
    }

    [Fact]
    public async Task AMapWithoutAnActiveBindingSetGetsABaselineAndAPendingNameButNoHold()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        ScriptedMapNames riot = new((9, "新厂一楼"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set((9, "新厂一楼-改"));

        IReadOnlyList<MapRenameObservation> observed = await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal("新厂一楼-改", (await Baselines(fixture).ReadAsync(9, Token))?.PendingName);
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(9, Token));
        MapRenameObservation renamed = Assert.Single(observed);
        Assert.Equal((MapRenameObservationKind.Renamed, 0), (renamed.Kind, renamed.HeldTaskTypes.Count));
    }

    /// <summary>看板暂停卡片上能看到来源与原因：「目录变化」「地图改名」。</summary>
    [Fact]
    public async Task TheDashboardShowsAMapRenameHoldAsACatalogChangeForAMapRename()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding);
        ScriptedMapNames riot = new((25, "老厂前线new"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        riot.Set((25, "老厂前线new-改"));
        await Convergence(fixture, riot).ObserveAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        object result = await new TaskTypeBindingsQueryEndpoint().ReadAsync(fixture.Context, Token);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        JsonElement gate = Assert.Single(document.RootElement.GetProperty("maps").EnumerateArray())
            .GetProperty("taskTypes").EnumerateArray()
            .Single(row => row.GetProperty("taskType").GetString() == TransportTaskTypes.WireToGate);
        Assert.Equal("HELD", gate.GetProperty("status").GetString());
        JsonElement hold = Assert.Single(gate.GetProperty("holds").EnumerateArray());
        Assert.Equal(("目录变化", "地图改名"), (hold.GetProperty("sourceLabel").GetString(), hold.GetProperty("reason").GetString()));
    }

    internal static MapRenameHoldConvergence Convergence(
        TaskTypeStationPersistenceFixture fixture,
        IRiotMapNameCatalog riot,
        DateTimeOffset? at = null) =>
        TaskTypeStationRuntimeSeed.MapRenameHolds(fixture.Context, riot, new FixedAt(at ?? Now));

    internal static MapNameBaselineStore Baselines(TaskTypeStationPersistenceFixture fixture) =>
        new(fixture.Context, fixture.Governance);

    private static Task<BusinessAuditRecordRow[]> AuditsAsync(TaskTypeStationPersistenceFixture fixture, string action) =>
        fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == action)
            .ToArrayAsync(Token);

    /// <summary>Every baseline, every hold and every audit, so that "untouched" is checked, not assumed.</summary>
    private static async Task<string> DescribeAsync(TaskTypeStationPersistenceFixture fixture)
    {
        MapNameBaseline? baseline = await Baselines(fixture).ReadAsync(26, Token);
        IReadOnlyList<TaskTypeStationHold> holds = await fixture.Holds.ListUnreleasedAsync(26, Token);
        int audits = await fixture.Context.Set<BusinessAuditRecordRow>().CountAsync(Token);
        return JsonSerializer.Serialize(new { baseline, holds, audits });
    }
}

internal sealed class FixedAt(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A Map list that answers what the test last set, or throws what it was told to.</summary>
internal sealed class ScriptedMapNames(params (int MapId, string Name)[] maps) : IRiotMapNameCatalog
{
    private (int MapId, string Name)[] _maps = maps;

    public Exception? Failure { get; set; }

    public void Set(params (int MapId, string Name)[] maps) => _maps = maps;

    public Task<RiotMapNameListing> ReadMapNamesAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Failure is { } failure
            ? Task.FromException<RiotMapNameListing>(failure)
            : Task.FromResult(new RiotMapNameListing(
                TaskTypeStationTestData.Now,
                [.. _maps.Select(map => new RiotMapName(map.MapId, map.Name))]));
    }
}
