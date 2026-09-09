using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class RecoveryResultEvidenceRowConfiguration
    : IEntityTypeConfiguration<RecoveryResultEvidenceRow>
{
    public void Configure(EntityTypeBuilder<RecoveryResultEvidenceRow> builder)
    {
        builder.HasKey(row => row.MessageId);
    }
}
