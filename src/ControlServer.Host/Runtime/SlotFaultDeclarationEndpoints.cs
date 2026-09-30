using ControlServer.Host.Runtime.Fleet;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// REQ-0359's entry point: an administrator declares that the slot a vehicle is waiting on is faulty.
/// </summary>
public static class SlotFaultDeclarationEndpoints
{
    public const string Route = "/api/safety/v1/slot-fault-declarations";

    public static IReadOnlyList<string> FaultCategories { get; } = ["LOCK", "LIGHT_CURTAIN", "DOOR_MECHANISM", "IO_MODULE"];

    public static IReadOnlyList<string> AdministratorRoles { get; } = ["MAINTENANCE_ADMINISTRATOR", "SYSTEM_ADMINISTRATOR"];

    public static void MapSlotFaultDeclaration(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync);
    }

    public static Task<Results<
        Accepted<SlotFaultDeclarationResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        SlotFaultDeclarationHttpRequest request,
        SlotFaultDeclarationService declarations,
        VehicleRoster roster,
        IOptions<SlotFaultDeclarationOptions> declarationOptions,
        CancellationToken cancellationToken)
    {
        _ = (context, request, declarations, roster, declarationOptions, cancellationToken);
        throw new NotImplementedException();
    }
}

/// <summary>
/// What an administrator submits. <c>demandId</c> and <c>slotOperationAttemptId</c> are not among them: the server takes
/// both from the slot operation in progress, because what is declared is "this vehicle, this slot, now".
/// </summary>
/// <param name="RequestId">A UUID the caller chooses; the same request sent again is answered from the first.</param>
public sealed record SlotFaultDeclarationHttpRequest(
    string? RequestId,
    string? AgvId,
    int? SlotNo,
    string? FaultCategory,
    string? Note,
    string? OperatorId,
    string? AdministratorRole);

/// <summary>The declaration as the server holds it.</summary>
/// <param name="SentToVehicle">
/// Whether the command went out on the vehicle's live session in this request. False means it waits in the outbox and goes
/// out when the vehicle reconnects; the declaration is persisted either way.
/// </param>
public sealed record SlotFaultDeclarationResponse(
    string DeclarationId,
    string AgvId,
    string DemandId,
    string SlotOperationAttemptId,
    int SlotNo,
    string State,
    bool SentToVehicle);
