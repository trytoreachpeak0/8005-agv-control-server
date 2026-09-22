using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 升级那一刻正在装货的那条归属被回填成 <c>LOADING</c>（批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 「这个停靠此刻在装哪一条需求」在本票之前不是落库的状态。本票把它变成归属行上的 <c>LOADING</c>，而
/// <c>JourneyRuntimeEngine</c> 在 <c>AwaitingLoadResult</c> 阶段找不到它就抛——那个 <c>throw</c> 是新立的不变量
/// 「命令与状态要么都在、要么都不在」的护栏，有意为之，不改。所以跨过本提交升级时，一条正停在
/// <c>AwaitingLoadResult</c> 的旅程会每一轮都抛、永久卡住，要人工介入。
/// </para>
/// <para>
/// <b>判据不是「有行被改了」，是「该改的改了、不该改的一个都没动」。</b>只断言目标那条变成 <c>LOADING</c>，
/// 一条把全表刷成 <c>LOADING</c> 的迁移也能通过——而那会让一条还没开始装的需求被当成正在装。
/// </para>
/// </remarks>
public sealed class Batch7LoadingMembershipBackfillMigrationTests
{
    private const string PreviousMigration = "20260920001500_AuditImmutabilityTriggers";

    /// <summary>正在装货的那一条，升级后是 <c>LOADING</c>。</summary>
    private const string LoadingDemandId = "D-LOADING";

    /// <summary>车还在去取货站的路上，一条命令都没发过：它必须原样留在 <c>PENDING_LOAD</c>。</summary>
    private const string OnTheWayDemandId = "D-ON-THE-WAY";

    /// <summary>已经装完的那一条：它已经是 <c>LOADED</c>，迁移不该碰。</summary>
    private const string LoadedDemandId = "D-LOADED";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheDemandBeingLoadedWhenTheServerIsUpgradedIsBackFilledAndNothingElseMoves()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);
        await SeedJourneysAsOldVersionLeftThemAsync(fixture);

        await fixture.Context.Database.MigrateAsync(cancellationToken);

        Assert.Equal(JourneyDemandStatuses.Loading, await StatusAsync(fixture, LoadingDemandId));
        Assert.Equal(JourneyDemandStatuses.PendingLoad, await StatusAsync(fixture, OnTheWayDemandId));
        Assert.Equal(JourneyDemandStatuses.Loaded, await StatusAsync(fixture, LoadedDemandId));
    }

    /// <summary>
    /// 回滚回本提交之前：<c>LOADING</c> 退回 <c>PENDING_LOAD</c>，因为那一版的代码不认识它。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task RollingBackReturnsTheLoadingMembershipToPendingLoad()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);
        await SeedJourneysAsOldVersionLeftThemAsync(fixture);
        await fixture.Context.Database.MigrateAsync(cancellationToken);
        // 先确认 Up 真的写下了 LOADING：没有这一句，Up 失效时这条用例照样绿——它断言的「回滚后是 PENDING_LOAD」
        // 在「从来没被改过」时也成立。前置断言让它测的是回滚，而不是「反正是 PENDING_LOAD」。
        Assert.Equal(JourneyDemandStatuses.Loading, await StatusAsync(fixture, LoadingDemandId));

        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);

        Assert.Equal(JourneyDemandStatuses.PendingLoad, await StatusAsync(fixture, LoadingDemandId));
        Assert.Equal(JourneyDemandStatuses.Loaded, await StatusAsync(fixture, LoadedDemandId));
    }

    /// <summary>
    /// 三趟旧版留下的旅程：一趟正在装、一趟还在路上、一趟已经装完。三条归属此刻都带着旧版写下的状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧版从不写 <c>LOADING</c>，所以「正在装」那一趟的归属也是 <c>PENDING_LOAD</c>——这正是迁移要认出来的那一条，
    /// 而认出它靠的是旅程的阶段，不是归属本身。
    /// </para>
    /// <para>
    /// 种在一个迁到最新的草稿库上，再按目标库此刻（<see cref="PreviousMigration"/>）有的列把行拷过去（control-server#273 起）。
    /// 在目标库上直接用 EF 种，等于拿今天的模型去写旧表：之后任何一张迁移给这几张表加了列，EF 就会去写一列旧表没有的列，
    /// 这条用例便会在与它无关的地方红掉——cs#273 给 <c>JourneyRuntimes</c> 加列时正是这样。
    /// </para>
    /// </remarks>
    private static async Task SeedJourneysAsOldVersionLeftThemAsync(Batch7JourneyFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = Batch7JourneyFixture.Now;
        await using Batch7JourneyFixture scratch = await Batch7JourneyFixture.CreateAsync();
        ControlServerDbContext context = scratch.Context;
        await Batch7JourneyFixture.AcceptAsync(context, LoadingDemandId, "agv-01", "VK-01", now);
        await Batch7JourneyFixture.AcceptAsync(context, OnTheWayDemandId, "agv-02", "VK-02", now);
        await Batch7JourneyFixture.AcceptAsync(context, LoadedDemandId, "agv-03", "VK-03", now);

        await SetStageAsync(context, LoadingDemandId, JourneyRuntimeStage.AwaitingLoadResult);
        await SetStageAsync(context, OnTheWayDemandId, JourneyRuntimeStage.AwaitingPickupArrival);
        // 已经装完的那一趟也停在 AwaitingLoadResult 之后的某个阶段，而它的归属已经是 LOADED：
        // 迁移的谓词必须同时看阶段和归属状态，只看阶段会把它一起刷掉。
        await SetStageAsync(context, LoadedDemandId, JourneyRuntimeStage.AwaitingGateArrival);
        (await context.Set<JourneyDemandRow>()
                .SingleAsync(row => row.DemandId == LoadedDemandId, cancellationToken))
            .Status = JourneyDemandStatuses.Loaded;
        await context.SaveChangesAsync(cancellationToken);

        foreach (string table in await TableNamesAsync(fixture.Connection))
        {
            if (await CountAsync(scratch.Connection, table) > 0 && await CountAsync(fixture.Connection, table) == 0)
            {
                await CopyRowsAsync(scratch.Connection, fixture.Connection, table, await ColumnsAsync(fixture.Connection, table));
            }
        }
    }

    private static async Task SetStageAsync(
        ControlServerDbContext context, string demandId, JourneyRuntimeStage stage) =>
        (await context.JourneyRuntimes.SingleAsync(
            row => row.DemandId == demandId, TestContext.Current.CancellationToken)).Stage = stage;

    private static async Task<string[]> TableNamesAsync(SqliteConnection connection)
    {
        List<string> tables = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }
        return [.. tables];
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string[]> ColumnsAsync(SqliteConnection connection, string table)
    {
        List<string> columns = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        return [.. columns];
    }

    private static async Task CopyRowsAsync(SqliteConnection from, SqliteConnection to, string table, string[] columns)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string list = string.Join(", ", columns.Select(column => $"\"{column}\""));
        await using SqliteCommand select = from.CreateCommand();
        select.CommandText = $"SELECT {list} FROM \"{table}\"";
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using SqliteCommand insert = to.CreateCommand();
            insert.CommandText =
                $"INSERT INTO \"{table}\" ({list}) VALUES ({string.Join(", ", columns.Select((_, index) => $"$p{index}"))})";
            for (int index = 0; index < columns.Length; index++)
            {
                insert.Parameters.AddWithValue($"$p{index}", reader.GetValue(index));
            }
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string> StatusAsync(Batch7JourneyFixture fixture, string demandId)
    {
        ControlServerDbContext context = fixture.NewContext();
        return (await context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken)).Status;
    }
}
