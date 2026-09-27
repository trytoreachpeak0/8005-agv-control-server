using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 三台车，其中一台的旅程行在引擎这一轮读完之后被入站提交过（control-server#357）：只有这一台这一轮让开，另外两台照常推进，
    /// 整轮不抛；下一轮这一台按新行推进。
    /// </summary>
    /// <remarks>
    /// 另两台推进时写的是检查点等待码，那是它们这一轮做了事的可见痕迹。冲突的那一台留着入站写下的码，说明引擎没有拿旧行盖上去。
    /// 把让开改成抛出去（整轮 fail-closed）时这一条红：<c>ExecuteOnceAsync</c> 抛出，冲突那台之后的车这一轮都不推进。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task OneVehiclesJourneyWrittenAfterTheRoundReadItYieldsThatVehicleAloneForTheRound()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string InboundCode = "LoadCancellationResult_NOT_RECONCILED";
        string? writtenJourneyId = null;
        JourneyRowLostUpdateTests.OnceAfterReading interleaver = new(text =>
            text.Contains("\"JourneyRuntimes\"", StringComparison.Ordinal) &&
            text.Contains("<> 'Completed'", StringComparison.Ordinal));
        await using FleetFixture fixture = await FleetFixture.CreateAsync(commands: interleaver);
        await fixture.Engine.ExecuteOnceAsync(token);
        JourneyRuntimeRow[] journeys = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .OrderBy(row => row.AgvId).ToArrayAsync(token);
        Assert.Equal(3, journeys.Length);
        fixture.Riot.MovementState = RiotMovementStates.WaitForCheckpoint;
        fixture.Context.ChangeTracker.Clear();

        // 引擎这一轮读完全部旅程的那一刻，入站写了其中一台（取中间那台，前后各有一台）。写法照入站：经 SaveChanges，版本由钩子加一。
        interleaver.Arm(() =>
        {
            using ControlServerDbContext inbound = new(
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(fixture.Context.Database.GetDbConnection()).Options);
            JourneyRuntimeRow row = inbound.JourneyRuntimes.Single(item => item.JourneyId == journeys[1].JourneyId);
            row.SetBlockReason(InboundCode, fixture.Clock.GetUtcNow());
            inbound.SaveChanges();
            writtenJourneyId = row.JourneyId;
        });
        Exception? thrown = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(token));

        Assert.Equal(1, interleaver.Fired);
        Assert.Null(thrown);
        Dictionary<string, string?> codes = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .ToDictionaryAsync(row => row.JourneyId, row => row.BlockReasonCode, token);
        Assert.Equal(
            journeys.Select(row => row.JourneyId == writtenJourneyId ? InboundCode : JourneyRuntimeEngine.CheckpointWaitReason),
            journeys.Select(row => codes[row.JourneyId]));
        Assert.Single(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2191);

        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.All(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(token),
            row => Assert.Equal(JourneyRuntimeEngine.CheckpointWaitReason, row.BlockReasonCode));
    }
}
