using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class StationClearanceRowConfiguration : IEntityTypeConfiguration<StationClearanceRow>
{
    public void Configure(EntityTypeBuilder<StationClearanceRow> builder)
    {
        builder.ToTable("StationClearances", table => table.HasCheckConstraint(
            "CK_StationClearances_Proof", CheckConstraintSql.OneOf("Proof", StationClearanceProofs.All)));
        builder.HasKey(row => row.ClearanceId);
        builder.HasIndex(row => row.CycleId).IsUnique();
        builder.HasIndex(row => new { row.MapId, row.StationId });
        // Completion is written once, against the row as it was read.
        builder.Property(row => row.CompletedAt).IsConcurrencyToken();
    }
}
