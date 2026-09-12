using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotModelVersionRowConfiguration : IEntityTypeConfiguration<SlotModelVersionRow>
{
    public void Configure(EntityTypeBuilder<SlotModelVersionRow> builder)
    {
        builder.ToTable("SlotModelVersions");
        builder.HasKey(row => row.SlotModelVersionId);
        builder.HasIndex(row => new { row.ModelKey, row.Version }).IsUnique();
    }
}
