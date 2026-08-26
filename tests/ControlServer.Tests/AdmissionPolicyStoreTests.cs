using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed class AdmissionPolicyStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
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
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task LoadCommitFreezesCurrentAdmissionAndRevocationBlocksOnlyNewOperations()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.ApplyAdmissionPolicyAsync(
            Policy(1, "DEPLOY-1", [new StationTaskTypeAdmission("PICKUP-01", "WIRE_TO_GATE")]),
            TestContext.Current.CancellationToken);
        StationOperationPlan committed = Plan("ATTEMPT-1");
        string wire = Wire("MESSAGE-1", "ATTEMPT-1");

        await fixture.Store.PrepareSlotOperationAsync(
            committed, "MESSAGE-1", wire, TestContext.Current.CancellationToken);
        await fixture.Store.ApplyAdmissionPolicyAsync(
            Policy(2, "DEPLOY-2", []), TestContext.Current.CancellationToken);
        await fixture.Store.PrepareSlotOperationAsync(
            committed, "MESSAGE-1", wire, TestContext.Current.CancellationToken);

        BusinessIdentityConflictException error = await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            fixture.Store.PrepareSlotOperationAsync(
                Plan("ATTEMPT-2"),
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

    private static StationOperationPlan Plan(string attemptId) => new(
        attemptId,
        "DEMAND-1",
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
