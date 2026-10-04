using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 等待点到点人工收尾（control-server#447）的用例共用的组装：按宿主的组装、用车队夹具的 RIoT、车载端与 R-11 名单（名单里只有 <c>fleet-r11</c>）。
/// </summary>
internal static class WaitingPointArrivalSettlementTestKit
{
    /// <summary>名单里的那个 R-11。</summary>
    public const string Operator = "fleet-r11";

    public static WaitingPointArrivalSettlement Settlement(FleetFixture fleet) =>
        new(
            fleet.Context,
            fleet.Riot,
            fleet.Riot,
            new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = fleet.ClearanceRosterPath })),
            new GovernanceStore(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            new OnboardJourneyPublisher(new WireToGateStore(fleet.Context), fleet.Peer, fleet.Clock),
            Options.Create(fleet.Options),
            fleet.Clock,
            NullLogger<WaitingPointArrivalSettlement>.Instance);

    public static WaitingPointArrivalSettlementRequest SettlementRequest(
        string agvId, string vehicleKey, string journeyId, int stationId, string verdict) =>
        new(agvId, vehicleKey, journeyId, stationId, verdict, Operator, "车停在等待点上，RIoT 报的地图不对", "SITE-447-01");

    public static async Task<WaitingPointArrivalSettlementResult> SettleAsync(
        FleetFixture fleet, WaitingPointArrivalSettlementRequest request)
    {
        fleet.Context.ChangeTracker.Clear();
        WaitingPointArrivalSettlementResult result =
            await Settlement(fleet).DecideAsync(request, TestContext.Current.CancellationToken);
        fleet.Context.ChangeTracker.Clear();
        return result;
    }

    /// <summary>这个入口写下的管理员审计，按写入先后。</summary>
    public static async Task<AdministratorAuditRecordRow[]> AuditsAsync(FleetFixture fleet)
    {
        AdministratorAuditRecordRow[] rows = await fleet.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == WaitingPointArrivalSettlement.AuditAction)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        return [.. rows.OrderBy(row => row.RecordedAtUtcTicks)];
    }
}
