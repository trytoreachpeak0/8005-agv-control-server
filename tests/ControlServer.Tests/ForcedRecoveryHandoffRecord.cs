using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The two fields protocol 3.0.0 added to <c>ForcedMechanicalRecoveryResult</c> (CP-0008, control-server#385), as a vehicle
/// fills them for a given exception recovery session: the session's demand copied from the command, and -- for a session
/// on a demand -- the named hand-off of that demand's own sublot. A session without a demand gets two nulls.
/// </summary>
/// <remarks>
/// Read from the store rather than passed in, so a fixture whose demand and sublot are set up elsewhere builds the record
/// a real vehicle would send without repeating them. The receiver is deliberately not the verifying administrator: the
/// server must take the hand-off from the record, never infer it from the operator.
/// </remarks>
internal static class ForcedRecoveryHandoffRecord
{
    internal const string ReceiverName = "Line lead Wang";

    internal static async Task<(string? DemandId, object? CargoHandoff)> ForSessionAsync(
        DbContextOptions<ControlServerDbContext> options,
        string sessionId,
        DateTimeOffset handedOverAt)
    {
        await using ControlServerDbContext context = new(options);
        CancellationToken token = TestContext.Current.CancellationToken;
        string? demandId = await context.ExceptionRecoverySessions.AsNoTracking()
            .Where(row => row.ExceptionRecoverySessionId == sessionId)
            .Select(row => row.DemandId)
            .SingleAsync(token);
        if (demandId is null)
        {
            return (null, null);
        }
        string sublot = await context.AcceptedDemands.AsNoTracking()
            .Where(row => row.DemandId == demandId)
            .Select(row => row.Sublot)
            .SingleAsync(token);
        return (demandId, new { sublot, receiverName = ReceiverName, handedOverAt });
    }
}
