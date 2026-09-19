using System.Data.Common;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.DispatchZoneParameterImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// 每区派车参数整表导入（control-server#216，REQ-0198、REQ-0203）的导入服务：校验、整份拒绝、版本、快照与审计、并发与崩溃。
/// </summary>
/// <remarks>
/// 途中追加增量的量纲是计划路径代价增量（毫米），不是时间（规格第 22.2 节第 2 条）；防饥饿阈值是秒。
/// </remarks>
public sealed class DispatchZoneParameterImportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- five kinds of bad table, each rejected whole ------------------------------------------

    public static TheoryData<string, string, int, string> BadTables => new()
    {
        {
            // The unit is in the header, so a table written in the wrong unit is caught here.
            "dispatch_zone,en_route_addition_max_path_cost_increase_seconds,starvation_threshold_seconds\n"
                + $"{ZoneA},20000,600\n",
            DispatchZoneParameterImportReasonCodes.HeaderInvalid, 1, "header names the wrong unit"
        },
        { "", DispatchZoneParameterImportReasonCodes.HeaderInvalid, 1, "empty file" },
        { Csv($"{ZoneA},20000,600", $"{ZoneB},20000"), DispatchZoneParameterImportReasonCodes.RowMalformed, 3, "a field short" },
        { Csv($"{ZoneA},20000,600", $"{ZoneB} ,20000,600"), DispatchZoneParameterImportReasonCodes.RowMalformed, 3, "outer whitespace" },
        { Csv($"{ZoneA},20000,600", "", $"{ZoneB},20000,600"), DispatchZoneParameterImportReasonCodes.RowMalformed, 3, "blank line between rows" },
        { Csv($"{ZoneA},20000,600", ",20000,600"), DispatchZoneParameterImportReasonCodes.RowMalformed, 3, "no zone" },
        { Csv($"{ZoneA},20000,600", "MAP-99-WIRE_TO_GATE,20000,600"), DispatchZoneParameterImportReasonCodes.DispatchZoneNotFound, 3, "unknown zone" },
        { Csv($"{ZoneA},20000,600", $"{ZoneB},1,1", $"{ZoneA},0,600"), DispatchZoneParameterImportReasonCodes.DispatchZoneDuplicated, 4, "zone twice" },
        { Csv($"{ZoneA},-1,600"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "negative increase" },
        { Csv($"{ZoneA},20000,-600"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "negative threshold" },
        { Csv($"{ZoneA},1.5,600"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "not a whole number" },
        { Csv($"{ZoneA},20m,600"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "a unit written into the value" },
        { Csv($"{ZoneA},20000,10min"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "a unit written into the threshold" },
        { Csv($"{ZoneA},+20000,600"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "a sign" },
        { Csv($"{ZoneA},1000001,600"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "increase above 1 km" },
        { Csv($"{ZoneA},20000,86401"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "threshold above 24 h" },
        { Csv($"{ZoneA},20000,0"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "threshold zero has no meaning (REQ-0203)" },
        { Csv($"{ZoneA},99999999999999999999,600"), DispatchZoneParameterImportReasonCodes.ValueInvalid, 2, "does not fit a long" },
    };

    [Theory]
    [MemberData(nameof(BadTables))]
    public async Task EachKindOfBadTableIsRejectedWholeWithItsReasonCodeAndPhysicalLineAndWritesNothing(
        string csv, string reasonCode, int line, string why)
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();

        DispatchZoneParameterImportResult result = await harness.ImportAsync(csv);

        Assert.True(result.Outcome == DispatchZoneParameterImportOutcome.Rejected, why);
        DispatchZoneParameterImportError error = Assert.Single(result.Errors);
        Assert.Equal((reasonCode, line), (error.ReasonCode, error.Line));
        Assert.Null(result.Version);
        Assert.Equal((0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    [Fact]
    public async Task ABadTableOnTopOfAConfiguredOneLeavesTheCurrentVersionAsItWas()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        Assert.Equal(DispatchZoneParameterImportOutcome.Accepted, (await harness.ImportAsync(Csv($"{ZoneA},20000,600"))).Outcome);

        DispatchZoneParameterImportResult rejected = await harness.ImportAsync(Csv($"{ZoneA},30000,600", $"{ZoneA},0,600"));

        Assert.Equal(DispatchZoneParameterImportOutcome.Rejected, rejected.Outcome);
        Assert.Equal(1, rejected.PreviousVersion);
        Assert.Equal((1L, 1L, 1L, 1L), await harness.FootprintAsync());
        Assert.Equal([$"{ZoneA} 20000 600"], Describe(await harness.ReadCurrentAsync()));
    }

    [Fact]
    public async Task EveryErrorInOneTableIsReportedAtOnceWithItsOwnLine()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();

        DispatchZoneParameterImportResult result = await harness.ImportAsync(Csv(
            $"{ZoneA},20000,600",
            $"{ZoneB},20000",
            "MAP-99-WIRE_TO_GATE,20000,600",
            $"{ZoneA},0,600",
            $"{ZoneC},-5,0"));

        Assert.Equal(DispatchZoneParameterImportOutcome.Rejected, result.Outcome);
        Assert.Equal(
            [
                (3, DispatchZoneParameterImportReasonCodes.RowMalformed, (string?)null),
                (4, DispatchZoneParameterImportReasonCodes.DispatchZoneNotFound, null),
                (5, DispatchZoneParameterImportReasonCodes.DispatchZoneDuplicated, null),
                (6, DispatchZoneParameterImportReasonCodes.ValueInvalid, "en_route_addition_max_path_cost_increase_mm"),
                (6, DispatchZoneParameterImportReasonCodes.ValueInvalid, "starvation_threshold_seconds"),
            ],
            result.Errors.Select(error => (error.Line, error.ReasonCode, error.Column)));
        Assert.Equal((0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    // ---- blank is unconfigured, zero forbids ----------------------------------------------------

    [Fact]
    public async Task ABlankValueIsUnconfiguredAndAZeroIncreaseForbidsAdditionAndTheTwoReadBackApart()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();

        DispatchZoneParameterImportResult result = await harness.ImportAsync(Csv(
            $"{ZoneA},0,",
            $"{ZoneB},,600",
            $"{ZoneC},20000,300"));

        Assert.Equal(DispatchZoneParameterImportOutcome.Accepted, result.Outcome);
        Assert.Equal(3, result.EntryCount);
        DispatchZoneParameterTableVersion? current = await harness.ReadCurrentAsync();
        // Ordered by zone: MAP-25-WIRE_TO_GATE, MAP-26-STAGING_TO_WIRE, MAP-26-WIRE_TO_GATE.
        Assert.Equal([$"{ZoneA} 0 -", $"{ZoneC} 20000 300", $"{ZoneB} - 600"], Describe(current));
        Assert.Equal(0L, current!.Zones[ZoneA].EnRouteAdditionMaxPathCostIncrease);
        Assert.Null(current.Zones[ZoneA].StarvationThresholdSeconds);
        Assert.Null(current.Zones[ZoneB].EnRouteAdditionMaxPathCostIncrease);
        Assert.Equal(600L, current.Zones[ZoneB].StarvationThresholdSeconds);
    }

    [Fact]
    public async Task AHeaderOnlyTableIsAWholeTableInWhichEveryZoneIsUnconfigured()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv($"{ZoneA},20000,600"));

        DispatchZoneParameterImportResult cleared = await harness.ImportAsync(Csv());

        Assert.Equal(DispatchZoneParameterImportOutcome.Accepted, cleared.Outcome);
        Assert.Equal(2, cleared.Version?.Version);
        Assert.Empty(Describe(await harness.ReadCurrentAsync()));
        Assert.Equal([$"{ZoneA} 20000 600"], Describe(await harness.ReadVersionAsync(1)));
    }

    // ---- versions: the same content twice, a change, a rollback, immutability -----------------

    [Fact]
    public async Task ImportingTheSameTableAgainWritesNoVersionSnapshotOrAudit()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        string table = Csv($"{ZoneA},20000,600", $"{ZoneB},0,");
        DispatchZoneParameterImportResult first = await harness.ImportAsync(table);

        DispatchZoneParameterImportResult again = await harness.ImportAsync(table);
        // Same values in another row order, and a zone listed with both values blank, which is what not listing it means.
        DispatchZoneParameterImportResult reordered = await harness.ImportAsync(
            Csv($"{ZoneB},0,", $"{ZoneC},,", $"{ZoneA},20000,600"));

        Assert.Equal(DispatchZoneParameterImportOutcome.Accepted, first.Outcome);
        Assert.Equal(DispatchZoneParameterImportOutcome.Unchanged, again.Outcome);
        Assert.Equal(DispatchZoneParameterImportOutcome.Unchanged, reordered.Outcome);
        Assert.Equal((1L, 1L), (again.Version?.Version, again.PreviousVersion));
        Assert.Empty(again.Changes);
        Assert.Equal((1L, 2L, 1L, 1L), await harness.FootprintAsync());
    }

    [Fact]
    public async Task ChangingOneValueWritesANewVersionTheOldOneStaysReadableAndReimportingItIsTheRollback()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        string original = Csv($"{ZoneA},20000,600", $"{ZoneB},0,");
        DispatchZoneParameterImportResult first = await harness.ImportAsync(original);

        DispatchZoneParameterImportResult changed = await harness.ImportAsync(Csv($"{ZoneA},25000,600", $"{ZoneB},0,"));
        DispatchZoneParameterImportResult rolledBack = await harness.ImportAsync(original);

        Assert.Equal((2L, 1L), (changed.Version?.Version, changed.PreviousVersion));
        Assert.Equal(
            [new DispatchZoneParameterChange(ZoneA, new(ZoneA, 20000, 600), new(ZoneA, 25000, 600))],
            changed.Changes);
        Assert.Equal((3L, 2L), (rolledBack.Version?.Version, rolledBack.PreviousVersion));
        Assert.Equal(first.Version!.ContentSha256, rolledBack.Version!.ContentSha256);
        Assert.Equal([$"{ZoneA} 20000 600", $"{ZoneB} 0 -"], Describe(await harness.ReadVersionAsync(1)));
        Assert.Equal([$"{ZoneA} 25000 600", $"{ZoneB} 0 -"], Describe(await harness.ReadVersionAsync(2)));
        Assert.Equal([$"{ZoneA} 20000 600", $"{ZoneB} 0 -"], Describe(await harness.ReadCurrentAsync()));
    }

    [Fact]
    public async Task AnImportedVersionRowAndItsZoneRowsRefuseToBeRewrittenOrDeleted()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv($"{ZoneA},20000,600"));

        await using ControlServerDbContext writer = harness.Open();
        DispatchZoneParameterRow zone = await writer.Set<DispatchZoneParameterRow>().SingleAsync(Token);
        zone.EnRouteAdditionMaxPathCostIncrease = 0;
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
        writer.ChangeTracker.Clear();
        DispatchZoneParameterVersionRow header = await writer.Set<DispatchZoneParameterVersionRow>().SingleAsync(Token);
        header.ContentSha256 = "rewritten";
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
        writer.ChangeTracker.Clear();
        writer.Remove(await writer.Set<DispatchZoneParameterRow>().SingleAsync(Token));
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));

        Assert.Equal([$"{ZoneA} 20000 600"], Describe(await harness.ReadCurrentAsync()));
    }

    // ---- snapshot and audit ----------------------------------------------------------------------

    [Fact]
    public async Task EveryVersionHasOneGovernedSnapshotOfItsContentAndOneBusinessAuditNamingIt()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv($"{ZoneA},20000,600"));
        await harness.ImportAsync(Csv($"{ZoneA},20000,600"));
        await harness.ImportAsync(Csv($"{ZoneA},0,600"));

        Assert.Equal(["v1 snapshot=1 audit=1", "v2 snapshot=1 audit=1"], await harness.GovernanceTrailAsync());
        Assert.Equal((2L, 2L, 2L, 2L), await harness.FootprintAsync());
    }

    // ---- dry run ---------------------------------------------------------------------------------

    [Fact]
    public async Task ADryRunWritesNothingAndPreviewsOnlyTheZonesWhoseValuesChange()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv($"{ZoneA},20000,600", $"{ZoneB},0,"));

        DispatchZoneParameterImportResult preview = await harness.ImportAsync(
            Csv($"{ZoneA},20000,600", $"{ZoneC},15000,"), dryRun: true);

        Assert.Equal((DispatchZoneParameterImportOutcome.Accepted, true), (preview.Outcome, preview.DryRun));
        Assert.Null(preview.Version);
        Assert.Equal(1, preview.PreviousVersion);
        Assert.Equal(
            [
                // Ordered by zone: MAP-26-STAGING_TO_WIRE before MAP-26-WIRE_TO_GATE.
                new DispatchZoneParameterChange(ZoneC, null, new(ZoneC, 15000, null)),
                new DispatchZoneParameterChange(ZoneB, new(ZoneB, 0, null), null),
            ],
            preview.Changes);
        Assert.Equal((1L, 2L, 1L, 1L), await harness.FootprintAsync());
    }

    // ---- crash before commit ---------------------------------------------------------------------

    [Fact]
    public async Task ACrashBeforeCommitLeavesNoVersionRowZoneRowSnapshotOrAudit()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        CrashOnCommit crash = new();

        await using (ControlServerDbContext context = harness.Open(crash))
        {
            await Assert.ThrowsAsync<ProcessCrashed>(() => Service(context).ImportAsync(
                Csv($"{ZoneA},20000,600", $"{ZoneB},0,"), dryRun: false, Imported, Token));
        }

        Assert.Equal((1L, 2L, 1L, 1L), crash.FootprintInsideTheTransaction);
        Assert.Equal((0L, 0L, 0L, 0L), await harness.FootprintAsync());
        DispatchZoneParameterImportResult retried = await harness.ImportAsync(Csv($"{ZoneA},20000,600", $"{ZoneB},0,"));
        Assert.Equal(1, retried.Version?.Version);
        Assert.Equal(["v1 snapshot=1 audit=1"], await harness.GovernanceTrailAsync());
    }

    // ---- two imports at once ---------------------------------------------------------------------

    /// <summary>
    /// Both imports read the current version before either committed; the other one commits version 1 first; this one then
    /// takes "current highest + 1" as 1 too and collides. It must lose whole: no second snapshot, no second audit, no zone row
    /// of its own.
    /// </summary>
    /// <remarks>
    /// The interleaving is placed, not hoped for. SQLite serialises writers (<c>BEGIN IMMEDIATE</c>), so on this database the
    /// second writer normally waits and reads the fresh highest; the stale read here is what the store's transaction still has to
    /// survive when two writers do read the same highest, which is the case its primary key exists for.
    /// </remarks>
    [Fact]
    public async Task TwoImportsThatReadTheSameHighestVersionLeaveOneWholeVersionAndTheLoserRollsBackEntirely()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        StaleHighestVersion interleave = new(async () =>
        {
            DispatchZoneParameterImportResult winner = await harness.ImportAsync(Csv($"{ZoneA},20000,600"));
            Assert.Equal(1, winner.Version?.Version);
        });

        await using (ControlServerDbContext loser = harness.Open(interleave))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Service(loser).ImportAsync(
                Csv($"{ZoneA},0,", $"{ZoneB},30000,900", $"{ZoneC},,300"), dryRun: false, Imported, Token));
        }

        Assert.True(interleave.WinnerRan && interleave.ReadStale, "The interleaving was not placed.");
        Assert.Equal((1L, 1L, 1L, 1L), await harness.FootprintAsync());
        Assert.Equal([$"{ZoneA} 20000 600"], Describe(await harness.ReadCurrentAsync()));
        Assert.Equal(["v1 snapshot=1 audit=1"], await harness.GovernanceTrailAsync());
    }

    [Fact]
    public async Task TwoImportsRunningTogetherBothLandAsWholeVersionsOneAfterTheOther()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();

        DispatchZoneParameterImportResult[] results = await Task.WhenAll(
            Task.Run(() => harness.ImportAsync(Csv($"{ZoneA},20000,600")), Token),
            Task.Run(() => harness.ImportAsync(Csv($"{ZoneB},30000,900", $"{ZoneC},0,")), Token));

        Assert.Equal([1L, 2L], results.Select(result => result.Version!.Version).Order());
        Assert.Equal(["v1 snapshot=1 audit=1", "v2 snapshot=1 audit=1"], await harness.GovernanceTrailAsync());
        Assert.Equal((2L, 3L, 2L, 2L), await harness.FootprintAsync());
    }

    // ---- an import in the middle of a dispatch round ---------------------------------------------

    /// <summary>
    /// A dispatch round reads the parameter version once. An import that lands after that read does not change what the round
    /// holds, the version it read stays readable as it was, and the next round's read is the new version with a higher number.
    /// </summary>
    [Fact]
    public async Task AnImportDuringARoundLeavesThatRoundOnTheVersionItReadAndTheNextRoundReadsTheNewOne()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv($"{ZoneA},20000,600"));
        await using ControlServerDbContext round = harness.Open();
        DispatchZoneParameterStore roundReader = Store(round);
        DispatchZoneParameterTableVersion held = (await roundReader.ReadCurrentAsync(Token))!;

        DispatchZoneParameterImportResult midRound = await harness.ImportAsync(Csv($"{ZoneA},0,600"));

        Assert.Equal(1, held.Version);
        Assert.Equal([$"{ZoneA} 20000 600"], Describe(held));
        Assert.Equal([$"{ZoneA} 20000 600"], Describe(await roundReader.ReadVersionAsync(held.Version, Token)));
        DispatchZoneParameterTableVersion next = (await Store(harness.Open()).ReadCurrentAsync(Token))!;
        Assert.Equal(midRound.Version!.Version, next.Version);
        Assert.True(next.Version > held.Version);
        Assert.Equal([$"{ZoneA} 0 600"], Describe(next));
    }

    private sealed class ProcessCrashed : Exception;

    /// <summary>Counts what the import staged inside its transaction, then crashes before the commit.</summary>
    private sealed class CrashOnCommit : DbTransactionInterceptor
    {
        public (long, long, long, long) FootprintInsideTheTransaction { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            FootprintInsideTheTransaction = (
                await CountAsync(transaction, "SELECT COUNT(*) FROM DispatchZoneParameterVersions", cancellationToken),
                await CountAsync(transaction, "SELECT COUNT(*) FROM DispatchZoneParameters", cancellationToken),
                await CountAsync(
                    transaction,
                    "SELECT COUNT(*) FROM GovernedConfigurationSnapshots WHERE ObjectKind = 'DispatchZoneParameters'",
                    cancellationToken),
                await CountAsync(
                    transaction,
                    $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{DispatchZoneParameterGovernance.VersionImportedAction}'",
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

    /// <summary>
    /// Lets the other import commit just before this one's transaction begins, then answers this one's read of the highest
    /// version inside that transaction as it stood before the other commit (none).
    /// </summary>
    private sealed class StaleHighestVersion(Func<Task> winner) : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public bool WinnerRan { get; private set; }

        public bool ReadStale { get; private set; }

        public async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (!WinnerRan)
            {
                WinnerRan = true;
                await winner();
            }
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (WinnerRan && !ReadStale && command.Transaction is not null &&
                command.CommandText.Contains("MAX(", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"DispatchZoneParameterVersions\"", StringComparison.Ordinal))
            {
                ReadStale = true;
                command.CommandText = "SELECT NULL";
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
