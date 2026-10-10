using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class StationExclusivityRowConfiguration : IEntityTypeConfiguration<StationExclusivityRow>
{
    public void Configure(EntityTypeBuilder<StationExclusivityRow> builder)
    {
        builder.ToTable("StationExclusivities", table =>
        {
            table.HasCheckConstraint(
                "CK_StationExclusivities_State", CheckConstraintSql.OneOf("State", StationExclusivityStates.All));
            table.HasCheckConstraint(
                "CK_StationExclusivities_StationKind",
                CheckConstraintSql.OneOf("StationKind", StationExclusivityKinds.All));
        });
        // One row per station: whoever inserts first holds it, and the key refuses the second (specification 5.4).
        builder.HasKey(row => new { row.MapId, row.StationId });
        builder.Property(row => row.MapId).ValueGeneratedNever();
        builder.Property(row => row.StationId).ValueGeneratedNever();
        // A state change or a release is written only against the holder and the state it was read with: a row released
        // and taken again by another journey in between answers "no rows", not the newcomer's row.
        builder.Property(row => row.JourneyId).IsConcurrencyToken();
        builder.Property(row => row.State).IsConcurrencyToken();
        builder.HasIndex(row => row.VehicleKey);
        builder.HasIndex(row => row.JourneyId);
        builder.HasIndex(row => row.RecordId).IsUnique();
    }
}
