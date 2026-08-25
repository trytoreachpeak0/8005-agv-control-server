using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed class DemandAcceptanceAtomicityTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AcceptedDemandAndToPickupIntentAreCommittedAtomically()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        DemandAcceptanceStore store = new(dbContext);
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, now),
            new OrderIntent("LEG-001", "D-001", "W2G-D-001-PICKUP-1", "TO_PICKUP", "ST-PICKUP", now),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await dbContext.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
    }
}
