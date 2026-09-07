using System.Text.Json;
using ControlServer.Application;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Derives how many baskets the sublot needs and checks the vehicle has that many free slots.
/// </summary>
/// <remarks>
/// <para>
/// A box-count read that fails over the network blocks this candidate rather than propagating:
/// the failure is logged and the round moves on to the next candidate. That distinction matters —
/// one unreachable sublot must not fail the whole round closed, but it must not be admitted with
/// an assumed count either.
/// </para>
/// <para>
/// The slots are chosen here, not later, because the count and the slots have to come from the
/// same onboard observation. Choosing them against a newer observation could pick slots that the
/// count was never checked against.
/// </para>
/// </remarks>
public sealed class SlotCapacityCriterion(
    ISublotBoxCountReader boxCountReader,
    ILogger<SlotCapacityCriterion> logger) : IDispatchAdmissionCriterion
{
    private static readonly Action<ILogger, string, Exception?> LogBoxCountFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogBoxCountFailed)),
            "Sublot box count unavailable for demand {DemandId}.");

    /// <summary>Baskets per journey, as the onboard rack is built. Outside this, the plan is wrong.</summary>
    private const int MinimumBasketCount = 1;
    private const int MaximumBasketCount = 8;

    public int Order => 100;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        OnboardDispatchFacts onboard = evaluation.Vehicle.Onboard
            ?? throw new InvalidOperationException(
                "Onboard facts are established by VehicleDynamicFactsCriterion before this runs.");

        int? maxBoxCount;
        try
        {
            maxBoxCount = await boxCountReader
                .ReadMaxBoxCountAsync(evaluation.Candidate.Sublot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogBoxCountFailed(logger, evaluation.Candidate.DemandId, error);
            maxBoxCount = null;
        }

        if (maxBoxCount is null or <= 0)
        {
            return "SUBLOT_BOX_COUNT_UNAVAILABLE";
        }

        int capacity = evaluation.PackageCapacity
            ?? throw new InvalidOperationException("An eligible PACKAGE must have a frozen capacity.");
        int expectedBasketCount = checked((maxBoxCount.Value + capacity - 1) / capacity);
        evaluation.ExpectedBasketCount = expectedBasketCount;

        if (expectedBasketCount is < MinimumBasketCount or > MaximumBasketCount)
        {
            return "EXPECTED_BASKET_COUNT_OUT_OF_RANGE";
        }

        if (onboard.AvailableSlots.Length < expectedBasketCount)
        {
            return "SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE";
        }

        evaluation.TargetSlots = onboard.AvailableSlots.Take(expectedBasketCount).ToArray();
        return DispatchAdmissionChain.Eligible;
    }
}
