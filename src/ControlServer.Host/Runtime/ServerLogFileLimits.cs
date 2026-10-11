using System.Globalization;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#587: the size limits of the Host's Serilog File sink, applied to whatever configuration installed it.
/// </summary>
/// <remarks>
/// <para>
/// The File sink is defined by <c>Install-ControlServerLocal.ps1</c> in <c>appsettings.Production.json</c>; the package's
/// <c>appsettings.json</c> has none. Until #587 it set <c>rollingInterval=Day</c> and nothing about size, so Serilog's
/// default 1 GiB <c>fileSizeLimitBytes</c> was a cap: on factory01 the v2 log reached it at 22:36 on 2026-10-10 and the
/// Host logged nothing more that day. An upgrade keeps the installed file, so fixing the installer alone would leave
/// every existing install capped; the Host therefore fills in, at startup, what the installed sink left out.
/// </para>
/// <para>
/// For each <c>Serilog:WriteTo</c> entry named <c>File</c>: <c>rollOnFileSizeLimit</c> is always true (a size limit
/// without it stops the log); <c>fileSizeLimitBytes</c> and <c>retainedFileCountLimit</c> are set when absent or not a
/// positive number (null means unlimited to Serilog). A positive value the installed file names is kept.
/// </para>
/// <para>
/// The disk the log takes is at most <see cref="RetainedFileCountLimit"/> x <see cref="FileSizeLimitBytes"/> = 3 GiB:
/// retention counts every file of the set, the current one and each <c>_NNN</c> roll included. On factory01 the v2
/// log lives on C: (39 GB free on 2026-10-11), next to the MVP's own.
/// </para>
/// </remarks>
public static class ServerLogFileLimits
{
    /// <summary>64 MiB: a read-back reads whole files, and at the 2026-10-11 rate (300 MB/h) a file still lasts ~13 min.</summary>
    public const long FileSizeLimitBytes = 64L * 1024 * 1024;

    /// <summary>48 files: about two weeks at the volume left after #587's level changes (about 180 MB a day).</summary>
    public const int RetainedFileCountLimit = 48;

    /// <summary>The configuration keys to add so that every File sink rolls at its size limit.</summary>
    public static IReadOnlyDictionary<string, string?> MissingSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Dictionary<string, string?> missing = new(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection sink in configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            if (!string.Equals(sink["Name"], "File", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string args = $"{sink.Path}:Args";
            if (!string.Equals(configuration[$"{args}:rollOnFileSizeLimit"], "true", StringComparison.OrdinalIgnoreCase))
            {
                missing[$"{args}:rollOnFileSizeLimit"] = "true";
            }
            if (!IsPositive(configuration[$"{args}:fileSizeLimitBytes"]))
            {
                missing[$"{args}:fileSizeLimitBytes"] = FileSizeLimitBytes.ToString(CultureInfo.InvariantCulture);
            }
            if (!IsPositive(configuration[$"{args}:retainedFileCountLimit"]))
            {
                missing[$"{args}:retainedFileCountLimit"] = RetainedFileCountLimit.ToString(CultureInfo.InvariantCulture);
            }
        }
        return missing;
    }

    private static bool IsPositive(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) && number > 0;
}
