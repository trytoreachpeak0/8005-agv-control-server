using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#728：车载端报来的时刻带它自己的本地偏移（<c>observedAt</c> 是 +08:00），以前原样写进库，SQLite 把
/// <see cref="DateTimeOffset"/> 存成带偏移的文本，同一列里 +08:00 与服务端写的 +00:00 混在一起，按文本排序就差 8 小时。
/// 现场两例都在最后一条需求卸完时的用途释放（<c>LAST_DEMAND_UNLOADED</c>）。这里走真实的卸货结果入口，读库里的原始文本。
/// </summary>
public sealed class VehicleTimesStoredInUtcTests
{
    private const string Demand = "D-728";
    private static readonly TimeSpan VehicleOffset = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;

    /// <summary>The columns an unload result writes from the vehicle's <c>observedAt</c>, the purpose release among them.</summary>
    public static TheoryData<string, string> UnloadResultTimeColumns => new()
    {
        { "VehiclePurposeClaimRecords", "ReleasedAt" },
        { "OperationResults", "ObservedAt" },
        { "OperationResults", "ReceivedAt" },
        { "StationOperations", "CommittedAt" },
        { "UnloadBatches", "CompletedAt" },
        { "StopClosures", "CommittedAt" },
        { "TransportDemandCompletions", "CompletedAt" },
    };

    [Theory]
    [MemberData(nameof(UnloadResultTimeColumns))]
    public async Task AnUnloadResultObservedInTheVehiclesLocalOffsetIsStoredInUtc(string table, string column)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);
        await AddUnloadOperationAsync(fixture);
        DateTimeOffset observedAt = At.AddMinutes(5).ToOffset(VehicleOffset);

        Assert.Equal(OperationResultDisposition.Accepted, await ApplyUnloadResultAsync(fixture, observedAt));

        Assert.Equal(
            VehiclePurposeReleaseReasons.LastDemandUnloaded,
            (await fixture.NewContext().Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).ReleaseReason);
        string stored = await ReadSingleTextAsync(fixture.Connection, table, column);
        Assert.EndsWith("+00:00", stored, StringComparison.Ordinal);
        Assert.Equal(observedAt, DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Only what a save writes is normalised. A row written before the fix keeps its +08:00 text when the save changes another
    /// column of it: rewriting a time nobody changed would touch history, audit rows included, that the save is not about.
    /// </summary>
    [Fact]
    public async Task ASaveLeavesAnEarlierLocalOffsetItDidNotChangeAsItWas()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);
        await AddUnloadOperationAsync(fixture);
        string written = At.ToOffset(VehicleOffset).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
        await using (SqliteCommand command = fixture.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE StationOperations SET CreatedAt = $at";
            command.Parameters.AddWithValue("$at", written);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        await using (ControlServerDbContext writing = fixture.NewContext())
        {
            StationOperationRow operation = await writing.StationOperations.SingleAsync(cancellationToken);
            operation.Status = StationOperationStatus.RecoveryRequired;
            await writing.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal(written, await ReadSingleTextAsync(fixture.Connection, "StationOperations", "CreatedAt"));
    }

    private static async Task AddUnloadOperationAsync(Batch7JourneyFixture fixture)
    {
        await using ControlServerDbContext writing = fixture.NewContext();
        writing.StationOperations.Add(new StationOperationRow
        {
            SlotOperationAttemptId = "unload-" + Demand,
            DemandId = Demand,
            SublotId = "SUBLOT-" + Demand,
            TargetSlotsJson = "[1]",
            OperationType = SlotOperationType.Unload,
            ForcedRecoveryGeneration = 0,
            ContentHash = new string('a', 64),
            Status = StationOperationStatus.Prepared,
            CreatedAt = At
        });
        await writing.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A completed unload result, applied the way the inbox applies it: inside its write transaction.</summary>
    private static async Task<OperationResultDisposition> ApplyUnloadResultAsync(
        Batch7JourneyFixture fixture, DateTimeOffset observedAt)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ControlServerDbContext writing = fixture.NewContext();
        await using var transaction = await writing.Database.BeginTransactionAsync(cancellationToken);
        OperationResultDisposition disposition = await new WireToGateStore(writing).ApplyOperationResultAsync(
            new StationOperationResult(
                "result-" + Demand,
                "unload-" + Demand,
                Demand,
                SlotOperationType.Unload,
                "COMPLETED",
                [new SlotPhysicalEvidence(1, SlotBusinessState.Empty, true, true)],
                true,
                observedAt,
                new string('c', 64),
                new string('e', 64)),
            "agv-01",
            0,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return disposition;
    }

    private static async Task<string> ReadSingleTextAsync(SqliteConnection connection, string table, string column)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM \"{table}\"";
        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }
        return Assert.Single(values);
    }
}
