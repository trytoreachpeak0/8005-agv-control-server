using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AgvLifecycleRowConfiguration : IEntityTypeConfiguration<AgvLifecycleRow>
{
    public void Configure(EntityTypeBuilder<AgvLifecycleRow> builder)
    {
        builder.ToTable("AgvLifecycles");
        builder.HasKey(row => row.AgvId);
    }
}
