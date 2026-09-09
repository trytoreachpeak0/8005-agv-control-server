using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class StationOperationRowConfiguration : IEntityTypeConfiguration<StationOperationRow>
{
    public void Configure(EntityTypeBuilder<StationOperationRow> builder)
    {
        builder.HasKey(row => row.SlotOperationAttemptId);
        builder.Property(row => row.Status).HasConversion<string>();
        builder.Property(row => row.OperationType).HasConversion<string>();
    }
}
