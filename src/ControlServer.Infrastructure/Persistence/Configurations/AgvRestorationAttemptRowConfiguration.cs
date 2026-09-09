using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AgvRestorationAttemptRowConfiguration
    : IEntityTypeConfiguration<AgvRestorationAttemptRow>
{
    public void Configure(EntityTypeBuilder<AgvRestorationAttemptRow> builder)
    {
        builder.ToTable("AgvRestorationAttempts");
        builder.HasKey(row => row.RestorationAttemptId);
        builder.Property(row => row.Outcome).HasConversion<string>();
        builder.HasIndex(row => new { row.AgvId, row.StartedAt });
    }
}
