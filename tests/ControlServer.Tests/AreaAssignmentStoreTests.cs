using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 分区归属表（含开门侧列）整表一个版本，每个版本不可改写、自带快照与审计（REQ-0350，program#68 决议 2）。
/// </summary>
public sealed class AreaAssignmentStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly AreaAssignment[] FirstTable =
    [
        new("N01", "MAP-25-WIRE_TO_GATE", "FRONT"),
        new("N02", "MAP-25-WIRE_TO_GATE", "REAR"),
    ];

    private static readonly AreaAssignment[] SecondTable =
    [
        new("N01", "MAP-25-WIRE_TO_GATE", "REAR"),
        new("T01", "MAP-25-DIE_ATTACH", "FRONT"),
    ];

    [Fact]
    public async Task AfterTwoVersionsTheCurrentOneIsTheSecondAndTheFirstStillReadsBackUnchanged()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        Assert.Null(await fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));

        AreaAssignmentTableVersion first = await fixture.AreaAssignments.WriteVersionAsync(
            FirstTable, Now, TestContext.Current.CancellationToken);
        AreaAssignmentTableVersion second = await fixture.AreaAssignments.WriteVersionAsync(
            SecondTable, Now.AddHours(1), TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);

        AreaAssignmentTableVersion current = Assert.IsType<AreaAssignmentTableVersion>(
            await fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, current.Version);
        Assert.Equal(second.SnapshotId, current.SnapshotId);
        Assert.Equal(["N01", "T01"], current.ByArea.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new AreaAssignment("N01", "MAP-25-WIRE_TO_GATE", "REAR"), current.ByArea["N01"]);
        Assert.Equal(new AreaAssignment("T01", "MAP-25-DIE_ATTACH", "FRONT"), current.ByArea["T01"]);

        AreaAssignmentTableVersion readFirst = Assert.IsType<AreaAssignmentTableVersion>(
            await fixture.AreaAssignments.ReadVersionAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(first.SnapshotId, readFirst.SnapshotId);
        Assert.Equal(first.ContentSha256, readFirst.ContentSha256);
        Assert.Equal(Now, readFirst.ImportedAt);
        Assert.Equal(["N01", "N02"], readFirst.ByArea.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new AreaAssignment("N01", "MAP-25-WIRE_TO_GATE", "FRONT"), readFirst.ByArea["N01"]);
        Assert.Equal(new AreaAssignment("N02", "MAP-25-WIRE_TO_GATE", "REAR"), readFirst.ByArea["N02"]);

        Assert.Null(await fixture.AreaAssignments.ReadVersionAsync(3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EveryVersionHasItsOwnFrozenSnapshotAndExactlyOneBusinessAuditRecord()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        AreaAssignmentTableVersion[] written =
        [
            await fixture.AreaAssignments.WriteVersionAsync(FirstTable, Now, TestContext.Current.CancellationToken),
            await fixture.AreaAssignments.WriteVersionAsync(
                SecondTable, Now.AddHours(1), TestContext.Current.CancellationToken),
        ];

        foreach ((AreaAssignmentTableVersion version, AreaAssignment[] table) in written.Zip([FirstTable, SecondTable]))
        {
            GovernedVersionView view = Assert.IsType<GovernedVersionView>(await fixture.Governance.ReadVersionAsync(
                GovernedObjectKind.DispatchZoneAreaAssignment,
                DispatchZoneAreaAssignmentGovernance.ObjectId,
                version.Version,
                TestContext.Current.CancellationToken));

            Assert.Equal(version.SnapshotId, view.Snapshot.SnapshotId);
            Assert.Equal(version.ContentSha256, view.Snapshot.ContentSha256);
            GovernanceAuditView audit = Assert.Single(view.BusinessAudit);
            Assert.Equal(DispatchZoneAreaAssignmentGovernance.VersionImportedAction, audit.Action);
            Assert.Equal(version.SnapshotId, audit.SnapshotId);

            // The snapshot is the complete table, so the frozen content alone says what this version was.
            using JsonDocument frozen = JsonDocument.Parse(view.Snapshot.ContentJson);
            AreaAssignment[] entries =
            [
                .. frozen.RootElement.GetProperty("entries").EnumerateArray().Select(entry => new AreaAssignment(
                    entry.GetProperty("area").GetString()!,
                    entry.GetProperty("dispatchZone").GetString()!,
                    entry.GetProperty("slotPosition").GetString()!))
            ];
            Assert.Equal(table, entries);
        }
    }

    [Fact]
    public async Task AWrittenVersionCannotBeRewrittenOrDeleted()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await fixture.AreaAssignments.WriteVersionAsync(FirstTable, Now, TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        DispatchZoneAreaAssignmentRow row = await fixture.Context.Set<DispatchZoneAreaAssignmentRow>()
            .SingleAsync(candidate => candidate.Area == "N01", TestContext.Current.CancellationToken);
        row.SlotPosition = "REAR";
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() =>
            fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        DispatchZoneAreaAssignmentVersionRow version = await fixture.Context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .SingleAsync(TestContext.Current.CancellationToken);
        fixture.Context.Remove(version);
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() =>
            fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal("FRONT", (await fixture.Context.Set<DispatchZoneAreaAssignmentRow>().AsNoTracking()
            .SingleAsync(candidate => candidate.Area == "N01", TestContext.Current.CancellationToken)).SlotPosition);
        Assert.Equal(1, await fixture.Context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATableThatNamesOneAreaTwiceIsRefusedAndNothingIsWritten()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.AreaAssignments.WriteVersionAsync(
            [new("N01", "MAP-25-WIRE_TO_GATE", "FRONT"), new("N01", "MAP-25-WIRE_TO_GATE", "REAR")],
            Now,
            TestContext.Current.CancellationToken));

        Assert.Null(await fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.Context.Set<GovernedConfigurationSnapshotRow>()
            .CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.Context.Set<BusinessAuditRecordRow>()
            .CountAsync(TestContext.Current.CancellationToken));
    }
}
