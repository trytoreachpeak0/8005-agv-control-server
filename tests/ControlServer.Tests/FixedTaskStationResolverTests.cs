using System.Reflection;
using System.Runtime.CompilerServices;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// The fixed-station seam batch 6 builds on (control-server#158): a resolver read once per round, a
/// view answering per task type with refusals as reason codes, and a route evidence id whose two ends
/// are named rather than positional.
/// </summary>
/// <remarks>
/// control-server#160 replaces the resolver's implementation and control-server#163 the reverse route;
/// these tests are what both may rely on without reopening the criterion.
/// </remarks>
public sealed class FixedTaskStationResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);

    private static readonly RiotMapStation Gate = new(210, "关卡");

    private static readonly RiotMapStationCatalogSnapshot Map = new(
        25, Now, new string('c', 64), [new RiotMapStation(12, "N1-1"), Gate]);

    private static readonly JourneyRuntimeOptions Configured = new()
    {
        GateStationId = "关卡",
        GateStationRiotId = 210,
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
    };

    [Fact]
    public async Task TheConfiguredResolverResolvesWireToGateToTheGateStationAtTheDestinationEnd()
    {
        IFixedTaskStationView view = await new ConfiguredGateStationResolver(
                new MapStationResolver(), Options.Create(Configured))
            .ReadForRoundAsync(Map, TestContext.Current.CancellationToken);

        FixedTaskStationResolution resolution = view.Resolve("WIRE_TO_GATE");

        Assert.Equal("WIRE_TO_GATE", resolution.TaskType);
        Assert.Equal(FixedStationEnd.Destination, resolution.FixedEnd);
        Assert.Equal(Gate, resolution.Station);
        Assert.Null(resolution.RefusalReasonCode);
        Assert.Null(resolution.RuleVersion);
        Assert.Null(resolution.BindingSetVersion);
    }

    /// <summary>
    /// A Map without the configured gate is still a whole-Map failure, under the reason the engine has
    /// always recorded for it -- not a per-task-type refusal.
    /// </summary>
    [Fact]
    public async Task TheConfiguredResolverFailsTheWholeMapWhenTheGateStationIsMissing()
    {
        RiotMapStationCatalogSnapshot withoutGate = Map with { Stations = [new RiotMapStation(12, "N1-1")] };

        StationResolutionException error = await Assert.ThrowsAsync<StationResolutionException>(() =>
            new ConfiguredGateStationResolver(new MapStationResolver(), Options.Create(Configured))
                .ReadForRoundAsync(withoutGate, TestContext.Current.CancellationToken));

        Assert.Equal("FIXED_STATION_BINDING_INVALID", error.ReasonCode);
    }

    /// <summary>
    /// REQ-0335: a task type the round cannot resolve is refused under the resolver's own reason, and
    /// a candidate of another task type in the same round is judged as if nothing had happened.
    /// </summary>
    [Fact]
    public async Task ARefusedTaskTypeIsRefusedUnderItsOwnReasonAndLeavesTheOtherTaskTypesOfTheRoundAlone()
    {
        DispatchRoundFacts round = Round(new ScriptedView(taskType => taskType == "STAGING_TO_WIRE"
            ? FixedTaskStationResolution.Refused(taskType, FixedStationEnd.Origin, "FIXED_STATION_BINDING_MISSING")
            : FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Destination, Gate)));
        DispatchCandidateEvaluation refused = Evaluation(round, "STAGING_TO_WIRE");
        DispatchCandidateEvaluation admitted = Evaluation(round, "WIRE_TO_GATE");

        Assert.Equal("FIXED_STATION_BINDING_MISSING", await EvaluateAsync(refused));
        Assert.Null(refused.Route);
        Assert.Equal("FIXED_STATION_BINDING_MISSING", refused.FixedStation?.RefusalReasonCode);

        Assert.Equal(DispatchAdmissionChain.Eligible, await EvaluateAsync(admitted));
        Assert.Equal("N1-1", admitted.Route?.PickupStationId);
        Assert.Equal(Gate, admitted.FixedStation?.Station);
    }

    /// <summary>
    /// A fixed station at the origin end is a reverse journey, which control-server#163 builds. Until
    /// then the plan builder refuses the route rather than hashing it as if the fixed station were the
    /// destination.
    /// </summary>
    [Fact]
    public async Task AFixedStationAtTheOriginEndIsRefusedUntilReverseJourneysExist()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            Round(new ScriptedView(taskType =>
                FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Origin, Gate))),
            "WIRE_TO_GATE");

        Assert.Equal("FIXED_STATION_AS_ORIGIN_NOT_SUPPORTED", await EvaluateAsync(evaluation));
        Assert.Null(evaluation.Route);
    }

    /// <summary>
    /// Two station parameters of one type can be swapped at a call site without the compiler saying a
    /// word, and the swapped id then fails to match on idempotent replay (scope specification 5.3). The
    /// ends travel in one object whose members are required and init-only, so a call site has to name
    /// them.
    /// </summary>
    [Fact]
    public void TheRouteEvidenceIdTakesItsEndpointsByNameNotByPosition()
    {
        MethodInfo method = typeof(MapStationResolver).GetMethod(nameof(MapStationResolver.BuildRouteEvidenceId))!;
        ParameterInfo[] parameters = method.GetParameters();

        Assert.True(
            parameters.Count(parameter => parameter.ParameterType == typeof(RiotMapStation)) < 2,
            "BuildRouteEvidenceId takes two RiotMapStation parameters, which a call site can swap silently.");

        Type endpoints = parameters.Single(parameter => parameter.ParameterType.Name == "RouteEndpoints").ParameterType;
        foreach (string end in (string[])["Origin", "Destination"])
        {
            PropertyInfo property = endpoints.GetProperty(end)!;
            Assert.Equal(typeof(RiotMapStation), property.PropertyType);
            Assert.NotNull(property.GetCustomAttribute<RequiredMemberAttribute>());
            Assert.Contains(
                typeof(IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }
        Assert.DoesNotContain(
            endpoints.GetConstructors(),
            constructor => constructor.GetParameters().Length > 0);
    }

    private static Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation) =>
        new StationResolutionCriterion(new MapStationResolver(), Options.Create(Configured))
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

    private static DispatchRoundFacts Round(IFixedTaskStationView view) => new(
        new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, []),
        Map,
        view,
        new HashSet<string>(StringComparer.Ordinal),
        Now,
        new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"));

    private static DispatchCandidateEvaluation Evaluation(DispatchRoundFacts round, string workType)
    {
        string demandId = $"DEMAND-{workType}";
        AcceptedDemandSnapshot candidate = new(
            demandId,
            $"SUBLOT-001|{workType}",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            $"SERIES-{demandId}",
            workType,
            "SUBLOT-001",
            1,
            Now.AddMinutes(-10),
            Now.AddMinutes(-9),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now.AddMinutes(-10), "PDFN5×6-8L(12R)"));
        return new DispatchCandidateEvaluation(
            candidate,
            round,
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now))
        {
            AreaAssignment = new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"),
        };
    }

    private sealed class ScriptedView(Func<string, FixedTaskStationResolution> resolve) : IFixedTaskStationView
    {
        public FixedTaskStationResolution Resolve(string taskType) => resolve(taskType);
    }
}
