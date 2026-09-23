using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Scratch probe (not committed): the engine clears STATION_TIMEOUT_DOOR_NOT_CLOSED once the door is proven
/// closed; if the journey was closed by another writer between the engine's read and its save, does the engine's
/// save overwrite the closure's BlockReasonCode?
/// </summary>
public sealed class LostUpdateProbeTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AClosureCommittedBetweenTheEnginesReadAndSaveKeepsItsReason()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await JourneyRuntimeWorkerLoadDeadlineTests.AdvanceToLoadWithStationDeadlineAsync(fixture);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow alarmed = await fixture.RuntimeAsync();
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", alarmed.BlockReasonCode);

        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Context.ChangeTracker.Clear();
        bool injected = false;
        fixture.SaveChanges.FailWhen = written =>
        {
            if (!injected && written.Contains("JourneyRuntimeRow.BlockReasonCode"))
            {
                injected = true;
                SqliteConnection connection = (SqliteConnection)fixture.Context.Database.GetDbConnection();
                using SqliteCommand close = connection.CreateCommand();
                close.CommandText =
                    "UPDATE JourneyRuntimes SET Stage = 'Completed', BlockReasonCode = 'CANCELLED_BY_OPERATOR' " +
                    "WHERE JourneyId = $id";
                close.Parameters.AddWithValue("$id", alarmed.JourneyId);
                Assert.Equal(1, close.ExecuteNonQuery());
            }
            return false;
        };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        fixture.SaveChanges.FailWhen = null;

        JourneyRuntimeRow after = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == alarmed.JourneyId, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"injected={injected}; after: stage {after.Stage}, code {after.BlockReasonCode ?? "(null)"}");
        Assert.True(injected);
        Assert.Equal(JourneyRuntimeStage.Completed, after.Stage);
        Assert.Equal("CANCELLED_BY_OPERATOR", after.BlockReasonCode);
    }
}
