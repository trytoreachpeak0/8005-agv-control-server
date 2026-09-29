using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargingPolicyVersionRowConfiguration : IEntityTypeConfiguration<ChargingPolicyVersionRow>
{
    public void Configure(EntityTypeBuilder<ChargingPolicyVersionRow> builder)
    {
        // Each field's own range only. The relation between the three thresholds (REQ-0281) is deliberately not here: it
        // has one definition, the validation the import and the startup share (batch 9-02, 9-05), and a second copy in
        // the schema would also stop that validation from ever getting its red evidence.
        builder.ToTable("ChargingPolicyVersions", table =>
        {
            table.HasCheckConstraint(
                "CK_ChargingPolicyVersions_MinimumPostTaskBatteryMarginPercent", "\"MinimumPostTaskBatteryMarginPercent\" BETWEEN 0 AND 100");
            table.HasCheckConstraint(
                "CK_ChargingPolicyVersions_MandatoryChargeEntryThresholdPercent", "\"MandatoryChargeEntryThresholdPercent\" BETWEEN 0 AND 100");
            table.HasCheckConstraint(
                "CK_ChargingPolicyVersions_ChargingCompletionThresholdPercent", "\"ChargingCompletionThresholdPercent\" BETWEEN 0 AND 100");
            table.HasCheckConstraint(
                "CK_ChargingPolicyVersions_EstimatedTaskConsumptionPercent", "\"EstimatedTaskConsumptionPercent\" BETWEEN 0 AND 100");
            table.HasCheckConstraint(
                "CK_ChargingPolicyVersions_ProgressStabilizationSeconds", "\"ProgressStabilizationSeconds\" > 0");
            table.HasCheckConstraint(
                "CK_ChargingPolicyVersions_ProgressObservationWindowSeconds", "\"ProgressObservationWindowSeconds\" > 0");
            table.HasCheckConstraint(
                "CK_ChargingPolicyVersions_ProgressMinimumIncreasePercent", "\"ProgressMinimumIncreasePercent\" BETWEEN 1 AND 100");
        });
        builder.HasKey(row => row.Version);
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
