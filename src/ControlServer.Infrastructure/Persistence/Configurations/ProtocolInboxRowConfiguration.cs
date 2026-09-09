using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ProtocolInboxRowConfiguration : IEntityTypeConfiguration<ProtocolInboxRow>
{
    public void Configure(EntityTypeBuilder<ProtocolInboxRow> builder)
    {
        builder.HasKey(row => row.MessageId);
    }
}
