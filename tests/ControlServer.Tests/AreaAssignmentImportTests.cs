using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 整表导入分区归属表（REQ-0350）：表自身的五类错误整份拒绝，正确的一份写成新版本，并预览
/// 开门侧会变的在途需求。
/// </summary>
public sealed class AreaAssignmentImportTests
{
    [Fact]
    public async Task RejectsACsvWhoseHeaderIsNotTheControlledThreeColumns()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            "area,zone,slot_position\nC15-13,MAP-25-WIRE_TO_GATE,FRONT\n");

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        Assert.Equal(
            [AreaAssignmentImportReasonCodes.HeaderInvalid],
            result.Errors.Select(error => error.ReasonCode));
    }

    [Fact]
    public async Task RejectsEveryRowWhoseAreaIsNotInTheAreaCodeFormat()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT
            c15-14,MAP-25-WIRE_TO_GATE,REAR
            N01,MAP-25-WIRE_TO_GATE,FRONT

            """);

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        Assert.Equal(
            [(3, "c15-14"), (4, "N01")],
            result.Errors
                .Where(error => error.ReasonCode == AreaAssignmentImportReasonCodes.AreaFormatInvalid)
                .Select(error => (error.Line, error.Area)));
        Assert.All(
            result.Errors,
            error => Assert.Equal(AreaAssignmentImportReasonCodes.AreaFormatInvalid, error.ReasonCode));
    }

    [Fact]
    public async Task RejectsAGroupValueOutsideThePublishedModelAndARowWithNoGroupAtAll()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT
            C15-14,MAP-25-WIRE_TO_GATE,LEFT
            C15-15,MAP-25-WIRE_TO_GATE,

            """);

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        Assert.Equal(
            [
                (3, AreaAssignmentImportReasonCodes.SlotPositionNotInPublishedModel, "C15-14"),
                (4, AreaAssignmentImportReasonCodes.SlotPositionMissing, "C15-15"),
            ],
            result.Errors.Select(error => (error.Line, error.ReasonCode, error.Area)));
    }

    [Fact]
    public async Task RejectsTheWholeTableWhenNoWholeVehicleSlotModelIsPublished()
    {
        await using AreaAssignmentImportHarness harness =
            await AreaAssignmentImportHarness.CreateAsync(seedApprovedModel: false);

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT

            """);

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        AreaAssignmentImportError error = Assert.Single(result.Errors);
        Assert.Equal(AreaAssignmentImportReasonCodes.NoPublishedSlotModel, error.ReasonCode);
    }

    /// <summary>
    /// REQ-0349：分组来自整车仓位模板，不在代码里写死。把库内模型的分组改成别的取值，<c>FRONT</c> 就得被拒。
    /// </summary>
    [Fact]
    public async Task TakesTheGroupSetFromTheDatabaseSoFrontIsIllegalWhenTheModelGroupsSlotsOtherwise()
    {
        await using AreaAssignmentImportHarness harness =
            await AreaAssignmentImportHarness.CreateAsync(seedApprovedModel: false);
        await harness.PublishModelGroupedAsync("8005-two-slot-boat", "PORT", "STARBOARD");

        AreaAssignmentImportResult rejected = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT

            """);
        AreaAssignmentImportResult accepted = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,PORT

            """,
            dryRun: true);

        AreaAssignmentImportError error = Assert.Single(rejected.Errors);
        Assert.Equal(AreaAssignmentImportReasonCodes.SlotPositionNotInPublishedModel, error.ReasonCode);
        Assert.Equal(AreaAssignmentImportOutcome.Accepted, accepted.Outcome);
    }

    [Fact]
    public async Task RejectsEveryRepeatOfAnAreaTheTableAlreadyNames()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT
            C15-14,MAP-25-WIRE_TO_GATE,REAR
            C15-13,MAP-25-WIRE_TO_GATE,REAR
            C15-13,MAP-25-WIRE_TO_GATE,FRONT

            """);

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        Assert.Equal(
            [(4, "C15-13"), (5, "C15-13")],
            result.Errors.Select(error => (error.Line, error.Area)));
        Assert.All(
            result.Errors,
            error => Assert.Equal(AreaAssignmentImportReasonCodes.AreaDuplicated, error.ReasonCode));
    }

    /// <summary>
    /// 「分区存在」按库内调度策略 <c>DispatchZoneVehicles</c> 判：那是 FieldOps 唯一能拿到的分区权威，因为它
    /// 只开 SQLite、不读设置文件。配置了却没有车服务的分区，在这里就是不存在。
    /// </summary>
    [Fact]
    public async Task RejectsADispatchZoneThatNoVehicleInTheStoredPolicyServes()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT
            C15-14,MAP-25-DIE_ATTACH,REAR

            """);

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        AreaAssignmentImportError error = Assert.Single(result.Errors);
        Assert.Equal(AreaAssignmentImportReasonCodes.DispatchZoneNotFound, error.ReasonCode);
        Assert.Equal(3, error.Line);
        Assert.Equal("C15-14", error.Area);
    }

    /// <summary>
    /// 现场改一轮表要跑一趟，所以一次把全部错误行报出来，而不是停在第一处。
    /// </summary>
    [Fact]
    public async Task ReportsEveryBadRowOfEveryKindInOnePass()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT
            c15-14,MAP-25-WIRE_TO_GATE,REAR
            C15-15,MAP-25-WIRE_TO_GATE,LEFT
            C15-16,MAP-25-WIRE_TO_GATE,
            C15-13,MAP-25-WIRE_TO_GATE,REAR
            C15-17,MAP-25-DIE_ATTACH,FRONT

            """);

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        Assert.Equal(
            [
                (3, AreaAssignmentImportReasonCodes.AreaFormatInvalid),
                (4, AreaAssignmentImportReasonCodes.SlotPositionNotInPublishedModel),
                (5, AreaAssignmentImportReasonCodes.SlotPositionMissing),
                (6, AreaAssignmentImportReasonCodes.AreaDuplicated),
                (7, AreaAssignmentImportReasonCodes.DispatchZoneNotFound),
            ],
            result.Errors.Select(error => (error.Line, error.ReasonCode)).Order());
        Assert.Null(result.Version);
        Assert.Null(await harness.Fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));
    }

    private const string TwoRowTable =
        """
        area,dispatch_zone,slot_position
        C15-13,MAP-25-WIRE_TO_GATE,FRONT
        C15-14,MAP-25-WIRE_TO_GATE,REAR

        """;

    [Fact]
    public async Task WritesAnAcceptedTableAsANewImmutableVersionWithItsSnapshotAndOneBusinessAuditRecord()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult first = await harness.ImportAsync(TwoRowTable);
        // 内容一模一样的一份也形成新版本：回滚就是把旧内容再导入一次（REQ-0350）。
        AreaAssignmentImportResult second = await harness.ImportAsync(TwoRowTable);
        harness.Context.ChangeTracker.Clear();

        Assert.Equal(AreaAssignmentImportOutcome.Accepted, first.Outcome);
        Assert.Equal(1, first.Version!.Version);
        Assert.Equal(2, second.Version!.Version);
        Assert.Equal(2, first.EntryCount);
        Assert.Equal(first.Version.ContentSha256, second.Version.ContentSha256);

        AreaAssignmentTableVersion current = Assert.IsType<AreaAssignmentTableVersion>(
            await harness.Fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, current.Version);
        Assert.Equal("FRONT", current.ByArea["C15-13"].SlotPosition);
        Assert.Equal("REAR", current.ByArea["C15-14"].SlotPosition);

        GovernedConfigurationSnapshotRow[] snapshots = await harness.Context
            .Set<GovernedConfigurationSnapshotRow>()
            .AsNoTracking()
            .Where(row => row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment)
            .OrderBy(row => row.Version)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L], snapshots.Select(row => row.Version));
        Assert.All(
            snapshots,
            row => Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.ContentJson))).ToLowerInvariant(),
                row.ContentSha256));
        Assert.Equal(first.Version.ContentSha256, snapshots[0].ContentSha256);
        Assert.Equal(first.Version.SnapshotId, snapshots[0].SnapshotId);

        string[] audits = await harness.Context.Set<BusinessAuditRecordRow>()
            .AsNoTracking()
            .Where(row => row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment)
            .Select(row => row.Action)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [
                DispatchZoneAreaAssignmentGovernance.VersionImportedAction,
                DispatchZoneAreaAssignmentGovernance.VersionImportedAction,
            ],
            audits);
    }

    [Fact]
    public async Task ADryRunWritesNoVersionNoSnapshotAndNoAudit()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(TwoRowTable, dryRun: true);

        Assert.Equal(AreaAssignmentImportOutcome.Accepted, result.Outcome);
        Assert.True(result.DryRun);
        Assert.Equal(2, result.EntryCount);
        Assert.Null(result.Version);
        Assert.Null(await harness.Fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await harness.Context.Set<BusinessAuditRecordRow>()
            .Where(row => row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment)
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// REQ-0350 的导入预览：列出开启分组将随之变化的在途需求。只提示，不阻止导入，也不改已冻结的需求。
    /// </summary>
    [Fact]
    public async Task PreviewsExactlyTheInFlightDemandsWhoseGroupChangesOrWhoseAreaStopsBeingMapped()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();
        await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT
            C15-14,MAP-25-WIRE_TO_GATE,FRONT
            C15-15,MAP-25-WIRE_TO_GATE,REAR

            """);
        // 三条在途需求：一条的分组被改掉，一条的 AREA 在新表里没有了，一条原样不动。
        await harness.AddInFlightDemandAsync("demand-flips", "C15-13", 1);
        await harness.AddInFlightDemandAsync("demand-dropped", "C15-14", 1);
        await harness.AddInFlightDemandAsync("demand-unchanged", "C15-15", 1);
        // 已跑完的需求不算在途，哪怕它的分组也会变。
        await harness.AddInFlightDemandAsync("demand-completed", "C15-13", 1);
        await harness.CompleteJourneyAsync("demand-completed");

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,REAR
            C15-15,MAP-25-WIRE_TO_GATE,REAR

            """,
            dryRun: true);

        Assert.Equal(AreaAssignmentImportOutcome.Accepted, result.Outcome);
        Assert.Equal(
            [
                new AreaAssignmentPreviewEntry("demand-dropped", "C15-14", 1, "FRONT", null),
                new AreaAssignmentPreviewEntry("demand-flips", "C15-13", 1, "FRONT", "REAR"),
            ],
            result.Preview);
    }

    /// <summary>
    /// <c>area-assignments</c> 背后的读取语义：不带版本读当前那一版，带版本读那一版，版本不存在时什么都没有；
    /// 读完库里一行没多。
    /// </summary>
    [Fact]
    public async Task ReadingTheTableReturnsTheCurrentOrTheNamedVersionAndWritesNothing()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();
        await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT

            """);
        await harness.ImportAsync(TwoRowTable);
        harness.Context.ChangeTracker.Clear();
        int versionsBefore = await harness.Context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .CountAsync(TestContext.Current.CancellationToken);
        int auditsBefore = await harness.Context.Set<BusinessAuditRecordRow>()
            .CountAsync(TestContext.Current.CancellationToken);

        AreaAssignmentTableVersion current = Assert.IsType<AreaAssignmentTableVersion>(
            await harness.Fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));
        AreaAssignmentTableVersion named = Assert.IsType<AreaAssignmentTableVersion>(
            await harness.Fixture.AreaAssignments.ReadVersionAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(2, current.Version);
        Assert.Equal(["C15-13", "C15-14"], current.ByArea.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, named.Version);
        Assert.Equal(["C15-13"], named.ByArea.Keys);
        Assert.Null(await harness.Fixture.AreaAssignments.ReadVersionAsync(3, TestContext.Current.CancellationToken));
        Assert.Equal(
            versionsBefore,
            await harness.Context.Set<DispatchZoneAreaAssignmentVersionRow>()
                .CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            auditsBefore,
            await harness.Context.Set<BusinessAuditRecordRow>()
                .CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// program#80 决议 7：预览与导入都不按站点检查同一站点所挂 AREA 的分组是否一致（REQ-0353），也不读站点位姿。
    /// 同一站点混挂前后两侧的 AREA 是正常配置——车身长，停在两台机台之间时前半段对着一台、后半段对着另一台。
    /// </summary>
    [Fact]
    public async Task AcceptsTwoAreasOfTheSameStationAssignedToOppositeGroupsWithNoWarningAtAll()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,FRONT
            C15-14,MAP-25-WIRE_TO_GATE,REAR

            """);

        Assert.Equal(AreaAssignmentImportOutcome.Accepted, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Preview);
        Assert.Equal(1, result.Version!.Version);
    }

    /// <summary>
    /// 整份拒绝要防的是半写，而库里已经有内容的时候才谈得上半写。
    /// </summary>
    /// <remarks>
    /// 前面几条拒绝用例都从空库起算，「没写」和「库本来就是空的」在那里长得一样。这一条先让库里有两版，
    /// 再喂一份含错的表：版本数、快照数、业务审计数三样都必须原封不动，当前版本仍是导入前那一版。
    /// </remarks>
    [Fact]
    public async Task RejectsOnATableThatAlreadyHasVersionsWithoutTouchingASingleRowOfThem()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();
        await harness.ImportAsync(TwoRowTable);
        await harness.ImportAsync(TwoRowTable);
        harness.Context.ChangeTracker.Clear();
        int versionsBefore = await harness.Context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .CountAsync(TestContext.Current.CancellationToken);
        int assignmentsBefore = await harness.Context.Set<DispatchZoneAreaAssignmentRow>()
            .CountAsync(TestContext.Current.CancellationToken);
        int snapshotsBefore = await harness.Context.Set<GovernedConfigurationSnapshotRow>()
            .CountAsync(row => row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment,
                TestContext.Current.CancellationToken);
        int auditsBefore = await harness.Context.Set<BusinessAuditRecordRow>()
            .CountAsync(row => row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment,
                TestContext.Current.CancellationToken);

        // 第一行本身是好的：拒绝必须整份退回，不能把好的那一行留下。
        AreaAssignmentImportResult result = await harness.ImportAsync(
            """
            area,dispatch_zone,slot_position
            C15-13,MAP-25-WIRE_TO_GATE,REAR
            C15-99,MAP-25-DIE_ATTACH,FRONT

            """);
        harness.Context.ChangeTracker.Clear();

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        Assert.Equal(
            AreaAssignmentImportReasonCodes.DispatchZoneNotFound,
            Assert.Single(result.Errors).ReasonCode);
        Assert.Null(result.Version);
        Assert.Equal(
            versionsBefore,
            await harness.Context.Set<DispatchZoneAreaAssignmentVersionRow>()
                .CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            assignmentsBefore,
            await harness.Context.Set<DispatchZoneAreaAssignmentRow>()
                .CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            snapshotsBefore,
            await harness.Context.Set<GovernedConfigurationSnapshotRow>()
                .CountAsync(row => row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment,
                    TestContext.Current.CancellationToken));
        Assert.Equal(
            auditsBefore,
            await harness.Context.Set<BusinessAuditRecordRow>()
                .CountAsync(row => row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment,
                    TestContext.Current.CancellationToken));

        // 当前版本还是被拒之前那一版，内容一个字没动。
        AreaAssignmentTableVersion current = Assert.IsType<AreaAssignmentTableVersion>(
            await harness.Fixture.AreaAssignments.ReadCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, current.Version);
        Assert.Equal("FRONT", current.ByArea["C15-13"].SlotPosition);
        Assert.Equal(["C15-13", "C15-14"], current.ByArea.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 表中间的空行是坏行，不是「没有这一行」。
    /// </summary>
    /// <remarks>
    /// 末尾那个空行是编辑器留下的，忽略它；中间冒出来的空行说明这份表被编辑坏了，静默跳过正是这条动词
    /// 要避免的方向——收下一份读起来和作者写的不一样的表，比退回它危险。
    /// </remarks>
    [Fact]
    public async Task RejectsABlankLineInTheMiddleOfTheTableButIgnoresTheTrailingOne()
    {
        await using AreaAssignmentImportHarness harness = await AreaAssignmentImportHarness.CreateAsync();

        AreaAssignmentImportResult result = await harness.ImportAsync(
            "area,dispatch_zone,slot_position\n"
            + "C15-13,MAP-25-WIRE_TO_GATE,FRONT\n"
            + "\n"
            + "C15-14,MAP-25-WIRE_TO_GATE,REAR\n");

        Assert.Equal(AreaAssignmentImportOutcome.Rejected, result.Outcome);
        AreaAssignmentImportError error = Assert.Single(result.Errors);
        Assert.Equal(AreaAssignmentImportReasonCodes.RowMalformed, error.ReasonCode);
        Assert.Equal(3, error.Line);
    }
}
