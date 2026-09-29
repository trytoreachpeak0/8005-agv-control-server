using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed class AdmissionPolicyStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task VersionedImportIsAuditedAndSameVersionCannotChangeContent()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        AdmissionPolicyDefinition versionOne = Policy(
            1, "DEPLOY-1", [new StationTaskTypeAdmission("PICKUP-01", "WIRE_TO_GATE")]);

        await fixture.Store.ApplyAdmissionPolicyAsync(versionOne, TestContext.Current.CancellationToken);
        await fixture.Store.ApplyAdmissionPolicyAsync(versionOne, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            fixture.Store.ApplyAdmissionPolicyAsync(
                Policy(1, "DEPLOY-1", []), TestContext.Current.CancellationToken));

        AdmissionPolicyStateRow current = await fixture.Context.AdmissionPolicyState
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, current.Version);
        Assert.Equal("DEPLOY-1", current.DeploymentId);
        Assert.Single(await fixture.Context.StationTaskTypeAdmissions
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.AdmissionPolicyAudit
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task LoadCommitFreezesCurrentAdmissionAndRevocationBlocksOnlyNewOperations()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.ApplyAdmissionPolicyAsync(
            Policy(1, "DEPLOY-1", [new StationTaskTypeAdmission("PICKUP-01", "WIRE_TO_GATE")]),
            TestContext.Current.CancellationToken);
        fixture.Context.AcceptedDemands.Add(TaskTypeStationTestData.AcceptedDemand("DEMAND-1", "WIRE_TO_GATE"));
        fixture.Context.AcceptedDemands.Add(TaskTypeStationTestData.AcceptedDemand("DEMAND-2", "WIRE_TO_GATE"));
        // Both demands load at PICKUP-01, the station their admission names (control-server#251).
        fixture.Context.Set<JourneyStopRow>().AddRange(
            JourneyMembershipSeed.Stop("J-1", "STOP-PICKUP", 1, JourneyStopRoles.Pickup, "PICKUP-01"),
            JourneyMembershipSeed.Stop("J-1", "STOP-GATE", 2, JourneyStopRoles.Unload, "GATE-01"));
        fixture.Context.Set<JourneyDemandRow>().AddRange(
            JourneyMembershipSeed.Membership("J-1", "DEMAND-1", "STOP-PICKUP", "STOP-GATE", "ATTEMPT-1", "UNLOAD-1"),
            JourneyMembershipSeed.Membership("J-1", "DEMAND-2", "STOP-PICKUP", "STOP-GATE", "ATTEMPT-2", "UNLOAD-2"));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        StationOperationPlan committed = Plan("ATTEMPT-1", "DEMAND-1");
        string wire = Wire("MESSAGE-1", "ATTEMPT-1");

        await fixture.Store.PrepareSlotOperationAsync(
            committed, "MESSAGE-1", wire, TestContext.Current.CancellationToken);
        await fixture.Store.ApplyAdmissionPolicyAsync(
            Policy(2, "DEPLOY-2", []), TestContext.Current.CancellationToken);
        await fixture.Store.PrepareSlotOperationAsync(
            committed, "MESSAGE-1", wire, TestContext.Current.CancellationToken);

        BusinessIdentityConflictException error = await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            fixture.Store.PrepareSlotOperationAsync(
                Plan("ATTEMPT-2", "DEMAND-2"),
                "MESSAGE-2",
                Wire("MESSAGE-2", "ATTEMPT-2"),
                TestContext.Current.CancellationToken));

        Assert.Equal("TASK_TYPE_NOT_ALLOWED_AT_STATION", error.Message);
        AdmissionDecisionSnapshotRow decision = await fixture.Context.AdmissionDecisionSnapshots
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, decision.AdmissionPolicyVersion);
        Assert.True(decision.Allowed);
        Assert.Single(await fixture.Context.StationOperations
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.ProtocolOutbox
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await fixture.Context.AdmissionPolicyAudit
            .CountAsync(TestContext.Current.CancellationToken));
    }

    private static AdmissionPolicyDefinition Policy(
        long version,
        string deploymentId,
        IReadOnlyList<StationTaskTypeAdmission> relations) =>
        new(version, deploymentId, relations, Now.AddMinutes(version));

    private static StationOperationPlan Plan(string attemptId, string demandId) => new(
        attemptId,
        demandId,
        "SUBLOT-1",
        [1, 2],
        SlotOperationType.Load,
        0,
        new string('a', 64),
        Now,
        "PICKUP-01",
        "WIRE_TO_GATE");

    private static string Wire(string messageId, string attemptId) => JsonSerializer.Serialize(new
    {
        messageType = "SlotOperationCommand",
        messageId,
        agvId = "AGV-1",
        sessionGeneration = 1,
        sentAt = Now,
        payload = new { slotOperationAttemptId = attemptId }
    });

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
            Store = new WireToGateStore(context);
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public WireToGateStore Store { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new StoreFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
