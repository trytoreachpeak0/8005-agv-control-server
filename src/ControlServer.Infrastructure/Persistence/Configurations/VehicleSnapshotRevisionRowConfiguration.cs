using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehicleSnapshotRevisionRowConfiguration : IEntityTypeConfiguration<VehicleSnapshotRevisionRow>
{
    public void Configure(EntityTypeBuilder<VehicleSnapshotRevisionRow> builder)
    {
        builder.ToTable("VehicleSnapshotRevisions");
        builder.HasKey(row => row.AgvId);
    }
}
