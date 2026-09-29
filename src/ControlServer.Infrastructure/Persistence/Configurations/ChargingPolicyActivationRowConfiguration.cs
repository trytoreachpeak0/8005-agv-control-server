using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargingPolicyActivationRowConfiguration : IEntityTypeConfiguration<ChargingPolicyActivationRow>
{
    public void Configure(EntityTypeBuilder<ChargingPolicyActivationRow> builder)
    {
        builder.ToTable("ChargingPolicyActivations");
        builder.HasKey(row => row.ActivationId);
        // The writer takes the current maximum plus one: two concurrent activations collide here and one rolls back whole,
        // so "the latest activation" is never a tie.
        builder.HasIndex(row => row.Sequence).IsUnique();
        builder.HasIndex(row => row.Version);
    }
}
