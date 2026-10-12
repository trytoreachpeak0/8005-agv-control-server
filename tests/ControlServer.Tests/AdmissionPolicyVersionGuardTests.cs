using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Runtime.TaskTypeStations;

namespace ControlServer.Tests;

/// <summary>
/// The station admission seed is every area-named machine station paired with <see cref="ExecutableTaskTypes"/>, and
/// the store refuses a seed whose content differs from what the configured <c>admissionPolicyVersion</c> was bound
/// with, and a version below the highest it has bound: the runtime then takes on no demand at all
/// (docs/defects/20260915-admission-policy-drift-halts-runtime.md). So the executable set and the shipped versions move
/// together with what the instance has bound, and these tests go red when they do not (control-server#545, #614).
/// </summary>
/// <remarks>
/// <para>
/// The shipped versions: the package's <c>appsettings.json</c>, and every v2 instance definition under
/// <c>scripts/parallel/</c> (<c>instance-*.json</c>). An instance installed with a definition naming a version below
/// the one it last ran under stops taking demands the moment its runtime starts.
/// </para>
/// <para>
/// The executable set is not the only thing that moves the version. The seed is re-read from the live map's area-named
/// stations every round, so renaming one on the map is drift too; and the version is not reset when the instance's
/// database is replaced, because 19-deploy-control-server-parallel.ps1 holds every deployment to the version recorded
/// in the server's installed-instance.json, which outlives a database swap. <see cref="History"/> is therefore one
/// table, in the order things happened, with the kind of each bump (control-server#614 review S1, M2): the package
/// ships the last executable-set version, the definitions ship the last version of all.
/// </para>
/// <para>
/// The executable sets are written out task type by task type; nothing in the table or in
/// <see cref="ExecutableTaskTypes"/> is derived from <see cref="TransportTaskTypes.All"/>, so neither can follow a
/// change to the other without someone writing it down (control-server#545 review M1).
/// </para>
/// </remarks>
public sealed class AdmissionPolicyVersionGuardTests
{
    private enum BumpKind
    {
        /// <summary>The build's executable task types changed: the seed changed everywhere, package included.</summary>
        ExecutableSet,

        /// <summary>The live map's area-named machine stations changed under the instance: its seed changed.</summary>
        StationNames,

        /// <summary>The instance was installed into a new database under an override; nothing in the seed changed.</summary>
        NewDatabaseOverride,
    }

    /// <summary>
    /// Every version the v2 line has shipped or the factory01 v2 instance has been installed and bound under, across
    /// database swaps, oldest first. A further bump is a new last row with a higher version, never an edit of an old
    /// one. <see cref="BumpKind.ExecutableSet"/> rows carry the set; the others carry none.
    /// </summary>
    private static readonly (long Version, BumpKind Kind, string[]? ExecutableTaskTypes, string Why)[] History =
    [
        (1, BumpKind.ExecutableSet, ["WIRE_TO_GATE"], "the first executable set"),
        (2, BumpKind.ExecutableSet, ["WIRE_TO_GATE", "STAGING_TO_WIRE"], "control-server#163"),
        (3, BumpKind.ExecutableSet,
            ["WIRE_TO_GATE", "STAGING_TO_WIRE", "DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_OPTICAL", "WIRE_TO_NITROGEN"],
            "control-server#545 (batch 10): the four same-direction task types"),
        (4, BumpKind.StationNames, null,
            "2026-10-11: map 26's T04-01..08 and T05-01..08 renamed T4-1..8 and T5-1..8, station 222 added; raised to 4 by the " +
            "field session (control-server#566), and installed-instance.json read 4 by 10-11 18:45"),
        (5, BumpKind.NewDatabaseOverride, null,
            "2026-10-11 18:45: installed into the new database newdb5 with -AdmissionPolicyVersionOverride 5 " +
            "(evidence/field/20261011-W2-prep6-two-car/08-install-newdb5.txt); every reinstall on 10-12 also carried 5"),
        (6, BumpKind.StationNames, null,
            "2026-10-12: map 26 re-uploaded and finalised at 21:12, 78 machine stations (T08-11 renamed T8-11 among " +
            "others); set by hand for the finalised map. Whether the database bound 6, and to which content, is read on " +
            "factory01 before the reinstall (control-server#614 review M1)"),
    ];

    /// <summary>
    /// The one instance every definition under <c>scripts/parallel/</c> installs, and so the one database
    /// <see cref="History"/> describes. A definition for another instance needs its own history.
    /// </summary>
    private const string InstanceId = "factory01-v2-parallel";

    private const string InstanceDataRoot = @"C:\ProgramData\8005\ControlServer.V2";

    /// <summary>
    /// The protocol's task types this build deliberately does not execute. Empty since batch 10; a task type that is
    /// in neither this list nor <see cref="ExecutableTaskTypes"/> is one nobody has decided about.
    /// </summary>
    private static readonly string[] DeliberatelyNotExecutable = [];

