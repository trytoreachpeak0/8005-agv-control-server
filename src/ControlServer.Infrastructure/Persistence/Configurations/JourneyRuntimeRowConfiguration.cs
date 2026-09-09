using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class JourneyRuntimeRowConfiguration : IEntityTypeConfiguration<JourneyRuntimeRow>
{
    public void Configure(EntityTypeBuilder<JourneyRuntimeRow> builder)
    {
        builder.HasKey(row => row.JourneyId);
        builder.Property(row => row.Stage).HasConversion<string>();
    }
}
