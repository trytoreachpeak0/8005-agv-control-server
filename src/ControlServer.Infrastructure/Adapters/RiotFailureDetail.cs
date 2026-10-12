using System.Text.RegularExpressions;
using RIoT.Sdk.Core;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// What RIoT said when a call failed, made fit for a log line (control-server#611).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the words come from.</b> <see cref="RiotApiException.ResponseBody"/> when the SDK filled it in, else the exception
/// message. The SDK in use (0.2.0-fp.6) builds a business failure's message as <c>RIoT business failure code=… message=…</c>
/// and leaves the response body empty, so RIoT's <c>message</c> is what arrives today; its <c>msgDetail</c> -- where RIoT puts a
/// stack trace such as the NullPointerException behind 00002 -- arrives once the SDK carries it in the body.
/// </para>
/// <para>
/// <b>Made safe to log</b>: line breaks flattened to one line, anything that looks like a credential masked, and cut to
/// <see cref="MaxLength"/> characters. The value never goes into the database: the receipt columns hold codes only.
/// </para>
/// </remarks>
internal static partial class RiotFailureDetail
{
    /// <summary>The longest detail kept, in UTF-16 code units, before the truncation mark.</summary>
    public const int MaxLength = 1000;

    public static string Of(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        string raw = error is RiotApiException { ResponseBody: { Length: > 0 } body } ? body : error.Message;
        string flat = LineBreaks().Replace(raw, " ").Trim();
        string masked = Credential().Replace(flat, match => match.Groups["key"].Value + "***");
        return masked.Length <= MaxLength ? masked : string.Concat(masked.AsSpan(0, MaxLength), "…(truncated)");
    }

    [GeneratedRegex(@"[\r\n\t]+")]
    private static partial Regex LineBreaks();

    // "Authorization: Bearer x" and "Bearer x" mask the x; "token=x", "password: x" and their kind need the separator, so a word
    // that merely starts with one of them ("tokenizer") is left alone.
    [GeneratedRegex(
        @"(?<key>authorization[""']?\s*[:=]\s*[""']?(?:bearer\s+|basic\s+)?|bearer\s+|(?:access_?token|token|password|passwd|secret|api_?key|callapikey)[""']?\s*[:=]\s*[""']?)[^\s""',;&}]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex Credential();
}
