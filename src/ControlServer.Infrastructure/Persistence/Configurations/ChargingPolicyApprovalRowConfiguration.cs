using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargingPolicyApprovalRowConfiguration : IEntityTypeConfiguration<ChargingPolicyApprovalRow>
{
    public void Configure(EntityTypeBuilder<ChargingPolicyApprovalRow> builder)
    {
        builder.ToTable("ChargingPolicyApprovals", table => table.HasCheckConstraint(
            "CK_ChargingPolicyApprovals_Source", CheckConstraintSql.OneOf("Source", ChargingPolicyApprovalSources.All)));
        builder.HasKey(row => row.ApprovalId);
        builder.HasIndex(row => row.Version);
    }
}
