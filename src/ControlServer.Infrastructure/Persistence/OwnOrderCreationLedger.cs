using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// <see cref="IOwnOrderCreationLedger"/> over this server's <c>OrderIntents</c> (control-server#573).
/// </summary>
/// <remarks>
/// <para>
/// Every RIoT order this server creates goes through <c>WireToGateOrchestration.ReconcileOrCreateAsync</c>, which arms the
/// intent (<c>CreateDispatchArmedAt</c>) and records the request as started (<c>LastCreateOutcome = CreateRequestStarted</c>,
/// <c>LastCreateOutcomeAt</c>) before the request is sent, and stamps <c>LastCreateOutcomeAt</c> again with the answer. So a
/// row whose <c>CreatedAt</c>, <c>CreateDispatchArmedAt</c> or <c>LastCreateOutcomeAt</c> is at or after <c>since</c> may
/// stand for an order that reached RIoT after <c>since</c>, and so may a request still waiting for its answer however long
/// ago it started. A row with no time at all is taken as just created.
/// </para>
/// <para>
/// The times are compared here, not in SQL: SQLite holds a <see cref="DateTimeOffset"/> as text with its offset and would
/// compare it as text (the production database is SQLite). Every row of the vehicle is fetched -- no limit and no lower
/// bound in the query, so none can be missed; three timestamps per leg the vehicle has ever driven, read only when a
/// listing did not add up.
/// </para>
/// </remarks>
public sealed class OwnOrderCreationLedger(ControlServerDbContext dbContext, TimeProvider timeProvider) : IOwnOrderCreationLedger
{
    /// <summary>The create outcome written just before the request is sent and replaced by its answer.</summary>
    private const string CreateRequestStarted = "CreateRequestStarted";

    /// <summary>
    /// How long a started create without an answer still counts as in flight. Ten times the RIoT client's default 30-second
    /// timeout; a row older than that is what a crash in the middle of a create leaves behind, and counting it forever would
    /// shut the carry-over off for that vehicle for good.
    /// </summary>
    private static readonly TimeSpan InFlightCreateWindow = TimeSpan.FromMinutes(5);

    public async Task<bool> MayHaveCreatedSinceAsync(
        string vehicleKey,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        var rows = await dbContext.OrderIntents.AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey)
            .Select(row => new { row.CreatedAt, row.CreateDispatchArmedAt, row.LastCreateOutcomeAt, row.LastCreateOutcome })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        return rows.Any(row =>
            (row.CreatedAt == default && row.CreateDispatchArmedAt is null && row.LastCreateOutcomeAt is null) ||
            row.CreatedAt >= since ||
            row.CreateDispatchArmedAt >= since ||
            row.LastCreateOutcomeAt >= since ||
            (string.Equals(row.LastCreateOutcome, CreateRequestStarted, StringComparison.Ordinal) &&
             (row.LastCreateOutcomeAt is null || now - row.LastCreateOutcomeAt.Value <= InFlightCreateWindow)));
    }
}
