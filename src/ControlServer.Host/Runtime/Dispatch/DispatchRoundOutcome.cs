namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>What the admission chain said about one candidate for one vehicle.</summary>
/// <param name="Evaluation">
/// The evaluation as the chain left it: the candidate, the round and vehicle facts it was judged against, and
/// whatever the criteria resolved before the chain stopped.
/// </param>
/// <param name="ReasonCode">
/// <see cref="DispatchAdmissionChain.Eligible"/>, or the first criterion's blocking reason.
/// </param>
/// <remarks>
/// The chain's verdict, not what became of the candidate afterwards: the final dynamic-facts check and intake
/// can still refuse an eligible candidate, and those refusals are about this instant, not about whether this
/// vehicle could ever take the demand.
/// </remarks>
public sealed record DispatchCandidateVerdict(DispatchCandidateEvaluation Evaluation, string ReasonCode);

/// <summary>Every verdict one vehicle's segment of the round reached, for a vehicle whose segment finished.</summary>
public sealed record DispatchVehicleOutcome(
    string AgvId,
    string VehicleKey,
    IReadOnlyList<DispatchCandidateVerdict> Verdicts);

/// <summary>One dispatch round, as seen once every vehicle it could serve has been served.</summary>
/// <param name="Round">The facts the round was decided against.</param>
/// <param name="CompletedVehicles">
/// The vehicles whose segment ran to its end, in the order they were served. A vehicle that ran out its
/// budget, or whose segment threw (control-server#231), is absent even when it had already judged
/// candidates: a segment cut off part-way has not finished deciding, and its verdicts are not a statement
/// about that vehicle. Both exclusions are one rule — a vehicle this round could not get an answer out of
/// has refused nothing, and reading its absence as a refusal is what would turn a momentarily unreadable
/// vehicle into a fleet-wide structural alarm.
/// </param>
public sealed record DispatchRoundOutcome(
    DispatchRoundFacts Round,
    IReadOnlyList<DispatchVehicleOutcome> CompletedVehicles);

/// <summary>
/// Receives each dispatch round's verdicts across all its vehicles, once the vehicle loop is over.
/// </summary>
/// <remarks>
/// <para>
/// Exists because some conclusions can only be drawn across vehicles. <c>JourneyBacklog</c> keeps one reason
/// per demand and every vehicle in the round overwrites it, so afterwards it holds the last vehicle's reason;
/// whether <i>no</i> vehicle could take a demand (REQ-0210) cannot be read from it.
/// </para>
/// <para>
/// Called once per round that reached the vehicle loop, including a round in which a vehicle ran out its
/// budget or threw. Not called when the round stopped before the loop, which today means the catalog could
/// not be read. Until control-server#231 a vehicle throwing also skipped it, and that is what made one
/// unreachable vehicle end the round for everyone behind it. Placed by control-server#69 with a default that
/// does nothing; the structural dispatch block (control-server#74) provides the implementation.
/// </para>
/// </remarks>
public interface IDispatchRoundOutcomeSink
{
    Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>The default round-end hook: nothing is concluded across vehicles yet.</summary>
public sealed class NoDispatchRoundOutcomeSink : IDispatchRoundOutcomeSink
{
    public Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
