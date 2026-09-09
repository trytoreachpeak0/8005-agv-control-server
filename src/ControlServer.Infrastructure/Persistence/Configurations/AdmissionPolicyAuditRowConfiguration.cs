using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AdmissionPolicyAuditRowConfiguration : IEntityTypeConfiguration<AdmissionPolicyAuditRow>
{
    public void Configure(EntityTypeBuilder<AdmissionPolicyAuditRow> builder)
    {
        builder.HasKey(row => row.Version);
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
