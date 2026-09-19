using System.Data.Common;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 批次 7 建表票（control-server#206）的受理事务：新从属行与需求、租约、旅程同一次提交，引擎看到的一切与改动前逐字相同。
/// </summary>
public sealed class Batch7JourneyAcceptanceTests
{
    [Fact]
    public async Task TwoJourneysOnOneVehicleKeepTheRevisionsAndMessageIdsTheyHadBeforeTheBatch7Migration()
    {
        // The expected values were produced by running this very test on fp/v2-impl@e74c0058, before any batch 7 change.
        // They are literals on purpose: the ticket's promise is that nothing the peer or RIoT sees changes by a byte.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        DateTimeOffset now = Batch7JourneyFixture.Now;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-701", "agv-01", "VK-01", now);
        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, "D-701", now.AddMinutes(10));
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-702", "agv-01", "VK-01", now.AddMinutes(11));

        JourneyRuntimeRow[] rows = await fixture.NewContext().JourneyRuntimes.AsNoTracking()
            .OrderBy(row => row.DemandId).ToArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "D-701 1/1/1 "
                + "beabe401-83fd-5755-b173-811df36cfe3c,"
                + "6654d4b6-ca53-2a5b-bf22-86bbbb42b3dd,"
                + "W2G-D-701-PICKUP-1,"
                + "3ef654c9-9820-8057-ae0d-d9bdf6bf895e,"
                + "W2G-D-701-GATE-1,"
                + "eb156f91-7a55-bb50-b42d-8332bf14d873,"
                + "9991484a-8345-5053-bc2d-ea6d84657c1f,"
                + "551c7463-aadb-f454-9170-bead71868576,"
                + "a5477e4b-2df8-335d-88fa-1c19d164e6ab,"
                + "620cc856-24a8-155c-9eaf-decd306f43d8,"
                + "00a69515-5354-3e54-a3ca-c3fd3bc4277c,"
                + "e0c965cd-4267-6a54-89fc-43b9c8b5c25e,"
                + "b69d7506-2d68-c05d-9d81-df126eb0bd3d,"
                + "29619241-3ee1-595b-b410-fb574c36cf20,"
                + "f7a1c425-aee2-4159-96ca-9a66ff6695c8,"
                + "07f84288-75de-d852-8d54-8e5a62968169,"
                + "39ea8fc9-fe2c-cb59-9abe-762f8706a925,"
                + "a30dcf80-0140-da57-9373-e5ee224c96c7",
                "D-702 3/3/4 "
                + "f3109db8-dfc9-d353-901c-77013d77ba05,"
                + "073a066e-0440-3052-b1f4-5c569ddd6479,"
                + "W2G-D-702-PICKUP-1,"
                + "b963c081-6eef-1d5f-a8ed-7185d6c3112c,"
                + "W2G-D-702-GATE-1,"
                + "0c2626eb-6d20-f350-b23b-593d7a8d79c0,"
                + "5c429dbd-801a-2e56-bfff-72da193193fb,"
                + "f7f11e39-e723-c357-9ed5-96ec2ecf95d6,"
                + "10a5ad7b-8ea3-605f-bddd-25c5ca71b637,"
                + "31014bfc-7e2b-4552-89a8-18a116d7bd10,"
                + "71086476-0c03-8956-888f-cf81a21cbb82,"
                + "6b7a7784-cbff-635b-9ee1-ae271f7890db,"
                + "a7c35e9e-c1ad-7251-889b-43081530371b,"
                + "8a71a4f1-3d67-d55a-a54b-ce22a6eb575f,"
                + "25675fb3-eff7-2453-b9ed-11a7972d59d1,"
                + "f02f70ca-79cc-ea53-a948-365db14991ff,"
                + "48e53eea-6b2d-c955-8296-7e336d4c74d4,"
                + "e37ab983-5e39-245c-9fb9-115fbc1d8a1a",
            ],
            rows.Select(Describe));

        // The counter records the seed the second journey was given, which is the highest the vehicle has stored.
        VehicleSnapshotRevisionRow counter = await fixture.NewContext().Set<VehicleSnapshotRevisionRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("agv-01 3/3/4", $"{counter.AgvId} {counter.VehicleBusinessRevision}/{counter.WorklistRevision}/{counter.PlanRevision}");
    }

    [Fact]
    public async Task AcceptingAJourneyWritesItsStopsItsDemandItsPurposeClaimAndTheCounterInTheOneSaveThatWritesTheJourney()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        SaveCounter saves = new();
        await using ControlServerDbContext context = fixture.NewContext(saves);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Batch7JourneyFixture.AcceptAsync(context, "D-711", "agv-01", "VK-01", Batch7JourneyFixture.Now);

        Assert.Equal(1, saves.Count);
        await using ControlServerDbContext read = fixture.NewContext();
        JourneyRuntimeRow journey = await read.JourneyRuntimes.AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal("journey:D-711", journey.JourneyId);
        Assert.Equal(
            ["journey:D-711|PICKUP 1 PICKUP PENDING", "journey:D-711|UNLOAD 2 UNLOAD PENDING"],
            (await read.Set<JourneyStopRow>().AsNoTracking().ToArrayAsync(cancellationToken))
                .OrderBy(stop => stop.Sequence)
                .Select(stop => $"{stop.StopId} {stop.Sequence} {stop.StopRole} {stop.Status}"));
        JourneyDemandRow demand = await read.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal(
            "journey:D-711 D-711 PENDING_LOAD journey:D-711|PICKUP journey:D-711|UNLOAD",
            $"{demand.JourneyId} {demand.DemandId} {demand.Status} {demand.PickupStopId} {demand.UnloadStopId}");
        VehiclePurposeClaimRow claim = await read.Set<VehiclePurposeClaimRow>().AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal(
            $"VK-01 TRANSPORT journey:D-711 {Batch7JourneyFixture.Now:O}",
            $"{claim.VehicleKey} {claim.Purpose} {claim.JourneyId} {claim.ClaimedAt:O}");
        VehicleDispatchLeaseRow lease = await read.VehicleDispatchLeases.AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal("journey:D-711", lease.JourneyId);
        VehicleSnapshotRevisionRow counter =
            await read.Set<VehicleSnapshotRevisionRow>().AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal("agv-01 1/1/1", $"{counter.AgvId} {counter.VehicleBusinessRevision}/{counter.WorklistRevision}/{counter.PlanRevision}");
    }

    [Fact]
    public async Task ANewlyAcceptedJourneyCannotBeToldApartFromTheSameJourneyBackFilledByTheMigration()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-712", "agv-01", "VK-01", Batch7JourneyFixture.Now);
        await fixture.RenewContextAsync();
        string[] tables =
            ["JourneyStops", "JourneyDemands", "VehiclePurposeClaims", "VehicleSnapshotRevisions", "JourneyRuntimes", "VehicleDispatchLeases"];
        Dictionary<string, string[]> written = [];
        foreach (string table in tables)
        {
            written[table] = await Batch7JourneyFixture.DumpAsync(fixture.Connection, table);
        }

        // Down drops every batch 7 table and column; Up rebuilds them from the batch 6 columns alone.
        await fixture.Context.GetService<IMigrator>().MigrateAsync(
            Batch7MigrationDisciplineTests.Batch6Migration, cancellationToken);
        await fixture.Context.Database.MigrateAsync(cancellationToken);

        foreach (string table in tables)
        {
            Assert.Equal(written[table], await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task WhenWritingThePurposeClaimFailsNothingOfTheAcceptanceIsLeftBehind()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        FailOnInsertInto failure = new("VehiclePurposeClaims");
        await using ControlServerDbContext context = fixture.NewContext(failure);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Batch7JourneyFixture.AcceptAsync(context, "D-713", "agv-01", "VK-01", Batch7JourneyFixture.Now));

        Assert.True(failure.Fired);
        foreach (string table in (string[])
                 ["AcceptedDemands", "VehicleDispatchLeases", "OrderIntents", "JourneyRuntimes", "JourneyStops",
                  "JourneyDemands", "VehiclePurposeClaims", "VehicleSnapshotRevisions"])
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    /// <summary>Throws from the database command that inserts into the named table, as a crash at that write would.</summary>
    private sealed class FailOnInsertInto(string table) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (command.CommandText.Contains($"INSERT INTO \"{table}\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException($"Injected failure while writing {table}.");
            }
        }
    }

    [Fact]
    public async Task ASecondAcceptanceOnTheSameVehicleIsRefusedByThePurposeClaimsKeyNotByAnyReadBeforeTheWrite()
    {
        // The moment two acceptances race: both passed the lease pre-read, and the other one's claim landed first. Built
        // here by writing that claim with no lease beside it, so the pre-read finds nothing and only the key can refuse.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        fixture.Context.Set<VehiclePurposeClaimRow>().Add(new VehiclePurposeClaimRow
        {
            VehicleKey = "VK-01",
            Purpose = "TRANSPORT",
            JourneyId = "journey:D-OTHER",
            ClaimedAt = Batch7JourneyFixture.Now.AddMinutes(-1)
        });
        await fixture.Context.SaveChangesAsync(cancellationToken);
        await using ControlServerDbContext context = fixture.NewContext();

        BusinessIdentityConflictException refusal = await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            Batch7JourneyFixture.AcceptAsync(context, "D-714", "agv-01", "VK-01", Batch7JourneyFixture.Now));

        Assert.Contains("VK-01", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["VehicleKey='VK-01'|Purpose='TRANSPORT'|JourneyId='journey:D-OTHER'|ClaimedAt='2026-09-19 08:59:00+00:00'"],
            await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
        foreach (string table in (string[])["AcceptedDemands", "VehicleDispatchLeases", "OrderIntents", "JourneyRuntimes", "JourneyStops"])
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    [Fact]
    public async Task ReplayingAnAcceptanceWhoseStopsAndDemandAreUnchangedIsTheSameAcceptance()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-715", "agv-01", "VK-01", Batch7JourneyFixture.Now);
        string[] tables = ["JourneyStops", "JourneyDemands", "VehiclePurposeClaims", "VehicleSnapshotRevisions", "JourneyRuntimes"];
        Dictionary<string, string[]> before = [];
        foreach (string table in tables)
        {
            before[table] = await Batch7JourneyFixture.DumpAsync(fixture.Connection, table);
        }

        await using ControlServerDbContext replay = fixture.NewContext();
        await Batch7JourneyFixture.AcceptAsync(replay, "D-715", "agv-01", "VK-01", Batch7JourneyFixture.Now);

        foreach (string table in tables)
        {
            Assert.Equal(before[table], await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    [Theory]
    [InlineData("UPDATE JourneyStops SET StationId = 'ST-ELSEWHERE' WHERE StopRole = 'PICKUP'")]
    [InlineData("UPDATE JourneyStops SET UpperId = 'W2G-D-716-GATE-9' WHERE StopRole = 'UNLOAD'")]
    [InlineData("UPDATE JourneyStops SET SublotRequestMessageId = 'another-message' WHERE StopRole = 'PICKUP'")]
    [InlineData("DELETE FROM JourneyStops WHERE StopRole = 'UNLOAD'")]
    [InlineData("UPDATE JourneyDemands SET LoadSlotOperationAttemptId = 'another-attempt'")]
    [InlineData("UPDATE JourneyDemands SET TargetSlotsJson = '[3,4]'")]
    [InlineData("DELETE FROM JourneyDemands")]
    public async Task ReplayingAnAcceptanceWhoseStopsOrDemandDifferIsAConflict(string change)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-716", "agv-01", "VK-01", Batch7JourneyFixture.Now);
        await using (SqliteCommand command = fixture.Connection.CreateCommand())
        {
            command.CommandText = change;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await using ControlServerDbContext replay = fixture.NewContext();
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            Batch7JourneyFixture.AcceptAsync(replay, "D-716", "agv-01", "VK-01", Batch7JourneyFixture.Now));
    }

    [Fact]
    public async Task ReplayingAnAcceptanceAfterTheRuntimeMovedItsStopsOnIsStillTheSameAcceptance()
    {
        // Status and sequence are the runtime's to move once control-server#208 and #211 land; a replayed acceptance must
        // not read that as different content.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-717", "agv-01", "VK-01", Batch7JourneyFixture.Now);
        await using (SqliteCommand command = fixture.Connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE JourneyStops SET Status = 'COMPLETED', Sequence = Sequence + 10; UPDATE JourneyDemands SET Status = 'LOADED';";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using ControlServerDbContext replay = fixture.NewContext();
        await Batch7JourneyFixture.AcceptAsync(replay, "D-717", "agv-01", "VK-01", Batch7JourneyFixture.Now);
    }

    [Fact]
    public async Task AnAcceptanceRefusedByThePurposeClaimLeavesNothingStagedForTheNextSaveOnTheSameContext()
    {
        // The pattern control-server#198 guards against: a caller that catches the refusal and saves the same context again
        // -- the engine writes the demand's backlog reason right after intake -- must not commit half an acceptance.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        fixture.Context.Set<VehiclePurposeClaimRow>().Add(new VehiclePurposeClaimRow
        {
            VehicleKey = "VK-01",
            Purpose = "TRANSPORT",
            JourneyId = "journey:D-OTHER",
            ClaimedAt = Batch7JourneyFixture.Now.AddMinutes(-1)
        });
        await fixture.Context.SaveChangesAsync(cancellationToken);
        await using ControlServerDbContext context = fixture.NewContext();

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            Batch7JourneyFixture.AcceptAsync(context, "D-718", "agv-01", "VK-01", Batch7JourneyFixture.Now));
        await context.SaveChangesAsync(cancellationToken);

        foreach (string table in (string[])
                 ["AcceptedDemands", "VehicleDispatchLeases", "OrderIntents", "JourneyRuntimes", "JourneyStops",
                  "JourneyDemands", "VehicleSnapshotRevisions"])
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
    }

    [Fact]
    public async Task ARefusedAcceptanceKeepsTheCallersOwnUnsavedChangesAsTheCallerLeftThem()
    {
        // Only what the acceptance itself staged is dropped. A change the caller had made before calling -- to an unrelated
        // row, or to the demand's backlog row that the acceptance would itself have rewritten -- survives, as the caller
        // left it, for the caller's next SaveChanges.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = Batch7JourneyFixture.Now;
        fixture.Context.Set<VehiclePurposeClaimRow>().Add(new VehiclePurposeClaimRow
        {
            VehicleKey = "VK-01",
            Purpose = "TRANSPORT",
            JourneyId = "journey:D-OTHER",
            ClaimedAt = now.AddMinutes(-1)
        });
        fixture.Context.MissingPackages.Add(new MissingPackageRow
        {
            Package = "PKG-1",
            FirstSeenAt = now,
            LastSeenAt = now,
            Status = "OPEN"
        });
        fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
        {
            DemandId = "D-719",
            TransportDemandKey = "SUBLOT-D-719|WIRE_TO_GATE",
            FirstSeenAt = now,
            DemandCreatedAt = now,
            DecisionFingerprint = "fp-1",
            ReasonCode = "WAITING",
            LastSeenAt = now
        });
        await fixture.Context.SaveChangesAsync(cancellationToken);

        await using ControlServerDbContext context = fixture.NewContext();
        (await context.MissingPackages.SingleAsync(cancellationToken)).Status = "RESOLVED";
        JourneyBacklogRow backlog = await context.JourneyBacklog.SingleAsync(cancellationToken);
        backlog.ReasonCode = "CALLER_REASON";
        backlog.LastSeenAt = now.AddMinutes(1);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            Batch7JourneyFixture.AcceptAsync(context, "D-719", "agv-01", "VK-01", now.AddMinutes(2)));
        await context.SaveChangesAsync(cancellationToken);

        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Equal("RESOLVED", (await read.MissingPackages.AsNoTracking().SingleAsync(cancellationToken)).Status);
        JourneyBacklogRow stored = await read.JourneyBacklog.AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal(("CALLER_REASON", now.AddMinutes(1), (DateTimeOffset?)null), (stored.ReasonCode, stored.LastSeenAt, stored.AcceptedAt));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "AcceptedDemands"));
    }

    private static string Describe(JourneyRuntimeRow row) =>
        $"{row.DemandId} {row.VehicleBusinessRevision}/{row.WorklistRevision}/{row.PlanRevision} "
        + string.Join(
            ",",
            row.OperationSessionId,
            row.PickupMovementLegId,
            row.PickupUpperId,
            row.GateMovementLegId,
            row.GateUpperId,
            row.VehicleBusinessMessageId,
            row.WorklistMessageId,
            row.PlanMessageId,
            row.SublotRequestMessageId,
            row.LoadCommandMessageId,
            row.LoadSlotOperationAttemptId,
            row.PreDepartureSafetyCheckMessageId,
            row.PreDepartureSafetyCheckId,
            row.GateVehicleBusinessMessageId,
            row.GateWorklistMessageId,
            row.GatePlanMessageId,
            row.UnloadCommandMessageId,
            row.UnloadSlotOperationAttemptId);
}
