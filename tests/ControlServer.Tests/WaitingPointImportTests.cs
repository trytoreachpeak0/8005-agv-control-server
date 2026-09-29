using System.Data.Common;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// 等待点登记的受治理导入（control-server#388，批次8-17；REQ-0289、REQ-0297，规格 5.4）：整份校验、同内容不出新版本、预演不写库、
/// 预览列出受影响的既有预占与占用、导入不动既有记录、崩溃整体回滚。真 SQLite 文件，真治理发布。
/// </summary>
public sealed class WaitingPointImportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] ThreePoints = ["26,214,等待点1,true,", "26,215,等待点2,true,", "26,216,等待点3,true,"];

    [Fact]
    public async Task AValidTableBecomesAGovernedVersionAndTheSameTableAgainWritesNothingNew()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        WaitingPointImportResult first = await harness.ImportAsync(Csv([.. ThreePoints]));
        WaitingPointImportResult again = await harness.ImportAsync(Csv([.. ThreePoints]));

        Assert.Equal((WaitingPointImportOutcome.Accepted, 1L), (first.Outcome, first.Version!.Version));
        Assert.False(string.IsNullOrEmpty(first.Version.SnapshotId));
        Assert.Equal(["214 ADDED", "215 ADDED", "216 ADDED"], first.Changes.Select(change => $"{change.StationId} {(change.Before is null ? "ADDED" : "?")}"));
        Assert.Equal((WaitingPointImportOutcome.Unchanged, 1L), (again.Outcome, again.Version!.Version));
        Assert.Empty(again.Changes);
        Assert.Equal((1L, 3L, 0L, 1L, 1L), await harness.FootprintAsync());
        Assert.Equal(
            ["26/214 等待点1 on *", "26/215 等待点2 on *", "26/216 等待点3 on *"],
            (await harness.ReadCurrentAsync())!.Points.Select(Describe));
    }

    [Fact]
    public async Task ADryRunReportsWhatItWouldDoAndWritesNothing()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        WaitingPointImportResult preview = await harness.ImportAsync(Csv([.. ThreePoints]), dryRun: true);

        Assert.Equal((WaitingPointImportOutcome.Accepted, true), (preview.Outcome, preview.DryRun));
        Assert.Null(preview.Version);
        Assert.Equal(3, preview.Changes.Count);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    /// <summary>每类错误一条：整份拒绝、原因码对、库里一行不多。</summary>
    [Theory]
    [InlineData("26,999,等待点9,true,", WaitingPointImportReasonCodes.StationNotInCatalog)]
    [InlineData("26,214,等待点九,true,", WaitingPointImportReasonCodes.StationNameMismatch)]
    [InlineData("26,12,N1-3_N1-7,true,", WaitingPointImportReasonCodes.MachineStation)]
    [InlineData("26,305,派工待送取货,true,", WaitingPointImportReasonCodes.FixedTaskStation)]
    [InlineData("26,210,关卡,true,", WaitingPointImportReasonCodes.ReservedRole)]
    [InlineData("26,211,充电点1,true,", WaitingPointImportReasonCodes.ReservedRole)]
    [InlineData("26,212,充电准备点1,true,", WaitingPointImportReasonCodes.ReservedRole)]
    [InlineData("26,214,等待点1,true,", WaitingPointImportReasonCodes.StationDuplicated)]
    [InlineData("26,216,等待点3,true,VK-A;VK-Z", WaitingPointImportReasonCodes.VehicleOutsideFleet)]
    [InlineData("25,216,等待点3,true,", WaitingPointImportReasonCodes.MapMismatch)]
    [InlineData("26,216,等待点3,yes,", WaitingPointImportReasonCodes.RowMalformed)]
    [InlineData("26, 216,等待点3,true,", WaitingPointImportReasonCodes.RowMalformed)]
    [InlineData("26,216,等待点3,true,VK-A;;VK-B", WaitingPointImportReasonCodes.RowMalformed)]
    public async Task EachKindOfErrorRejectsTheWholeTableAndWritesNothing(string badRow, string reasonCode)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.BindStagingStationAsync();

        WaitingPointImportResult result = await harness.ImportAsync(Csv("26,214,等待点1,true,", "26,215,等待点2,true,", badRow));

        Assert.Equal(WaitingPointImportOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Errors, error => error.ReasonCode == reasonCode && error.Line == 4);
        Assert.Null(result.Version);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    [Fact]
    public async Task AWrongHeaderAndACatalogOfAnotherMapAreRejectedToo()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        WaitingPointImportResult header = await harness.ImportAsync("MapId,StationId,Name,Enabled,Scope\n26,214,等待点1,true,\n");
        WaitingPointImportResult catalog = await harness.ImportAsync(Csv([.. ThreePoints]), catalog: Catalog(25));

        Assert.Equal(
            [WaitingPointImportReasonCodes.HeaderInvalid],
            header.Errors.Select(error => error.ReasonCode));
        Assert.Contains(catalog.Errors, error => error.ReasonCode == WaitingPointImportReasonCodes.MapMismatch && error.Line == 0);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    [Fact]
    public async Task EveryErrorInOneTableIsReportedInOneRun()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        WaitingPointImportResult result = await harness.ImportAsync(
            Csv("26,212,充电准备点1,true,", "26,999,等待点9,true,", "26,214,等待点1,true,VK-Z"));

        Assert.Equal(
            [
                $"2 {WaitingPointImportReasonCodes.ReservedRole}",
                $"3 {WaitingPointImportReasonCodes.StationNotInCatalog}",
                $"4 {WaitingPointImportReasonCodes.VehicleOutsideFleet}",
            ],
            result.Errors.Select(error => $"{error.Line} {error.ReasonCode}"));
    }

    /// <summary>
    /// REQ-0297：停用一个已被预占的点，预览列出那条预占并写明它保留到离点对账；导入之后那一行照旧，仍引用预占时的版本，
    /// 判定函数对新承诺说不接。
    /// </summary>
    [Fact]
    public async Task DisablingAReservedPointListsTheReservationKeepsItAndStopsNewCommitments()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv([.. ThreePoints]));
        await harness.ReserveAsync(214, VehicleA, "idle:VK-A:1", version: 1);
        StationExclusivity before = (await harness.ReadExclusivityAsync(214))!;

        WaitingPointImportResult preview = await harness.ImportAsync(
            Csv("26,214,等待点1,false,", "26,215,等待点2,true,", "26,216,等待点3,true,"), dryRun: true);
        WaitingPointImportResult imported = await harness.ImportAsync(
            Csv("26,214,等待点1,false,", "26,215,等待点2,true,", "26,216,等待点3,true,"));

        foreach (WaitingPointImportResult result in (WaitingPointImportResult[])[preview, imported])
        {
            WaitingPointRetainedReference retained = Assert.Single(result.RetainedReferences);
            Assert.Equal((214, StationExclusivityStates.Reserved, VehicleA, 1L),
                (retained.Exclusivity.StationId, retained.Exclusivity.State, retained.Exclusivity.VehicleKey, retained.Exclusivity.WaitingPointVersion));
            Assert.Equal(WaitingPointEligibilityReasons.Disabled, retained.NewVersionReason);
            Assert.Contains("REQ-0297", retained.Retention, StringComparison.Ordinal);
        }
        Assert.Equal(2L, imported.Version!.Version);
        Assert.Equal(before, await harness.ReadExclusivityAsync(214));

        WaitingPointRegistrationVersion current = (await harness.ReadCurrentAsync())!;
        Assert.Equal(
            (false, WaitingPointEligibilityReasons.Disabled),
            Decision(WaitingPointEligibility.Judge(current, Catalog(), Map, 214, VehicleA)));
        Assert.Equal(
            (true, WaitingPointEligibilityReasons.Accepts),
            Decision(WaitingPointEligibility.Judge(current, Catalog(), Map, 215, VehicleA)));

        await using ControlServerDbContext context = harness.Open();
        WaitingPointReservationReference reference = await WaitingPointReservationReader.ReadAsync(
            new StationExclusivityStore(context), new WaitingPointRegistry(context, Publisher(context)), Map, 214, Token);
        Assert.Equal(1L, reference.ReservedUnderVersion!.Version);
        Assert.True(reference.ReservedUnder!.Enabled);
    }

    [Theory]
    [InlineData("26,215,等待点2,true,|26,216,等待点3,true,", WaitingPointEligibilityReasons.NotRegistered)]
    [InlineData("26,214,等待点1,true,VK-B|26,215,等待点2,true,|26,216,等待点3,true,", WaitingPointEligibilityReasons.VehicleNotInScope)]
    public async Task DeletingAReservedPointOrNarrowingItsWhitelistPastTheHolderIsListedToo(string rows, string reason)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv([.. ThreePoints]));
        await harness.ReserveAsync(214, VehicleA, "idle:VK-A:1", version: 1);

        WaitingPointImportResult imported = await harness.ImportAsync(Csv(rows.Split('|')));

        Assert.Equal(reason, Assert.Single(imported.RetainedReferences).NewVersionReason);
        Assert.NotNull(await harness.ReadExclusivityAsync(214));
    }

    [Fact]
    public async Task ImportingOneMapKeepsTheOtherMapsRegistration()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv([.. ThreePoints]));

        WaitingPointImportResult map25 = await harness.ImportAsync(
            Csv("25,214,等待点1,true,"), catalog: Catalog(25), mapId: 25);

        Assert.Equal(WaitingPointImportOutcome.Accepted, map25.Outcome);
        Assert.Equal(
            ["25/214 等待点1 on *", "26/214 等待点1 on *", "26/215 等待点2 on *", "26/216 等待点3 on *"],
            (await harness.ReadCurrentAsync())!.Points.Select(Describe));
    }

    /// <summary>运行中导入让点数少于车辆数：不拒收（坏掉的点必须停得掉），覆盖不足写进结果。</summary>
    [Fact]
    public async Task AnImportThatLeavesTooFewPointsIsAcceptedAndSaysTheNextStartWillBeRefused()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        WaitingPointImportResult result = await harness.ImportAsync(Csv("26,214,等待点1,true,", "26,215,等待点2,false,"));

        Assert.Equal(WaitingPointImportOutcome.Accepted, result.Outcome);
        Assert.Equal((2, 1, 1, false), (result.Coverage!.VehicleCount, result.Coverage.EnabledOnMap, result.Coverage.Assignable, result.Coverage.Sufficient));
    }

    [Fact]
    public async Task ACatalogTheServerLastConfirmedIsReportedAsSuchAndAnyOtherIsStillAccepted()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        WaitingPointImportResult unconfirmed = await harness.ImportAsync(Csv([.. ThreePoints]), dryRun: true);
        await using (ControlServerDbContext context = harness.Open())
        {
            await new CatalogAvailabilityStore(context).RecordCompleteConfirmationAsync(
                Map, TaskTypeStationCatalogEvidence.RevisionOf(Catalog().ContentSha256), 30, 300, Imported, Token);
        }

        WaitingPointImportResult confirmed = await harness.ImportAsync(Csv([.. ThreePoints]), dryRun: true);
        WaitingPointImportResult other = await harness.ImportAsync(
            Csv([.. ThreePoints]), dryRun: true,
            catalog: TaskTypeStationCatalogEvidence.Supplied(Map, [.. Stations, new(217, "等待点4")], Imported));

        Assert.Equal((null, true, false), (unconfirmed.CatalogMatchesServerConfirmation, confirmed.CatalogMatchesServerConfirmation, other.CatalogMatchesServerConfirmation));
        Assert.All((WaitingPointImportResult[])[unconfirmed, confirmed, other], result => Assert.Equal(WaitingPointImportOutcome.Accepted, result.Outcome));
    }

    /// <summary>崩溃点：写到提交前一刻崩溃，版本行、等待点行、白名单行、快照、审计一起回滚。</summary>
    [Fact]
    public async Task ACrashBeforeCommitLeavesNoVersionPointScopeSnapshotOrAudit()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        CrashOnCommit crash = new();

        await using (ControlServerDbContext context = harness.Open(crash))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Token);
            await Assert.ThrowsAsync<ProcessCrashed>(async () =>
            {
                WaitingPointImportResult result = await Service(context).ImportAsync(
                    new WaitingPointImportRequest(Csv("26,214,等待点1,true,VK-A", "26,215,等待点2,true,"), Map, Fleet, Catalog()),
                    dryRun: false, Imported, Token);
                await transaction.CommitAsync(Token);
            });
        }

        Assert.Equal((1L, 2L, 1L, 1L, 1L), crash.FootprintInsideTheTransaction);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    private static (bool, string) Decision(WaitingPointEligibilityDecision decision) => (decision.Accepts, decision.Reason);

    private static string Describe(WaitingPointEntry point) =>
        $"{point.MapId}/{point.StationId} {point.StationName} {(point.Enabled ? "on" : "off")} "
        + (point.VehicleScope.Count == 0 ? "*" : string.Join(";", point.VehicleScope));

    private sealed class ProcessCrashed : Exception;

    /// <summary>Counts what the import staged inside its transaction, then crashes before the commit.</summary>
    private sealed class CrashOnCommit : DbTransactionInterceptor
    {
        public (long, long, long, long, long) FootprintInsideTheTransaction { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            FootprintInsideTheTransaction = (
                await CountAsync(transaction, "SELECT COUNT(*) FROM WaitingPointVersions", cancellationToken),
                await CountAsync(transaction, "SELECT COUNT(*) FROM WaitingPoints", cancellationToken),
                await CountAsync(transaction, "SELECT COUNT(*) FROM WaitingPointVehicleScopes", cancellationToken),
                await CountAsync(
                    transaction,
                    $"SELECT COUNT(*) FROM GovernedConfigurationSnapshots WHERE ObjectKind = '{ControlServer.Domain.GovernedObjectKind.WaitingPointRegistration}'",
                    cancellationToken),
                await CountAsync(
                    transaction,
                    $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{WaitingPointGovernance.VersionImportedAction}'",
                    cancellationToken));
            throw new ProcessCrashed();
        }

        private static async Task<long> CountAsync(DbTransaction transaction, string sql, CancellationToken cancellationToken)
        {
            await using DbCommand count = transaction.Connection!.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = sql;
            return Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
