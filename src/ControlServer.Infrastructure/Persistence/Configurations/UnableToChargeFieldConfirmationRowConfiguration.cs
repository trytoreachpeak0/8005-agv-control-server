using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class UnableToChargeFieldConfirmationRowConfiguration : IEntityTypeConfiguration<UnableToChargeFieldConfirmationRow>
{
    public void Configure(EntityTypeBuilder<UnableToChargeFieldConfirmationRow> builder)
    {
        builder.ToTable("UnableToChargeFieldConfirmations");
        builder.HasKey(row => row.ConfirmationRequestId);
        builder.HasIndex(row => row.RequestMessageId).IsUnique();
    }
}
