using ControlServer.TestDoubles;

namespace ControlServer.FakeMesIngest;

/// <summary>
/// Adds or replaces one demand in the catalog, keyed by <c>demandId</c>. Every catalog change
/// bumps <c>catalogRevision</c>, which is part of the identity the control server verifies.
/// </summary>
public sealed record DemandCommand : CommandEnvelope
{
    public string? DemandId { get; init; }
    public string? SeriesId { get; init; }
    public string? WorkType { get; init; }
    public string? Sublot { get; init; }
    public int? Generation { get; init; }
    public long? DemandRevision { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ValueObservedAt { get; init; }
    public string? ValuePollTraceId { get; init; }
    public string? ValueProjectionCommitId { get; init; }
    public string? Area { get; init; }
    public string? Eqp { get; init; }
    public string? Step { get; init; }
    public DateTimeOffset? MesSourceDate { get; init; }
    public string? Package { get; init; }
    public int? MaxBoxCount { get; init; }
}

public sealed record ContractCommand : CommandEnvelope
{
    public bool? BreakContract { get; init; }
}

public static class ControlPlane
{
    public static void MapControlPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeMesIngestState> engine =
            app.Services.GetRequiredService<CommandEngine<FakeMesIngestState>>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/openapi.json", ControlPlaneConventions.OpenApiDocument);

        control.MapGet("/health", () => Results.Json(ControlPlaneConventions.Envelope(engine, new
        {
            status = "live",
            demandCount = engine.Snapshot().State.Demands.Count
        })));

        control.MapGet("/snapshot", () =>
        {
            FakeMesIngestState state = engine.Snapshot().State;
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                state.HistoryEpoch,
                state.CatalogRevision,
                state.BreakContract,
                demands = state.Demands
            }));
        });

        control.MapPost("/reset", (CommandEnvelope command) =>
        {
            if (string.IsNullOrWhiteSpace(command.CommandId))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, null);
            }
            CommandOutcome outcome = engine.Reset(command.RunId, command.CommandId, command.ExpectedRevision);
            return ControlPlaneConventions.Respond(engine, command.CommandId, outcome);
        });

        control.MapPut("/demands/{demandId}", (string demandId, DemandCommand command) =>
            ControlPlaneConventions.Handle(engine, "demand:" + demandId, command, state =>
            {
                FakeDemand? existing = state.Demands.FirstOrDefault(item =>
                    string.Equals(item.DemandId, demandId, StringComparison.Ordinal));
                DateTimeOffset now = DateTimeOffset.UtcNow;
                FakeDemand updated = new()
                {
                    DemandId = demandId,
                    SeriesId = command.SeriesId ?? existing?.SeriesId ?? "SERIES-" + demandId,
                    WorkType = command.WorkType ?? existing?.WorkType ?? "WIRE_TO_GATE",
                    Sublot = command.Sublot ?? existing?.Sublot
                        ?? throw new CommandRefusedException(ReasonCodes.InvalidArgument),
                    Generation = command.Generation ?? existing?.Generation ?? 1,
                    DemandRevision = command.DemandRevision ?? existing?.DemandRevision ?? 1,
                    CreatedAt = command.CreatedAt ?? existing?.CreatedAt ?? now.AddMinutes(-10),
                    ValueObservedAt = command.ValueObservedAt ?? existing?.ValueObservedAt ?? now.AddMinutes(-1),
                    ValuePollTraceId = command.ValuePollTraceId ?? existing?.ValuePollTraceId ?? "TRACE-" + demandId,
                    ValueProjectionCommitId = command.ValueProjectionCommitId
                        ?? existing?.ValueProjectionCommitId ?? "COMMIT-" + demandId,
                    Area = command.Area ?? existing?.Area
                        ?? throw new CommandRefusedException(ReasonCodes.InvalidArgument),
                    Eqp = command.Eqp ?? existing?.Eqp
                        ?? throw new CommandRefusedException(ReasonCodes.InvalidArgument),
                    Step = command.Step ?? existing?.Step,
                    MesSourceDate = command.MesSourceDate ?? existing?.MesSourceDate,
                    Package = command.Package ?? existing?.Package
                        ?? throw new CommandRefusedException(ReasonCodes.InvalidArgument),
                    MaxBoxCount = command.MaxBoxCount ?? existing?.MaxBoxCount ?? 4
                };
                if (updated.MaxBoxCount <= 0)
                {
                    throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                }
                if (updated == existing)
                {
                    return null;
                }
                List<FakeDemand> demands = state.Demands
                    .Where(item => !string.Equals(item.DemandId, demandId, StringComparison.Ordinal))
                    .Append(updated)
                    .OrderBy(item => item.DemandId, StringComparer.Ordinal)
                    .ToList();
                return state with { Demands = demands, CatalogRevision = state.CatalogRevision + 1 };
            }));

        // [FromBody] is required rather than tidy: minimal APIs refuse to infer a body on
        // DELETE, and the whole route table fails to build without it.
        control.MapDelete("/demands/{demandId}", (string demandId,
            [Microsoft.AspNetCore.Mvc.FromBody] CommandEnvelope command) =>
            ControlPlaneConventions.Handle(engine, "demand-delete:" + demandId, command, state =>
            {
                List<FakeDemand> demands = state.Demands
                    .Where(item => !string.Equals(item.DemandId, demandId, StringComparison.Ordinal))
                    .ToList();
                return demands.Count == state.Demands.Count
                    ? null
                    : state with { Demands = demands, CatalogRevision = state.CatalogRevision + 1 };
            }));

        control.MapPut("/contract", (ContractCommand command) =>
            ControlPlaneConventions.Handle(engine, "contract", command, state =>
            {
                bool broken = command.BreakContract
                    ?? throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                return state.BreakContract == broken ? null : state with { BreakContract = broken };
            }));
    }
}
