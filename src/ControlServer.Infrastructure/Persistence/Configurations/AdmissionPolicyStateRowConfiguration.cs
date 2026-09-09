using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AdmissionPolicyStateRowConfiguration : IEntityTypeConfiguration<AdmissionPolicyStateRow>
{
    public void Configure(EntityTypeBuilder<AdmissionPolicyStateRow> builder)
    {
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
    }
}
