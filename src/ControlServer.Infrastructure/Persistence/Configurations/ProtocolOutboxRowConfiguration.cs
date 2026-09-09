using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ProtocolOutboxRowConfiguration : IEntityTypeConfiguration<ProtocolOutboxRow>
{
    public void Configure(EntityTypeBuilder<ProtocolOutboxRow> builder)
    {
        builder.HasKey(row => row.MessageId);
    }
}
