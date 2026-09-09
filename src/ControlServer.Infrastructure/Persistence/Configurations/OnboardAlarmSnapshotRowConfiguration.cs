using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class OnboardAlarmSnapshotRowConfiguration : IEntityTypeConfiguration<OnboardAlarmSnapshotRow>
{
    public void Configure(EntityTypeBuilder<OnboardAlarmSnapshotRow> builder)
    {
        builder.ToTable("OnboardAlarmSnapshots");
        // One row per vehicle: a later snapshot replaces the earlier one whole, never merges into it.
        builder.HasKey(row => row.AgvId);
    }
}
