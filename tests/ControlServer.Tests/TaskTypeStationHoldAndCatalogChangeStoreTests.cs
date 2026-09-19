using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 按 <c>Map + TASK_TYPE</c> 的暂停（REQ-0340）与目录变化记录（REQ-0341、REQ-0342）的存取。
/// </summary>
public sealed class TaskTypeStationHoldAndCatalogChangeStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AManualAndACatalogHoldOnTheSameTaskTypeBothStandUntilEachIsReleasedAndReleasingDeletesNothing()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        Assert.False(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));

        TaskTypeStationHold manual = await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "OPERATOR_SUSPECTS_BINDING",
            """{"note":"door sensor"}""", "operator:zhang", Now, Token);
        TaskTypeStationHold catalog = await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "STATION_RENAMED",
            """{"stationRiotId":210}""", "server", Now.AddMinutes(1), Token);
        await fixture.Holds.RaiseAsync(
            26, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "OTHER_MAP", "{}", "operator:li",
            Now, Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.NotEqual(manual.HoldId, catalog.HoldId);
        Assert.True(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.False(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.StagingToWire, Token));
        Assert.Equal(
            [manual.HoldId, catalog.HoldId],
            (await fixture.Holds.ListUnreleasedAsync(25, Token)).Select(hold => hold.HoldId));

        Assert.True(await fixture.Holds.ReleaseAsync(manual.HoldId, "operator:zhang", Now.AddMinutes(5), Token));
        Assert.False(await fixture.Holds.ReleaseAsync(manual.HoldId, "operator:zhang", Now.AddMinutes(6), Token));
        fixture.Context.ChangeTracker.Clear();

        // The catalog hold still stands on its own.
        Assert.True(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Equal([catalog.HoldId], (await fixture.Holds.ListUnreleasedAsync(25, Token)).Select(hold => hold.HoldId));

        Assert.True(await fixture.Holds.ReleaseAsync(catalog.HoldId, "server", Now.AddMinutes(7), Token));
        fixture.Context.ChangeTracker.Clear();
        Assert.False(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));

        TaskTypeStationHoldRow released = await fixture.Context.Set<TaskTypeStationHoldRow>()
            .SingleAsync(row => row.HoldId == manual.HoldId, Token);
        Assert.Equal(Now.AddMinutes(5), released.ReleasedAt);
        Assert.Equal("operator:zhang", released.ReleasedBy);
        Assert.Equal("OPERATOR_SUSPECTS_BINDING", released.ReasonCode);
        Assert.Equal(3, await fixture.Context.Set<TaskTypeStationHoldRow>().CountAsync(Token));
    }

    [Fact]
    public async Task ACatalogChangeIsRecordedOncePerStationAndRevisionAndListsBackInObservationOrder()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        TaskTypeStationCatalogChange renamed = new(
            "change-1", 25, 210, "关卡", "关卡-新", "RENAMED", "HIGH", 12, Now,
            [TransportTaskTypes.WireToGate], null);
        TaskTypeStationCatalogChange deleted = new(
            "change-2", 25, 305, "派工待送取货", null, "DELETED", "HIGH", 13, Now.AddMinutes(1),
            [TransportTaskTypes.StagingToWire], "hold-9");

        Assert.Equal(renamed, await fixture.CatalogChanges.RecordAsync(renamed, Token) with { AffectedTaskTypes = renamed.AffectedTaskTypes });
        TaskTypeStationCatalogChange repeat = await fixture.CatalogChanges.RecordAsync(
            renamed with { ChangeId = "change-1-again", ObservedAt = Now.AddMinutes(3) }, Token);
        await fixture.CatalogChanges.RecordAsync(deleted, Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal("change-1", repeat.ChangeId);
        IReadOnlyList<TaskTypeStationCatalogChange> listed = await fixture.CatalogChanges.ListAsync(25, Token);
        Assert.Equal(["change-1", "change-2"], listed.Select(change => change.ChangeId));
        Assert.Equal([TransportTaskTypes.WireToGate], listed[0].AffectedTaskTypes);
        Assert.Null(listed[1].CurrentStationName);
        Assert.Equal("hold-9", listed[1].HoldId);
        Assert.Empty(await fixture.CatalogChanges.ListAsync(26, Token));
    }
}
