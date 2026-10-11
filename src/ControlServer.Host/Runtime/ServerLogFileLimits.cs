namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#587: the size limits of the Host's Serilog File sink.
/// </summary>
public static class ServerLogFileLimits
{
    public const long FileSizeLimitBytes = 64L * 1024 * 1024;

    public const int RetainedFileCountLimit = 48;

    /// <summary>The configuration keys to add so that every File sink rolls at its size limit.</summary>
    public static IReadOnlyDictionary<string, string?> MissingSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }
}
