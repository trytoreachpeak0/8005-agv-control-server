using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class OperationResultRowConfiguration : IEntityTypeConfiguration<OperationResultRow>
{
    public void Configure(EntityTypeBuilder<OperationResultRow> builder)
    {
        builder.HasKey(row => row.ResultId);
        // One live result per attempt per generation. A result superseded by an authorized
        // RESUME_AFTER_REPAIR replacement stays in the table as the record of what the vehicle
        // reported when it failed, and steps out of the uniqueness scope rather than being erased.
        builder
            .HasIndex(row => new { row.SlotOperationAttemptId, row.ForcedRecoveryGeneration })
            .IsUnique()
            .HasFilter("SupersededByResultId IS NULL");
    }
}
