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
/// 批次 9 建表票（control-server#399）给批次9-02～9-12 的持久化端口：充电桩名册、充电策略版本、充电周期、两类暂停、清桩记录、
/// 人工充电等待、两类现场确认请求。本票没有运行时读者，这里是它们唯一的调用方；输赢由数据库约束说，所以两个竞争者各用一个上下文，
/// 谁也不先读。
/// </summary>
public sealed class Batch9PersistencePortTests
{
    private static readonly DateTimeOffset Now = Batch7JourneyFixture.Now;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryBatch9PortIsRegisteredWithTheChargingModuleSoNoLaterTicketEditsIt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        ServiceCollection services = new();
        services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connection));
        services.AddGovernance(new ConfigurationBuilder().Build());
        services.AddCharging();
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.IsType<ChargerRosterStore>(scope.ServiceProvider.GetRequiredService<IChargerRoster>());
        Assert.IsType<ChargingPolicyStore>(scope.ServiceProvider.GetRequiredService<IChargingPolicyStore>());
        Assert.IsType<ChargingCycleStore>(scope.ServiceProvider.GetRequiredService<IChargingCycleStore>());
        Assert.IsType<ChargingHoldStore>(scope.ServiceProvider.GetRequiredService<IChargingHoldStore>());
        Assert.IsType<StationClearanceStore>(scope.ServiceProvider.GetRequiredService<IStationClearanceStore>());
        Assert.IsType<ManualChargingHoldStore>(scope.ServiceProvider.GetRequiredService<IManualChargingHoldStore>());
        Assert.IsType<FieldConfirmationRequestStore>(
            scope.ServiceProvider.GetRequiredService<IFieldConfirmationRequestStore>());
    }

    [Fact]
    public void TheCycleStatesAreTheProtocolsChargingCycleStatesWordForWord()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "vendor", "8005-agv-protocol", "schemas", "messages", "VehicleBusinessStateSnapshot.schema.json")));
        Assert.Equal(FindEnum(schema.RootElement, "chargingCycleState"), ChargingCycleWireStates.All);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Charger roster

    [Fact]
    public async Task AnEmptyRosterIsAVersionAndReadsAsAnEmptySetNotAsNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargerRosterStore roster = Roster(fixture.Context);
        Assert.Null(await roster.ReadCurrentAsync(Token));

        ChargerRosterVersion written = await roster.WriteVersionAsync([], Approval("roster emptied"), Now, Token);

        ChargerRosterVersion current = Assert.IsType<ChargerRosterVersion>(await roster.ReadCurrentAsync(Token));
        Assert.Equal(1, current.Version);
        Assert.NotNull(current.Chargers);
        Assert.Empty(current.Chargers);
        Assert.Equal(written.ContentSha256, current.ContentSha256);
        Assert.Equal(("szy", "user decision 2026-09-29", "roster emptied"),
            (current.Approval.ApprovedBy, current.Approval.ApprovalBasis, current.Approval.ChangeNote));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargerRosterEntries"));
        // Governed like any other version: a frozen snapshot and a business audit record.
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "GovernedConfigurationSnapshots"));
    }

    [Fact]
    public async Task EmptyThenCharger211ThenEmptyAreThreeVersionsEachReadableByNumber()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargerRosterStore roster = Roster(fixture.Context);

        ChargerRosterVersion first = await roster.WriteVersionAsync([], Approval("empty"), Now, Token);
        ChargerRosterVersion second = await roster.WriteVersionAsync(
            [Charger211(["agv03", "agv02", "agv02"])], Approval("window opened"), Now.AddHours(1), Token);
        ChargerRosterVersion third = await roster.WriteVersionAsync([], Approval("window closed"), Now.AddHours(2), Token);

        Assert.Equal([1L, 2L, 3L], [first.Version, second.Version, third.Version]);
        Assert.Equal(first.ContentSha256, third.ContentSha256);
        Assert.Empty((await roster.ReadCurrentAsync(Token))!.Chargers);
        Assert.Empty((await roster.ReadVersionAsync(1, Token))!.Chargers);
        Assert.Empty((await roster.ReadVersionAsync(3, Token))!.Chargers);
        ChargerRosterEntry charger = Assert.Single((await roster.ReadVersionAsync(2, Token))!.Chargers);
        Assert.Equal((26, 211, "充电点1", (int?)210, (int?)212),
            (charger.MapId, charger.StationId, charger.StationName, charger.EntryStationId, charger.ExitStationId));
        Assert.Equal(["agv02", "agv03"], charger.VehicleScope);
        Assert.Equal("window opened", (await roster.ReadVersionAsync(2, Token))!.Approval.ChangeNote);
        Assert.Null(await roster.ReadVersionAsync(4, Token));

        // The same content as the current version writes nothing.
        ChargerRosterVersion again = await roster.WriteVersionAsync([], Approval("again"), Now.AddHours(3), Token);
        Assert.Equal(3, again.Version);
        Assert.Equal(3, (await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargerRosterVersions")).Length);
    }

    [Fact]
    public async Task RosterAndPolicyRowsAreNeverRewrittenOrDeleted()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Roster(fixture.Context).WriteVersionAsync([Charger211(["agv02"])], Approval("211"), Now, Token);
        ChargingPolicyStore policies = Policies(fixture.Context);
        await policies.WriteVersionAsync(Policy(["agv02"]), null, Now, Token);
        await policies.ApproveAsync(1, "szy", "SYSTEM_ADMINISTRATOR", Now, "evidence/charging-test", "FIELD", Token);
        await policies.ActivateAsync(1, "szy", Now, Token);

        await using ControlServerDbContext writer = fixture.NewContext();
        foreach (object row in new object[]
                 {
                     await writer.Set<ChargerRosterVersionRow>().SingleAsync(Token),
                     await writer.Set<ChargerRosterEntryRow>().SingleAsync(Token),
                     await writer.Set<ChargerRosterVehicleScopeRow>().SingleAsync(Token),
                     await writer.Set<ChargingPolicyVersionRow>().SingleAsync(Token),
                     await writer.Set<ChargingPolicyVehicleScopeRow>().SingleAsync(Token),
                     await writer.Set<ChargingPolicyApprovalRow>().SingleAsync(Token),
                     await writer.Set<ChargingPolicyActivationRow>().SingleAsync(Token),
                 })
        {
            writer.Remove(row);
            await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
            writer.Entry(row).State = EntityState.Modified;
            await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
            writer.Entry(row).State = EntityState.Unchanged;
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Charging policy

    [Fact]
    public async Task AnUnapprovedVersionIsNotEffectiveAndCannotBeActivated()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingPolicyStore policies = Policies(fixture.Context);
        await policies.WriteVersionAsync(Policy([]), "first", Now, Token);

        Assert.Null(await policies.ReadEffectiveForVehicleAsync("agv02", Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policies.ActivateAsync(1, "szy", Now, Token));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargingPolicyActivations"));
        Assert.Null(await policies.ReadEffectiveForVehicleAsync("agv02", Token));
    }

    [Fact]
    public async Task AnApprovedVersionThatWasNeverActivatedIsNotEffective()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingPolicyStore policies = Policies(fixture.Context);
        await policies.WriteVersionAsync(Policy([]), null, Now, Token);
        await policies.ApproveAsync(1, "szy", "SYSTEM_ADMINISTRATOR", Now, "evidence/charging-test", "FIELD", Token);

        Assert.Null(await policies.ReadEffectiveForVehicleAsync("agv02", Token));
    }

    [Fact]
    public async Task AnActivatedVersionIsEffectiveForAVehicleInItsScopeAndForNoOtherAndTheLatestActivationWins()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingPolicyStore policies = Policies(fixture.Context);
        await policies.WriteVersionAsync(Policy(["agv02"]), null, Now, Token);
        await policies.WriteVersionAsync(Policy([]) with { ChargingCompletionThresholdPercent = 95 }, null, Now, Token);
        await policies.ApproveAsync(1, "szy", "SYSTEM_ADMINISTRATOR", Now, "evidence/1", "FIELD", Token);
        await policies.ApproveAsync(2, "szy", "SYSTEM_ADMINISTRATOR", Now, "evidence/2", "FIELD", Token);

        ChargingPolicyActivation activation = await policies.ActivateAsync(1, "szy", Now.AddMinutes(1), Token);
        EffectiveChargingPolicy effective =
            Assert.IsType<EffectiveChargingPolicy>(await policies.ReadEffectiveForVehicleAsync("agv02", Token));
        Assert.Equal((1L, 1L, 90), (effective.Policy.Version, effective.Activation.Sequence,
            effective.Policy.Content.ChargingCompletionThresholdPercent));
        Assert.Equal(activation, effective.Activation);
        // Out of the version's scope: nothing is effective for it.
        Assert.Null(await policies.ReadEffectiveForVehicleAsync("agv03", Token));

        await policies.ActivateAsync(2, "szy", Now.AddMinutes(2), Token);
        Assert.Equal(2, (await policies.ReadEffectiveForVehicleAsync("agv03", Token))!.Policy.Version);
        Assert.Equal(2, (await policies.ReadEffectiveForVehicleAsync("agv02", Token))!.Activation.Sequence);
        // Version 1 is still there to read back by number: a cycle frozen on it keeps it.
        Assert.Equal(["agv02"], (await policies.ReadVersionAsync(1, Token))!.Content.VehicleScope);
    }

    [Fact]
    public async Task ATestFixtureApprovalReadsApartFromAFieldApprovalAndAFourthSourceIsRefused()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingPolicyStore policies = Policies(fixture.Context);
        await policies.WriteVersionAsync(Policy([]), null, Now, Token);
        await policies.ApproveAsync(1, "fixture", "TEST", Now, "tests/Batch9PersistencePortTests", "TEST_FIXTURE", Token);
        await policies.ActivateAsync(1, "fixture", Now, Token);

        EffectiveChargingPolicy effective = (await policies.ReadEffectiveForVehicleAsync("agv02", Token))!;
        Assert.Equal(["TEST_FIXTURE"], effective.Approvals.Select(approval => approval.Source));
        Assert.DoesNotContain(effective.Approvals, approval => approval.Source == ChargingPolicyApprovalSources.Field);

        await policies.ApproveAsync(1, "szy", "SYSTEM_ADMINISTRATOR", Now.AddMinutes(1), "evidence/site", "FIELD", Token);
        await policies.ApproveAsync(1, "l2", "L2", Now.AddMinutes(2), "scripts/l2", "L2_PRESET", Token);
        Assert.Equal(["TEST_FIXTURE", "FIELD", "L2_PRESET"],
            (await policies.ListApprovalsAsync(1, Token)).Select(approval => approval.Source));

        DbUpdateException refused = await Assert.ThrowsAsync<DbUpdateException>(() => Policies(fixture.NewContext())
            .ApproveAsync(1, "szy", "SYSTEM_ADMINISTRATOR", Now, "x", "SITE", Token));
        Assert.Contains("CK_ChargingPolicyApprovals_Source", refused.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDatabaseChecksEachFieldsRangeButNotTheRelationBetweenTheThresholds()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        // Completion below entry below margin breaks REQ-0281's relation, and the database takes it: that relation has one
        // definition, the validation batch 9-02 writes, and a CHECK here would be a second one.
        ChargingPolicyVersion inverted = await Policies(fixture.Context).WriteVersionAsync(
            Policy([]) with
            {
                MinimumPostTaskBatteryMarginPercent = 50,
                MandatoryChargeEntryThresholdPercent = 40,
                ChargingCompletionThresholdPercent = 30
            },
            null, Now, Token);
        Assert.Equal(1, inverted.Version);

        foreach (ChargingPolicyContent outOfRange in new[]
                 {
                     Policy([]) with { MinimumPostTaskBatteryMarginPercent = 101 },
                     Policy([]) with { MandatoryChargeEntryThresholdPercent = -1 },
                     Policy([]) with { ChargingCompletionThresholdPercent = 101 },
                     Policy([]) with { EstimatedTaskConsumptionPercent = -1 },
                     Policy([]) with { ProgressStabilizationSeconds = 0 },
                     Policy([]) with { ProgressObservationWindowSeconds = 0 },
                     Policy([]) with { ProgressMinimumIncreasePercent = 0 },
                 })
        {
            DbUpdateException refused = await Assert.ThrowsAsync<DbUpdateException>(
                () => Policies(fixture.NewContext()).WriteVersionAsync(outOfRange, null, Now, Token));
            Assert.Equal(275, ((SqliteException)refused.InnerException!).SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_CHECK
        }
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargingPolicyVersions"));
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Station exclusivity: CHARGER

    [Fact]
    public async Task AChargerExclusivityIsWrittenWithItsRosterVersionAndAFourthKindIsRefused()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        StationExclusivityStore store = new(fixture.Context);

        Assert.Equal(
            StationExclusivityAcquisitionOutcome.Acquired,
            await store.TryAcquireAsync(Charger(211, rosterVersion: 2), "agv02", "charge:agv02", Now, Token));
        StationExclusivity held = (await store.ReadAsync(26, 211, Token))!;
        Assert.Equal(("CHARGER", "RESERVED", (long?)null, (long?)2),
            (held.StationKind, held.State, held.WaitingPointVersion, held.ChargerRosterVersion));
        StationExclusivityRecord record = Assert.Single(await store.ListHistoryAsync(26, 211, Token));
        Assert.Equal(("CHARGER", (long?)2), (record.StationKind, record.ChargerRosterVersion));

        DbUpdateException refused = await Assert.ThrowsAsync<DbUpdateException>(() => new StationExclusivityStore(fixture.NewContext())
            .TryAcquireAsync(Charger(212, 2) with { StationKind = "PARKING" }, "agv03", "charge:agv03", Now, Token));
        Assert.Equal(275, ((SqliteException)refused.InnerException!).SqliteExtendedErrorCode);
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "StationExclusivities"));
    }

    [Fact]
    public async Task TwoVehiclesReservingTheSameChargerWithoutReadingFirstOnlyOneHoldsIt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingCycleStore first = new(fixture.Context);
        ChargingCycleStore second = new(fixture.NewContext());

        // Neither store reads before writing: the second insert reaches the database and the station's key refuses it,
        // taking its claim and its cycle back with it (REQ-0173).
        Assert.Equal(ChargingCycleStartOutcome.Started, await first.TryStartAsync(Start("C-1", "agv02"), null, Token));
        Assert.Equal(ChargingCycleStartOutcome.StationHeld, await second.TryStartAsync(Start("C-2", "agv03"), null, Token));

        Assert.Equal(["agv02"], (await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"))
            .Select(row => row.Split('|')[0]["VehicleKey='".Length..^1]));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargingCycles"));
        Assert.Null(await second.ReadOpenAsync("agv03", Token));
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Charging cycles

    [Fact]
    public async Task StartingACycleWritesTheCycleTheChargingClaimAndTheChargerReservationInOneSave()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingCycleStore cycles = new(fixture.Context);

        Assert.Equal(ChargingCycleStartOutcome.Started, await cycles.TryStartAsync(Start("C-1", "agv02"), null, Token));

        ChargingCycle cycle = (await cycles.ReadOpenAsync("agv02", Token))!;
        Assert.Equal(("C-1", "charge:agv02", 26, 211, 2L, 7L, "ALLOCATED", "ACTIVE", 1L),
            (cycle.CycleId, cycle.JourneyId, cycle.MapId, cycle.StationId, cycle.ChargerRosterVersion,
             cycle.ChargingPolicyVersion, cycle.WireState, cycle.Phase, cycle.Version));
        Assert.Equal(
            ("CHARGING", "charge:agv02"),
            ((await new VehiclePurposeLedgerStore(fixture.Context).ReadClaimAsync("agv02", Token))!.Purpose,
             (await new VehiclePurposeLedgerStore(fixture.Context).ReadClaimAsync("agv02", Token))!.JourneyId));
        StationExclusivity reservation = (await new StationExclusivityStore(fixture.Context).ReadAsync(26, 211, Token))!;
        Assert.Equal(("CHARGER", "RESERVED", "agv02", "charge:agv02", (long?)2),
            (reservation.StationKind, reservation.State, reservation.VehicleKey, reservation.JourneyId,
             reservation.ChargerRosterVersion));

        // A retry of the same start after a crash writes nothing more.
        Assert.Equal(ChargingCycleStartOutcome.AlreadyStarted,
            await new ChargingCycleStore(fixture.NewContext()).TryStartAsync(Start("C-1", "agv02"), null, Token));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaimRecords"));
    }

    [Fact]
    public async Task AFailureWhileWritingTheReservationLeavesNoCycleNoClaimAndNoReservation()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        FailOnInsertInto failure = new("StationExclusivities");
        ChargingCycleStore cycles = new(fixture.NewContext(failure));

        await Assert.ThrowsAnyAsync<Exception>(() => cycles.TryStartAsync(Start("C-1", "agv02"), [ChargingIntent("C-1")], Token));

        // Some of the save's inserts had already run when the reservation failed: what is gone was rolled back, not
        // never written.
        Assert.True(failure.Fired);
        Assert.True(failure.InsertsBefore > 0, "The reservation was the save's first insert; nothing tested the rollback.");
        foreach (string table in new[]
                 {
                     "ChargingCycles", "VehiclePurposeClaims", "VehiclePurposeClaimRecords", "StationExclusivities",
                     "StationExclusivityRecords", "OrderIntents",
                 })
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    [Fact]
    public async Task RowsTheCallerSavesAlongsideAreWrittenWithTheCycleAndLeaveNothingBehindWhenTheStartIsRefused()
    {
        // Batch 9-06 writes the journey and the pending order intent in the same save as the cycle (#416 review, 2).
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        Assert.Equal(ChargingCycleStartOutcome.Started,
            await new ChargingCycleStore(fixture.Context).TryStartAsync(Start("C-1", "agv02"), [ChargingIntent("C-1")], Token));
        Assert.Contains("MovementLegId='LEG-C-1'", Assert.Single(
            await Batch7JourneyFixture.DumpAsync(fixture.Connection, "OrderIntents")), StringComparison.Ordinal);

        // The second vehicle's start is refused by the charger's key: its intent goes with it, and a later save of the
        // same context writes nothing of it.
        await using ControlServerDbContext second = fixture.NewContext();
        Assert.Equal(ChargingCycleStartOutcome.StationHeld,
            await new ChargingCycleStore(second).TryStartAsync(Start("C-2", "agv03"), [ChargingIntent("C-2")], Token));
        Assert.Empty(second.ChangeTracker.Entries());
        await second.SaveChangesAsync(Token);
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "OrderIntents"));
    }

    [Fact]
    public async Task ALostUpdateForgetsOnlyItsOwnRowAndLeavesWhatTheCallerTracksInPlace()
    {
        // A concurrency failure used to clear the whole change tracker, taking the caller's own pending rows with it
        // (#416 review, 3).
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await new ChargingCycleStore(fixture.Context).TryStartAsync(Start("C-1", "agv02"), null, Token);
        ChargingCycle read = (await new ChargingCycleStore(fixture.Context).ReadAsync("C-1", Token))!;
        await using ControlServerDbContext caller = fixture.NewContext(new BeforeSave(fixture.Connection,
            "UPDATE ChargingCycles SET Version = Version + 1 WHERE CycleId = 'C-1'"));
        ManualChargingHoldRow pending = new() { VehicleKey = "agv09", HoldId = "MH-9", Reason = "ROSTER_EMPTY", Since = Now };
        caller.Add(pending);

        Assert.False(await new ChargingCycleStore(caller).UpdateAsync(read with { WireState = "EN_ROUTE" }, Token));

        Assert.Equal(EntityState.Added, caller.Entry(pending).State);
        Assert.Equal(["MH-9"], caller.ChangeTracker.Entries().Select(entry => entry.Entity).OfType<ManualChargingHoldRow>()
            .Select(row => row.HoldId));
        await caller.SaveChangesAsync(Token);
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ManualChargingHolds"));
        Assert.Equal("ALLOCATED", (await new ChargingCycleStore(fixture.NewContext()).ReadAsync("C-1", Token))!.WireState);
    }

    [Fact]
    public async Task ASecondUnfinishedCycleForTheSameVehicleIsRefusedByTheIndexAndAnEndedOneDoesNotCount()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingCycleStore cycles = new(fixture.Context);
        Assert.Equal(ChargingCycleStartOutcome.Started, await cycles.TryStartAsync(Start("C-1", "agv02"), null, Token));

        // The index itself, not the claim's key in front of it: a row written around the store.
        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(fixture.Connection, CycleInsert("C-2", "CLEARING")));
        Assert.Equal(2067, refused.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_UNIQUE
        Assert.Contains("ChargingCycles.VehicleKey", refused.Message, StringComparison.Ordinal);
        await ExecuteAsync(fixture.Connection, CycleInsert("C-0", "ENDED"));

        // Through the store, the second start is told why.
        Assert.Equal(ChargingCycleStartOutcome.OpenCycleExists,
            await new ChargingCycleStore(fixture.NewContext()).TryStartAsync(Start("C-3", "agv02") with { StationId = 212 }, null, Token));

        ChargingCycle open = (await cycles.ReadOpenAsync("agv02", Token))!;
        Assert.True(await cycles.UpdateAsync(
            open with { Phase = "ENDED", WireState = "COMPLETE", EndedAt = Now.AddHours(1), EndReason = "COMPLETED" }, Token));
        await ExecuteAsync(fixture.Connection, CycleInsert("C-4", "ACTIVE"));
    }

    [Fact]
    public async Task AnUpdateOnlyAppliesToTheVersionItWasReadAt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingCycleStore cycles = new(fixture.Context);
        await cycles.TryStartAsync(Start("C-1", "agv02"), null, Token);
        ChargingCycle read = (await cycles.ReadAsync("C-1", Token))!;

        Assert.True(await new ChargingCycleStore(fixture.NewContext()).UpdateAsync(
            read with { WireState = "EN_ROUTE", UpperId = "W2G-CHARGE-1", OrderConfirmedAt = Now.AddSeconds(5) }, Token));
        Assert.False(await cycles.UpdateAsync(read with { WireState = "UNKNOWN" }, Token));

        ChargingCycle now = (await cycles.ReadAsync("C-1", Token))!;
        Assert.Equal(("EN_ROUTE", "W2G-CHARGE-1", 2L), (now.WireState, now.UpperId, now.Version));
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Holds

    [Fact]
    public async Task TheSameTriggerMakesOneStationHoldAndARecoveryIsANewRowThatLeavesTheHoldAsItWas()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingHoldStore holds = new(fixture.Context);

        HoldWriteResult<ChargingStationAllocationHold> first = await holds.RecordStationHoldAsync(StationHold("H-1"), Token);
        HoldWriteResult<ChargingStationAllocationHold> second =
            await new ChargingHoldStore(fixture.NewContext()).RecordStationHoldAsync(StationHold("H-2"), Token);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal("H-1", second.Hold.HoldId);
        string[] holdRow = await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargingStationAllocationHolds");
        Assert.Contains("RootCause='UNKNOWN'", Assert.Single(holdRow), StringComparison.Ordinal);
        Assert.Equal(["H-1"], (await holds.ListActiveStationHoldsAsync(26, 211, Token)).Select(hold => hold.HoldId));

        ChargingHoldRecovery recovery = Recovery("R-1", "H-1");
        Assert.Equal(recovery, await holds.RecoverStationHoldAsync(recovery, Token));
        // Recovering again returns the recovery already written.
        Assert.Equal(recovery, await new ChargingHoldStore(fixture.NewContext()).RecoverStationHoldAsync(Recovery("R-2", "H-1"), Token));

        Assert.Equal(holdRow, await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargingStationAllocationHolds"));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ChargingStationRecoveries"));
        Assert.Empty(await holds.ListActiveStationHoldsAsync(26, 211, Token));
        Assert.Equal(StationHold("H-1"), await holds.ReadStationHoldAsync("H-1", Token));
    }

    [Fact]
    public async Task VehicleHoldsAreIdempotentRecoveredByANewRowAndTheirReasonIsChecked()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingHoldStore holds = new(fixture.Context);
        VehicleChargingEligibilityHold hold = new("VH-1", "cycle:C-1:no-progress", "agv02", "C-1", "NO_PROGRESS_CONFIRMED", Now, "ev-1");

        Assert.True((await holds.RecordVehicleHoldAsync(hold, Token)).Created);
        Assert.False((await new ChargingHoldStore(fixture.NewContext()).RecordVehicleHoldAsync(hold with { HoldId = "VH-2" }, Token)).Created);
        Assert.Equal([hold], await holds.ListActiveVehicleHoldsAsync("agv02", Token));

        await holds.RecoverVehicleHoldAsync(Recovery("VR-1", "VH-1"), Token);
        Assert.Empty(await holds.ListActiveVehicleHoldsAsync("agv02", Token));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehicleChargingEligibilityHolds"));

        DbUpdateException refused = await Assert.ThrowsAsync<DbUpdateException>(() => new ChargingHoldStore(fixture.NewContext())
            .RecordVehicleHoldAsync(hold with { HoldId = "VH-3", IdempotencyKey = "k", Reason = "UNABLE_TO_CHARGE_CONFIRMED" }, Token));
        Assert.Contains("CK_VehicleChargingEligibilityHolds_Reason", refused.InnerException!.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => holds.RecoverVehicleHoldAsync(Recovery("VR-2", "VH-404"), Token));
    }

    [Fact]
    public async Task HoldAndRecoveryRowsAreNeverRewrittenAndTheRootCauseIsAlwaysUnknown()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ChargingHoldStore holds = new(fixture.Context);
        await holds.RecordStationHoldAsync(StationHold("H-1"), Token);
        await holds.RecoverStationHoldAsync(Recovery("R-1", "H-1"), Token);
        await holds.RecordVehicleHoldAsync(
            new VehicleChargingEligibilityHold("VH-1", "k", "agv02", null, "INTERRUPTION_CONFIRMED", Now, null), Token);
        await holds.RecoverVehicleHoldAsync(Recovery("VR-1", "VH-1"), Token);

        await using ControlServerDbContext writer = fixture.NewContext();
        foreach (object row in new object[]
                 {
                     await writer.Set<ChargingStationAllocationHoldRow>().SingleAsync(Token),
                     await writer.Set<ChargingStationRecoveryRow>().SingleAsync(Token),
                     await writer.Set<VehicleChargingEligibilityHoldRow>().SingleAsync(Token),
                     await writer.Set<VehicleChargingEligibilityRecoveryRow>().SingleAsync(Token),
                 })
        {
            writer.Remove(row);
            await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
            writer.Entry(row).State = EntityState.Modified;
            await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(Token));
            writer.Entry(row).State = EntityState.Unchanged;
        }

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            fixture.Connection,
            "INSERT INTO ChargingStationAllocationHolds (HoldId, IdempotencyKey, Trigger, RootCause, MapId, StationId, HeldAt) " +
            "VALUES ('H-9', 'k9', 'MAINTENANCE', 'CHARGER_FAULT', 26, 211, 'x')"));
        Assert.Contains("CK_ChargingStationAllocationHolds_RootCause", refused.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Station clearance, manual charging hold, field confirmations

    [Fact]
    public async Task ACycleHasOneClearanceAndItCompletesOnce()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        StationClearanceStore clearances = new(fixture.Context);

        StationClearance started = await clearances.StartAsync("SC-1", "C-1", "agv02", 26, 211, Now, Token);
        Assert.Equal(started,
            await new StationClearanceStore(fixture.NewContext()).StartAsync("SC-2", "C-1", "agv02", 26, 211, Now.AddMinutes(1), Token));

        StationClearanceCompletion manual = new(
            Now.AddMinutes(5), "MANUAL_CONFIRMATION", null, null, "person-7", "R-11", Now.AddMinutes(4), "safe bay 3",
            "CANCELLED_CONFIRMED", ["person-8"], "STATION_EMPTY", "MC-1");
        Assert.True(await clearances.CompleteAsync("SC-1", manual, Token));
        Assert.False(await new StationClearanceStore(fixture.NewContext()).CompleteAsync(
            "SC-1", manual with { Proof = "ARRIVED_AT_WAITING_POINT" }, Token));

        StationClearance done = (await clearances.ReadByCycleAsync("C-1", Token))!;
        Assert.Equal(("MANUAL_CONFIRMATION", "person-7", "safe bay 3", "CANCELLED_CONFIRMED"),
            (done.Proof, done.ConfirmedBy, done.VehicleFinalPosition, done.OldOrderDisposition));
        Assert.Equal(["person-8"], done.Assistants);
        // What the field confirmed and which request it came through (batch 9-08 item 6, #416 review 1).
        Assert.Equal(("STATION_EMPTY", "MC-1"), (done.ClearedCondition, done.ConfirmationRequestId));
    }

    [Fact]
    public async Task AManualChargingHoldIsPlacedOnceAndReleasedByAReturnToServiceRequest()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ManualChargingHoldStore holds = new(fixture.Context);

        Assert.Equal(ManualChargingHoldPlacement.Placed,
            await holds.PlaceAsync("MH-1", "agv02", ManualChargingHoldReasons.RosterEmpty, Now, Token));
        Assert.Equal(ManualChargingHoldPlacement.AlreadyHeld,
            await new ManualChargingHoldStore(fixture.NewContext()).PlaceAsync("MH-2", "agv02", "OTHER", Now.AddMinutes(1), Token));
        Assert.True(await holds.MarkWarnedAsync("agv02", Now.AddMinutes(2), Token));
        Assert.Equal(new ManualChargingHold("MH-1", "agv02", "ROSTER_EMPTY", Now, Now.AddMinutes(2), null, null),
            await holds.ReadAsync("agv02", Token));

        Assert.True(await holds.ReleaseAsync("agv02", "REQ-RTS-1", Now.AddHours(1), Token));
        Assert.False(await holds.ReleaseAsync("agv02", "REQ-RTS-2", Now.AddHours(2), Token));
        Assert.Null(await holds.ReadAsync("agv02", Token));
        Assert.Equal(
            [new ManualChargingHold("MH-1", "agv02", "ROSTER_EMPTY", Now, Now.AddMinutes(2), Now.AddHours(1), "REQ-RTS-1")],
            await holds.ListHistoryAsync("agv02", Token));

        Assert.Equal(ManualChargingHoldPlacement.Placed,
            await holds.PlaceAsync("MH-3", "agv02", ManualChargingHoldReasons.RosterEmpty, Now.AddHours(3), Token));
        Assert.Equal(2, (await holds.ListHistoryAsync("agv02", Token)).Count);
    }

    [Fact]
    public async Task ASecondDecisionOfTheSameUnableToChargeRequestGetsTheFirstOne()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        UnableToChargeFieldConfirmation first = new(
            Request("UC-1", "hash-1"), "211", "CONNECTION_FAILED",
            new FieldConfirmationDecision("CONFIRMED", null, null, null, Now), "MANUAL_CHARGING_HOLD");

        Assert.Equal(first, await new FieldConfirmationRequestStore(fixture.Context).DecideUnableToChargeAsync(first, Token));
        UnableToChargeFieldConfirmation again = first with
        {
            Decision = new FieldConfirmationDecision("REJECTED", "X", "payload", "no", Now.AddMinutes(1)),
            ChargingPolicyDecision = null
        };
        Assert.Equal(first, await new FieldConfirmationRequestStore(fixture.NewContext()).DecideUnableToChargeAsync(again, Token));

        await Assert.ThrowsAsync<FieldConfirmationContentConflictException>(() =>
            new FieldConfirmationRequestStore(fixture.NewContext())
                .DecideUnableToChargeAsync(first with { Request = Request("UC-1", "hash-2") }, Token));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "UnableToChargeFieldConfirmations"));
    }

    [Fact]
    public async Task ASecondDecisionOfTheSameManualStationClearanceRequestGetsTheFirstOne()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        ManualStationClearanceConfirmation first = new(
            Request("MC-1", "hash-1"), "211", null, "STATION_EMPTY",
            new FieldConfirmationDecision("CONFIRMED", null, null, null, Now), true);

        Assert.Equal(first, await new FieldConfirmationRequestStore(fixture.Context).DecideManualStationClearanceAsync(first, Token));
        Assert.Equal(first, await new FieldConfirmationRequestStore(fixture.NewContext()).DecideManualStationClearanceAsync(
            first with { StationReleased = false, Decision = first.Decision with { Outcome = "REJECTED" } }, Token));
        Assert.Equal(first, await new FieldConfirmationRequestStore(fixture.NewContext()).ReadManualStationClearanceAsync("MC-1", Token));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "ManualStationClearanceConfirmations"));
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Order intent shape

    [Fact]
    public async Task AnOrderIntentKeepsItsShapeAndEveryExistingWriterLeavesItASingleMove()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyExecutionPlan plan = await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-1", "AGV-01", "VK-01", Now);

        StoredMovementIntent stored = (await new WireToGateStore(fixture.NewContext()).GetByUpperIdAsync(plan.PickupUpperId, Token))!;
        Assert.Equal(OrderShapes.SingleMove, stored.Intent.OrderShape);
        Assert.Contains("OrderShape='SINGLE_MOVE'", Assert.Single(
            await Batch7JourneyFixture.DumpAsync(fixture.Connection, "OrderIntents")), StringComparison.Ordinal);

        // The shape is the record's last parameter, by position: control-server#401 builds charging intents this way.
        OrderIntent charging = new("LEG-C", "D-1", "W2G-CHARGE-1", "TO_CHARGER", "ST-211", Now, "VK-01", 26, 211, 1, 1, OrderShapes.Charge);
        Assert.Equal("CHARGE", charging.OrderShape);
        Assert.Equal(OrderShapes.SingleMove, new OrderIntent("LEG", "D", "U", "P", "T", Now).OrderShape);
    }

    // ----------------------------------------------------------------------------------------------------------------

    private static ChargerRosterStore Roster(ControlServerDbContext context) => new(context, Publisher(context));

    private static ChargingPolicyStore Policies(ControlServerDbContext context) => new(context, Publisher(context));

    private static GovernedConfigurationPublisher Publisher(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
        return new GovernedConfigurationPublisher(governance, governance);
    }

    private static ChargerRosterApproval Approval(string note) => new("szy", "user decision 2026-09-29", note);

    private static ChargerRosterEntry Charger211(IReadOnlyList<string> scope) => new(26, 211, "充电点1", 210, 212, scope);

    private static ChargingPolicyContent Policy(IReadOnlyList<string> scope) => new(10, 30, 90, 8, 120, 600, 2, scope);

    private static StationExclusivityRequest Charger(int stationId, long rosterVersion) =>
        new(26, stationId, StationExclusivityKinds.Charger, StationExclusivityStates.Reserved, null, rosterVersion);

    private static ChargingCycleStart Start(string cycleId, string vehicleKey) =>
        new(cycleId, vehicleKey, "charge:" + vehicleKey, 26, 211, 2, 7, Now);

    private static OrderIntentRow ChargingIntent(string cycleId) => new()
    {
        MovementLegId = "LEG-" + cycleId,
        DemandId = null!,
        UpperId = "W2G-CHARGE-" + cycleId,
        Purpose = "TO_CHARGER",
        TargetStationId = "ST-211",
        VehicleKey = "agv02",
        MapId = 26,
        DestinationStationId = 211,
        CreatedAt = Now,
        OrderShape = OrderShapes.Charge
    };

    private static string CycleInsert(string cycleId, string phase) =>
        "INSERT INTO ChargingCycles (CycleId, VehicleKey, JourneyId, MapId, StationId, ChargerRosterVersion, " +
        "ChargingPolicyVersion, WireState, Phase, AllocatedAt, Version) " +
        $"VALUES ('{cycleId}', 'agv02', 'charge:agv02', 26, 212, 2, 7, 'UNKNOWN', '{phase}', 'x', 1)";

    private static ChargingStationAllocationHold StationHold(string holdId) => new(
        holdId, "cycle:C-1:unable-to-charge", "UNABLE_TO_CHARGE_CONFIRMED", 26, 211, 2, "agv02", "REC-1", "C-1",
        "W2G-CHARGE-1", "ORDER-1", Now, null, Now.AddMinutes(1), Now.AddMinutes(2), Now.AddMinutes(3), Now.AddMinutes(4),
        "{\"x\":1}", "{\"state\":9}", "{\"act\":78}", "{\"battery\":40}", "evidence/1", "riot-build-7", "riot-contract-2",
        "person-7", "R-11", Now.AddMinutes(3), "moved to bay 3");

    private static ChargingHoldRecovery Recovery(string recoveryId, string holdId) =>
        new(recoveryId, holdId, "person-9", "MAINTENANCE_ADMINISTRATOR", Now.AddHours(1), "repaired");

    private static FieldConfirmationRequestIdentity Request(string id, string hash) =>
        new(id, "AGV-8005-02", 3, "MSG-" + id, hash, "person-7", "BADGE", Now, Now);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string[] FindEnum(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty child in element.EnumerateObject())
            {
                if (child.Name == property && child.Value.TryGetProperty("enum", out JsonElement values))
                {
                    return [.. values.EnumerateArray().Select(value => value.GetString()!)];
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

    /// <summary>Runs <paramref name="sql"/> once, just before the first save: another writer committing in between.</summary>
    private sealed class BeforeSave(SqliteConnection connection, string sql) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await ExecuteAsync(connection, sql);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Throws before the first command that inserts into <paramref name="table"/> reaches the database.</summary>
    private sealed class FailOnInsertInto(string table) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public int InsertsBefore { get; private set; }

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
                throw new InvalidOperationException($"Injected failure while inserting into {table}.");
            }
            if (command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal))
            {
                InsertsBefore++;
            }
        }
    }
}
