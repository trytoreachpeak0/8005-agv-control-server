using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Tells a save refused because a journey row was committed to since it was read (control-server#357) from every other
/// failure of a save.
/// </summary>
/// <remarks>
/// A catch written for its own conflict -- an order intent's tokens, a vehicle occupancy's unique index -- must let this one
/// through: while the runtime advances a vehicle every save it makes re-checks the journey row
/// (<see cref="ControlServerDbContext.GuardedJourneyId"/>), so this conflict can surface from inside any of them, and read as
/// theirs it would be answered as theirs ("another order occupies the vehicle"). The runtime is the one that answers it: the
/// vehicle yields for the round.
/// </remarks>
public static class JourneyRowConflict
{
    public static bool Is(Exception failure) =>
        failure is DbUpdateConcurrencyException conflict &&
        conflict.Entries.Any(entry => entry.Entity is JourneyRuntimeRow);
}