    private static (long Version, BumpKind Kind, string[]? ExecutableTaskTypes, string Why) LastExecutableSet =>
        History.Last(row => row.Kind == BumpKind.ExecutableSet);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheHistoryMovesStrictlyUpward()
    {
        long[] versions = [.. History.Select(row => row.Version)];

        Assert.True(
            versions.Zip(versions.Skip(1)).All(pair => pair.Second > pair.First),
            $"The history's versions are {string.Join(", ", versions)}; each bump, of whatever kind, must be above the one " +
            "before: the instance has bound every one of them, and the store refuses a version below the highest it bound.");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void OnlyExecutableSetRowsCarryASet()
    {
        Assert.All(
            History,
            row => Assert.True(
                (row.Kind == BumpKind.ExecutableSet) == (row.ExecutableTaskTypes is not null),
                $"Version {row.Version} is a {row.Kind} row; only ExecutableSet rows carry the executable task types."));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheExecutableSetThisBuildShipsIsTheLastExecutableSetRow()
    {
        string[] last = LastExecutableSet.ExecutableTaskTypes!;

        Assert.True(
            ExecutableTaskTypes.All.SetEquals(last),
            $"The executable task types are now {Describe(ExecutableTaskTypes.All)}, but the history's last executable set " +
            $"is {Describe(last)}. Changing them changes the admission seed: add a new last ExecutableSet row with a version " +
            $"above {History[^1].Version} (the last version of any kind), and set admissionPolicyVersion to it in " +
            "appsettings.json and every instance definition. Going back to an earlier set is a change too -- an instance " +
            "that bound the later one would read the earlier one as drift.");
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
            "it executable, which is a new last row of the history and a version bump.");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void ThePackageShipsTheLastExecutableSetVersion()
    {
        (string File, JourneyRuntimeValues Values) package = Package();

        Assert.True(
            package.Values.Version == LastExecutableSet.Version,
            $"{package.File} ships admissionPolicyVersion {package.Values.Version}; the history's last executable set " +
            $"is version {LastExecutableSet.Version}. The package's default moves with the executable set, and only with it: " +
            "station-name and new-database bumps belong to one instance's database, not to the package.");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void EveryInstanceDefinitionShipsTheLastVersionOfTheHistory()
    {
        // Equal, not at-or-above: a definition raised without a new row would leave the history behind what the
        // instance has bound, and the next bump would be read off a table that is already wrong (review S2).
        Assert.All(
            Definitions(),
            definition => Assert.True(
                definition.Values.Version == History[^1].Version,
                $"{definition.File} installs {InstanceId} under admissionPolicyVersion {definition.Values.Version}, but the " +
                $"history's last version is {History[^1].Version} ({History[^1].Why}). Below it the version moves backwards " +
                "and the server takes on no demand; above it, add the row that says why."));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void EveryInstanceDefinitionInstallsTheInstanceTheHistoryDescribes()
    {
        Assert.All(
            Definitions(),
            definition => Assert.True(
                definition.Values.InstanceId == InstanceId && definition.Values.DataRoot == InstanceDataRoot,
                $"{definition.File} installs instance '{definition.Values.InstanceId}' with data root " +
                $"'{definition.Values.DataRoot}'; the history describes {InstanceId} at {InstanceDataRoot}. Another instance " +
                "has another database and needs its own history."));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheInstanceDefinitionsAgreeOnTheDeploymentIdentity()
    {
        // The store binds the version together with admissionPolicyDeploymentId; changing the identity alone is drift.
        (string File, JourneyRuntimeValues Values)[] definitions = Definitions();

        Assert.True(
            definitions.Select(item => item.Values.DeploymentId).Distinct(StringComparer.Ordinal).Count() == 1,
            "The instance definitions' admissionPolicyDeploymentId differs: " +
            string.Join(", ", definitions.Select(item => $"{item.File} = {item.Values.DeploymentId}")) +
            ". They install the same database under the same version, so a different identity there is drift.");
    }

    private sealed record JourneyRuntimeValues(long Version, string DeploymentId, string? InstanceId, string? DataRoot);

    private static (string File, JourneyRuntimeValues Values) Package() =>
        Read(FindRepositoryRoot(), "src/ControlServer.Host/appsettings.json", "JourneyRuntime");

    /// <summary>
    /// Every v2 instance definition, by pattern. At least the two factory01 definitions must be found, so a renamed
    /// directory or pattern cannot leave the guard checking nothing.
    /// </summary>
    private static (string File, JourneyRuntimeValues Values)[] Definitions()
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
        return [.. definitions.Select(file => Read(root, file, "journeyRuntime"))];
    }

    private static (string File, JourneyRuntimeValues Values) Read(string root, string file, string section)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, file)));
        JsonElement top = document.RootElement;
        JsonElement journeyRuntime = top.GetProperty(section);
        return (file, new JourneyRuntimeValues(
            journeyRuntime.GetProperty("admissionPolicyVersion").GetInt64(),
            journeyRuntime.GetProperty("admissionPolicyDeploymentId").GetString() ?? "",
            top.TryGetProperty("instanceId", out JsonElement instanceId) ? instanceId.GetString() : null,
            top.TryGetProperty("dataRoot", out JsonElement dataRoot) ? dataRoot.GetString() : null));
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
