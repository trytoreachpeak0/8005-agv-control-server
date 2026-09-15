using System.Net;
using System.Text.RegularExpressions;

namespace ControlServer.EmergencyDrill;

/// <summary>
/// The fixed limits of the reduced W1 emergency-stop drill (batch 2 ticket 19).
/// </summary>
internal static class DrillGuards
{
    /// <summary>agv02's RIoT deviceKey -- the only real vehicle this tool will ever address.</summary>
    /// <remarks>
    /// On 2026-09-14 the product owner approved, in chat, a one-off exception to section 1.5 of
    /// <c>vendor/8005-agv-program/docs/riot-call-allowlist.md</c> for this reduced drill only and for
    /// agv02 only: one move order through byDefaultMissions, at most one triggerEmergency while the
    /// empty vehicle travels between two stations on map 25, then CMD_ORDER_CANCEL for the drill order,
    /// and only once that order is terminal, cancelEmergency from CAN_RECOVER after on-site staff
    /// confirm stopped / empty / doors closed.
    /// On 2026-09-15 the product owner approved in chat, for issue control-server#63 and agv02 only, a
    /// follow-up run: one move order, one CMD_ORDER_HELD while driving between stations, then one
    /// triggerEmergency on the HELD order, then CMD_ORDER_CANCEL and cancelEmergency as before.
    /// Pointing this tool at another vehicle is a new approval, not an edit to this line.
    /// </remarks>
    internal const string ApprovedDeviceKey = "BROKERX-f38975561adf46ccb1d2f23833c7d0e4";

    internal const string ApprovedVehicleAlias = "agv02";

    /// <summary>agv01 runs the MVP line and customer demos. Named only so the refusal can say so.</summary>
    internal const string Agv01DeviceKey = "BROKERX-0c20ff0600d644869a6a80c186065d85";

    /// <summary>
    /// The key the self-test seeds into ControlServer.FakeRiot. Accepted only with <c>--fake-riot</c>
    /// and a literal loopback RIoT address, and every later command re-checks the address.
    /// </summary>
    internal const string SelfTestDeviceKey = "BROKERX-DRILL-SELFTEST-0001";

    internal const int ApprovedMapId = 25;

    /// <summary>What RIoT's vehicle card reports as currentMap for map 25 (JourneyRuntime:mapIdentity).</summary>
    internal const string DefaultMapIdentity = "老厂前线new";

    internal const int MinimumBatteryPercent = 30;

    internal const string ApiKeyEnvironmentVariable = "CONTROL_SERVER_RIOT_CALL_API_KEY";

    /// <summary>A preflight older than this, or taken on another host, does not license create-move or trigger.</summary>
    internal static readonly TimeSpan PreflightMaxAge = TimeSpan.FromMinutes(60);

    internal const string FieldConfirmationSuffix = "stopped,empty,doors-closed";

    /// <summary>Null when the key may be addressed; otherwise the reason it may not.</summary>
    internal static string? RefuseDeviceKey(string deviceKey, bool fakeRiot, Uri baseUrl)
    {
        if (string.Equals(deviceKey, ApprovedDeviceKey, StringComparison.Ordinal))
        {
            return null;
        }
        if (string.Equals(deviceKey, SelfTestDeviceKey, StringComparison.Ordinal))
        {
            if (!fakeRiot)
            {
                return "the self-test device key is only accepted together with --fake-riot";
            }
            return IsLiteralLoopback(baseUrl)
                ? null
                : "--fake-riot requires a literal loopback RIoT address (127.0.0.0/8 or [::1])";
        }
        if (string.Equals(deviceKey, Agv01DeviceKey, StringComparison.Ordinal))
        {
            return "that is agv01 (MVP line); the 2026-09-14 exception covers agv02 only";
        }
        return "only agv02 (" + ApprovedDeviceKey + ") is covered by the 2026-09-14 exception";
    }

    /// <summary>
    /// A FakeRiot self-test run: the self-test key, <c>--fake-riot</c> and a literal loopback address.
    /// Self-test-only waivers (<c>trigger --allow-stationary</c>) are honoured on nothing else.
    /// </summary>
    internal static bool IsSelfTestRun(string deviceKey, bool fakeRiot, Uri baseUrl) =>
        string.Equals(deviceKey, SelfTestDeviceKey, StringComparison.Ordinal) && fakeRiot && IsLiteralLoopback(baseUrl);

    internal static bool IsLiteralLoopback(Uri baseUrl) =>
        IPAddress.TryParse(baseUrl.DnsSafeHost, out IPAddress? address) && IPAddress.IsLoopback(address);

    /// <summary>An absolute http(s) origin with no credentials, query or fragment; null otherwise.</summary>
    internal static Uri? ParseBaseUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 ||
            uri.AbsolutePath != "/")
        {
            return null;
        }
        return uri;
    }

    internal static string Normalize(Uri baseUrl) => baseUrl.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// Parses <c>"&lt;name&gt; stopped,empty,doors-closed"</c>. The fixed suffix is the point: the person
    /// typing it is stating three facts on site, not choosing words.
    /// </summary>
    internal static string? ParseFieldConfirmation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        Match match = Regex.Match(
            text.Trim(),
            @"^(?<name>\S(?:.*\S)?)\s+stopped,empty,doors-closed$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["name"].Value : null;
    }
}
