using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ConnectionRecoveryRowConfiguration : IEntityTypeConfiguration<ConnectionRecoveryRow>
{
    public void Configure(EntityTypeBuilder<ConnectionRecoveryRow> builder)
    {
        builder.HasKey(row => row.AgvId);
        builder.Property(row => row.Status).HasConversion<string>();
    }
}
