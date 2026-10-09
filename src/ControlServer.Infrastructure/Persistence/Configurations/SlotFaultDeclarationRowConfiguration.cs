using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class SlotFaultDeclarationRowConfiguration : IEntityTypeConfiguration<SlotFaultDeclarationRow>
{
    public void Configure(EntityTypeBuilder<SlotFaultDeclarationRow> builder)
    {
        builder.ToTable("SlotFaultDeclarations");
        builder.HasKey(row => row.DeclarationId);
        builder.HasIndex(row => row.RequestId).IsUnique();
        builder.HasIndex(row => row.CommandMessageId).IsUnique();
        // The database, not a read before the write, is what keeps a second declaration off an attempt that already has one
        // waiting for the vehicle (REQ-0359; two administrators at the same moment both pass every read).
        builder
            .HasIndex(row => row.SlotOperationAttemptId)
            .IsUnique()
            .HasFilter("State = 'PENDING'")
            .HasDatabaseName("IX_SlotFaultDeclarations_PendingAttempt");
        builder.HasIndex(row => row.AgvId);
    }
}
