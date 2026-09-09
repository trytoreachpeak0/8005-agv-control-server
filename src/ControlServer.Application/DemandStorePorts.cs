using ControlServer.Domain;

namespace ControlServer.Application;

public interface IDemandAcceptanceStore
{
    Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        CancellationToken cancellationToken);
}

public interface IJourneyAcceptanceStore : IDemandAcceptanceStore
{
    Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        JourneyExecutionPlan journey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes a demand onto a journey that is already under way, at a stop the journey already has or
    /// at one appended for it. There is no order intent: the leg that reaches a stop is authorized
    /// when the vehicle leaves the previous one, after a pre-departure safety check, and no lease is
    /// taken either -- the journey already holds the vehicle.
    /// </summary>
    Task JoinJourneyAsync(
        AcceptedDemandSnapshot snapshot,
        string journeyId,
        JourneyStopPlan? appendedStop,
        JourneyDemandPlan demand,
        CancellationToken cancellationToken);
}
