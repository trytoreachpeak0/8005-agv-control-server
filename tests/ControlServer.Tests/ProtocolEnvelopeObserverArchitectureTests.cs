namespace ControlServer.Tests;

/// <summary>
/// The machine guard on <c>ProtocolEnvelope.OutboundObserver</c>: no product code assigns it, and the
/// product files that name it at all are on a ledger (control-server#85).
/// </summary>
/// <remarks>
/// <para>
/// The observer is how the test host sees every line this side sends. It is deliberately a hook and
/// not a gate: production only null-tests it, because a vehicle-facing server has no good answer to a
/// non-conforming line at the moment it is about to go out. The day product code assigns it, that
/// stops being true -- some sender would be deciding what the test host is allowed to see, and the
/// gate would silently measure a subset of the traffic.
/// </para>
/// <para>
/// The file ledger is the second half, and it is what keeps the first from being vacuous: a new
/// sender that builds a line by hand and hands it over by hand would otherwise appear here with
/// nothing to argue with. <c>OnboardJourneyPublisher.ReplayPendingForSessionAsync</c> is the one
/// legal second site and the ledger says why.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster</b>, for the reason
/// <see cref="RiotCallAllowlistArchitectureTests"/> gives: this is a cross-cutting guard rather than
/// evidence for a slice, and hanging it off a slice would leave it unguarded whenever that slice is
/// deferred.
/// </para>
/// </remarks>
public sealed class ProtocolEnvelopeObserverArchitectureTests
{
    private const string Observer = "OutboundObserver";

    /// <summary>
    /// The product files that name the observer, and what each one does with it.
    /// </summary>
    private static readonly SortedDictionary<string, string> FileLedger = new(StringComparer.Ordinal)
    {
        ["src/ControlServer.Domain/ProtocolEnvelope.cs"] = "declares the hook and invokes it once, in Serialize",
        ["src/ControlServer.Host/Transport/OnboardJourneyPublisher.cs"] = "ReplayPendingForSessionAsync rewrites a stored envelope through JsonNode instead of building it from fields, so that one line hands itself over by hand"
    };

    [Fact]
    public void NoProductCodeAssignsTheOutboundObserver()
    {
        string[] assignments = [.. ProductSourceFiles()
            .Where(file => Assigns(File.ReadAllText(file)))
            .Select(Relative)];
        Assert.Empty(assignments);

        static bool Assigns(string source)
        {
            int index = source.IndexOf(Observer, StringComparison.Ordinal);
            while (index >= 0)
            {
                int after = index + Observer.Length;
                // '=' but not '==', and not the 'property { get; set; }' declaration, which has no '='
                // after the name at all.
                if (after < source.Length && source[after] == '=')
                {
                    return true;
                }
                index = source.IndexOf(Observer, after, StringComparison.Ordinal);
            }
            return false;
        }
    }

    [Fact]
    public void EveryProductFileThatNamesTheObserverIsOnTheLedger()
    {
        string[] naming = [.. ProductSourceFiles()
            .Where(file => File.ReadAllText(file).Contains(Observer, StringComparison.Ordinal))
            .Select(Relative)];

        Assert.Equal(FileLedger.Keys, naming);
    }

    /// <summary>
    /// Every product source file under <c>src/</c> and <c>tools/</c>, with build output left out.
    /// </summary>
    private static IEnumerable<string> ProductSourceFiles()
    {
        string root = RepositoryRoot();
        string[] roots = ["src", "tools"];
        return roots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(RepositoryRoot(), file).Replace(Path.DirectorySeparatorChar, '/');

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No ControlServer.sln above " + AppContext.BaseDirectory);
    }
}
