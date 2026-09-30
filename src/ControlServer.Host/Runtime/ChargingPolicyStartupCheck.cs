using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Refuses to start when a charging policy version in effect breaks REQ-0281's threshold relation, or leaves the waiting
/// journey watch's rescue line at or above its mandatory charge entry threshold (batch 9-05, control-server#403).
/// </summary>
/// <remarks>
/// <para>
/// <b>The relation has one definition</b>, <see cref="ChargingPolicyRules.ThresholdRelationViolations"/>, which the FieldOps
/// import also calls (control-server#400). This check does not restate it; it only decides which versions to hand it. The
/// import refuses a bad version, so what this catches is a version that reached the database some other way, or a rule
/// that tightened after the version was written.
/// </para>
/// <para>
/// <b>Which versions are "in effect".</b> The latest activation, when that version has an approval (the resolver reads
/// the same pair), and every version frozen on a journey not yet completed or on a charging cycle not yet ended: those
/// keep being read back until they finish (REQ-0282), so a bad one there is as live as the active one.
/// </para>
/// <para>
/// <b>No version at all starts normally.</b> That is every vehicle not commissioned (control-server#400, specification
/// 8.6), judged vehicle by vehicle, not a reason to stop the server.
/// </para>
/// <para>
/// <b>The rescue line.</b> Until batch 9-05 the options validator required
/// <see cref="JourneyRuntimeOptions.WaitingJourneyRescueBatteryPercent"/> below <c>MinimumBatteryPercent</c>. That option is
/// gone, so the same requirement is now judged here, against the <c>MandatoryChargeEntryThreshold</c> of every version in
/// effect: the rescue line has to stay the more urgent of the watch's two lines. A version activated while the server runs
/// is not re-checked -- like the waiting point check, the next start does.
/// </para>
/// </remarks>
public static class ChargingPolicyStartupCheck
{
    public const string ReasonCode = ChargingPolicyReasonCodes.ThresholdRelationViolated;

    public const string RescueLineReasonCode = "WAITING_JOURNEY_RESCUE_LINE_NOT_BELOW_MANDATORY_CHARGE_ENTRY";

    private static readonly Action<ILogger, string, Exception?> Refused =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(9403, "ChargingPolicyStartupRefused"),
            "Charging policy refused at startup: {Detail}");

    /// <summary>
    /// The refusal for these versions, or null when every one keeps the relation and sits above the rescue line.
    /// </summary>
    /// <param name="inEffect">Each version with why it counts (for example "active" or "frozen on journey J").</param>
    public static string? Judge(
        IReadOnlyList<(ChargingPolicyVersion Version, string Why)> inEffect,
        int rescueBatteryPercent)
    {
        ArgumentNullException.ThrowIfNull(inEffect);
        List<string> problems = [];
        foreach ((ChargingPolicyVersion version, string why) in inEffect.OrderBy(item => item.Version.Version))
        {
            ChargingPolicyContent content = version.Content;
            string values = string.Create(
                CultureInfo.InvariantCulture,
                $"charging policy version {version.Version} ({why}): ChargingCompletionThreshold {content.ChargingCompletionThresholdPercent}, " +
                $"MandatoryChargeEntryThreshold {content.MandatoryChargeEntryThresholdPercent}, " +
                $"minimum post-task battery margin {content.MinimumPostTaskBatteryMarginPercent}");
            foreach (ChargingPolicyViolation violation in ChargingPolicyRules.ThresholdRelationViolations(
                         content.ChargingCompletionThresholdPercent,
                         content.MandatoryChargeEntryThresholdPercent,
                         content.MinimumPostTaskBatteryMarginPercent))
            {
                problems.Add($"{ReasonCode}: {values}: {violation.Detail}");
            }
            if (rescueBatteryPercent >= content.MandatoryChargeEntryThresholdPercent)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{RescueLineReasonCode}: {values}: JourneyRuntime:WaitingJourneyRescueBatteryPercent {rescueBatteryPercent} must be " +
                    $"below MandatoryChargeEntryThreshold {content.MandatoryChargeEntryThresholdPercent}."));
            }
        }

        return problems.Count == 0
            ? null
            : string.Join(" ", problems)
              + " Import, approve and activate a corrected charging policy with ControlServer.FieldOps (with the server stopped), "
              + "or lower JourneyRuntime:waitingJourneyRescueBatteryPercent, then start again. A version frozen on a journey or "
              + "charging cycle stays in effect until that journey or cycle ends.";
    }

    /// <summary>Reads the versions in effect and refuses (throws) when <see cref="Judge"/> finds a problem.</summary>
    public static async Task EnsureAsync(
        ControlServerDbContext dbContext,
        IChargingPolicyStore store,
        IChargingPolicyGovernanceFacts facts,
        JourneyRuntimeOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        Dictionary<long, string> why = [];
        if (await facts.ReadLatestActivationAsync(cancellationToken).ConfigureAwait(false) is { } activation &&
            (await store.ListApprovalsAsync(activation.Version, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            why[activation.Version] = "active";
        }

        var frozenOnJourneys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage != JourneyRuntimeStage.Completed && row.ChargingPolicyVersion != null)
            .Select(row => new { row.JourneyId, Version = row.ChargingPolicyVersion!.Value })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var frozen in frozenOnJourneys.OrderBy(item => item.JourneyId, StringComparer.Ordinal))
        {
            why.TryAdd(frozen.Version, $"frozen on journey {frozen.JourneyId}");
        }
        foreach (ChargingCycle cycle in await facts.ListOpenCyclesAsync(cancellationToken).ConfigureAwait(false))
        {
            why.TryAdd(cycle.ChargingPolicyVersion, $"frozen on charging cycle {cycle.CycleId}");
        }

        List<(ChargingPolicyVersion, string)> inEffect = [];
        foreach ((long number, string reason) in why)
        {
            // A version number recorded on a row is always a written one; a missing one is a database edit, and refusing is
            // what fail-closed means here.
            ChargingPolicyVersion version = await store.ReadVersionAsync(number, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture, $"{ReasonCode}: charging policy version {number} ({reason}) does not exist."));
            inEffect.Add((version, reason));
        }

        string? detail = Judge(inEffect, options.WaitingJourneyRescueBatteryPercent);
        if (detail is null)
        {
            return;
        }
        Refused(logger, detail, null);
        throw new InvalidOperationException(detail);
    }

    /// <summary>The host's entry point: resolves the stores from a fresh scope.</summary>
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        ControlServerDbContext dbContext = provider.GetRequiredService<ControlServerDbContext>();
        await EnsureAsync(
                dbContext,
                provider.GetRequiredService<IChargingPolicyStore>(),
                // The same read-only facts the FieldOps policy verbs use (control-server#400); the host registers no port for it.
                new ChargingGovernanceFacts(dbContext, provider.GetRequiredService<ITaskTypeStationBindingStore>()),
                provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value,
                provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ChargingPolicyStartupCheck).FullName!)
                    ?? NullLogger.Instance,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
