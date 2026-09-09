using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class RecoveryWorkflowRowConfiguration : IEntityTypeConfiguration<RecoveryWorkflowRow>
{
    public void Configure(EntityTypeBuilder<RecoveryWorkflowRow> builder)
    {
        builder.HasKey(row => row.WorkflowId);
        builder.Property(row => row.State).HasConversion<string>();
        builder.HasIndex(row => row.CommandMessageId).IsUnique();
    }
}
