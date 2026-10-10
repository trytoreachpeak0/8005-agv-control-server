using System.Text.Json;

namespace ControlServer.Tests;

/// <summary>
/// control-server#578. The package's own <c>appsettings*.json</c> carries no vehicle roster.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is pinned.</b> <c>JourneyRuntimeOptionsRegistration</c> takes the roster whole from the last layer that
/// names a vehicle, and an empty array names none. The v2 parallel instance's single-car overlay writes
/// <c>"fleet": []</c> to mean "one vehicle, the primary pair", which therefore cannot remove a roster an earlier layer
/// wrote. That is harmless only while no earlier layer writes one -- the package's <c>appsettings.json</c> is that
/// earlier layer -- and while the installer's read-back refuses a roster the definition did not write
/// (control-server#571).
/// </para>
/// <para>
/// <b>When this turns red</b>, change the empty-array rule for the roster first (a layer writing <c>"fleet": []</c> must
/// then count as naming it), and only then add the roster to the package.
/// </para>
/// <para>
/// No <c>IntegrationSlice</c> trait, for the same reason as <c>DispatchGatePremiseArchitectureTests</c>: a cross-cutting
/// guard hung on one slice goes dark when that slice is deferred.
/// </para>
/// </remarks>
public sealed class PackageAppSettingsRosterArchitectureTests
{
    [Fact]
    public void PackageAppSettingsNameNoVehicleRoster()
    {
        string host = Path.Combine(FindRepositoryRoot(), "src", "ControlServer.Host");
        string[] files = Directory.GetFiles(host, "appsettings*.json", SearchOption.TopDirectoryOnly);
        Assert.Contains(files, file => Path.GetFileName(file) == "appsettings.json");

        foreach (string file in files)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (JsonProperty section in document.RootElement.EnumerateObject()
                         .Where(property => string.Equals(property.Name, "JourneyRuntime", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.False(
                    section.Value.ValueKind == JsonValueKind.Object &&
                    section.Value.EnumerateObject().Any(property => string.Equals(property.Name, "fleet", StringComparison.OrdinalIgnoreCase)),
                    $"{Path.GetFileName(file)} names JourneyRuntime:fleet. An empty roster in a later layer cannot remove it " +
                    "(JourneyRuntimeOptionsRegistration); change that rule before shipping a roster in the package.");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }
}
