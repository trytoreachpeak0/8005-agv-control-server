using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class UnloadBatchRowConfiguration : IEntityTypeConfiguration<UnloadBatchRow>
{
    public void Configure(EntityTypeBuilder<UnloadBatchRow> builder)
    {
        builder.HasKey(row => row.UnloadBatchId);
    }
}
