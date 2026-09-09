using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ExperimentalRiotCreateAuthorizationRowConfiguration
    : IEntityTypeConfiguration<ExperimentalRiotCreateAuthorizationRow>
{
    public void Configure(EntityTypeBuilder<ExperimentalRiotCreateAuthorizationRow> builder)
    {
        builder.HasKey(row => row.AuthorizationId);
        builder
            .HasIndex(row => row.UpperId)
            .IsUnique();
        builder
            .HasIndex(row => row.ConsumedByAttemptId)
            .IsUnique()
            .HasFilter("ConsumedByAttemptId IS NOT NULL");
        builder
            .Property(row => row.ConsumedByAttemptId)
            .IsConcurrencyToken();
    }
}
