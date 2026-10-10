using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Runtime.TaskTypeStations;

namespace ControlServer.Tests;

/// <summary>
/// The station admission seed is every area-named machine station paired with <see cref="ExecutableTaskTypes"/>, and
/// the store refuses a seed whose content differs from what the configured <c>admissionPolicyVersion</c> was bound
/// with: the runtime then takes on no demand at all (docs/defects/20260915-admission-policy-drift-halts-runtime.md).
/// So the executable set and the shipped version move together, and these tests go red when they do not
/// (control-server#545): the set changed without a row here, a shipped version below its row, or the three shipped
/// versions disagreeing.
/// </summary>
/// <remarks>
/// The three shipped versions: the package's <c>appsettings.json</c>, and the two definitions of the v2 instance on
/// factory01 under <c>scripts/parallel/</c> -- the injected-demand one and the production MesIngest one. An instance
/// installed with a definition still naming the previous version stops taking demands the moment it starts.
/// </remarks>
public sealed class AdmissionPolicyVersionGuardTests
{
    /// <summary>
    /// Every executable set this server has shipped, with the lowest admission policy version a build carrying it may
    /// ship. A new set is a new row with a higher version, never an edit of an old one.
    /// </summary>
    private static readonly (string[] ExecutableTaskTypes, long MinimumVersion)[] VersionByExecutableSet =
    [
        ([TransportTaskTypes.WireToGate], 1),
        // control-server#163
        ([TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire], 2),
        // control-server#545 (batch 10): the four same-direction task types.
        ([.. TransportTaskTypes.All], 3),
    ];

    private static readonly string[] ShippedVersionFiles =
    [
        "src/ControlServer.Host/appsettings.json",
        "scripts/parallel/instance-factory01-v2.json",
        "scripts/parallel/instance-factory01-v2.production-mes.json",
    ];

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheExecutableSetThisBuildShipsHasARowInTheVersionTable()
    {
        Assert.True(
            RequiredVersion() is not null,
            $"The executable task types are now {{{string.Join(", ", ExecutableTaskTypes.All.Order(StringComparer.Ordinal))}}}, " +
            "which no row of the version table names. Changing them changes the admission seed: add a row with a version " +
            "above the last one, and raise admissionPolicyVersion to it in all of " + string.Join(", ", ShippedVersionFiles) + ".");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void NoShippedAdmissionPolicyVersionIsBelowTheOneItsExecutableSetRequires()
    {
        long required = RequiredVersion() ?? long.MaxValue;

        Assert.All(
            ShippedVersions(),
            shipped => Assert.True(
                shipped.Version >= required,
                $"{shipped.File} ships admissionPolicyVersion {shipped.Version}, but this build's executable task types " +
                $"need at least {required}: installed with it, the server reads its own seed as drift and takes on no demand."));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheThreeShippedAdmissionPolicyVersionsAgree()
    {
        (string File, long Version)[] shipped = ShippedVersions();

        Assert.True(
            shipped.Select(item => item.Version).Distinct().Count() == 1,
            "The shipped admissionPolicyVersion differs between files: " +
            string.Join(", ", shipped.Select(item => $"{item.File} = {item.Version}")) +
            ". They describe the same seed, so they move together.");
    }

    private static long? RequiredVersion()
    {
        foreach ((string[] executable, long minimum) in VersionByExecutableSet)
        {
            if (ExecutableTaskTypes.All.SetEquals(executable))
            {
                return minimum;
            }
        }
        return null;
    }

    private static (string File, long Version)[] ShippedVersions()
    {
        string root = FindRepositoryRoot();
        return
        [
            .. ShippedVersionFiles.Select(file =>
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, file)));
                JsonElement runtime = document.RootElement.TryGetProperty("JourneyRuntime", out JsonElement settings)
                    ? settings
                    : document.RootElement.GetProperty("journeyRuntime");
                return (file, runtime.GetProperty("admissionPolicyVersion").GetInt64());
            }),
        ];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("ControlServer.sln was not found above the test output.");
    }
}
