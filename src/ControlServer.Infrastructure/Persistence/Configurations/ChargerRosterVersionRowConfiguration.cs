using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargerRosterVersionRowConfiguration : IEntityTypeConfiguration<ChargerRosterVersionRow>
{
    public void Configure(EntityTypeBuilder<ChargerRosterVersionRow> builder)
    {
        builder.ToTable("ChargerRosterVersions");
        builder.HasKey(row => row.Version);
        // Assigned by the writer as the current maximum plus one, so two concurrent writers collide on the key and
        // one of them rolls back whole.
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
