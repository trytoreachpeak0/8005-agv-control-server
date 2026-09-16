using System.Text.Json;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the message surface: every one of the sixty-three message types protocol v2
/// froze is either named in <c>src/</c> or pinned here as unimplemented with its reason, and none of
/// the eleven denylisted types has come back.
/// </summary>
/// <remarks>
/// <para>
/// "The server implements the message surface" was, like "all thirty-one vectors are tested", a
/// sentence somebody counted by hand. v2 took the surface from 54 messages to 63, and on the day
/// the identity switched, twelve of the sixty-three appeared nowhere in <c>src/</c> -- nine of them
/// the messages v2 added, three of them types that had been unimplemented since v1 and had ridden
/// through eight green gates. Nothing said so, because nothing was comparing the two lists.
/// </para>
/// <para>
/// <b>The list is not copied here.</b> <c>vendor/8005-agv-protocol/manifest/release.json</c> is the
/// protocol's own manifest byte for byte, and
/// <see cref="ProtocolIdentityArchitectureTests.TheVendoredManifestIsTheProtocolManifestByteForByte"/>
/// binds it to <c>ProtocolCandidateIdentity.ManifestSha256</c> -- the digest the server already puts
/// on every envelope. So the message table arrives with the same guarantee the identity has, and no
/// second approved hash was invented to hold it.
/// </para>
/// <para>
/// <b>A message counts as named when <c>src/</c> contains its type as a quoted string.</b> That is
/// how the server refers to a message type at all: <c>OnboardMessageProcessor</c> dispatches on
/// <c>case "OperationResult":</c>, publishers serialise <c>messageType</c> literals. Matching the
/// bare word instead would count <c>UnloadCommandMessageId</c>, an EF column, as an implementation
/// of the denylisted <c>UnloadCommand</c> -- which is exactly the false green this class exists to
/// avoid.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster</b>, for the reason
/// <see cref="ProtocolVectorTestBindingArchitectureTests"/> gives: a cross-cutting guard hung off a
/// slice goes dark for as long as that slice is deferred.
/// </para>
/// </remarks>
public sealed class ProtocolMessageSurfaceArchitectureTests
{
    /// <summary>
    /// The frozen message types with no implementation in <c>src/</c>, each with why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two kinds of entry, and they are not the same kind of debt.</b> Nine are the messages v2
    /// added, and each belongs to a slice section 7.2 of the second edition of the scope specification
    /// schedules into a later batch; those empty as their batches land, exactly like
    /// <see cref="ProtocolVectorTestBindingArchitectureTests.VectorsAwaitingTheirSlice"/>. Three
    /// predate v2 and are pinned to what the server does instead -- they are findings, not
    /// schedules, and the note on each is the thing to argue with.
    /// </para>
    /// <para>
    /// Each of the three was checked rather than assumed on 2026-09-08.
    /// <c>SublotRejected</c>: <c>OnboardMessageProcessor</c> answers <c>SublotSubmitted</c> with an
    /// unconditional <c>DurableAck</c>, so no code path can reject a sublot and the response type
    /// has no emitter. <c>CapabilitySnapshotRequested</c> and <c>SafetyStateSnapshotRequested</c>:
    /// the server never asks for a snapshot again; it refuses readiness instead, via
    /// <c>WireToGateStore</c> raising <c>CAPABILITY_SNAPSHOT_REQUIRED</c> and
    /// <c>SAFETY_SNAPSHOT_REQUIRED</c>, which <c>ProtocolErrorCodes.ToSessionReadinessReasonCode</c>
    /// puts on the wire as <c>CAPABILITY_VERSION_GAP</c> and <c>SAFETY_STATE_VERSION_GAP</c>.
    /// </para>
    /// <para>
    /// <b>Pinning is not waiving.</b> The comparison is exact in both directions: implementing a
    /// message without deleting its line here fails, and a message quietly losing its last mention
    /// in <c>src/</c> fails too. <b>Keep the field when it empties</b> -- empty is itself the
    /// assertion.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> MessagesWithoutAnImplementation =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["CapabilitySnapshotRequested"] =
                "predates v2; the server refuses readiness with CAPABILITY_VERSION_GAP instead of asking again",
            ["DemandSelectionRequested"] = "FP-IS-09, batch 11",
            ["DemandSelectionResult"] = "FP-IS-09, batch 11",
            ["ManualStationClearanceConfirmationRequested"] = "FP-IS-13, batch 9",
            ["ManualStationClearanceConfirmationResult"] = "FP-IS-13, batch 9",
            ["SafetyStateSnapshotRequested"] =
                "predates v2; the server refuses readiness with SAFETY_STATE_VERSION_GAP instead of asking again",
            ["SublotRejected"] =
                "predates v2; SublotSubmitted is answered with an unconditional DurableAck, so nothing emits it",
            ["UnableToChargeFieldConfirmationRequested"] = "FP-IS-13, batch 9",
            ["UnableToChargeFieldConfirmationResult"] = "FP-IS-13, batch 9"
        };

    /// <summary>
    /// The parse of the manifest, checked against the shape v2 froze.
    /// </summary>
    /// <remarks>
    /// Without this, every assertion below could pass over an empty parse: an empty message table
    /// makes "no denylisted type is implemented" trivially true and turns the pinned set into a list
    /// of names nothing compares. The two counts differ from each other, so a parse that read the
    /// wrong property is reported here.
    /// </remarks>
    [Fact]
    public void TheManifestParsesIntoSixtyThreeMessagesAndElevenDenylistedTypes()
    {
        Assert.Equal(63, FrozenMessageTypes().Length);
        Assert.Equal(11, DenylistedMessageTypes().Length);
        Assert.Empty(FrozenMessageTypes().Intersect(DenylistedMessageTypes(), StringComparer.Ordinal));
    }

    /// <summary>
    /// Every frozen message type is implemented or pinned, and the comparison fails both ways.
    /// </summary>
    [Fact]
    public void EveryFrozenMessageTypeIsNamedInTheServerOrPinnedAsUnimplemented()
    {
        string source = ServerSource();

        string[] unimplemented =
        [
            .. FrozenMessageTypes().Where(type => !IsNamedIn(source, type)).Order(StringComparer.Ordinal)
        ];
        string[] unimplementedAndUnpinned =
        [
            .. unimplemented
                .Except(MessagesWithoutAnImplementation.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
        string[] pinnedInVain =
        [
            .. MessagesWithoutAnImplementation.Keys
                .Except(unimplemented, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            unimplementedAndUnpinned.Length == 0,
            "These frozen message types are named nowhere in src/ and are not pinned. Either the "
            + "implementation was deleted, or the surface grew and nobody followed it: "
            + string.Join(", ", unimplementedAndUnpinned));

        Assert.True(
            pinnedInVain.Length == 0,
            "These message types are pinned as unimplemented but src/ now names them -- delete "
            + "their line from " + nameof(MessagesWithoutAnImplementation) + ", or the pinned name "
            + "is not a frozen message type at all and is misspelled: "
            + string.Join(", ", pinnedInVain));
    }

    /// <summary>
    /// None of the eleven types v2 denylisted is spoken by this server.
    /// </summary>
    /// <remarks>
    /// The denylist is the set of v1 message types the profile removed rather than renamed. A
    /// server that still emits one is not speaking a slightly older protocol; it is speaking a
    /// message its peer will refuse with <c>PROFILE_MESSAGE_NOT_ALLOWED</c>.
    /// </remarks>
    [Fact]
    public void NoDenylistedMessageTypeIsNamedInTheServer()
    {
        string source = ServerSource();

        string[] resurrected =
        [
            .. DenylistedMessageTypes().Where(type => IsNamedIn(source, type)).Order(StringComparer.Ordinal)
        ];

        Assert.True(
            resurrected.Length == 0,
            "src/ names message types the profile denylisted: " + string.Join(", ", resurrected));
    }

    /// <summary>
    /// Proves the implementation check is not vacuous, by running it over a source text that lost a
    /// message the server really does speak.
    /// </summary>
    /// <remarks>
    /// A real message rather than a fabricated one: <c>OperationResult</c> is dispatched by name
    /// today, so removing its literal reproduces exactly what deleting that handler would do. The
    /// second assertion is the load-bearing half -- it says the message is reported <i>because</i>
    /// nothing names it, not because somebody had pinned it.
    /// </remarks>
    [Fact]
    public void TheImplementationCheckReportsAMessageThatLosesItsLastMention()
    {
        const string message = "OperationResult";
        string source = ServerSource();
        Assert.True(IsNamedIn(source, message), "Precondition: src/ names " + message + ".");
        Assert.DoesNotContain(message, MessagesWithoutAnImplementation.Keys, StringComparer.Ordinal);

        string without = source.Replace($"\"{message}\"", "\"MessageTypeThatDoesNotExist\"", StringComparison.Ordinal);

        Assert.False(IsNamedIn(without, message));
    }

    /// <summary>
    /// A message type counts as named when the source contains it as a quoted string.
    /// </summary>
    private static bool IsNamedIn(string source, string messageType) =>
        source.Contains($"\"{messageType}\"", StringComparison.Ordinal);

    /// <summary>
    /// Read once. The manifest is 474 KB and <c>src/</c> is the whole server; parsing and reading
    /// them per assertion made four passes over both in a single test class for no gain.
    /// </summary>
    private static readonly Lazy<string> Source = new(ReadServerSource);

    private static readonly Lazy<string[]> FrozenMessages =
        new(() => ManifestNames("messages", element => element.EnumerateObject().Select(m => m.Name)));

    private static readonly Lazy<string[]> Denylisted =
        new(() => ManifestNames(
            "denylistedMessageTypes", element => element.EnumerateArray().Select(t => t.GetString()!)));

    private static string ServerSource() => Source.Value;

    private static string ReadServerSource()
    {
        string sourceRoot = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src");
        string[] files =
        [
            .. Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
        ];

        Assert.NotEmpty(files);

        return string.Join('\n', files.Select(File.ReadAllText));
    }

    private static string[] FrozenMessageTypes() => FrozenMessages.Value;

    private static string[] DenylistedMessageTypes() => Denylisted.Value;

    private static string[] ManifestNames(
        string property,
        Func<JsonElement, IEnumerable<string>> read)
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(ProtocolIdentityArchitectureTests.ManifestPath()));

        return [.. read(manifest.RootElement.GetProperty(property)).Order(StringComparer.Ordinal)];
    }
}
