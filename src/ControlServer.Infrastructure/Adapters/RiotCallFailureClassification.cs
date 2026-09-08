using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using RIoT.Sdk.Core;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// How a failed RIoT call is classified: whether it is the SDK failing at all, whether it timed
/// out, and which failure category goes into the receipt.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than copied. The distinction it draws — a refusal against a call that may never
/// have arrived — is the one that decides whether a retry is safe, so the two adapters that reach
/// RIoT must not be able to drift apart on it.
/// </para>
/// <para>
/// The categories themselves are open strings and belong to the receipt, not to a domain
/// enumeration: they describe how a call failed for an auditor to read, and adding one must not be
/// a schema change.
/// </para>
/// </remarks>
internal static class RiotCallFailureClassification
{
    /// <summary>Whether the SDK is what failed, rather than the caller having made a bad request.</summary>
    internal static bool IsSdkFailure(Exception error) =>
        error is RiotApiException or ApiException or HttpRequestException or IOException or JsonException or
            InvalidOperationException or OperationCanceledException;

    /// <summary>
    /// Whether the call timed out, in any of the three shapes it arrives in.
    /// </summary>
    /// <remarks>
    /// A timeout is the case where the command may have taken effect and the answer was lost, so
    /// it is separated from every other failure before anything else is decided about it.
    /// </remarks>
    internal static bool IsTimeout(Exception error) =>
        error is OperationCanceledException ||
        error.InnerException is OperationCanceledException ||
        error is RiotApiException { BusinessCode: "riot-read-timeout" };

    internal static int? HttpStatusCode(Exception error) => error switch
    {
        RiotApiException riot => riot.StatusCode,
        ApiException api => api.ResponseStatusCode,
        _ => null
    };

    internal static string? RawBusinessCode(Exception error) =>
        error is RiotApiException riot ? riot.BusinessCode : null;

    internal static string FailureCategory(Exception error) => IsTimeout(error)
        ? "TIMEOUT"
        : error switch
        {
            HttpRequestException or IOException => "TRANSPORT_FAILURE",
            JsonException => "PROTOCOL_FAILURE",
            RiotApiException => "RIOT_API_FAILURE",
            ApiException => "HTTP_API_FAILURE",
            _ => "SDK_FAILURE"
        };
}
