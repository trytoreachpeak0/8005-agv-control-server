using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class PackageCapacityRuleRowConfiguration : IEntityTypeConfiguration<PackageCapacityRuleRow>
{
    public void Configure(EntityTypeBuilder<PackageCapacityRuleRow> builder)
    {
        builder.HasKey(row => row.RuleId);
        builder
            .HasIndex(row => new { row.Pattern, row.MatchType })
            .IsUnique()
            .HasFilter("SupersededAt IS NULL");
        builder.HasData(PackageCapacitySeed.Rows);
    }
}
