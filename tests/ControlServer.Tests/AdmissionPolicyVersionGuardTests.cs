using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Runtime.TaskTypeStations;

namespace ControlServer.Tests;

/// <summary>
/// The station admission seed is every area-named machine station paired with <see cref="ExecutableTaskTypes"/>, and
/// the store refuses a seed whose content differs from what the configured <c>admissionPolicyVersion</c> was bound
/// with: the runtime then takes on no demand at all (docs/defects/20260915-admission-policy-drift-halts-runtime.md).
/// So the executable set and the shipped version move together, and these tests go red when they do not
/// (control-server#545).
/// </summary>
/// <remarks>
/// <para>
/// The shipped versions: the package's <c>appsettings.json</c>, and every v2 instance definition under
/// <c>scripts/parallel/</c> (<c>instance-*.json</c>). An instance installed with a definition still naming the previous
/// version stops taking demands the moment it starts.
/// </para>
/// <para>
/// The table is history, written out task type by task type; nothing in it or in <see cref="ExecutableTaskTypes"/> is
/// derived from <see cref="TransportTaskTypes.All"/>, so neither can follow a change to the other without someone
/// writing it down (control-server#545 review M1).
/// </para>
/// </remarks>
public sealed class AdmissionPolicyVersionGuardTests
{
    /// <summary>
    /// Every executable set this server has shipped, oldest first, with the admission policy version that set was
    /// shipped under. A new set is a new last row with a higher version, never an edit of an old one.
    /// </summary>
    private static readonly (string[] ExecutableTaskTypes, long Version)[] VersionByExecutableSet =
    [
        (["WIRE_TO_GATE"], 1),
        // control-server#163
        (["WIRE_TO_GATE", "STAGING_TO_WIRE"], 2),
        // control-server#545 (batch 10): the four same-direction task types.
        (["WIRE_TO_GATE", "STAGING_TO_WIRE", "DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_OPTICAL", "WIRE_TO_NITROGEN"], 3),
    ];

    /// <summary>
    /// The protocol's task types this build deliberately does not execute. Empty since batch 10; a task type that is
    /// in neither this list nor <see cref="ExecutableTaskTypes"/> is one nobody has decided about.
    /// </summary>
    private static readonly string[] DeliberatelyNotExecutable = [];

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheVersionTableMovesStrictlyUpward()
    {
        long[] versions = [.. VersionByExecutableSet.Select(row => row.Version)];

        Assert.True(
            versions.Zip(versions.Skip(1)).All(pair => pair.Second > pair.First),
            $"The version table's versions are {string.Join(", ", versions)}; each new set must ship under a version " +
            "above the one before, or a store that bound the old set under it reads the new one as drift.");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheExecutableSetThisBuildShipsIsTheLastRowOfTheVersionTable()
    {
        string[] last = VersionByExecutableSet[^1].ExecutableTaskTypes;

        Assert.True(
            ExecutableTaskTypes.All.SetEquals(last),
            $"The executable task types are now {Describe(ExecutableTaskTypes.All)}, but the version table's last row is " +
            $"{Describe(last)}. Changing them changes the admission seed: add a new last row with a version above " +
            $"{VersionByExecutableSet[^1].Version}, and raise admissionPolicyVersion to it in every shipped file. Going back " +
            "to an earlier set is a change too -- an instance that bound the later one would read the earlier one as drift.");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void EveryProtocolTaskTypeIsDecidedEitherExecutableOrNot()
    {
        string[] undecided =
        [
            .. TransportTaskTypes.All.Where(taskType =>
                !ExecutableTaskTypes.Contains(taskType) && !DeliberatelyNotExecutable.Contains(taskType, StringComparer.Ordinal)),
        ];

        Assert.True(
            undecided.Length == 0,
            $"{Describe(undecided)} is a protocol task type that is neither in ExecutableTaskTypes nor in this test's " +
            "DeliberatelyNotExecutable list. Decide: list it here (it is refused as TASK_TYPE_NOT_YET_EXECUTABLE), or make " +
            "it executable, which is a new last row of the version table and a version bump.");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void NoShippedAdmissionPolicyVersionIsBelowTheLastRowOfTheVersionTable()
    {
        long required = VersionByExecutableSet[^1].Version;

        Assert.All(
            ShippedVersions(),
            shipped => Assert.True(
                shipped.Version >= required,
                $"{shipped.File} ships admissionPolicyVersion {shipped.Version}, but this build's executable task types " +
                $"need at least {required}: installed with it, the server reads its own seed as drift and takes on no demand."));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheShippedAdmissionPolicyVersionsAgree()
    {
        (string File, long Version)[] shipped = ShippedVersions();

        Assert.True(
            shipped.Select(item => item.Version).Distinct().Count() == 1,
            "The shipped admissionPolicyVersion differs between files: " +
            string.Join(", ", shipped.Select(item => $"{item.File} = {item.Version}")) +
            ". They describe the same seed, so they move together.");
    }

    /// <summary>
    /// The package's settings and every v2 instance definition. At least the two factory01 definitions must be found,
    /// so a renamed directory or pattern cannot leave the guard checking nothing.
    /// </summary>
    private static (string File, long Version)[] ShippedVersions()
    {
        string root = FindRepositoryRoot();
        string[] definitions =
        [
            .. Directory.GetFiles(Path.Combine(root, "scripts", "parallel"), "instance-*.json")
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];
        Assert.True(
            definitions.Length >= 2,
            $"Found {definitions.Length} instance definitions under scripts/parallel (instance-*.json); expected at least " +
            "instance-factory01-v2.json and instance-factory01-v2.production-mes.json.");
        return
        [
            ("src/ControlServer.Host/appsettings.json", ReadVersion(root, "src/ControlServer.Host/appsettings.json", "JourneyRuntime")),
            .. definitions.Select(file => (file, ReadVersion(root, file, "journeyRuntime"))),
        ];
    }

    private static long ReadVersion(string root, string file, string section)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, file)));
        return document.RootElement.GetProperty(section).GetProperty("admissionPolicyVersion").GetInt64();
    }

    private static string Describe(IEnumerable<string> taskTypes) =>
        $"{{{string.Join(", ", taskTypes.Order(StringComparer.Ordinal))}}}";

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
