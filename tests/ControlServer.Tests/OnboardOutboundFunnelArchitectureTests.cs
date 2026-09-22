using System.Reflection;
using System.Text.RegularExpressions;
using ControlServer.Application;
using ControlServer.Host.Transport;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on how server-originated traffic reaches a vehicle's socket: through
/// <see cref="OnboardPeer"/> and nowhere else, so the handshake gate on that class gates every sender
/// (control-server#259).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a guard and not one more check per sender.</b> Before control-server#259 the danger of pushing
/// into a handshake was known and guarded in exactly one place, <c>AppendSafetySnapshotRequest</c>, while
/// the engine's plan and command pushes and the recovery sends went out unguarded. Adding the same check to
/// each of them would have been the same shape again: the next sender would have to remember it. The gate
/// sits in <see cref="OnboardPeer"/>'s routing table instead -- a connection is filed there only once its
/// handshake is done -- and what keeps that sufficient is that nothing reaches a socket another way. That is
/// what these tests pin.
/// </para>
/// <para>
/// There are exactly two ways onto the socket, and both are on the ledger below: <see cref="OnboardPeer"/>
/// routing a push, and the listener writing the answer to the line it just read. The answer needs no gate --
/// the vehicle is waiting for exactly that line.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait</b>, for the reason <see cref="RiotCallAllowlistArchitectureTests"/>
/// gives: a cross-cutting guard hung off a slice goes unguarded whenever that slice is deferred.
/// </para>
/// </remarks>
public sealed class OnboardOutboundFunnelArchitectureTests
{
    private const string Connection = "OnboardPeerConnection";
    private const string Listener = "src/ControlServer.Host/Transport/OnboardTcpServer.cs";

    /// <summary>The product files that name the socket wrapper, and what each one does with it.</summary>
    private static readonly SortedDictionary<string, string> FileLedger = new(StringComparer.Ordinal)
    {
        ["src/ControlServer.Host/Transport/OnboardPeer.cs"] =
            "declares it, and routes a push to one only once that connection's handshake is done",
        [Listener] =
            "creates one per socket, writes the answer to the line it just read, and attaches it once the handshake is done"
    };

    [Fact]
    public void OnboardPeerIsTheOnlyImplementationOfTheOutboundPort()
    {
        string[] implementations = [.. ProductAssemblies()
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IOnboardPeer).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)];

        Assert.Equal([typeof(OnboardPeer).FullName!], implementations);
    }

    [Fact]
    public void EveryProductFileThatNamesTheSocketWrapperIsOnTheLedger()
    {
        string[] naming = [.. ProductSourceFiles()
            .Where(file => File.ReadAllText(file).Contains(Connection, StringComparison.Ordinal))
            .Select(Relative)];

        Assert.Equal(FileLedger.Keys, naming);
    }

    /// <summary>
    /// Inside the listener, the socket is written once -- the answer -- and the connection is made routable
    /// once, at the point the handshake is done.
    /// </summary>
    /// <remarks>
    /// Counted as text, like the rest of this class, so a second write or a second attach added anywhere in the
    /// file is caught whichever branch it is in. The count of attaches is taken over every product file: a second
    /// one elsewhere would be a second, ungated moment of becoming routable.
    /// </remarks>
    [Fact]
    public void TheListenerWritesOnlyTheAnswerAndAttachesInOnePlace()
    {
        string listener = File.ReadAllText(Path.Combine(RepositoryRoot(), Listener));
        Assert.Equal(1, Regex.Count(listener, @"\bconnection\.SendAsync\("));

        string[] attaching = [.. ProductSourceFiles()
            .SelectMany(file => Enumerable.Repeat(Relative(file), Regex.Count(File.ReadAllText(file), @"\.Attach\(")))];
        Assert.Equal([Listener], attaching);
    }

    /// <summary>
    /// The connection becomes routable after the answer is written and before the deferred flush.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this one is pinned as text.</b> Both ends of the order are load-bearing, and only one of them can
    /// be caught at runtime. Attaching after the flush leaves the recovery report's replay with no route, which
    /// <c>OnboardHandshakePushGateTests.ARecoveryPushHeldBackDuringTheHandshakeReachesTheVehicleAfterIt</c>
    /// catches. Attaching before the answer is written opens a window between the processor setting
    /// <c>HandshakeCompleted</c> and the recovery report's DurableAck and SessionReadiness going out, in which a
    /// push would be read in place of them -- but that window lies inside one iteration of the read loop, with
    /// nothing a test can hold open, so a test would only ever catch it by luck.
    /// </para>
    /// <para>
    /// If this red names a refactor that kept the order by other means, move the check with it rather than
    /// deleting it: the order is the fix.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheListenerAttachesAfterWritingTheAnswerAndBeforeTheDeferredFlush()
    {
        string listener = File.ReadAllText(Path.Combine(RepositoryRoot(), Listener));
        int answer = listener.IndexOf("connection.SendAsync(", StringComparison.Ordinal);
        int attach = listener.IndexOf("_peer.Attach(", StringComparison.Ordinal);
        int flush = listener.IndexOf("FlushDeferredOutboundAsync(", StringComparison.Ordinal);

        Assert.True(answer >= 0 && attach >= 0 && flush >= 0, "A landmark of the read loop is gone.");
        Assert.True(answer < attach, "The connection is made routable before the answer to the line is written.");
        Assert.True(attach < flush, "The connection is made routable only after the deferred flush.");
    }

    private static IEnumerable<Assembly> ProductAssemblies() =>
    [
        typeof(OnboardPeer).Assembly,
        typeof(IOnboardPeer).Assembly,
        typeof(ControlServer.Infrastructure.Persistence.WireToGateStore).Assembly,
        typeof(ControlServer.Domain.ProtocolEnvelope).Assembly
    ];

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.OfType<Type>();
        }
    }

    /// <summary>Every product source file under <c>src/</c> and <c>tools/</c>, with build output left out.</summary>
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

    private static string RepositoryRoot() => ProtocolIdentityArchitectureTests.RepositoryRoot();
}
