using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ProtocolInboxRowConfiguration : IEntityTypeConfiguration<ProtocolInboxRow>
{
    public void Configure(EntityTypeBuilder<ProtocolInboxRow> builder)
    {
        builder.HasKey(row => row.MessageId);
        // The inbox keeps every heartbeat the vehicle ever sent -- about 17,000 rows a day while it is
        // connected, nine in ten of the table. Without these two indexes every read by message type and
        // every liveness read scanned all of it (8005-agv-control-server#29).
        builder.HasIndex(row => row.MessageType);
        builder.HasIndex(row => row.ReceivedAtUtcTicks);
    }
}
