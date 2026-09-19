using System.Globalization;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Pins what a WIRE_TO_GATE acceptance produces, byte for byte, so that moving the plan and its legs
/// into a plan builder and the fixed station behind a resolver (control-server#158) is provably a
/// restructuring and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Characterization, not specification: every literal below was computed by the code as it stood
/// before that restructuring (<c>fp/v2-impl@2ec7a2eb</c>) and is correct only in the sense that it has
/// not moved. A later ticket that changes a plan on purpose updates the literal in the same commit and
/// says why.
/// </para>
/// <para>
/// Both tests are the ones that turn red when the route's origin and destination are swapped: the
/// route evidence id hashes them in order, and the plan carries them as its two ends.
/// </para>
/// </remarks>
public sealed class JourneyPlanCharacterizationTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    /// <summary>
    /// The route evidence id a WIRE_TO_GATE candidate resolves to: AREA pickup station first, fixed
    /// station second. The id is what the journey freezes and what an idempotent replay compares.
    /// </summary>
    [Fact]
    public async Task TheRouteEvidenceIdOfAWireToGateCandidateIsPinned()
    {
        RiotMapStationCatalogSnapshot map = new(
            25,
            Now,
            "5f0c3e4d2a1b09876543210fedcba9876543210fedcba9876543210fedcba98",
            [new RiotMapStation(12, "N1-1"), new RiotMapStation(210, "关卡")]);
        JourneyRuntimeOptions options = new()
        {
            GateStationId = "关卡",
            GateStationRiotId = 210,
            AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
        };
        AcceptedDemandSnapshot candidate = new(
            DemandId,
            "SUBLOT-001|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            $"SERIES-{DemandId}",
            "WIRE_TO_GATE",
            "SUBLOT-001",
            1,
            Now.AddMinutes(-10),
            Now.AddMinutes(-9),
            $"TRACE-{DemandId}",
            $"COMMIT-{DemandId}",
            new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now.AddMinutes(-10), "PDFN5×6-8L(12R)"));
        DispatchCandidateEvaluation evaluation = new(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate]),
                map,
                new ConfiguredGateStationView(new RiotMapStation(210, "关卡")),
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                new VehicleDispatchPolicy(
                    [], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY")),
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now))
        {
            AreaAssignment = new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"),
        };

        string reason = await new StationResolutionCriterion(new MapStationResolver(), Options.Create(options))
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Equal(
            "MAPCAT-0aa0f18581b7398e15375d080e9ed6af20005814663877de20c1a28e6bfa9739",
            evaluation.Route!.RouteEvidenceId);
    }

    /// <summary>
    /// One acceptance carried to the gate: the journey row's stations, legs and upper ids, the two
    /// frozen endpoints, both move orders, and the three plan snapshots as sent.
    /// </summary>
    [Fact]
    public async Task TheJourneyPlanOfOneAcceptanceIsPinned()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.RunToGateUnloadAsync();

        Assert.Equal(
            """
            runtime.AgvId=老厂前线新多仓位1
            runtime.VehicleKey=BROKERX-0c20ff0600d644869a6a80c186065d85
            runtime.AgvLifecycleGeneration=1
            runtime.MapId=25
            runtime.MapIdentity=MAP-25
            runtime.DispatchZone=MAP-25-WIRE_TO_GATE
            runtime.RouteEvidenceId=MAPCAT-4e2fcaac8d9f6ca75363de7df61e2823ddd365be5c5498cff1a4d578b6ea40ab
            runtime.PickupStationId=N1-1
            runtime.PickupStationRiotId=12
            runtime.GateStationId=关卡
            runtime.GateStationRiotId=210
            runtime.ExpectedBasketCount=1
            runtime.TargetSlotsJson=[1]
            runtime.OperationSessionId=7e7ee0e2-5b9f-0e52-a69a-df22d71c836a
            runtime.PickupMovementLegId=f13413b8-c674-ce57-a847-b4f653a98921
            runtime.PickupUpperId=W2G-10000000-0000-4000-8000-000000000001-PICKUP-1
            runtime.GateMovementLegId=6418afc8-35b0-9b5b-95e5-5cea49ca7333
            runtime.GateUpperId=W2G-10000000-0000-4000-8000-000000000001-GATE-1
            runtime.DispatchGeneration=1
            runtime.PlanRevision=1
            runtime.PlanMessageId=272ec99b-8836-0654-b7a4-8aee0fe78710
            runtime.GatePlanMessageId=b3a68bab-29c8-785a-b4a0-1fdda0a8c242
            frozen.Pickup=10000000-0000-4000-8000-000000000001|SUBLOT-001|WIRE_TO_GATE|25|12|N1-1|5534023222112865484|2026-08-26T01:00:00.0000000+00:00
            frozen.Dropoff=10000000-0000-4000-8000-000000000001|SUBLOT-001|WIRE_TO_GATE|25|210|关卡|5534023222112865484|2026-08-26T01:00:00.0000000+00:00
            intent.TO_GATE=6418afc8-35b0-9b5b-95e5-5cea49ca7333|10000000-0000-4000-8000-000000000001|W2G-10000000-0000-4000-8000-000000000001-GATE-1|关卡|BROKERX-0c20ff0600d644869a6a80c186065d85|25|210|1|1|2026-08-26T01:00:00.0000000+00:00
            intent.TO_PICKUP=f13413b8-c674-ce57-a847-b4f653a98921|10000000-0000-4000-8000-000000000001|W2G-10000000-0000-4000-8000-000000000001-PICKUP-1|N1-1|BROKERX-0c20ff0600d644869a6a80c186065d85|25|12|1|1|2026-08-26T01:00:00.0000000+00:00
            plan.2694bd09-7092-ef5c-886a-6cdba914299d={"planRevision":1,"legs":[{"movementLegId":"f13413b8-c674-ce57-a847-b4f653a98921","legType":"TO_PICKUP","stopPurposeCategory":"BUSINESS","demandId":"10000000-0000-4000-8000-000000000001","publicStationFunction":null,"sequence":1,"stationId":"N1-1","mapId":"MAP-25","state":"ACTIVE"},{"movementLegId":"6418afc8-35b0-9b5b-95e5-5cea49ca7333","legType":"TO_DROPOFF","stopPurposeCategory":"BUSINESS","demandId":"10000000-0000-4000-8000-000000000001","publicStationFunction":null,"sequence":2,"stationId":"\u5173\u5361","mapId":"MAP-25","state":"PLANNED"}]}
            plan.272ec99b-8836-0654-b7a4-8aee0fe78710={"planRevision":2,"legs":[{"movementLegId":"f13413b8-c674-ce57-a847-b4f653a98921","legType":"TO_PICKUP","stopPurposeCategory":"BUSINESS","demandId":"10000000-0000-4000-8000-000000000001","publicStationFunction":null,"sequence":1,"stationId":"N1-1","mapId":"MAP-25","state":"ARRIVED"},{"movementLegId":"6418afc8-35b0-9b5b-95e5-5cea49ca7333","legType":"TO_DROPOFF","stopPurposeCategory":"BUSINESS","demandId":"10000000-0000-4000-8000-000000000001","publicStationFunction":null,"sequence":2,"stationId":"\u5173\u5361","mapId":"MAP-25","state":"PLANNED"}]}
            plan.b3a68bab-29c8-785a-b4a0-1fdda0a8c242={"planRevision":3,"legs":[{"movementLegId":"f13413b8-c674-ce57-a847-b4f653a98921","legType":"TO_PICKUP","stopPurposeCategory":"BUSINESS","demandId":"10000000-0000-4000-8000-000000000001","publicStationFunction":null,"sequence":1,"stationId":"N1-1","mapId":"MAP-25","state":"COMPLETED"},{"movementLegId":"6418afc8-35b0-9b5b-95e5-5cea49ca7333","legType":"TO_DROPOFF","stopPurposeCategory":"BUSINESS","demandId":"10000000-0000-4000-8000-000000000001","publicStationFunction":null,"sequence":2,"stationId":"\u5173\u5361","mapId":"MAP-25","state":"ARRIVED"}]}

            """,
            await DescribePlanAsync(fixture.Context));
    }

    /// <summary>Everything the plan decides, one fact per line, in a fixed order.</summary>
    private static async Task<string> DescribePlanAsync(ControlServerDbContext context)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        StringBuilder text = new();

        JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(cancellationToken);
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.AgvId={runtime.AgvId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.VehicleKey={runtime.VehicleKey}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.AgvLifecycleGeneration={runtime.AgvLifecycleGeneration}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.MapId={runtime.MapId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.MapIdentity={runtime.MapIdentity}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.DispatchZone={runtime.DispatchZone}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.RouteEvidenceId={runtime.RouteEvidenceId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.PickupStationId={runtime.PickupStationId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.PickupStationRiotId={runtime.PickupStationRiotId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.GateStationId={runtime.GateStationId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.GateStationRiotId={runtime.GateStationRiotId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.ExpectedBasketCount={runtime.ExpectedBasketCount}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.TargetSlotsJson={runtime.TargetSlotsJson}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.OperationSessionId={runtime.OperationSessionId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.PickupMovementLegId={runtime.PickupMovementLegId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.PickupUpperId={runtime.PickupUpperId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.GateMovementLegId={runtime.GateMovementLegId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.GateUpperId={runtime.GateUpperId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.DispatchGeneration={runtime.DispatchGeneration}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.PlanRevision={runtime.PlanRevision}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.PlanMessageId={runtime.PlanMessageId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"runtime.GatePlanMessageId={runtime.GatePlanMessageId}");

        foreach (FrozenDemandStationRow frozen in (await context.FrozenDemandStations.AsNoTracking()
                     .ToArrayAsync(cancellationToken)).OrderBy(row => row.Role))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"frozen.{frozen.Role}={frozen.DemandId}|{frozen.TransportDemandKey}|{frozen.MapId}|" +
                $"{frozen.StationId}|{frozen.StationName}|{frozen.CatalogRevision}|{frozen.FrozenAt:O}");
        }

        foreach (OrderIntentRow intent in (await context.OrderIntents.AsNoTracking()
                     .ToArrayAsync(cancellationToken)).OrderBy(row => row.Purpose, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"intent.{intent.Purpose}={intent.MovementLegId}|{intent.DemandId}|{intent.UpperId}|" +
                $"{intent.TargetStationId}|{intent.VehicleKey}|{intent.MapId}|{intent.DestinationStationId}|" +
                $"{intent.AgvLifecycleGeneration}|{intent.DispatchGeneration}|{intent.CreatedAt:O}");
        }

        foreach (ProtocolOutboxRow plan in (await context.ProtocolOutbox.AsNoTracking()
                     .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
                     .ToArrayAsync(cancellationToken)).OrderBy(row => row.MessageId, StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(plan.PayloadJson);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"plan.{plan.MessageId}={document.RootElement.GetProperty("payload").GetRawText()}");
        }

        return text.ToString().ReplaceLineEndings("\n");
    }
}
