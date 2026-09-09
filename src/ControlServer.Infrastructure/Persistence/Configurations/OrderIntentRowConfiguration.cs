using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class OrderIntentRowConfiguration : IEntityTypeConfiguration<OrderIntentRow>
{
    public void Configure(EntityTypeBuilder<OrderIntentRow> builder)
    {
        builder.HasKey(row => row.MovementLegId);
        builder.HasIndex(row => row.UpperId).IsUnique();
        builder.Property(row => row.Status).IsConcurrencyToken();
        builder.Property(row => row.CreateAttemptCount).IsConcurrencyToken();
        builder.Property(row => row.DispatchAuditSequence).IsConcurrencyToken();
        builder.Property(row => row.ExperimentalCreateAuthorizationId).IsConcurrencyToken();
        builder
            .HasIndex(row => row.CreateAttemptId)
            .IsUnique()
            .HasFilter("CreateAttemptId IS NOT NULL");
        builder
            .HasIndex(row => row.ExperimentalCreateAuthorizationId)
            .IsUnique()
            .HasFilter("ExperimentalCreateAuthorizationId IS NOT NULL");
    }
}
