using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#535 review M2, extended by control-server#571: what this process really bound, logged once it has
/// started (options validated).
/// </summary>
public static class EffectiveConfigurationEvent
{
    public const string Template =
        "EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl}";

    public static readonly EventId Id = new(5350, "EffectiveConfiguration");

    private static readonly Action<ILogger, string[], string[], string, Exception?> Write =
        LoggerMessage.Define<string[], string[], string>(LogLevel.Information, Id, Template);

    public static void Log(ILogger logger, JourneyRuntimeOptions effective, string mesIngestBaseUrl) =>
        Write(logger, effective.AllowedWorkTypes, effective.AllowedDispatchZones, mesIngestBaseUrl, null);
}
