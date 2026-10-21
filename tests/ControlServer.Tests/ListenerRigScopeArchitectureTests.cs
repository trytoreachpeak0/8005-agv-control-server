using System.Text.RegularExpressions;

namespace ControlServer.Tests;

/// <summary>
/// No test registers one existing instance to be handed out per scope -- <c>AddScoped(_ =&gt; instance)</c> and its
/// <c>AddTransient</c>/<c>AddSingleton</c> forms -- unless the ledger below says why that instance is safe to share
/// (control-server#836).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Six listener rigs registered one <c>OnboardMessageProcessor</c> that way, so every connection the
/// listener served shared one processor and one <c>DbContext</c>. A reconnect put the old connection's last line and the
/// new one's <c>SessionHello</c> on that context at once; EF threw "A second operation was started on this context
/// instance" and the server ended the new connection with 1003 (CI run 39545289891). The failure is a timing
/// coincidence, red a few times in a hundred runs under load, so a rig written back to that shape would not go red on
/// its own behaviour. <see cref="TestOnboardProcessorFactory.AddScopedProcessor"/> is the shape to use instead.
/// </para>
/// <para>
/// <b>What it cannot see, by construction.</b> It reads text. A lambda returning a bare name or member access is what
/// it matches; a captured instance behind a method call, a <c>??</c>, a cast, a block-bodied lambda, or a plain
/// <c>AddSingleton(instance)</c> passes it. It is a regression guard against the one shape this defect had, not a proof.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait</b>, for the reason <see cref="RiotCallAllowlistArchitectureTests"/> gives: a
/// cross-cutting guard hung off a slice goes unguarded whenever that slice is deferred.
/// </para>
/// </remarks>
public sealed class ListenerRigScopeArchitectureTests
{
    private static readonly Regex SharedInstancePerScope = new(
        @"\.Add(?:Scoped|Transient|Singleton)(?:<[^>()]+>)?\(\s*\w+\s*=>\s*(?<instance>[A-Za-z_][\w.]*)\s*\)",
        RegexOptions.CultureInvariant);

    /// <summary>File name and the instance expression, each with why sharing it between scopes is safe.</summary>
    private static readonly Dictionary<string, string> Ledger = new(StringComparer.Ordinal)
    {
        ["IdleReturnCommitmentTests.cs: TestChargingPolicies.AllApproved"] =
            "a fixed in-memory charging policy resolver; no DbContext, nothing mutable",
        ["RouteGraphRefreshTimeoutTests.cs: runtime.Engine"] =
            "one runtime worker round (poll interval one hour, the test stops the worker before a second); no listener, " +
            "nothing else resolves the engine while the round runs",
        ["StagingPickupNoCountdownTests.cs: fixture.ChargingPolicy"] =
            "a charging policy resolver over in-memory versions (TestChargingPolicies); no DbContext"
    };

    [Fact]
    public void NoTestHandsOneExistingInstanceToEveryScopeOutsideTheLedger()
    {
        string root = RepositoryRoot();
        string[] found = [.. Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => SharedInstancePerScope.Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetFileName(file)}: {match.Groups["instance"].Value}"))
            .Order(StringComparer.Ordinal)];

        string[] unexplained = [.. found.Where(site => !Ledger.ContainsKey(site))];
        Assert.True(
            unexplained.Length == 0,
            "One instance registered for every scope; use TestOnboardProcessorFactory.AddScopedProcessor or a factory " +
            "that builds per scope, or say in the ledger why sharing it is safe: " + string.Join(", ", unexplained));

        // A ledger line whose site is gone would let the shape come back there unremarked.
        Assert.Equal(Ledger.Keys.Order(StringComparer.Ordinal), found.Distinct(StringComparer.Ordinal));
    }

    private static bool IsBuildOutput(string file)
    {
        string normalized = file.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the ControlServer repository root.");
    }
}
