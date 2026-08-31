using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public static class OnboardVehicleSafetyEndpoints
{
    public static void MapOnboardVehicleSafety(this WebApplication app)
    {
        app.MapGet("/api/onboard/v1/vehicle-safety", HandleAsync)
            .WithName("GetOnboardVehicleSafety")
            .WithSummary("Get the fail-closed RIoT vehicle motion projection for Onboard")
            .WithDescription("Returns STOPPED only for the complete RIoT Behavior Lab Round-41 predicate.")
            .Produces<OnboardVehicleSafetyResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<OnboardVehicleSafetyResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        IRiotVehicleSafetyFacts safetyFacts,
        IOptions<JourneyRuntimeOptions> journeyOptions,
        IOptions<OnboardSafetyProjectionOptions> projectionOptions,
        CancellationToken cancellationToken)
    {
        OnboardSafetyProjectionOptions options = projectionOptions.Value;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        string? expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Vehicle safety projection unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !FixedTimeEquals(expected, header.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        RiotVehicleSafetyObservation observation = await safetyFacts.ReadVehicleSafetyAsync(
            journeyOptions.Value.VehicleKey, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new OnboardVehicleSafetyResponse(
            observation.VehicleKey,
            observation.MotionState.ToString().ToUpperInvariant(),
            observation.ObservedAt,
            observation.Source,
            observation.ReasonCodes));
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

/// <summary>Fail-closed vehicle motion evidence returned to the protected Onboard owner.</summary>
public sealed record OnboardVehicleSafetyResponse(
    string VehicleKey,
    string MotionState,
    DateTimeOffset ObservedAt,
    string Source,
    IReadOnlyList<string> ReasonCodes);
