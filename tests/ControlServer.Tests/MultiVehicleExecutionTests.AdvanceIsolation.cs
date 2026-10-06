using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Tests;

/// <summary>
/// control-server#487: one vehicle whose advance throws something other than an unavailable Onboard connection every
/// round no longer stops the round for the vehicles behind it or for the dispatch round.
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// The first vehicle's advance throws every round (its order read); the second vehicle's leg order goes FAILED while it is still moving.
    /// The second vehicle's fault is still recorded, held and escalated to an emergency stop, the dispatch round still runs,
    /// and the failing vehicle is not offered to it.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AVehicleWhoseAdvanceThrowsEveryRoundDoesNotStopTheOthersSupervisionOrTheDispatchRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        string failing = FleetFixture.AgvIds[0];
        string failingOrder = (await fixture.JourneyOfAsync(failing)).PickupUpperId;
        JourneyRuntimeRow stopped = await fixture.JourneyOfAsync(FleetFixture.AgvIds[1]);
        // Not a connection failure and not caught on the way: the shape of control-server#291's every-round throw.
        fixture.Riot.BeforeReconcile = upperId => upperId == failingOrder
            ? throw new InvalidOperationException("Injected: this vehicle's advance throws every round.")
            : Task.CompletedTask;
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(stopped.PickupUpperId);
        int outcomesBefore = fixture.RoundOutcomes.Outcomes.Count;

        for (int round = 0; round < 2; round++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunRoundAsync(TimeSpan.FromSeconds(1)));
        }

        VehicleFaultStateRow fault = Assert.Single(
            await fixture.Context.VehicleFaultStates.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            (FleetFixture.AgvIds[1], VehicleFaultEvidence.OrderFailed, true),
            (fault.AgvId, fault.EvidenceCode, fault.EscalatedAt is not null));
        Assert.Contains(
            fixture.Riot.EmergencyCommands,
            command => command.CommandType == RiotCommandTypeNames.TriggerEmergency &&
                       command.DeviceKey == FleetFixture.VehicleKeys[1]);

        Assert.Equal(outcomesBefore + 2, fixture.RoundOutcomes.Outcomes.Count);
        Assert.All(
            fixture.RoundOutcomes.Outcomes.Skip(outcomesBefore),
            outcome => Assert.DoesNotContain(outcome.CompletedVehicles, vehicle => vehicle.AgvId == failing));
        Assert.Equal(
            JourneyRuntimeEngine.AdvanceFailedReason,
            (await fixture.JourneyOfAsync(failing)).BlockReasonCode);
    }

    /// <summary>
    /// What the failing vehicle staged on another vehicle's journey row and never saved is withdrawn: neither that vehicle's
    /// own advance nor the dispatch round, both of which save on the same context later in the round, carries it out.
    /// </summary>
    [Fact]
    public async Task WhatAVehicleWhoseAdvanceThrowsHadStagedOnAnotherRowIsNotSavedLaterInTheRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        string failingOrder = (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).PickupUpperId;
        string other = FleetFixture.AgvIds[2];
        fixture.Riot.BeforeReconcile = upperId =>
        {
            if (upperId != failingOrder)
            {
                return Task.CompletedTask;
            }
            fixture.Context.ChangeTracker.Entries<JourneyRuntimeRow>()
                .Single(entry => entry.Entity.AgvId == other).Entity.SetBlockReason(StagedReason, fixture.Clock.GetUtcNow());
            throw new InvalidOperationException("Injected: this vehicle's advance throws after staging a change.");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunRoundAsync(TimeSpan.FromSeconds(1)));

        Assert.NotEqual(StagedReason, (await fixture.JourneyOfAsync(other)).BlockReasonCode);
        Assert.Equal(
            JourneyRuntimeEngine.AdvanceFailedReason,
            (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).BlockReasonCode);
    }

    /// <summary>
    /// The failing vehicle saved inside a transaction that then rolled back: a row tracked before its advance (another
    /// vehicle's journey) and a row it began tracking (a backlog row) are both tracked as Unchanged with what the database does
    /// not have. The first is read again, so its own vehicle advances on what the database says and does not yield to a
    /// conflict with a version that never landed; the second is let go, so nothing later in the round finds it.
    /// </summary>
    [Fact]
    public async Task WhatAVehicleWhoseAdvanceThrowsSavedInARolledBackTransactionIsNotTakenAsFact()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        string failingOrder = (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).PickupUpperId;
        string other = FleetFixture.AgvIds[2];
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        fixture.Riot.BeforeReconcile = async upperId =>
        {
            if (upperId != failingOrder)
            {
                return;
            }
            ControlServerDbContext context = fixture.Context;
            await using (IDbContextTransaction transaction = await context.Database
                             .BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                context.ChangeTracker.Entries<JourneyRuntimeRow>()
                    .Single(entry => entry.Entity.AgvId == other).Entity.SetBlockReason(StagedReason, fixture.Clock.GetUtcNow());
                context.JourneyBacklog.Add(new JourneyBacklogRow
                {
                    DemandId = PhantomDemandId,
                    TransportDemandKey = PhantomDemandId,
                    FirstSeenAt = now,
                    DemandCreatedAt = now,
                    DecisionFingerprint = "cs487",
                    ReasonCode = StagedReason,
                    LastSeenAt = now,
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            throw new InvalidOperationException("Injected: this vehicle's advance throws after a rolled back save.");
        };
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            fixture.Context.ChangeTracker.Entries<JourneyRuntimeRow>(),
            entry => entry.Entity.AgvId == other && entry.Entity.BlockReasonCode == StagedReason);
        Assert.DoesNotContain(
            fixture.Context.ChangeTracker.Entries<JourneyBacklogRow>(),
            entry => entry.Entity.DemandId == PhantomDemandId);
        Assert.DoesNotContain(
            fixture.EngineLog.Entries,
            entry => entry.EventId.Id == 2191 && entry.Message.Contains(other, StringComparison.Ordinal));
        fixture.Context.ChangeTracker.Clear();
        Assert.NotEqual(StagedReason, (await fixture.JourneyOfAsync(other)).BlockReasonCode);
        Assert.False(await fixture.Context.JourneyBacklog.AnyAsync(
            row => row.DemandId == PhantomDemandId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A change an earlier step left on another vehicle's row for a later save, which the failing vehicle saved along with its
    /// own inside a transaction that rolled back, goes back to being that change -- its values, its original values and
    /// Modified -- and the save it was left for writes it, against the version the database really has.
    /// </summary>
    /// <remarks>
    /// Driven on the withdrawal itself rather than through a round: no advance today ends with a change left unsaved (every one
    /// saves before it is done, and the round's own hooks only run inside an advance), so a round cannot hand the next vehicle
    /// such an entry. Tried both ways through the RIoT double while writing this; the premise asserted below failed each time.
    /// </remarks>
    [Fact]
    public async Task AChangeLeftForALaterSaveThatTheFailingVehicleSavedAndRolledBackGoesBackToBeingThatChange()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        ControlServerDbContext context = fixture.Context;
        context.ChangeTracker.Clear();
        string other = FleetFixture.AgvIds[2];
        JourneyRuntimeRow row = await context.JourneyRuntimes
            .SingleAsync(journey => journey.AgvId == other, TestContext.Current.CancellationToken);
        long versionInDatabase = row.Version;
        row.SetBlockReason(StagedReason, fixture.Clock.GetUtcNow());
        EntityEntry<JourneyRuntimeRow> entry = context.Entry(row);
        Assert.Equal(EntityState.Modified, entry.State);
        JourneyRuntimeEngine.TrackedBeforeAdvance before = JourneyRuntimeEngine.TrackedBeforeAdvance.Take(context);

        await using (IDbContextTransaction transaction = await context.Database
                         .BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        // The premise: saved and rolled back, it reads as in step with the database, at a version the database never got.
        Assert.Equal((EntityState.Unchanged, versionInDatabase + 1), (entry.State, entry.Property(journey => journey.Version).OriginalValue));

        await before.RestoreAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(EntityState.Modified, entry.State);
        Assert.Equal(StagedReason, row.BlockReasonCode);
        Assert.Equal(versionInDatabase, entry.Property(journey => journey.Version).OriginalValue);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        JourneyRuntimeRow saved = await fixture.JourneyOfAsync(other);
        Assert.Equal((StagedReason, versionInDatabase + 1), (saved.BlockReasonCode, saved.Version));
    }

    private const string StagedReason = "CS487_STAGED_BY_ANOTHER_VEHICLE";

    private const string PhantomDemandId = "cs487-phantom-demand";
}
