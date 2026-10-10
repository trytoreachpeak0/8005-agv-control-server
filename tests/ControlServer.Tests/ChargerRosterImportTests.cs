using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.ChargingGovernanceHarness;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// 充电桩名册的受治理导入（control-server#400，批次9-02；REQ-0171、REQ-0288，规格 5.5）：整份校验、零条目合法、预演不写库、
/// 置空与启用各是一次版本导入并留审计、同内容不出新版本但仍记「已是此内容」、输出列出进行中的充电周期与预占且不动它们、崩溃整体回滚。
/// 真 SQLite 文件，真治理发布。
/// </summary>
public sealed class ChargerRosterImportTests
{
    [Fact]
    public async Task ARosterRegisteringStation211OnMap26BecomesAGovernedVersionWithASnapshotAndAnAudit()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        ChargerRosterImportResult result = await harness.ImportRosterAsync(Roster211());

        Assert.Equal((ChargerRosterImportOutcome.Accepted, 1L, 1), (result.Outcome, result.Version!.Version, result.EntryCount));
        Assert.False(string.IsNullOrEmpty(result.Version.SnapshotId));
        ChargerRosterEntry charger = Assert.Single(result.Version.Chargers);
        Assert.Equal((26, 211, "充电点1", (int?)212, (int?)212), (charger.MapId, charger.StationId, charger.StationName, charger.EntryStationId, charger.ExitStationId));
        Assert.Equal(("Zhengyu Shao", "test: charger roster"), (result.Version.Approval.ApprovedBy, result.Version.Approval.ApprovalBasis));
        ChargerRosterChange change = Assert.Single(result.Changes);
        Assert.Equal((211, true), (change.StationId, change.Before is null));
        Assert.Null(result.WindowCanClose);
        Assert.Equal((1L, 1L, 0L, 1L, 1L, 0L), await harness.RosterFootprintAsync());
    }

    /// <summary>
    /// 桩的身份只来自名册文件，校验不按站点类型或站名认桩：名字里不带「充电」的站 216 与带「充电」的 212 一样能登记——
    /// 212 不是桩是现场事实，挡它的是登记的人，不是这里的名字规则。
    /// </summary>
    [Fact]
    public async Task NoStationIsRecognisedOrRefusedAsAChargerByItsName()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        ChargerRosterImportResult result = await harness.ImportRosterAsync(RosterFile(Charger(212, "充电准备点1"), Charger(216, "等待点3")));

        Assert.Equal(ChargerRosterImportOutcome.Accepted, result.Outcome);
        Assert.Equal([212, 216], result.Version!.Chargers.Select(charger => charger.StationId));
    }

    /// <summary>每类错误一条，整份拒绝、一行都不写。</summary>
    [Theory]
    [InlineData("map", ChargerRosterImportReasonCodes.MapMismatch)]
    [InlineData("catalog", ChargerRosterImportReasonCodes.StationNotInCatalog)]
    [InlineData("entry", ChargerRosterImportReasonCodes.StationNotInCatalog)]
    [InlineData("name", ChargerRosterImportReasonCodes.StationNameMismatch)]
    [InlineData("duplicate", ChargerRosterImportReasonCodes.StationDuplicated)]
    [InlineData("waiting", ChargerRosterImportReasonCodes.WaitingPoint)]
    [InlineData("fixed", ChargerRosterImportReasonCodes.FixedTaskStation)]
    [InlineData("machine", ChargerRosterImportReasonCodes.MachineStation)]
    [InlineData("fleet", ChargerRosterImportReasonCodes.VehicleOutsideFleet)]
    [InlineData("malformed", ChargerRosterImportReasonCodes.FileMalformed)]
    [InlineData("extra-field", ChargerRosterImportReasonCodes.FileMalformed)]
    [InlineData("catalog-map", ChargerRosterImportReasonCodes.MapMismatch)]
    public async Task EachClassOfErrorRejectsTheWholeFileAndWritesNothing(string kind, string reasonCode)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv("26,214,等待点1,true,"));
        await harness.BindStagingStationAsync();
        (long, long, long, long, long, long) before = await harness.RosterFootprintAsync();
        RiotMapStationCatalogSnapshot? catalog = null;
        string file = kind switch
        {
            "map" => RosterFile(Charger(211, "充电点1", mapId: 25)),
            "catalog" => RosterFile(Charger(999, "充电点9")),
            "entry" => RosterFile(Charger(211, "充电点1", entry: 998)),
            "name" => RosterFile(Charger(211, "充电桩")),
            "duplicate" => RosterFile(Charger(211, "充电点1"), Charger(211, "充电点1")),
            "waiting" => RosterFile(Charger(214, "等待点1")),
            "fixed" => RosterFile(Charger(305, "派工待送取货")),
            "machine" => RosterFile(Charger(12, "N1-3_N1-7")),
            "fleet" => Roster211("VK-Z"),
            "malformed" => "{ not json",
            "extra-field" => Roster211().Replace("\"chargers\"", "\"type\":1,\"chargers\"", StringComparison.Ordinal),
            _ => Roster211(),
        };
        if (kind == "catalog-map")
        {
            catalog = Catalog(25);
        }

        ChargerRosterImportResult result = await harness.ImportRosterAsync(file, catalog: catalog);

        Assert.Equal(ChargerRosterImportOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Errors, error => error.ReasonCode == reasonCode);
        Assert.Null(result.Version);
        Assert.Equal(before, await harness.RosterFootprintAsync());
    }

    [Fact]
    public async Task AFileWithSeveralErrorsReportsThemAllAtOnce()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv("26,214,等待点1,true,"));

        ChargerRosterImportResult result = await harness.ImportRosterAsync(RosterFile(
            Charger(999, "不存在"), Charger(214, "等待点1"), Charger(211, "充电点1", scope: ["VK-Z"])));

        Assert.Equal(
            [ChargerRosterImportReasonCodes.StationNotInCatalog, ChargerRosterImportReasonCodes.WaitingPoint, ChargerRosterImportReasonCodes.VehicleOutsideFleet],
            result.Errors.Select(error => error.ReasonCode));
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await harness.RosterFootprintAsync());
    }

    [Fact]
    public async Task ADryRunPreviewsTheChangeAndWritesNothing()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        ChargerRosterImportResult preview = await harness.ImportRosterAsync(Roster211(), dryRun: true);

        Assert.Equal((ChargerRosterImportOutcome.Accepted, true, (ChargerRosterVersion?)null), (preview.Outcome, preview.DryRun, preview.Version));
        Assert.Equal(211, Assert.Single(preview.Changes).StationId);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await harness.RosterFootprintAsync());
    }

    /// <summary>零条目不是错误：它就是名册置空。从「一版都没有」导入空名册写成版本 1，名册从此是「明确置空」而不是「从没登记」。</summary>
    [Fact]
    public async Task AnEmptyRosterOntoNothingIsVersionOneAndEmptyingAgainIsUnchangedButAudited()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        ChargerRosterImportResult first = await harness.ImportRosterAsync(EmptyRoster());
        ChargerRosterImportResult again = await harness.ImportRosterAsync(EmptyRoster());

        Assert.Equal((ChargerRosterImportOutcome.Accepted, 1L, 0), (first.Outcome, first.Version!.Version, first.EntryCount));
        Assert.Empty(first.Version.Chargers);
        Assert.Equal((ChargerRosterImportOutcome.Unchanged, 1L), (again.Outcome, again.Version!.Version));
        Assert.Equal((true, true), (first.WindowCanClose, again.WindowCanClose));
        Assert.Equal((1L, 0L, 0L, 1L, 1L, 1L), await harness.RosterFootprintAsync());
    }

    /// <summary>置空与启用各是一次版本导入：211 → 空 → 211，三个版本、三条写版本审计，当前版本依次是 {211}、{}、{211}。</summary>
    [Fact]
    public async Task EmptyingAndReEnablingAreEachAGovernedVersionWithTheirOwnAudit()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportRosterAsync(Roster211());

        ChargerRosterImportResult emptied = await harness.ImportRosterAsync(EmptyRoster());
        ChargerRosterVersion? afterEmptying = await ReadCurrentRosterAsync(harness);
        ChargerRosterImportResult enabled = await harness.ImportRosterAsync(Roster211());
        ChargerRosterVersion? afterEnabling = await ReadCurrentRosterAsync(harness);

        Assert.Equal((ChargerRosterImportOutcome.Accepted, 2L, 1L), (emptied.Outcome, emptied.Version!.Version, emptied.PreviousVersion));
        ChargerRosterChange removed = Assert.Single(emptied.Changes);
        Assert.Equal((211, true), (removed.StationId, removed.After is null));
        Assert.Equal((2L, 0), (afterEmptying!.Version, afterEmptying.Chargers.Count));
        Assert.Equal((ChargerRosterImportOutcome.Accepted, 3L), (enabled.Outcome, enabled.Version!.Version));
        Assert.Equal((3L, 211), (afterEnabling!.Version, Assert.Single(afterEnabling.Chargers).StationId));
        Assert.Equal((3L, 2L, 0L, 3L, 3L, 0L), await harness.RosterFootprintAsync());
    }

    /// <summary>
    /// 关窗时还有一个进行中的周期：输出列出周期与桩上的预占、说「窗口还不能关」；周期行、预占行、用途占有逐字不变——导入不取消、不释放、不结束。
    /// </summary>
    [Fact]
    public async Task EmptyingWhileACycleIsUnderWayListsItAndLeavesItsRowsExactlyAsTheyWere()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportRosterAsync(Roster211());
        await harness.StartCycleAsync("C-1", VehicleA, rosterVersion: 1, policyVersion: 1);
        string cyclesBefore = await harness.DumpAsync("ChargingCycles");
        string stationsBefore = await harness.DumpAsync("StationExclusivities");
        string purposesBefore = await harness.DumpAsync("VehiclePurposeClaims");

        ChargerRosterImportResult preview = await harness.ImportRosterAsync(EmptyRoster(), dryRun: true);
        ChargerRosterImportResult emptied = await harness.ImportRosterAsync(EmptyRoster());

        foreach (ChargerRosterImportResult result in (ChargerRosterImportResult[])[preview, emptied])
        {
            ChargingCycle cycle = Assert.Single(result.InProgress.OpenCycles);
            Assert.Equal(("C-1", VehicleA, 211, ChargingCycleWireStates.Allocated, 1L), (cycle.CycleId, cycle.VehicleKey, cycle.StationId, cycle.WireState, cycle.ChargerRosterVersion));
            StationExclusivity reservation = Assert.Single(result.InProgress.ChargerReservations);
            Assert.Equal((211, VehicleA, StationExclusivityStates.Reserved), (reservation.StationId, reservation.VehicleKey, reservation.State));
            Assert.False(result.WindowCanClose);
        }
        Assert.Equal(2L, emptied.Version!.Version);
        Assert.Equal(cyclesBefore, await harness.DumpAsync("ChargingCycles"));
        Assert.Equal(stationsBefore, await harness.DumpAsync("StationExclusivities"));
        Assert.Equal(purposesBefore, await harness.DumpAsync("VehiclePurposeClaims"));
    }

    [Fact]
    public async Task TheSameRosterAgainWritesNoVersionButRecordsTheImportInTheAudit()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportRosterAsync(Roster211());

        ChargerRosterImportResult preview = await harness.ImportRosterAsync(Roster211(), dryRun: true);
        ChargerRosterImportResult again = await harness.ImportRosterAsync(Roster211());

        Assert.Equal((ChargerRosterImportOutcome.Unchanged, ChargerRosterImportOutcome.Unchanged), (preview.Outcome, again.Outcome));
        Assert.Equal(1L, again.Version!.Version);
        // The preview recorded nothing; the import did.
        Assert.Equal((1L, 1L, 0L, 1L, 1L, 1L), await harness.RosterFootprintAsync());
    }

    /// <summary>崩溃点：版本行、桩行、快照与审计在同一个事务里，提交前一刻崩溃就一起不在。</summary>
    [Fact]
    public async Task ACrashBeforeCommitLeavesNoVersionChargerSnapshotOrAudit()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        CrashOnCommit crash = new("SELECT (SELECT COUNT(*) FROM ChargerRosterVersions) + (SELECT COUNT(*) FROM ChargerRosterEntries) "
            + "+ (SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = 'CHARGER_ROSTER_VERSION_IMPORTED')");

        await using (ControlServerDbContext context = harness.Open(crash))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Token);
            await Assert.ThrowsAsync<ProcessCrashed>(async () =>
            {
                await RosterService(context).ImportAsync(
                    new ChargerRosterImportRequest(Roster211(), Map, Fleet, Catalog()), dryRun: false, Now, Token);
                await transaction.CommitAsync(Token);
            });
        }

        Assert.Equal(3L, crash.CountInsideTheTransaction);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await harness.RosterFootprintAsync());
    }

    /// <summary>「已是此内容」的审计也在调用方的事务里：提交前崩溃，它同样不在。</summary>
    [Fact]
    public async Task ACrashBeforeCommitOfAnUnchangedImportLeavesNoAudit()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportRosterAsync(EmptyRoster());
        CrashOnCommit crash = new($"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{ChargerRosterImportAudit.UnchangedAction}'");

        await using (ControlServerDbContext context = harness.Open(crash))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Token);
            await Assert.ThrowsAsync<ProcessCrashed>(async () =>
            {
                await RosterService(context).ImportAsync(
                    new ChargerRosterImportRequest(EmptyRoster(), Map, Fleet, Catalog()), dryRun: false, Now, Token);
                await transaction.CommitAsync(Token);
            });
        }

        Assert.Equal(1L, crash.CountInsideTheTransaction);
        Assert.Equal((1L, 0L, 0L, 1L, 1L, 0L), await harness.RosterFootprintAsync());
    }

    private static async Task<ChargerRosterVersion?> ReadCurrentRosterAsync(WaitingPointImportHarness harness)
    {
        await using ControlServerDbContext context = harness.Open();
        GovernanceStore governance = Governance(context);
        return await new ChargerRosterStore(context, new GovernedConfigurationPublisher(governance, governance)).ReadCurrentAsync(Token);
    }
}
