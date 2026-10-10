using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 批次8-16（control-server#387）：车辆占用只剩 <c>VehiclePurposeClaims</c> 一处之后，受理与关闭旅程这两处写入的性质。
/// </summary>
public sealed class Batch8VehicleOccupancyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 同一辆车第二次受理由用途占有的主键挡住。存储里受理之前已不再预读任何占用（租约的预读随租约退役），所以挡住它的
    /// 只有主键：这一条直接调存储，绕开派车轮那一处预读，正是两轮并发时第二个到达数据库的那一刻。
    /// </summary>
    [Fact]
    public async Task ASecondAcceptanceOnAHeldVehicleIsRefusedByTheClaimsKeyAndLeavesNothingOfItBehind()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-1", "agv-01", "VK-01", Batch7JourneyFixture.Now);
        string[] before = await DumpAsync(fixture);

        BusinessIdentityConflictException refusal = await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            Batch7JourneyFixture.AcceptAsync(fixture.NewContext(), "D-2", "agv-01", "VK-01", Batch7JourneyFixture.Now.AddMinutes(1)));

        Assert.Contains("already claimed by another journey", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("VehiclePurposeClaims.VehicleKey", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, await DumpAsync(fixture));
    }

    /// <summary>
    /// 关闭旅程时，删占有行、关记录、写 <c>Completed</c> 在同一次保存里。写记录那一刻数据库拒绝（触发器注入），整次保存回滚：
    /// 车仍被这趟旅程占着、记录仍开着、旅程没有 <c>Completed</c>、需求没有被取消。
    /// </summary>
    [Fact]
    public async Task WhenClosingTheRecordFailsTheClaimTheJourneyAndTheDemandAreAllLeftAsTheyWere()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-1", "agv-01", "VK-01", Batch7JourneyFixture.Now);
        await fixture.RenewContextAsync();
        string[] before = await DumpAsync(fixture);
        await using (SqliteCommand trigger = fixture.Connection.CreateCommand())
        {
            trigger.CommandText =
                """
                CREATE TEMP TRIGGER "inject_record_close" BEFORE UPDATE OF "ReleasedAt" ON "VehiclePurposeClaimRecords"
                BEGIN SELECT RAISE(ABORT, 'injected: closing the claim record fails'); END;
                """;
            await trigger.ExecuteNonQueryAsync(Token);
        }
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(Token);

        await new PickupStopTermination(fixture.Context).StageAsync(
            runtime, "CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now.AddMinutes(5), Token);
        DbUpdateException failure = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Context.SaveChangesAsync(Token));

        Assert.Contains("injected: closing the claim record fails", failure.InnerException?.Message, StringComparison.Ordinal);
        Assert.Equal(before, await DumpAsync(fixture));
        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Equal("journey:D-1", (await read.Set<VehiclePurposeClaimRow>().SingleAsync(Token)).JourneyId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await read.JourneyRuntimes.SingleAsync(Token)).Stage);
    }

    private static async Task<string[]> DumpAsync(Batch7JourneyFixture fixture) =>
    [
        .. (await Task.WhenAll(
                ((string[])
                [
                    "AcceptedDemands", "VehiclePurposeClaims", "VehiclePurposeClaimRecords", "OrderIntents", "JourneyRuntimes",
                    "JourneyDemands", "JourneyStops",
                ]).Select(async table => (await Batch7JourneyFixture.DumpAsync(fixture.Connection, table))
                    .Select(row => $"{table}: {row}"))))
            .SelectMany(rows => rows),
    ];
}
