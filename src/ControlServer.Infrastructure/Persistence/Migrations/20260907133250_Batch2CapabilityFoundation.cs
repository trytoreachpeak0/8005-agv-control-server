using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch2CapabilityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VehicleOccupancyClaimedAt",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VehicleOccupancyReleasedAt",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CreateGateAudit",
                columns: table => new
                {
                    GateAuditId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetStationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Verdict = table.Column<string>(type: "TEXT", nullable: false),
                    RiotRouteCostMm = table.Column<long>(type: "INTEGER", nullable: true),
                    GraphTraversalCostMm = table.Column<long>(type: "INTEGER", nullable: true),
                    GraphReachable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConflictDetail = table.Column<string>(type: "TEXT", nullable: true),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreateGateAudit", x => x.GateAuditId);
                });

            migrationBuilder.CreateTable(
                name: "DispatchZoneVehicles",
                columns: table => new
                {
                    Zone = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchZoneVehicles", x => new { x.Zone, x.AgvId });
                });

            migrationBuilder.CreateTable(
                name: "FaultedVehicleCargo",
                columns: table => new
                {
                    CargoBindingId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    FaultGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    MovementLegId = table.Column<string>(type: "TEXT", nullable: true),
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    LoadingWitnessed = table.Column<bool>(type: "INTEGER", nullable: false),
                    BoundAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleasedReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaultedVehicleCargo", x => x.CargoBindingId);
                });

            migrationBuilder.CreateTable(
                name: "FrozenDemandStations",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationName = table.Column<string>(type: "TEXT", nullable: false),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrozenDemandStations", x => new { x.DemandId, x.Role });
                });

            migrationBuilder.CreateTable(
                name: "MapStationCatalogStates",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    LastCompleteConfirmationAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastFailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ApprovedSyncPeriodSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    ApprovedMaxUnconfirmedSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapStationCatalogStates", x => x.MapId);
                });

            migrationBuilder.CreateTable(
                name: "RiotOrderCommandAudit",
                columns: table => new
                {
                    CommandAuditId = table.Column<string>(type: "TEXT", nullable: false),
                    CommandType = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetUpperId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestSemanticSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    ReconciledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReceiptJson = table.Column<string>(type: "TEXT", nullable: true),
                    FaultGeneration = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiotOrderCommandAudit", x => x.CommandAuditId);
                });

            migrationBuilder.CreateTable(
                name: "RouteGraphEdgeGroups",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupName = table.Column<string>(type: "TEXT", nullable: false),
                    EdgeId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupType = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteGraphEdgeGroups", x => new { x.MapId, x.GroupName, x.EdgeId });
                });

            migrationBuilder.CreateTable(
                name: "RouteGraphEdges",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    EdgeId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartNode = table.Column<int>(type: "INTEGER", nullable: false),
                    EndNode = table.Column<int>(type: "INTEGER", nullable: false),
                    CostMm = table.Column<double>(type: "REAL", nullable: false),
                    StartX = table.Column<int>(type: "INTEGER", nullable: false),
                    StartY = table.Column<int>(type: "INTEGER", nullable: false),
                    EndX = table.Column<int>(type: "INTEGER", nullable: false),
                    EndY = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBackEdge = table.Column<bool>(type: "INTEGER", nullable: false),
                    DesignRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteGraphEdges", x => new { x.MapId, x.EdgeId });
                });

            migrationBuilder.CreateTable(
                name: "RouteGraphRemovedEdges",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    EdgeId = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteGraphRemovedEdges", x => new { x.MapId, x.EdgeId });
                });

            migrationBuilder.CreateTable(
                name: "RouteGraphRemovedStations",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteGraphRemovedStations", x => new { x.MapId, x.StationId });
                });

            migrationBuilder.CreateTable(
                name: "RouteGraphSnapshots",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    DesignRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    DesignRefreshedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DesignSourceGmtUpdate = table.Column<string>(type: "TEXT", nullable: true),
                    DesignEdgeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DesignStationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RuntimeRefreshedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RuntimeRemovedEdgeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RuntimeRemovedStationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EdgeGroupFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    EdgeGroupRefreshedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DynamicRouteCostPresent = table.Column<bool>(type: "INTEGER", nullable: false),
                    DynamicRouteCostObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StaleReason = table.Column<string>(type: "TEXT", nullable: true),
                    StaleSince = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteGraphSnapshots", x => x.MapId);
                });

            migrationBuilder.CreateTable(
                name: "RouteGraphStations",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    EdgeId = table.Column<int>(type: "INTEGER", nullable: false),
                    PosX = table.Column<double>(type: "REAL", nullable: false),
                    PosY = table.Column<double>(type: "REAL", nullable: false),
                    ResolvedNode = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolutionResidualMm = table.Column<double>(type: "REAL", nullable: false),
                    DesignRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteGraphStations", x => new { x.MapId, x.StationId });
                });

            migrationBuilder.CreateTable(
                name: "VehicleDispatchBudgets",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    RoundTimeoutMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDispatchBudgets", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "VehicleFaultStates",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    FaultGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceCode = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceOnAutoConfirmWhitelist = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StopProven = table.Column<bool>(type: "INTEGER", nullable: false),
                    StopProvenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EscalatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClearedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClearedReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleFaultStates", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "VehicleTaskTypeAdmissions",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    Allowed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigurationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleTaskTypeAdmissions", x => new { x.AgvId, x.TaskType });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_VehicleKey",
                table: "OrderIntents",
                column: "VehicleKey",
                unique: true,
                filter: "VehicleOccupancyClaimedAt IS NOT NULL AND VehicleOccupancyReleasedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreateGateAudit_DemandId_EvaluatedAt",
                table: "CreateGateAudit",
                columns: new[] { "DemandId", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FaultedVehicleCargo_AgvId_FaultGeneration",
                table: "FaultedVehicleCargo",
                columns: new[] { "AgvId", "FaultGeneration" },
                unique: true,
                filter: "ReleasedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FaultedVehicleCargo_DemandId",
                table: "FaultedVehicleCargo",
                column: "DemandId");

            migrationBuilder.CreateIndex(
                name: "IX_FrozenDemandStations_TransportDemandKey",
                table: "FrozenDemandStations",
                column: "TransportDemandKey");

            migrationBuilder.CreateIndex(
                name: "IX_RiotOrderCommandAudit_AgvId_IssuedAt",
                table: "RiotOrderCommandAudit",
                columns: new[] { "AgvId", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RiotOrderCommandAudit_CommandType_TargetUpperId_AttemptNumber",
                table: "RiotOrderCommandAudit",
                columns: new[] { "CommandType", "TargetUpperId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteGraphEdges_MapId_EndNode",
                table: "RouteGraphEdges",
                columns: new[] { "MapId", "EndNode" });

            migrationBuilder.CreateIndex(
                name: "IX_RouteGraphEdges_MapId_StartNode",
                table: "RouteGraphEdges",
                columns: new[] { "MapId", "StartNode" });

            migrationBuilder.CreateIndex(
                name: "IX_RouteGraphStations_MapId_ResolvedNode",
                table: "RouteGraphStations",
                columns: new[] { "MapId", "ResolvedNode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreateGateAudit");

            migrationBuilder.DropTable(
                name: "DispatchZoneVehicles");

            migrationBuilder.DropTable(
                name: "FaultedVehicleCargo");

            migrationBuilder.DropTable(
                name: "FrozenDemandStations");

            migrationBuilder.DropTable(
                name: "MapStationCatalogStates");

            migrationBuilder.DropTable(
                name: "RiotOrderCommandAudit");

            migrationBuilder.DropTable(
                name: "RouteGraphEdgeGroups");

            migrationBuilder.DropTable(
                name: "RouteGraphEdges");

            migrationBuilder.DropTable(
                name: "RouteGraphRemovedEdges");

            migrationBuilder.DropTable(
                name: "RouteGraphRemovedStations");

            migrationBuilder.DropTable(
                name: "RouteGraphSnapshots");

            migrationBuilder.DropTable(
                name: "RouteGraphStations");

            migrationBuilder.DropTable(
                name: "VehicleDispatchBudgets");

            migrationBuilder.DropTable(
                name: "VehicleFaultStates");

            migrationBuilder.DropTable(
                name: "VehicleTaskTypeAdmissions");

            migrationBuilder.DropIndex(
                name: "IX_OrderIntents_VehicleKey",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "VehicleOccupancyClaimedAt",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "VehicleOccupancyReleasedAt",
                table: "OrderIntents");
        }
    }
}
