using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

/// <summary>
/// 升级前留下的普通 <c>*_NOT_RECONCILED</c> 由一次性数据迁移改成不可放行的 <c>*_NOT_RECONCILED_BEFORE_UPGRADE</c>
/// （control-server#505，<c>UnreleasableNotReconciledBlocksBeforeUpgrade</c>），启动时数一遍不可放行的阻塞。
/// </summary>
/// <remarks>
/// 迁移都走真实的 <c>Migrate</c>：先退回上一个迁移，写下旧版本会写的那一行（新代码已经写不出它——需求已终结时它改写另一种后缀），再升上来。
/// 改写只来自迁移本身，用例不替它写。
/// </remarks>
public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    private const string MigrationBefore505 = "20260929114754_Batch9ChargingPersistence";

    /// <summary>
    /// 调度举的放错链：旧版本在已卸的第一条上写下一条无标记的 <c>LoadCorrectionResult_NOT_RECONCILED</c>，盖在第二条的装货恢复之上；第二条是
    /// <c>RecoveryRequired</c>。升级之后第二条修复续行对上了——没有迁移时第 4 条通过、普通码又不再一律不放，旅程被放出，那次纠错没对上的仓位事实就丢了。
    /// 迁移把那一行改成不可放行的码，旅程留在 <c>Blocked</c>、不发离站核验。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AnUnmarkedBlockTheOldVersionLeftIsNotReleasedByARepairResumeAfterTheUpgrade()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(migrate: true);
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBefore505, token);
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                (await context.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                    JourneyDemandStatuses.Unloaded;
                (await context.AcceptedDemands.SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                    DemandExecutionStatus.Succeeded;
                (await context.JourneyRuntimes.SingleAsync(token)).SetBlockReason(
                    "LoadCorrectionResult_NOT_RECONCILED", fixture.Clock.GetUtcNow());
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await fixture.Context.Database.MigrateAsync(token);
            const string upgraded = "LoadCorrectionResult_NOT_RECONCILED_BEFORE_UPGRADE";
            Assert.Equal(upgraded, (await JourneyOfAsync(fixture, SecondDemandId)).BlockReasonCode);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await ResumeLoadAsync(fixture, SecondDemandId);
            JourneyRuntimeRow atResult = await JourneyOfAsync(fixture, SecondDemandId);
            await fixture.RestoreSessionReadyAsync();
            Exception? round = await RunRoundAsync(fixture);
            await RunRoundAsync(fixture);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, SecondDemandId);
            int checks = await CountAsync(fixture, "PreDepartureSafetyCheck") - checksBefore;
            Assert.True(
                (atResult.Stage, atResult.BlockReasonCode, after.Stage, checks) ==
                (JourneyRuntimeStage.Blocked, upgraded, JourneyRuntimeStage.Blocked, 0),
                $"A block the old version left unmarked was released after the upgrade. at-result={atResult.Stage}/" +
                $"{atResult.BlockReasonCode} after-rounds={after.Stage}/{after.BlockReasonCode} checks={checks} round={round?.Message}");
        });
    }

    /// <summary>
    /// 迁移只改「停在 <c>Blocked</c>、码以 <c>_NOT_RECONCILED</c> 结尾」的行，别的一个都不动——只断言目标行变了，一条把全表都加上后缀的迁移也能通过。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "FaultCargoRecoveryResult_NOT_RECONCILED",
        "FaultCargoRecoveryResult_NOT_RECONCILED_BEFORE_UPGRADE")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "LOAD_RESULT_REQUIRES_RECOVERY", "LOAD_RESULT_REQUIRES_RECOVERY")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "LoadCorrectionResult_NOT_RECONCILED_ON_ENDED_DEMAND",
        "LoadCorrectionResult_NOT_RECONCILED_ON_ENDED_DEMAND")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "load_correction_result_not_reconciled", "load_correction_result_not_reconciled")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), null, null)]
    [InlineData(nameof(JourneyRuntimeStage.AwaitingLoadResult), "LoadCorrectionResult_NOT_RECONCILED",
        "LoadCorrectionResult_NOT_RECONCILED")]
    public async Task TheUpgradeRenamesOnlyBlockedNotReconciledCodes(string stage, string? before, string? expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(migrate: true);
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBefore505, token);
        await WriteJourneyAsync(fixture, Enum.Parse<JourneyRuntimeStage>(stage), before);

        await fixture.Context.Database.MigrateAsync(token);

        Assert.Equal(expected, (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 回滚到本票之前：那一版只挡普通的 <c>*_NOT_RECONCILED</c>，不认识的码照常放行。所以迁移改过的行恢复原样，本票之后新写下的
    /// <c>_ON_ENDED_DEMAND</c> 也退回普通码；别的码不动。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("FaultCargoRecoveryResult_NOT_RECONCILED_BEFORE_UPGRADE", "FaultCargoRecoveryResult_NOT_RECONCILED")]
    [InlineData("LoadCorrectionResult_NOT_RECONCILED_ON_ENDED_DEMAND", "LoadCorrectionResult_NOT_RECONCILED")]
    [InlineData("LoadCompensationResult_NOT_RECONCILED", "LoadCompensationResult_NOT_RECONCILED")]
    [InlineData("LOAD_RESULT_REQUIRES_RECOVERY", "LOAD_RESULT_REQUIRES_RECOVERY")]
    public async Task RollingBackLeavesOnlyCodesTheOldVersionStillHolds(string atLatest, string expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(migrate: true);
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await WriteJourneyAsync(fixture, JourneyRuntimeStage.Blocked, atLatest);

        await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBefore505, token);

        Assert.Equal(expected, (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 启动时的只读检查：停在不可放行阻塞码上的旅程，记一条 Warning，写清总数与两种后缀各多少；普通码与非 <c>Blocked</c> 的不算，没有就不记。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "LoadCorrectionResult_NOT_RECONCILED_ON_ENDED_DEMAND", 1, 0)]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "FaultCargoRecoveryResult_NOT_RECONCILED_BEFORE_UPGRADE", 0, 1)]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "FaultCargoRecoveryResult_NOT_RECONCILED", 0, 0)]
    [InlineData(nameof(JourneyRuntimeStage.AwaitingLoadResult), "LoadCorrectionResult_NOT_RECONCILED_ON_ENDED_DEMAND", 0, 0)]
    public async Task StartingUpCountsTheUnreleasableBlocks(string stage, string code, int onEndedDemand, int beforeUpgrade)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(migrate: true);
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await WriteJourneyAsync(fixture, Enum.Parse<JourneyRuntimeStage>(stage), code);
        RecordingLogger logger = new();

        await UnreleasableBlockReport.LogAsync(fixture.Context, logger, token);

        if (onEndedDemand + beforeUpgrade == 0)
        {
            Assert.Empty(logger.Entries);
            return;
        }
        (LogLevel level, EventId eventId, string message) = Assert.Single(logger.Entries);
        Assert.Equal((LogLevel.Warning, 2138), (level, eventId.Id));
        Assert.Equal(
            $"1 journey(s) are blocked under a code no release path lifts: {onEndedDemand} ending in " +
            $"_NOT_RECONCILED_ON_ENDED_DEMAND and {beforeUpgrade} ending in _NOT_RECONCILED_BEFORE_UPGRADE. Each holds its " +
            "vehicle until a person checks the slots and settles it (control-server#505).",
            message);
    }

    private static async Task WriteJourneyAsync(RuntimeFixture fixture, JourneyRuntimeStage stage, string? code)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using (ControlServerDbContext context = fixture.OpenConnectionContext())
        {
            JourneyRuntimeRow row = await context.JourneyRuntimes.SingleAsync(token);
            row.Stage = stage;
            row.SetBlockReason(code, fixture.Clock.GetUtcNow());
            await context.SaveChangesAsync(token);
        }
        fixture.Context.ChangeTracker.Clear();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
