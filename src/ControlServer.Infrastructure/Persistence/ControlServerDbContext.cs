using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

public sealed class ControlServerDbContext(DbContextOptions<ControlServerDbContext> options) : DbContext(options)
{
    public DbSet<AcceptedDemandRow> AcceptedDemands => Set<AcceptedDemandRow>();
    public DbSet<OrderIntentRow> OrderIntents => Set<OrderIntentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AcceptedDemandRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<AcceptedDemandRow>().HasIndex(row => row.TransportDemandKey).IsUnique();
        modelBuilder.Entity<OrderIntentRow>().HasKey(row => row.MovementLegId);
        modelBuilder.Entity<OrderIntentRow>().HasIndex(row => row.UpperId).IsUnique();
    }
}

public sealed class AcceptedDemandRow
{
    public required string DemandId { get; init; }
    public required string TransportDemandKey { get; init; }
    public long DemandRevision { get; init; }
    public required string HistoryEpoch { get; init; }
    public long CatalogRevision { get; init; }
    public DateTimeOffset AcceptedAt { get; init; }
}

public sealed class OrderIntentRow
{
    public required string MovementLegId { get; init; }
    public required string DemandId { get; init; }
    public required string UpperId { get; init; }
    public required string Purpose { get; init; }
    public required string TargetStationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class DemandAcceptanceStore(ControlServerDbContext dbContext) : IDemandAcceptanceStore
{
    public Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        CancellationToken cancellationToken)
    {
        _ = dbContext;
        _ = snapshot;
        _ = orderIntent;
        _ = cancellationToken;
        throw new NotImplementedException("W2G-IS-01 must atomically persist the accepted demand and order intent.");
    }
}
