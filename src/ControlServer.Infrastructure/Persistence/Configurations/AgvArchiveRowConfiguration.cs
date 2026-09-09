using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AgvArchiveRowConfiguration : IEntityTypeConfiguration<AgvArchiveRow>
{
    public void Configure(EntityTypeBuilder<AgvArchiveRow> builder)
    {
        builder.ToTable("AgvArchives");
        // Keyed on the vehicle: exactly one archive record per physical car, ever. That is what makes
        // restoring to a second record impossible by shape rather than by a check.
        builder.HasKey(row => row.AgvId);
    }
}
