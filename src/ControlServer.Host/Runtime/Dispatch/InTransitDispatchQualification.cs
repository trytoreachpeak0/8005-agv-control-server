using ControlServer.Host.Runtime.Fleet;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Whether a vehicle already under way on a journey may be offered an appended demand this round -- the second of the
/// two paths a vehicle reaches dispatch by.
/// </summary>
/// <remarks>
/// <para>
/// An idle vehicle is judged by the admission chain. A vehicle under way cannot be: the chain requires RIoT to report
/// it <c>IDLE</c> and no order on it (<see cref="Criteria.VehicleDynamicFactsCriterion"/>), which a vehicle carrying a
/// journey never is. Its qualification is a different question -- is appending allowed where it is, and does the
/// addition fit its plan -- and so it is a different path rather than a relaxed copy of the idle one.
/// </para>
/// <para>
/// It is asked only in a round that reached dispatch, so a round with every vehicle under way still ends before the
/// catalog is read and before the orphan check.
/// </para>
/// </remarks>
public interface IInTransitDispatchQualification
{
    /// <summary>True when <paramref name="vehicle"/>, under way, may be offered an appended demand in this round.</summary>
    Task<bool> QualifiesAsync(DispatchRoundFacts round, FleetVehicle vehicle, CancellationToken cancellationToken);
}

/// <summary>
/// The in-transit path until appending is opened: no vehicle under way qualifies, and asking reads and writes nothing.
/// </summary>
/// <remarks>
/// Appending to a journey under way is constrained per area and forbidden where nothing is configured, and before the
/// parameters are approved an initial dispatch takes one demand only (scope specification 5.2), so the path refuses
/// every vehicle. control-server#211 replaces this with the real qualification. Refusing leaves no trace on purpose:
/// no backlog row, no verdict and nothing for the structural block, which judges only the vehicles the idle path
/// served.
/// </remarks>
public sealed class InTransitAppendNotOpened : IInTransitDispatchQualification
{
    public Task<bool> QualifiesAsync(DispatchRoundFacts round, FleetVehicle vehicle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(vehicle);
        _ = cancellationToken;
        return Task.FromResult(false);
    }
}
