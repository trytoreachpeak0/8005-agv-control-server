using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SessionRecoveryRowConfiguration : IEntityTypeConfiguration<SessionRecoveryRow>
{
    public void Configure(EntityTypeBuilder<SessionRecoveryRow> builder)
    {
        builder.HasKey(row => row.AgvId);
        builder.Property(row => row.Readiness).HasConversion<string>();
    }
}
