using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotTemplateRowConfiguration : IEntityTypeConfiguration<SlotTemplateRow>
{
    public void Configure(EntityTypeBuilder<SlotTemplateRow> builder)
    {
        builder.ToTable("SlotTemplates");
        builder.HasKey(row => row.SlotTemplateId);
        builder.HasIndex(row => new { row.TemplateKey, row.Version }).IsUnique();
    }
}
