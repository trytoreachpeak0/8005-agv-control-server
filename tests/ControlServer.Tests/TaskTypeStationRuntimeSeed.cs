using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// What a runtime test's database needs since control-server#160, where the fixed station comes from the rules and the
/// Map's active binding set rather than from configuration: the factory preset's six rules and map 25's
/// <c>WIRE_TO_GATE</c> bound to station 210 「关卡」, active -- what startup loads on a commissioned server.
/// </summary>
internal static class TaskTypeStationRuntimeSeed
{
    public const int GateStationRiotId = 210;

    public const string GateStationName = "关卡";

    public static readonly TaskTypeStationBinding GateBinding =
        new(TransportTaskTypes.WireToGate, GateStationRiotId, GateStationName, "MAP-25-WIRE_TO_GATE-20260827");

    /// <summary>Writes a rule version and a binding set version for the Map and makes the latter active.</summary>
    public static async Task<(long RuleVersion, long BindingSetVersion)> ActivateAsync(
        DbContextOptions<ControlServerDbContext> options,
        DateTimeOffset at,
        IReadOnlyList<string>? requiredTaskTypes = null,
        IReadOnlyList<TaskTypeStationBinding>? bindings = null,
        int mapId = 25)
    {
        await using ControlServerDbContext context = new(options);
        TaskTypeStationAccess access = Access(context);
        TaskTypeStationRuleVersion rules = (await access.Rules.WriteVersionAsync(
            TaskTypeStationTestData.SixRules, "preset:test", at, TestContext.Current.CancellationToken)).Version;
        TaskTypeStationBindingSetVersion set = (await access.Bindings.WriteVersionAsync(
            mapId,
            rules.Version,
            requiredTaskTypes ?? [TransportTaskTypes.WireToGate],
            bindings ?? [GateBinding],
            null,
            "preset:test",
            at,
            TestContext.Current.CancellationToken)).Version;
        await access.Bindings.SetActiveAsync(mapId, set.Version, at, TestContext.Current.CancellationToken);
        return (rules.Version, set.Version);
    }

    /// <summary>The catalog change convergence the engine runs after each confirmation (control-server#162).</summary>
    public static CatalogBindingHoldConvergence CatalogBindingHolds(ControlServerDbContext context, TimeProvider clock)
    {
        GovernanceDeploymentIdentity deployment = new("deployment:8005-controlserver@test");
        GovernanceStore governance = new(context, deployment, AuditRetentionPolicy.Default);
        return new CatalogBindingHoldConvergence(
            context,
            new TaskTypeStationBindingStore(context, new GovernedConfigurationPublisher(governance, governance)),
            new TaskTypeStationHoldStore(context),
            new TaskTypeStationCatalogChangeStore(context),
            governance,
            deployment,
            clock);
    }

    /// <summary>The batch 6 stores over one context, the way the host's scope builds them.</summary>
    public static TaskTypeStationAccess Access(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        GovernedConfigurationPublisher publisher = new(governance, governance);
        return new TaskTypeStationAccess(
            new TaskTypeStationRuleStore(context, publisher),
            new TaskTypeStationBindingStore(context, publisher),
            new TaskTypeStationHoldStore(context),
            new DemandTaskTypeStationFreezeStore(context));
    }
}

/// <summary>
/// A round's view in which every task type resolves to one station at the destination end, unversioned -- the
/// stand-in for criterion tests that are not about bindings (it used to be the configured gate resolver's own view,
/// which control-server#160 removed).
/// </summary>
internal sealed class SingleStationView(RiotMapStation station) : IFixedTaskStationView
{
    public FixedTaskStationResolution Resolve(string taskType) =>
        FixedTaskStationResolution.Resolved(
            taskType, FixedStationEnd.Destination, station);
}
