using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 批次 8 建表票（control-server#386）给后续票的三个持久化端口：用途占有、站点独占、等待点登记。本票没有运行时读者，
/// 这里是它们唯一的调用方；输赢都要由数据库约束说，所以两个竞争者各用一个上下文，谁也不先读。
/// </summary>
public sealed class Batch8PersistencePortTests
{
    private static readonly DateTimeOffset Now = Batch7JourneyFixture.Now;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryBatch8PortIsRegisteredWithTheVehiclePurposeModuleSoNoLaterTicketEditsIt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        ServiceCollection services = new();
        services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connection));
        services.AddGovernance(new ConfigurationBuilder().Build());
        services.AddVehiclePurposes();
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.IsType<VehiclePurposeLedgerStore>(scope.ServiceProvider.GetRequiredService<IVehiclePurposeLedger>());
        Assert.IsType<StationExclusivityStore>(scope.ServiceProvider.GetRequiredService<IStationExclusivityStore>());
        Assert.IsType<WaitingPointRegistry>(scope.ServiceProvider.GetRequiredService<IWaitingPointRegistry>());
    }

    [Fact]
    public void TheFourPurposesAreDefinedOnceAndEachHasItsWireCounterpartInTheProtocolEnum()
    {
        Assert.Equal(["TRANSPORT", "CHARGING", "CLEARING_MAINTENANCE", "IDLE_RETURN"], VehiclePurposes.All);

        // Two vocabularies, deliberately: this one is the server's record, the protocol's is the wire. Every purpose a claim
        // can hold must still have a value to put on the wire as activePurpose, or batch 8-18 has nothing to map it to.
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "vendor", "8005-agv-protocol", "schemas", "messages", "VehicleBusinessStateSnapshot.schema.json")));
        string[] wire = FindEnum(schema.RootElement, "activePurpose");
        Assert.Subset(wire.ToHashSet(StringComparer.Ordinal), VehiclePurposes.All.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASecondClaimOnTheSameVehicleIsRefusedByTheKeyWhileTheSameJourneyAskingAgainIsAlreadyHeld()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        VehiclePurposeLedgerStore first = new(fixture.Context);
        VehiclePurposeLedgerStore second = new(fixture.NewContext());

        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.Acquired,
            await first.TryAcquireAsync(Claim("journey:D-1", VehiclePurposes.Transport), null, Token));
        // Neither store read before writing: the second one's insert reached the database and the key refused it.
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.VehicleHeld,
            await second.TryAcquireAsync(Claim("idle:VK-01", VehiclePurposes.IdleReturn), null, Token));
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.VehicleHeld,
            await second.TryAcquireAsync(Claim("journey:D-1", VehiclePurposes.IdleReturn), null, Token));
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.AlreadyHeld,
            await second.TryAcquireAsync(Claim("journey:D-1", VehiclePurposes.Transport), null, Token));

        Assert.Equal(
            ["VehicleKey='VK-01'|Purpose='TRANSPORT'|JourneyId='journey:D-1'|ClaimedAt='2026-09-19 09:00:00+00:00'"],
            await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
        VehiclePurposeClaimRecord record = Assert.Single(await second.ListClaimHistoryAsync("VK-01", Token));
        Assert.Equal(("VK-01", "TRANSPORT", "journey:D-1", Now, (DateTimeOffset?)null, (string?)null),
            (record.VehicleKey, record.Purpose, record.JourneyId, record.AcquiredAt, record.ReleasedAt, record.ReleaseReason));
    }

    [Fact]
    public async Task AnUnreleasedRecordAloneStillRefusesASecondClaimSoTheHistoryAndTheClaimCannotDisagree()
    {
        // The moment a claim row was lost but its record was not: the record's filtered unique index refuses the second
        // acquisition on its own, and the claim row inserted beside it goes back with it.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await ExecuteAsync(
            fixture.Connection,
            "INSERT INTO VehiclePurposeClaimRecords (RecordId, VehicleKey, Purpose, JourneyId, AcquiredAt) " +
            "VALUES ('R-0', 'VK-01', 'TRANSPORT', 'journey:D-0', '2026-09-19 08:00:00+00:00')");

        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.VehicleHeld,
            await new VehiclePurposeLedgerStore(fixture.Context)
                .TryAcquireAsync(Claim("journey:D-1", VehiclePurposes.Transport), null, Token));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaimRecords"));
    }

    [Theory]
    [InlineData("VehiclePurposeClaims", "INSERT INTO VehiclePurposeClaims (VehicleKey, Purpose, JourneyId, ClaimedAt) VALUES ('VK-01', '{0}', 'j', 'x')")]
    [InlineData("VehiclePurposeClaimRecords", "INSERT INTO VehiclePurposeClaimRecords (RecordId, VehicleKey, Purpose, JourneyId, AcquiredAt) VALUES ('R', 'VK-01', '{0}', 'j', 'x')")]
    public async Task ThePurposeColumnsAcceptTheFourPurposesAndTheDatabaseRefusesAnyOther(string table, string insert)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        foreach (string refused in new[] { "PARKING", "transport", "", "TRANSPORT " })
        {
            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
                () => ExecuteAsync(fixture.Connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, refused)));
            Assert.Equal(275, failure.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_CHECK
        }
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));

        foreach (string purpose in VehiclePurposes.All)
        {
            await ExecuteAsync(fixture.Connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, purpose));
            await ExecuteAsync(fixture.Connection, $"DELETE FROM {table}");
        }
    }

    [Fact]
    public async Task TheLedgerPassesAFifthPurposeToTheDatabaseAndTheDatabaseRefusesIt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        DbUpdateException failure = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new VehiclePurposeLedgerStore(fixture.Context).TryAcquireAsync(Claim("journey:D-1", "PARKING"), null, Token));
        Assert.Equal(275, Assert.IsType<SqliteException>(failure.InnerException).SqliteExtendedErrorCode);
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaimRecords"));
    }

    [Fact]
    public async Task ReleasingAClaimRemovesItAndWritesWhenAndWhyOnItsRecordWhileOnlyItsOwnJourneyCanRelease()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        VehiclePurposeLedgerStore ledger = new(fixture.Context);
        await ledger.TryAcquireAsync(Claim("idle:VK-01:1", VehiclePurposes.IdleReturn), null, Token);

        VehiclePurposeLedgerStore other = new(fixture.NewContext());
        Assert.False(await other.ReleaseAsync("VK-01", "journey:D-9", Now.AddMinutes(1), "NOT_MINE", Token));
        Assert.True(await other.ReleaseAsync("VK-01", "idle:VK-01:1", Now.AddMinutes(2), "TRANSPORT_ACCEPTED", Token));
        Assert.False(await other.ReleaseAsync("VK-01", "idle:VK-01:1", Now.AddMinutes(3), "TWICE", Token));

        Assert.Null(await ledger.ReadClaimAsync("VK-01", Token));
        VehiclePurposeClaimRecord released = Assert.Single(await ledger.ListClaimHistoryAsync("VK-01", Token));
        Assert.Equal(
            ("IDLE_RETURN", "idle:VK-01:1", Now, (DateTimeOffset?)Now.AddMinutes(2), (string?)"TRANSPORT_ACCEPTED"),
            (released.Purpose, released.JourneyId, released.AcquiredAt, released.ReleasedAt, released.ReleaseReason));

        // Released, the vehicle can be claimed again, and the history keeps both.
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.Acquired,
            await other.TryAcquireAsync(Claim("journey:D-2", VehiclePurposes.Transport, Now.AddMinutes(4)), null, Token));
        Assert.Equal(
            ["idle:VK-01:1 released", "journey:D-2 held"],
            (await ledger.ListClaimHistoryAsync("VK-01", Token))
                .Select(record => $"{record.JourneyId} {(record.ReleasedAt is null ? "held" : "released")}"));
    }

    [Fact]
    public async Task AClaimTheEngineWroteWithoutARecordIsReleasedAllTheSame()
    {
        // Until batch 8-16 moves the engine onto this port, an acceptance writes the claim row alone.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-1", "AGV-01", "VK-01", Now);

        Assert.True(await new VehiclePurposeLedgerStore(fixture.NewContext())
            .ReleaseAsync("VK-01", "journey:D-1", Now.AddMinutes(1), "TEST", Token));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaimRecords"));
    }

    [Fact]
    public async Task TwoVehiclesReservingTheSameStationAtOnceOnlyOneHoldsItAndTheOtherHoldsItAfterTheRelease()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        StationExclusivityStore first = new(fixture.Context);
        StationExclusivityStore second = new(fixture.NewContext());

        Assert.True(await first.TryAcquireAsync(WaitingPoint(214), "VK-01", "idle:VK-01:1", Now, Token));
        Assert.False(await second.TryAcquireAsync(WaitingPoint(214), "VK-02", "idle:VK-02:1", Now, Token));
        // The loser's rows went back whole: one station row, one passage.
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "StationExclusivities"));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "StationExclusivityRecords"));
        Assert.Equal(
            new StationExclusivity(26, 214, "WAITING_POINT", "RESERVED", "VK-01", "idle:VK-01:1", Now, 3),
            await second.ReadAsync(26, 214, Token));

        // Only the holder moves it on, and only the holder releases it.
        Assert.False(await second.MarkOccupiedAsync(26, 214, "idle:VK-02:1", Now.AddMinutes(1), Token));
        Assert.True(await second.MarkOccupiedAsync(26, 214, "idle:VK-01:1", Now.AddMinutes(2), Token));
        Assert.True(await first.MarkOccupiedAsync(26, 214, "idle:VK-01:1", Now.AddMinutes(3), Token));
        Assert.Equal(
            new StationExclusivity(26, 214, "WAITING_POINT", "OCCUPIED", "VK-01", "idle:VK-01:1", Now.AddMinutes(2), 3),
            await first.ReadAsync(26, 214, Token));
        Assert.False(await second.ReleaseAsync(26, 214, "idle:VK-02:1", Now.AddMinutes(4), "NOT_MINE", Token));
        Assert.True(await second.ReleaseAsync(26, 214, "idle:VK-01:1", Now.AddMinutes(5), "DEPARTED:LEG-7", Token));
        Assert.Null(await first.ReadAsync(26, 214, Token));

        Assert.True(await first.TryAcquireAsync(WaitingPoint(214), "VK-02", "idle:VK-02:1", Now.AddMinutes(6), Token));
        Assert.Equal(
            ["VK-01 R=09:00 O=09:02 X=09:05 DEPARTED:LEG-7", "VK-02 R=09:06 O=- X=- -"],
            (await second.ListHistoryAsync(26, 214, Token)).Select(record =>
                $"{record.VehicleKey} R={Clock(record.ReservedAt)} O={Clock(record.OccupiedAt)} X={Clock(record.ReleasedAt)} {record.ReleaseReason ?? "-"}"));
        Assert.Equal(["VK-02"], (await first.ListByVehicleAsync("VK-02", Token)).Select(row => row.VehicleKey));
        Assert.Empty(await first.ListByVehicleAsync("VK-01", Token));
    }

    [Fact]
    public async Task AVehicleAlreadyAtAFixedStationTakesItOccupiedWithNoReservation()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        StationExclusivityStore store = new(fixture.Context);

        Assert.True(await store.TryAcquireAsync(
            new StationExclusivityRequest(26, 101, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            "VK-01", "journey:D-1", Now, Token));

        StationExclusivityRecord record = Assert.Single(await store.ListHistoryAsync(26, 101, Token));
        Assert.Equal(("FIXED_TASK_STATION", (DateTimeOffset?)null, (DateTimeOffset?)Now, (long?)null),
            (record.StationKind, record.ReservedAt, record.OccupiedAt, record.WaitingPointVersion));
    }

    [Theory]
    [InlineData("StationExclusivities", "State", "LEAVING")]
    [InlineData("StationExclusivities", "State", "reserved")]
    [InlineData("StationExclusivities", "StationKind", "CHARGER")]
    [InlineData("StationExclusivityRecords", "StationKind", "CHARGER")]
    public async Task TheDatabaseRefusesAThirdStateOrAnUnknownKindOfStation(string table, string column, string value)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        string insert = table == "StationExclusivities"
            ? "INSERT INTO StationExclusivities (MapId, StationId, StationKind, State, VehicleKey, JourneyId, StateSince, RecordId) " +
              "VALUES (26, 214, @StationKind, @State, 'VK-01', 'j', 'x', 'R')"
            : "INSERT INTO StationExclusivityRecords (RecordId, MapId, StationId, StationKind, VehicleKey, JourneyId) " +
              "VALUES ('R', 26, 214, @StationKind, 'VK-01', 'j')";
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["StationKind"] = StationExclusivityKinds.WaitingPoint,
            ["State"] = StationExclusivityStates.Reserved,
            [column] = value
        };

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(fixture.Connection, insert, values));
        Assert.Equal(275, failure.SqliteExtendedErrorCode);

        // The store does not pre-check either: a third state reaches the database and is refused there.
        if (column == "State")
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => new StationExclusivityStore(fixture.Context).TryAcquireAsync(
                new StationExclusivityRequest(26, 214, StationExclusivityKinds.WaitingPoint, value, 3),
                "VK-01", "j", Now, Token));
        }
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
    }

    [Fact]
    public async Task AClaimTakenTogetherWithAStationIsOneSaveSoAHeldStationLeavesNoClaimBehind()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        Assert.True(await new StationExclusivityStore(fixture.NewContext())
            .TryAcquireAsync(WaitingPoint(214), "VK-02", "idle:VK-02:1", Now, Token));
        VehiclePurposeLedgerStore ledger = new(fixture.Context);

        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.StationHeld,
            await ledger.TryAcquireAsync(Claim("idle:VK-01:1", VehiclePurposes.IdleReturn), WaitingPoint(214), Token));
        Assert.Null(await ledger.ReadClaimAsync("VK-01", Token));
        Assert.Empty(await ledger.ListClaimHistoryAsync("VK-01", Token));

        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.Acquired,
            await ledger.TryAcquireAsync(Claim("idle:VK-01:1", VehiclePurposes.IdleReturn), WaitingPoint(215), Token));
        Assert.Equal("idle:VK-01:1", (await ledger.ReadClaimAsync("VK-01", Token))?.JourneyId);
        Assert.Equal(
            ["VK-01"],
            (await new StationExclusivityStore(fixture.NewContext()).ListByVehicleAsync("VK-01", Token))
                .Select(row => row.VehicleKey));
    }

    [Theory]
    [InlineData("StationExclusivities")]
    [InlineData("StationExclusivityRecords")]
    [InlineData("VehiclePurposeClaimRecords")]
    [InlineData("VehiclePurposeClaims")]
    public async Task AFailureAtAnyOfTheFourInsertsOfAClaimWithAStationLeavesNoneOfThemBehind(string failAt)
    {
        // The crash point batch 8-18's atomic commitment rests on. EF sends the four inserts in its own order (by table
        // name today, so the station's go first), which is why every one of them is a failure point here: whichever fails,
        // the ones already executed must go back with it.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        FailOnInsertInto failure = new(failAt);
        await using ControlServerDbContext failing = fixture.NewContext(failure);

        await Assert.ThrowsAnyAsync<Exception>(() => new VehiclePurposeLedgerStore(failing)
            .TryAcquireAsync(Claim("idle:VK-01:1", VehiclePurposes.IdleReturn), WaitingPoint(214), Token));

        Assert.True(failure.Fired);
        foreach (string table in ClaimWithStationTables)
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    [Fact]
    public async Task TheLastOfTheFourInsertsRunsAfterTheOtherThreeReachedTheDatabaseSoItsFailureIsARealRollback()
    {
        // Pins the premise of the theory above: without it, a failure injected before anything was executed would leave
        // the tables empty for the trivial reason and prove nothing about the transaction.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        FailOnInsertInto observer = new("none");
        await using ControlServerDbContext observed = fixture.NewContext(observer);

        await new VehiclePurposeLedgerStore(observed)
            .TryAcquireAsync(Claim("idle:VK-01:1", VehiclePurposes.IdleReturn), WaitingPoint(214), Token);

        Assert.Equal(ClaimWithStationTables.Order(StringComparer.Ordinal), observer.Inserted.Order(StringComparer.Ordinal));
        Assert.Equal(4, observer.Inserted.Count);
        FailOnInsertInto lastFails = new(observer.Inserted[^1]);
        await using Batch7JourneyFixture again = await Batch7JourneyFixture.CreateAsync();
        await using ControlServerDbContext failing = again.NewContext(lastFails);
        await Assert.ThrowsAnyAsync<Exception>(() => new VehiclePurposeLedgerStore(failing)
            .TryAcquireAsync(Claim("idle:VK-01:1", VehiclePurposes.IdleReturn), WaitingPoint(214), Token));
        Assert.Equal(observer.Inserted[..^1], lastFails.Inserted);
    }

    [Fact]
    public async Task AWaitingPointRegistrationIsVersionedThroughGovernanceAndTheSameContentMakesNoNewVersion()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        WaitingPointRegistry registry = Registry(fixture.Context);

        WaitingPointRegistrationVersion first = await registry.WriteVersionAsync(
            [
                new WaitingPointEntry(26, 216, "等待点3", true, []),
                new WaitingPointEntry(26, 214, "等待点1", true, ["VK-02", "VK-01", "VK-02"]),
                new WaitingPointEntry(26, 215, "等待点2", false, [])
            ],
            Now,
            Token);
        Assert.Equal((1L, WaitingPointSources.GovernedImport), (first.Version, first.Source));
        Assert.NotNull(first.SnapshotId);

        // The same registration in another order is the same content: no second version, no second snapshot.
        WaitingPointRegistrationVersion again = await Registry(fixture.NewContext()).WriteVersionAsync(
            [
                new WaitingPointEntry(26, 215, "等待点2", false, []),
                new WaitingPointEntry(26, 214, "等待点1", true, ["VK-01", "VK-02"]),
                new WaitingPointEntry(26, 216, "等待点3", true, [])
            ],
            Now.AddMinutes(1),
            Token);
        Assert.Equal((1L, first.ContentSha256, Now), (again.Version, again.ContentSha256, again.LoadedAt));

        WaitingPointRegistrationVersion second = await Registry(fixture.NewContext()).WriteVersionAsync(
            [new WaitingPointEntry(26, 214, "等待点1", true, [])], Now.AddMinutes(2), Token);
        Assert.Equal(2, second.Version);

        WaitingPointRegistry reader = Registry(fixture.NewContext());
        Assert.Equal(2, (await reader.ReadCurrentAsync(Token))?.Version);
        WaitingPointRegistrationVersion? readBack = await reader.ReadVersionAsync(1, Token);
        Assert.NotNull(readBack);
        Assert.Equal(first.ContentSha256, readBack.ContentSha256);
        // A point with no scope rows is open to every vehicle on its map; one with rows only to those.
        Assert.Equal(
            ["26/214 等待点1 on VK-01,VK-02", "26/215 等待点2 off *", "26/216 等待点3 on *"],
            readBack.Points.Select(point =>
                $"{point.MapId}/{point.StationId} {point.StationName} {(point.Enabled ? "on" : "off")} " +
                (point.VehicleScope.Count == 0 ? "*" : string.Join(",", point.VehicleScope))));
        Assert.Equal(
            [GovernedObjectKind.WaitingPointRegistration, GovernedObjectKind.WaitingPointRegistration],
            await fixture.NewContext().Set<GovernedConfigurationSnapshotRow>().AsNoTracking()
                .Select(row => row.ObjectKind).ToArrayAsync(Token));
    }

    [Fact]
    public async Task APublishedWaitingPointVersionItsPointsAndItsScopeCannotBeRewrittenOrDeleted()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Registry(fixture.Context).WriteVersionAsync(
            [new WaitingPointEntry(26, 214, "等待点1", true, ["VK-01"])], Now, Token);

        await using ControlServerDbContext writer = fixture.NewContext();
        (await writer.Set<WaitingPointRow>().SingleAsync(Token)).Enabled = false;
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
        writer.ChangeTracker.Clear();
        writer.Remove(await writer.Set<WaitingPointVehicleScopeRow>().SingleAsync(Token));
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
        writer.ChangeTracker.Clear();
        writer.Remove(await writer.Set<WaitingPointVersionRow>().SingleAsync(Token));
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
    }

    [Theory]
    [InlineData(214, 214, "A", "VK-01")]
    [InlineData(214, 215, " ", "VK-01")]
    [InlineData(214, 215, "A", " ")]
    public async Task ARegistrationThatNamesAStationTwiceOrLeavesANameOrVehicleBlankIsRefusedWhole(
        int firstStation, int secondStation, string name, string vehicleKey)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Registry(fixture.Context).WriteVersionAsync(
            [new WaitingPointEntry(26, firstStation, "A", true, []), new WaitingPointEntry(26, secondStation, name, true, [vehicleKey])],
            Now,
            Token));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "WaitingPointVersions"));
    }

    private static VehiclePurposeClaim Claim(string journeyId, string purpose, DateTimeOffset? at = null) =>
        new("VK-01", purpose, journeyId, at ?? Now);

    private static StationExclusivityRequest WaitingPoint(int stationId) =>
        new(26, stationId, StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Reserved, 3);

    private static string Clock(DateTimeOffset? at) =>
        at?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static WaitingPointRegistry Registry(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
        return new WaitingPointRegistry(context, new GovernedConfigurationPublisher(governance, governance));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, string sql, IReadOnlyDictionary<string, string>? parameters = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, string value) in parameters ?? new Dictionary<string, string>())
        {
            command.Parameters.AddWithValue("@" + name, value);
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    private static string[] FindEnum(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty child in element.EnumerateObject())
            {
                if (child.Name == property)
                {
                    foreach (JsonElement branch in child.Value.GetProperty("anyOf").EnumerateArray())
                    {
                        if (branch.TryGetProperty("enum", out JsonElement values))
                        {
                            return [.. values.EnumerateArray().Select(value => value.GetString()!)];
                        }
                    }
                }
                string[] found = FindEnum(child.Value, property);
                if (found.Length > 0)
                {
                    return found;
                }
            }
        }
        return [];
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the ControlServer repository root.");
    }

    private static readonly string[] ClaimWithStationTables =
        ["VehiclePurposeClaims", "VehiclePurposeClaimRecords", "StationExclusivities", "StationExclusivityRecords"];

    /// <summary>Fails the command that inserts into one table, and records the inserts that did execute before it.</summary>
    private sealed class FailOnInsertInto(string table) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        /// <summary>The tables inserted into by commands that executed, in order.</summary>
        public List<string> Inserted { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailIfTargeted(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailIfTargeted(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void FailIfTargeted(DbCommand command)
        {
            if (command.CommandText.Contains($"INSERT INTO \"{table}\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException($"Injected failure while writing {table}.");
            }
        }

        private void Record(DbCommand command)
        {
            foreach (string candidate in ClaimWithStationTables
                         .Select(name => (name, at: command.CommandText.IndexOf($"INSERT INTO \"{name}\"", StringComparison.Ordinal)))
                         .Where(hit => hit.at >= 0)
                         .OrderBy(hit => hit.at)
                         .Select(hit => hit.name))
            {
                Inserted.Add(candidate);
            }
        }
    }
}
