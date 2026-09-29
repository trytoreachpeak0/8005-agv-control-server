using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ManualStationClearanceConfirmationRowConfiguration : IEntityTypeConfiguration<ManualStationClearanceConfirmationRow>
{
    public void Configure(EntityTypeBuilder<ManualStationClearanceConfirmationRow> builder)
    {
        builder.ToTable("ManualStationClearanceConfirmations");
        builder.HasKey(row => row.ConfirmationRequestId);
        builder.HasIndex(row => row.RequestMessageId).IsUnique();
    }
}
