using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// <c>CV-TASK-TYPE-ADMISSION-FAIL-CLOSED</c> 的服务端同名测试（<c>FP-IS-10</c> 服务端半边，control-server#160）：
/// <c>ADMIT_ONLY_BOUND_TASK_TYPES</c> 与 <c>FAIL_CLOSED_ON_MISSING_BINDING</c>。
/// </summary>
public sealed class TaskTypeAdmissionFailClosedVectorTests
{
    private const string BoundDemand = "10000000-0000-4000-8000-000000000001";

    private const string UnboundDemand = "10000000-0000-4000-8000-0000000000b0";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] AdmissionReasons =
    [
        DispatchReasonCodes.OutOfScopeWorkType,
        DispatchReasonCodes.TaskTypeBindingMissing,
        TaskTypeStationReasonCodes.BindingStationNotInCatalog,
        DispatchReasonCodes.TaskTypeHeld,
        DispatchReasonCodes.TaskTypeNotYetExecutable,
    ];

    /// <summary>
    /// 一轮里有已绑定的 <c>WIRE_TO_GATE</c> 需求与未绑定的 <c>STAGING_TO_WIRE</c> 需求：只有前者被受理、排出取货计划快照；
    /// 后者没有计划、没有 RIoT 订单，积压原因是缺绑定；之后发给车载端的 <c>VehicleBusinessStateSnapshot</c> 的
    /// <c>blockingFacts</c> 里没有任何准入原因——缺绑定的原因只在服务端与看板（规格 5.3）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [Trait("ProtocolVector", "CV-TASK-TYPE-ADMISSION-FAIL-CLOSED")]
    public async Task OnlyTheBoundTaskTypeIsAdmittedAndTheMissingBindingFailsClosedWithoutReachingTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AllowedWorkTypes = [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire];
        AcceptedDemandSnapshot bound = fixture.Demand(BoundDemand, "SUBLOT-001", Now.AddMinutes(-10));
        AcceptedDemandSnapshot unbound = fixture.Demand(UnboundDemand, "SUBLOT-002", Now.AddMinutes(-20), area: "N1-2") with
        {
            WorkType = TransportTaskTypes.StagingToWire,
            TransportDemandKey = $"SUBLOT-002|{TransportTaskTypes.StagingToWire}",
        };
        fixture.Catalog.Set(unbound, bound);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.BoxCounts.Set("SUBLOT-002", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(BoundDemand);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        // ADMIT_ONLY_BOUND_TASK_TYPES
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync(BoundDemand)).Stage);
        JsonElement[] sent = [.. fixture.Peer.Lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())];
        JsonElement[] plans = [.. sent.Where(root => root.GetProperty("messageType").GetString() == "UpcomingStopPlanSnapshot")];
        Assert.NotEmpty(plans);
        Assert.All(
            plans.SelectMany(plan => plan.GetProperty("payload").GetProperty("legs").EnumerateArray()),
            leg => Assert.Equal(BoundDemand, leg.GetProperty("demandId").GetString()));

        // FAIL_CLOSED_ON_MISSING_BINDING
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == UnboundDemand, Token);
        Assert.Equal(DispatchReasonCodes.TaskTypeBindingMissing, backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        Assert.False(await fixture.Context.AcceptedDemands.AnyAsync(row => row.DemandId == UnboundDemand, Token));
        Assert.False(await fixture.Context.JourneyRuntimes.AnyAsync(row => row.DemandId == UnboundDemand, Token));
        Assert.False(await fixture.Context.OrderIntents.AnyAsync(row => row.DemandId == UnboundDemand, Token));
        Assert.DoesNotContain(
            sent,
            root => root.GetRawText().Contains(UnboundDemand, StringComparison.Ordinal));

        JsonElement[] businessStates =
            [.. sent.Where(root => root.GetProperty("messageType").GetString() == "VehicleBusinessStateSnapshot")];
        Assert.NotEmpty(businessStates);
        Assert.All(businessStates, state =>
        {
            string blockingFacts = state.GetProperty("payload").GetProperty("blockingFacts").GetRawText();
            Assert.All(AdmissionReasons, reason => Assert.DoesNotContain(reason, blockingFacts, StringComparison.Ordinal));
        });
    }
}
