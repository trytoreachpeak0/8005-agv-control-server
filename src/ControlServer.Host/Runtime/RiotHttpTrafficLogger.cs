using Microsoft.Extensions.Http.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#587: the RIoT client's request log, in place of the four lines per request the default HttpClient
/// loggers write (LogicalHandler and ClientHandler, start and end, events 100 and 101).
/// </summary>
/// <remarks>
/// <para>
/// On factory01 on 2026-10-11 those lines were about 28 % of a log growing at 300 MB an hour: ten RIoT reads a second,
/// nearly all of them successful polls of orderRecord, vehicle info and the map. The same client carries every
/// command the server sends RIoT -- order create (<c>/api/order/v1/add</c>), order commands, triggerEmergency and
/// cancelEmergency -- a handful an hour, and for some of them this line is the only record that the call went out.
/// </para>
/// <para>
/// So: a command (anything but GET and HEAD) is logged when it is sent and when it is answered; any request answered
/// with a status that is not success, or failing with an exception, is logged too; all of these at Information, the
/// level the default loggers used, so the set of Warning records -- which several L2 "silent" criteria read, and the
/// fake RIoT answers an absent order with 404 -- is what it was. A successful read is logged at Debug, which the Host's
/// minimum level drops; the caller still logs its own verdict on a failed read. To count reads again for a diagnosis (control-server#573 did so from
/// these lines), set <c>Serilog:MinimumLevel:Override:ControlServer.Host.Runtime.RiotHttpTrafficLogger</c> to
/// <c>Debug</c> in the instance's <c>appsettings.Production.json</c> and restart.
/// </para>
/// </remarks>
public sealed class RiotHttpTrafficLogger(ILogger<RiotHttpTrafficLogger> logger) : IHttpClientLogger
{
    private static readonly Action<ILogger, string, string?, Exception?> LogCommandSent = LoggerMessage.Define<string, string?>(
        LogLevel.Information,
        new EventId(5870, nameof(LogCommandSent)),
        "RIoT command {HttpMethod} {Uri} sent");

    private static readonly Action<ILogger, string, string?, int, double, Exception?> LogCommandAnswered =
        LoggerMessage.Define<string, string?, int, double>(
            LogLevel.Information,
            new EventId(5871, nameof(LogCommandAnswered)),
            "RIoT command {HttpMethod} {Uri} answered {StatusCode} after {ElapsedMilliseconds} ms");

    private static readonly Action<ILogger, string, string?, int, double, Exception?> LogReadAnswered =
        LoggerMessage.Define<string, string?, int, double>(
            LogLevel.Debug,
            new EventId(5872, nameof(LogReadAnswered)),
            "RIoT read {HttpMethod} {Uri} answered {StatusCode} after {ElapsedMilliseconds} ms");

    private static readonly Action<ILogger, string, string?, int, double, Exception?> LogNotSuccess =
        LoggerMessage.Define<string, string?, int, double>(
            LogLevel.Information,
            new EventId(5873, nameof(LogNotSuccess)),
            "RIoT request {HttpMethod} {Uri} answered {StatusCode} after {ElapsedMilliseconds} ms, which is not a success status");

    private static readonly Action<ILogger, string, string?, double, Exception?> LogFailed =
        LoggerMessage.Define<string, string?, double>(
            LogLevel.Information,
            new EventId(5874, nameof(LogFailed)),
            "RIoT request {HttpMethod} {Uri} failed after {ElapsedMilliseconds} ms");

    public object? LogRequestStart(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsRead(request))
        {
            LogCommandSent(logger, request.Method.Method, request.RequestUri?.ToString(), null);
        }
        return null;
    }

    public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        string? uri = request.RequestUri?.ToString();
        int status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            LogNotSuccess(logger, request.Method.Method, uri, status, elapsed.TotalMilliseconds, null);
        }
        else if (IsRead(request))
        {
            LogReadAnswered(logger, request.Method.Method, uri, status, elapsed.TotalMilliseconds, null);
        }
        else
        {
            LogCommandAnswered(logger, request.Method.Method, uri, status, elapsed.TotalMilliseconds, null);
        }
    }

    public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);
        LogFailed(logger, request.Method.Method, request.RequestUri?.ToString(), elapsed.TotalMilliseconds, exception);
    }

    private static bool IsRead(HttpRequestMessage request) => request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
}
