using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// CA1861 fires on the composite-index column arrays EF generates. They are generated code written
// once per index and never mutated, and hoisting them into fields would mean hand-editing every
// regeneration of this file. Suppressed here rather than repo-wide so ordinary code keeps the rule.
#pragma warning disable CA1861

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch3GovernanceAndSlotConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActiveSlotConfigurations",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotModelVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveSlotConfigurations", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "AdministratorAuditRecords",
                columns: table => new
                {
                    AuditRecordId = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RecordedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ActorAttribution = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimedAdministratorRole = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectKind = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    DetailJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdministratorAuditRecords", x => x.AuditRecordId);
                });

            migrationBuilder.CreateTable(
                name: "AgvArchives",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ArchiveReason = table.Column<string>(type: "TEXT", nullable: false),
                    ArchivedLifecycleGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ArchivedActiveSlotConfigurationFingerprint = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgvArchives", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "AgvLifecycles",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    LifecycleGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Commissioned = table.Column<bool>(type: "INTEGER", nullable: false),
                    CandidateRiotBindingJson = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgvLifecycles", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "AgvRestorationAttempts",
                columns: table => new
                {
                    RestorationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: true),
                    ArchiveReason = table.Column<string>(type: "TEXT", nullable: false),
                    RestoredLifecycleGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    AuditRecordId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgvRestorationAttempts", x => x.RestorationAttemptId);
                });

            migrationBuilder.CreateTable(
                name: "BusinessAuditRecords",
                columns: table => new
                {
                    AuditRecordId = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RecordedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ActorAttribution = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectKind = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    DetailJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessAuditRecords", x => x.AuditRecordId);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationConsumerBindings",
                columns: table => new
                {
                    ConsumerKind = table.Column<string>(type: "TEXT", nullable: false),
                    ConsumerId = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectKind = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    FrozenVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationConsumerBindings", x => new { x.ConsumerKind, x.ConsumerId, x.ObjectKind, x.ObjectId });
                });

            migrationBuilder.CreateTable(
                name: "GovernedConfigurationSnapshots",
                columns: table => new
                {
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectKind = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernedConfigurationSnapshots", x => x.SnapshotId);
                });

            migrationBuilder.CreateTable(
                name: "OnboardAlarmSnapshots",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AlarmsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardAlarmSnapshots", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "SlotConfigurationActivations",
                columns: table => new
                {
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotModelVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RolledBackToVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveryRole = table.Column<string>(type: "TEXT", nullable: false),
                    CommandMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResultReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotConfigurationActivations", x => x.ActivationId);
                });

            migrationBuilder.CreateTable(
                name: "SlotConfigurationReadiness",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotModelVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    Ready = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    InvalidatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotConfigurationReadiness", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "SlotConfigurationVerifications",
                columns: table => new
                {
                    VerificationId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotModelVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalSlotNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenSignalConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CloseSignalConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    InPlaceSignalConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FieldRecordReference = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotConfigurationVerifications", x => x.VerificationId);
                });

            migrationBuilder.CreateTable(
                name: "SlotIoBindings",
                columns: table => new
                {
                    SlotIoBindingId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotModelVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalSlotNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    UnlockOutputPoint = table.Column<string>(type: "TEXT", nullable: false),
                    LockFeedbackInputPoint = table.Column<string>(type: "TEXT", nullable: false),
                    LightCurtainInputPoint = table.Column<string>(type: "TEXT", nullable: false),
                    SignalPolarity = table.Column<string>(type: "TEXT", nullable: false),
                    PulseResetMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotIoBindings", x => x.SlotIoBindingId);
                });

            migrationBuilder.CreateTable(
                name: "SlotModelSlots",
                columns: table => new
                {
                    SlotModelVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalSlotNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SlotPosition = table.Column<string>(type: "TEXT", nullable: false),
                    SlotTemplateId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotModelSlots", x => new { x.SlotModelVersionId, x.PhysicalSlotNumber });
                });

            migrationBuilder.CreateTable(
                name: "SlotModelVersions",
                columns: table => new
                {
                    SlotModelVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelKey = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SlotCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotModelVersions", x => x.SlotModelVersionId);
                });

            migrationBuilder.CreateTable(
                name: "SlotTemplates",
                columns: table => new
                {
                    SlotTemplateId = table.Column<string>(type: "TEXT", nullable: false),
                    TemplateKey = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    LengthMm = table.Column<int>(type: "INTEGER", nullable: false),
                    WidthMm = table.Column<int>(type: "INTEGER", nullable: false),
                    HeightMm = table.Column<int>(type: "INTEGER", nullable: false),
                    CompatibleBasketTypesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotTemplates", x => x.SlotTemplateId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSlotConfigurations_Fingerprint",
                table: "ActiveSlotConfigurations",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_AdministratorAuditRecords_ObjectKind_ObjectId",
                table: "AdministratorAuditRecords",
                columns: new[] { "ObjectKind", "ObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdministratorAuditRecords_RecordedAtUtcTicks",
                table: "AdministratorAuditRecords",
                column: "RecordedAtUtcTicks");

            migrationBuilder.CreateIndex(
                name: "IX_AgvRestorationAttempts_AgvId_StartedAt",
                table: "AgvRestorationAttempts",
                columns: new[] { "AgvId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessAuditRecords_ObjectKind_ObjectId",
                table: "BusinessAuditRecords",
                columns: new[] { "ObjectKind", "ObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessAuditRecords_RecordedAtUtcTicks",
                table: "BusinessAuditRecords",
                column: "RecordedAtUtcTicks");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationConsumerBindings_FrozenAt",
                table: "ConfigurationConsumerBindings",
                column: "FrozenAt");

            migrationBuilder.CreateIndex(
                name: "IX_GovernedConfigurationSnapshots_ContentSha256",
                table: "GovernedConfigurationSnapshots",
                column: "ContentSha256");

            migrationBuilder.CreateIndex(
                name: "IX_GovernedConfigurationSnapshots_ObjectKind_ObjectId_Version",
                table: "GovernedConfigurationSnapshots",
                columns: new[] { "ObjectKind", "ObjectId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlotConfigurationActivations_AgvId_IssuedAt",
                table: "SlotConfigurationActivations",
                columns: new[] { "AgvId", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlotConfigurationActivations_CommandMessageId",
                table: "SlotConfigurationActivations",
                column: "CommandMessageId",
                unique: true,
                filter: "CommandMessageId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SlotConfigurationVerifications_AgvId_SlotModelVersionId_PhysicalSlotNumber",
                table: "SlotConfigurationVerifications",
                columns: new[] { "AgvId", "SlotModelVersionId", "PhysicalSlotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlotIoBindings_AgvId_SlotModelVersionId_PhysicalSlotNumber_Version",
                table: "SlotIoBindings",
                columns: new[] { "AgvId", "SlotModelVersionId", "PhysicalSlotNumber", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlotModelSlots_SlotTemplateId",
                table: "SlotModelSlots",
                column: "SlotTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_SlotModelVersions_ModelKey_Version",
                table: "SlotModelVersions",
                columns: new[] { "ModelKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlotTemplates_TemplateKey_Version",
                table: "SlotTemplates",
                columns: new[] { "TemplateKey", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveSlotConfigurations");

            migrationBuilder.DropTable(
                name: "AdministratorAuditRecords");

            migrationBuilder.DropTable(
                name: "AgvArchives");

            migrationBuilder.DropTable(
                name: "AgvLifecycles");

            migrationBuilder.DropTable(
                name: "AgvRestorationAttempts");

            migrationBuilder.DropTable(
                name: "BusinessAuditRecords");

            migrationBuilder.DropTable(
                name: "ConfigurationConsumerBindings");

            migrationBuilder.DropTable(
                name: "GovernedConfigurationSnapshots");

            migrationBuilder.DropTable(
                name: "OnboardAlarmSnapshots");

            migrationBuilder.DropTable(
                name: "SlotConfigurationActivations");

            migrationBuilder.DropTable(
                name: "SlotConfigurationReadiness");

            migrationBuilder.DropTable(
                name: "SlotConfigurationVerifications");

            migrationBuilder.DropTable(
                name: "SlotIoBindings");

            migrationBuilder.DropTable(
                name: "SlotModelSlots");

            migrationBuilder.DropTable(
                name: "SlotModelVersions");

            migrationBuilder.DropTable(
                name: "SlotTemplates");
        }
    }
}
#pragma warning restore CA1861
