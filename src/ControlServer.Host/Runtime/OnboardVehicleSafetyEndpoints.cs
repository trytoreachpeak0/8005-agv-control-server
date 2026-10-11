using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ControlServer.Host.Runtime;

public static class OnboardVehicleSafetyEndpoints
{
    private static readonly Action<ILogger, string, long, Exception?> LogCancelledByOnboard =
        LoggerMessage.Define<string, long>(
            LogLevel.Warning,
            new EventId(5730, nameof(LogCancelledByOnboard)),
            "Onboard vehicle-safety request for {VehicleKey} was cancelled by the onboard after {ElapsedMilliseconds} ms");

    // control-server#600. The claimed key itself is not logged: it is whatever the request carried.
    private static readonly Action<ILogger, string, string, Exception?> LogRefused =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(5731, nameof(LogRefused)),
            "Onboard vehicle-safety request refused ({Reason}); the credential belongs to {VehicleKey}");

    /// <summary>The optional query parameter in which the onboard names its own vehicle (control-server#600).</summary>
    public const string VehicleKeyQueryParameter = "vehicleKey";

    public static void MapOnboardVehicleSafety(this WebApplication app)
    {
        app.MapGet("/api/onboard/v1/vehicle-safety", HandleAsync)
            .WithName("GetOnboardVehicleSafety")
            .WithSummary("Get the fail-closed RIoT vehicle motion projection for Onboard")
            .WithDescription("Returns STOPPED only for the complete RIoT Behavior Lab Round-41 predicate.")
            .Produces<OnboardVehicleSafetyResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
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

        // control-server#600: which vehicle is asking is decided by the credential, never by the request. With one credential
        // per vehicle configured, the bearer names the vehicle; without, the shared credential stands for the single vehicle
        // JourneyRuntime names, and a roster of more than one vehicle has no way to tell them apart, so nothing is answered.
        CredentialResolution resolution = ResolveVehicle(context, journeyOptions.Value, options);
        if (resolution.Unavailable)
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Vehicle safety projection unavailable");
        if (resolution.VehicleKey is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }
        string vehicleKey = resolution.VehicleKey;

        // The vehicle key in the query is the onboard's own claim, checked against the credential's vehicle and the roster; it
        // never chooses the vehicle. Absent, the credential's vehicle is read.
        string? refusal = RefuseClaimedVehicle(context, vehicleKey, journeyOptions.Value);
        if (refusal is not null)
        {
            LogRefused(
                loggerFactory.CreateLogger(typeof(OnboardVehicleSafetyEndpoints).FullName!),
                refusal,
                vehicleKey,
                null);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Vehicle safety projection refused",
                detail: refusal);
        }

        // control-server#573: a listing that does not add up is read again within this request, inside a budget below the
        // onboard's own timeout (IOnboardVehicleSafetyProjection).
        RiotVehicleSafetyObservation observation;
        try
        {
            observation = await safetyProjection.ReadForOnboardAsync(
                vehicleKey,
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
                vehicleKey,
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

    private readonly record struct CredentialResolution(string? VehicleKey, bool Unavailable);

    private static CredentialResolution ResolveVehicle(
        HttpContext context,
        JourneyRuntimeOptions journey,
        OnboardSafetyProjectionOptions options)
    {
        string? presented = PresentedBearer(context);

        if (options.VehicleCredentials.Length == 0)
        {
            string? shared = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
            // OnboardSafetyProjectionOptionsValidator refuses to start this way; refused here too, so no path answers one
            // vehicle's projection to whichever vehicle holds the shared credential.
            if (string.IsNullOrWhiteSpace(shared) || OnboardSafetyProjectionOptions.RosterVehicleKeys(journey).Count > 1)
                return new CredentialResolution(null, Unavailable: true);
            return presented is not null && FixedTimeEquals(shared, presented)
                ? new CredentialResolution(journey.VehicleKey, Unavailable: false)
                : new CredentialResolution(null, Unavailable: false);
        }

        // Every configured credential is compared, matched or not, so the time taken does not say which vehicle came close.
        string? matched = null;
        bool anyPopulated = false;
        foreach (OnboardSafetyVehicleCredentialOptions vehicle in options.VehicleCredentials)
        {
            string? credential = Environment.GetEnvironmentVariable(vehicle.CredentialEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(credential)) continue;
            anyPopulated = true;
            if (presented is not null && FixedTimeEquals(credential, presented)) matched ??= vehicle.VehicleKey;
        }
        if (!anyPopulated) return new CredentialResolution(null, Unavailable: true);
        return new CredentialResolution(matched, Unavailable: false);
    }

    private static string? PresentedBearer(HttpContext context) =>
        AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) &&
        string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(header.Parameter)
            ? header.Parameter
            : null;

    /// <summary>Why the request's own vehicle key is refused, or null when it is absent or names the credential's vehicle.</summary>
    private static string? RefuseClaimedVehicle(HttpContext context, string vehicleKey, JourneyRuntimeOptions journey)
    {
        if (!OnboardSafetyProjectionOptions.RosterVehicleKeys(journey).Contains(vehicleKey))
            return "VEHICLE_NOT_IN_ROSTER";
        if (!context.Request.Query.TryGetValue(VehicleKeyQueryParameter, out StringValues claimed)) return null;
        if (claimed.Count != 1 || string.IsNullOrWhiteSpace(claimed[0])) return "VEHICLE_KEY_EMPTY_OR_REPEATED";
        if (!OnboardSafetyProjectionOptions.RosterVehicleKeys(journey).Contains(claimed[0]!)) return "VEHICLE_NOT_IN_ROSTER";
        return string.Equals(claimed[0], vehicleKey, StringComparison.Ordinal) ? null : "VEHICLE_KEY_NOT_THE_CREDENTIALS";
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
