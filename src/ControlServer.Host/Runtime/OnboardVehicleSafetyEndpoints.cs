using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public static class OnboardVehicleSafetyEndpoints
{
    private static readonly Action<ILogger, string, long, Exception?> LogCancelledByOnboard =
        LoggerMessage.Define<string, long>(
            LogLevel.Warning,
            new EventId(5730, nameof(LogCancelledByOnboard)),
            "Onboard vehicle-safety request for {VehicleKey} was cancelled by the onboard after {ElapsedMilliseconds} ms");

    public static void MapOnboardVehicleSafety(this WebApplication app)
    {
        app.MapGet("/api/onboard/v1/vehicle-safety", HandleAsync)
            .WithName("GetOnboardVehicleSafety")
            .WithSummary("Get the fail-closed RIoT vehicle motion projection for Onboard")
            .WithDescription("Returns STOPPED only for the complete RIoT Behavior Lab Round-41 predicate.")
            .Produces<OnboardVehicleSafetyResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status499ClientClosedRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<OnboardVehicleSafetyResponse>,
        UnauthorizedHttpResult,
        StatusCodeHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        IOnboardVehicleSafetyProjection safetyProjection,
        IOptions<JourneyRuntimeOptions> journeyOptions,
        IOptions<OnboardSafetyProjectionOptions> projectionOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        OnboardSafetyProjectionOptions options = projectionOptions.Value;
        DateTimeOffset requestStarted = DateTimeOffset.UtcNow;
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

        // control-server#573: a listing that does not add up is read again within this request, inside a budget below the
        // onboard's own timeout (IOnboardVehicleSafetyProjection).
        RiotVehicleSafetyObservation observation;
        try
        {
            observation = await safetyProjection.ReadForOnboardAsync(
                journeyOptions.Value.VehicleKey,
                options.NonFinalOrderReadRetries,
                TimeSpan.FromMilliseconds(options.ReadBudgetMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The onboard gave up on this request (its timeout, or it went away): a cancellation, not a server error. No one
            // reads this answer; the onboard already treats the missing reading as unknown.
            LogCancelledByOnboard(
                loggerFactory.CreateLogger(typeof(OnboardVehicleSafetyEndpoints).FullName!),
                journeyOptions.Value.VehicleKey,
                (long)(DateTimeOffset.UtcNow - requestStarted).TotalMilliseconds,
                null);
            return TypedResults.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
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
