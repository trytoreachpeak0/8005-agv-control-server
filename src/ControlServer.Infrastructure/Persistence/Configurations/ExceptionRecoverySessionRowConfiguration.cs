using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ExceptionRecoverySessionRowConfiguration
    : IEntityTypeConfiguration<ExceptionRecoverySessionRow>
{
    public void Configure(EntityTypeBuilder<ExceptionRecoverySessionRow> builder)
    {
        builder.HasKey(row => row.ExceptionRecoverySessionId);
        builder.HasIndex(row => row.RequestId).IsUnique();
    }
}
