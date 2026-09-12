using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ProtocolInboxRowConfiguration : IEntityTypeConfiguration<ProtocolInboxRow>
{
    public void Configure(EntityTypeBuilder<ProtocolInboxRow> builder)
    {
        builder.HasKey(row => row.MessageId);
        // The inbox keeps every heartbeat ever sent, so without these every read by message type and every
        // liveness read scans all of it. See JourneyRuntimeEngine.LatestInboundAtForSessionAsync.
        builder.HasIndex(row => row.MessageType);
        builder.HasIndex(row => row.ReceivedAtUtcTicks);
    }
}
