using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class RiotDispatchAuditEventRowConfiguration : IEntityTypeConfiguration<RiotDispatchAuditEventRow>
{
    public void Configure(EntityTypeBuilder<RiotDispatchAuditEventRow> builder)
    {
        builder.HasKey(row => row.AuditEventId);
        builder
            .HasIndex(row => new { row.MovementLegId, row.Sequence })
            .IsUnique();
        builder.HasIndex(row => row.AttemptId);
        builder.HasIndex(row => row.ExperimentalAuthorizationId);
    }
}
