using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class MissingPackageRowConfiguration : IEntityTypeConfiguration<MissingPackageRow>
{
    public void Configure(EntityTypeBuilder<MissingPackageRow> builder)
    {
        builder.HasKey(row => row.Package);
    }
}
