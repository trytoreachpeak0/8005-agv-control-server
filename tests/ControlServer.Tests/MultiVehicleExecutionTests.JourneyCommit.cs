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
        EventRecordingLogger<JourneyRuntimeEngine>.Entry yieldEntry = Assert.Single(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2191);
        Assert.Contains(writtenJourneyId!, yieldEntry.Message, StringComparison.Ordinal);

        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.All(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(token),
            row => Assert.Equal(JourneyRuntimeEngine.CheckpointWaitReason, row.BlockReasonCode));
    }

    /// <summary>
    /// 车 A 推进时改了车 B 的行（让站就是这样：<c>StationYield.StageTriggerAsync</c> 改的是 <c>active</c> 里 B 那个实例），然后 A 让开
    /// （control-server#357 独立审查必修 2）。A 替 B 做的那个没保存的改动撤回，B 仍被跟踪、这一轮照常推进。
    /// </summary>
    /// <remarks>
    /// 修之前让开把所有挂着改动的条目一律解除跟踪：B 的实例脱离跟踪，轮到 B 时守护核不到它，它的码写不进库。
    /// </remarks>
    [Fact]
    public async Task AVehicleThatYieldsWithdrawsWhatItStagedOnAnotherVehiclesRowAndThatVehicleStillAdvances()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool journeysRead = false;
        string? yieldingJourneyId = null;
        string? otherJourneyId = null;
        JourneyRowLostUpdateTests.OnceAfterReading interleaver = new(text =>
        {
            if (text.Contains("\"JourneyRuntimes\"", StringComparison.Ordinal) &&
                text.Contains("<> 'Completed'", StringComparison.Ordinal))
            {
                journeysRead = true;
                return false;
            }
            return journeysRead && text.Contains("FROM \"SessionRecoveries\"", StringComparison.Ordinal);
        });
        await using FleetFixture fixture = await FleetFixture.CreateAsync(commands: interleaver);
        await fixture.Engine.ExecuteOnceAsync(token);
        fixture.Riot.MovementState = RiotMovementStates.WaitForCheckpoint;
        fixture.Context.ChangeTracker.Clear();

        // 第一次读会话行时正在推进的是 active 里的第一台（按受理时刻排，同时刻按读出的顺序）。
        interleaver.Arm(() =>
        {
            JourneyRuntimeRow[] tracked = [.. fixture.Context.ChangeTracker.Entries<JourneyRuntimeRow>()
                .Select(entry => entry.Entity).OrderBy(row => row.CreatedAt)];
            yieldingJourneyId = tracked[0].JourneyId;
            otherJourneyId = tracked[1].JourneyId;
            tracked[1].YieldTriggeredAt = fixture.Clock.GetUtcNow();
            tracked[1].YieldTriggeredByVehicleKey = tracked[0].VehicleKey;
            using ControlServerDbContext inbound = new(
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(fixture.Context.Database.GetDbConnection()).Options);
            JourneyRuntimeRow row = inbound.JourneyRuntimes.Single(item => item.JourneyId == yieldingJourneyId);
            row.SetBlockReason("LoadCancellationResult_NOT_RECONCILED", fixture.Clock.GetUtcNow());
            inbound.SaveChanges();
        });
        Exception? thrown = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(token));

        Assert.Equal(1, interleaver.Fired);
        Assert.Null(thrown);
        EventRecordingLogger<JourneyRuntimeEngine>.Entry yieldEntry =
            Assert.Single(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2191);
        Assert.Contains(yieldingJourneyId!, yieldEntry.Message, StringComparison.Ordinal);
        JourneyRuntimeRow other = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == otherJourneyId, token);
        Assert.Equal(
            (JourneyRuntimeEngine.CheckpointWaitReason, (DateTimeOffset?)null),
            (other.BlockReasonCode, other.YieldTriggeredAt));
    }
}
