using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// REQ-0359's entry point: an administrator declares that the slot a vehicle is waiting on is faulty.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="SlotFaultDeclarationService"/>.</b> This authenticates the call, checks the
/// request's shape, resolves the named vehicle and maps the decision onto HTTP, in the shape of
/// <see cref="EmergencyStopReleaseEndpoints"/>: the vehicle is named, never inferred, and a vehicle this server does not
/// drive is a 404.
/// </para>
/// <para>
/// <b>202, not 200</b>: the declaration is persisted and queued, and whether it applies is the vehicle's to say
/// (<c>SlotFaultDeclarationResult</c>). The same request again is 202 with the same declaration. A precondition that does
/// not hold is 409 with every reason; a request id reused for another request is 409 too. A field missing, or outside
/// what the command can carry, is 422 before anything is read.
/// </para>
/// <para>
/// Authentication is the shared bearer credential described on <see cref="SlotFaultDeclarationOptions"/>, and it is not a
/// login.
/// </para>
/// </remarks>
public static class SlotFaultDeclarationEndpoints
{
    public const string Route = "/api/safety/v1/slot-fault-declarations";

    /// <summary>The command's <c>faultCategory</c> enumeration.</summary>
    public static IReadOnlyList<string> FaultCategories { get; } = ["LOCK", "LIGHT_CURTAIN", "DOOR_MECHANISM", "IO_MODULE"];

    /// <summary>The command's <c>administratorRole</c> enumeration, the same as the recovery session's.</summary>
    public static IReadOnlyList<string> AdministratorRoles { get; } = ["MAINTENANCE_ADMINISTRATOR", "SYSTEM_ADMINISTRATOR"];

    public static void MapSlotFaultDeclaration(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("DeclareSlotFault")
            .WithSummary("Declare the slot a vehicle is waiting on faulty (REQ-0359)")
            .WithDescription(
                "Checks that the vehicle is executing a load or unload, that the slot is the one it waits on, that the slot "
                + "reported SLOT_EXPECTED_ACTION_OVERDUE, and that the operation has neither closed nor been judged UNKNOWN; "
                + "then persists the declaration with its SlotFaultDeclarationCommand and sends it. Returns 202: the vehicle "
                + "answers APPLIED or NOT_APPLICABLE.")
            .Produces<SlotFaultDeclarationResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
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
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(declarationOptions);

        SlotFaultDeclarationOptions options = declarationOptions.Value;
        context.Response.Headers.CacheControl = "no-store";

        string? expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Slot fault declaration unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !FixedTimeEquals(expected, header.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        if (request is null ||
            !Guid.TryParseExact(request.RequestId, "D", out _) ||
            string.IsNullOrWhiteSpace(request.AgvId) ||
            request.SlotNo is not >= 1 ||
            request.FaultCategory is null ||
            !FaultCategories.Contains(request.FaultCategory, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(request.Note) ||
            string.IsNullOrWhiteSpace(request.OperatorId) ||
            request.AdministratorRole is null ||
            !AdministratorRoles.Contains(request.AdministratorRole, StringComparer.Ordinal))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete slot fault declaration",
                detail: "requestId (a UUID), agvId, slotNo (1 or more), a non-empty note and operatorId are required; "
                    + $"faultCategory must be one of {string.Join(", ", FaultCategories)} and administratorRole one of "
                    + $"{string.Join(", ", AdministratorRoles)}.");
        }

        if (roster.ByAgvId(request.AgvId) is not FleetVehicle vehicle)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown vehicle",
                detail: $"'{request.AgvId}' is not a vehicle this server drives.");
        }

        SlotFaultDeclarationDecision decision = await declarations.DeclareAsync(
            new SlotFaultDeclarationRequest(
                request.RequestId!,
                vehicle.AgvId,
                request.SlotNo.Value,
                request.FaultCategory,
                request.Note,
                request.OperatorId,
                request.AdministratorRole),
            cancellationToken).ConfigureAwait(false);

        if (decision.Outcome is SlotFaultDeclarationOutcome.Accepted or SlotFaultDeclarationOutcome.AlreadyAccepted)
        {
            SlotFaultDeclarationRow declaration = decision.Declaration!;
            return TypedResults.Accepted(
                (string?)null,
                new SlotFaultDeclarationResponse(
                    declaration.DeclarationId,
                    declaration.AgvId,
                    declaration.DemandId,
                    declaration.SlotOperationAttemptId,
                    declaration.SlotNo,
                    declaration.State,
                    decision.SentToVehicle));
        }

        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: decision.Outcome == SlotFaultDeclarationOutcome.RequestIdConflict
                ? "Request id already used for another declaration"
                : "Slot fault declaration refused",
            detail: string.Join(',', decision.Reasons),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agvId"] = vehicle.AgvId,
                ["slotNo"] = request.SlotNo.Value,
                ["reasons"] = decision.Reasons,
            });
    }

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
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
