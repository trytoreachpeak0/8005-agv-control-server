using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AdmissionDecisionSnapshotRowConfiguration
    : IEntityTypeConfiguration<AdmissionDecisionSnapshotRow>
{
    public void Configure(EntityTypeBuilder<AdmissionDecisionSnapshotRow> builder)
    {
        builder.HasKey(row => row.SlotOperationAttemptId);
    }
}
