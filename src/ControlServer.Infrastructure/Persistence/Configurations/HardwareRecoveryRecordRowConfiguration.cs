using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class HardwareRecoveryRecordRowConfiguration
    : IEntityTypeConfiguration<HardwareRecoveryRecordRow>
{
    public void Configure(EntityTypeBuilder<HardwareRecoveryRecordRow> builder)
    {
        builder.HasKey(row => row.RecordId);
    }
}
