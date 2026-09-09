using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class RecoveryDecisionRowConfiguration : IEntityTypeConfiguration<RecoveryDecisionRow>
{
    public void Configure(EntityTypeBuilder<RecoveryDecisionRow> builder)
    {
        builder.HasKey(row => row.RecoveryActionId);
    }
}
