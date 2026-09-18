using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the shared slot-configuration version line: every writer that freezes a
/// version of <see cref="GovernedObjectKind.ActiveSlotConfiguration"/> allocates its version number
/// through <see cref="SlotConfigurationVersionLine"/>, and no fourth writer can appear without this
/// going red.
/// </summary>
/// <remarks>
/// <para>
/// A fourth call site numbering its own versions is exactly how the defect fixed in
/// <c>docs/defects/20260916-binding-publication-and-activation-could-take-the-same-version.md</c>
/// came about in the first place: three writers each held their own copy of "what is the next
/// version", the three copies counted different things, and two of them could therefore hand out the
/// same number. Collapsing them into one allocator fixed the three that existed. Nothing stopped a
/// fourth, and nothing reported one -- a collision is silent by construction, because the path that
/// produces it contains no INSERT for a unique index to refuse.
/// </para>
/// <para>
/// <b>Two legs, because a new writer can arrive in two shapes.</b>
/// <see cref="EverySourceFileThatNamesTheSlotConfigurationLineIsOnTheLedger"/> reads source text and
/// catches the shape a person actually writes -- a file that names the governed kind. It is
/// deliberately the coarser of the two: naming the kind is the first thing any new writer does, and
/// a file-level ledger is something a reviewer can read.
/// <see cref="EveryWriterThatFreezesAGovernedVersionIsOnTheLedger"/> reads compiled IL and catches
/// the shape a refactor produces -- a type that freezes a version through some helper without ever
/// naming the kind itself. Neither leg subsumes the other.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster</b>, for the reason
/// <see cref="RiotCallAllowlistArchitectureTests"/> gives: this is a cross-cutting guard rather than
/// evidence for a slice, and hanging it off <c>FP-C7</c> would leave the version line unguarded for
/// as long as that cluster is deferred.
/// </para>
/// </remarks>
public sealed class SlotConfigurationVersionLineArchitectureTests
{
    /// <summary>
    /// The assemblies this guard covers: everything built out of <c>src/</c>, plus
    /// <c>ControlServer.FieldOps</c>.
    /// </summary>
    /// <remarks>
    /// <c>ControlServer.FieldOps</c> is in <c>tools/</c>, which
    /// <see cref="RiotCallAllowlistArchitectureTests"/> excludes as "not product code". That
    /// judgement does not carry here: FieldOps is the <i>second process</i> writing this database --
    /// its <c>bind-io</c> command publishes IO bindings on this very version line - and the whole
    /// reason the line needs one allocator is that two processes share it. Leaving it out would
    /// leave out half the problem. The other tools under <c>tools/</c> are doubles and stay out.
    /// </remarks>
    private static readonly string[] ScannedAssemblies =
    [
        "ControlServer.Domain",
        "ControlServer.Application",
        "ControlServer.Infrastructure",
        "ControlServer.Host",
        "ControlServer.Dashboard",
        "ControlServer.FieldOps"
    ];

    /// <summary>
    /// Every product source file that names <c>GovernedObjectKind.ActiveSlotConfiguration</c>, and
    /// what it does with the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default-deny, like the RIoT allowlist: a file that is not here fails the test rather than
    /// being assumed harmless. The value is the claim a reviewer argues with -- "reads only" means
    /// the file never allocates a version on this line, and the moment that stops being true the
    /// entry has to change.
    /// </para>
    /// <para>
    /// Writing audit about the line is not allocating on it. The three "audit only" entries pass a
    /// version somebody else already allocated (or none at all), and an audit record is not a frozen
    /// snapshot -- it cannot collide with one.
    /// </para>
    /// </remarks>
    private static readonly SortedDictionary<string, string> LineLedger =
        new(StringComparer.Ordinal)
        {
            ["src/ControlServer.Host/Transport/SlotConfigurationActivationDispatcher.cs"] =
                "audit only: records a fingerprint mismatch a vehicle reported, against the version already active",
            ["src/ControlServer.Infrastructure/Persistence/GovernedActivationStore.cs"] =
                "writer: rollback, allocates through SlotConfigurationVersionLine",
            ["src/ControlServer.Infrastructure/Persistence/SlotConfigurationActivationCoordinator.cs"] =
                "writer: activation, allocates through SlotConfigurationVersionLine",
            ["src/ControlServer.Infrastructure/Persistence/SlotConfigurationAuthorityStore.cs"] =
                "writer: IO binding publication, allocates through SlotConfigurationVersionLine",
            ["src/ControlServer.Infrastructure/Persistence/SlotConfigurationBindingSnapshotAudit.cs"] =
                "reads only: the read-only diagnostic that compares published binding rows against their snapshots",
            ["src/ControlServer.Infrastructure/Persistence/SlotConfigurationReadinessGate.cs"] =
                "audit only: per-slot verification and release records, no version of its own",
            ["src/ControlServer.Infrastructure/Persistence/SlotConfigurationVersionLine.cs"] =
                "the allocator itself: the one definition of the objectId, of the next version, and of how a version lands",
            ["tools/ControlServer.FieldOps/Program.cs"] =
                "audit only: enable-gate records the fleet-wide gate switch, which carries no version"
        };

    /// <summary>
    /// Every product type that freezes a version of a governed object, and which line it writes on.
    /// </summary>
    /// <remarks>
    /// Freezing is the act that takes a version number, so this is the complete set of places a
    /// version number is consumed. The three entries marked as the slot-configuration line are the
    /// ones <see cref="EveryWriterOnTheSlotConfigurationLineAllocatesThroughTheVersionLine"/> then
    /// holds to the allocator; the other two are here so that the set is closed rather than
    /// filtered, which is what makes a fourth writer visible.
    /// </remarks>
    private static readonly SortedDictionary<string, LineRole> SnapshotWriters =
        new(StringComparer.Ordinal)
        {
            ["ControlServer.Application.GovernedConfigurationPublisher"] = LineRole.TheMechanismItself,
            ["ControlServer.Infrastructure.Persistence.AreaAssignmentStore"] = LineRole.AnotherLine,
            ["ControlServer.Infrastructure.Persistence.GovernedActivationStore"] = LineRole.SlotConfiguration,
            ["ControlServer.Infrastructure.Persistence.SlotConfigurationActivationCoordinator"] = LineRole.SlotConfiguration,
            ["ControlServer.Infrastructure.Persistence.SlotConfigurationAuthorityStore"] = LineRole.SlotConfiguration
        };

    private enum LineRole
    {
        /// <summary>Writes on the shared <c>ActiveSlotConfiguration</c> line.</summary>
        SlotConfiguration,

        /// <summary>Writes on a version line of its own, which it does not share with anybody.</summary>
        AnotherLine,

        /// <summary>The publisher itself: it freezes whatever version its caller allocated.</summary>
        TheMechanismItself
    }

    /// <summary>The two calls that freeze a version of a governed object.</summary>
    private static readonly HashSet<string> FreezingCalls = new(StringComparer.Ordinal)
    {
        $"{nameof(GovernedConfigurationPublisher)}.{nameof(GovernedConfigurationPublisher.PublishVersionAsync)}",
        $"{nameof(IConfigurationSnapshotStore)}.{nameof(IConfigurationSnapshotStore.FreezeAsync)}"
    };

    /// <summary>The allocator's two members a writer on this line has to go through.</summary>
    private static readonly HashSet<string> AllocatorCalls = new(StringComparer.Ordinal)
    {
        $"{nameof(SlotConfigurationVersionLine)}.{nameof(SlotConfigurationVersionLine.PublishNextVersionAsync)}",
        $"{nameof(SlotConfigurationVersionLine)}.get_{nameof(SlotConfigurationVersionLine.ObjectId)}"
    };

    /// <summary>
    /// No product source file names the shared line without the ledger accounting for it.
    /// </summary>
    [Fact]
    public void EverySourceFileThatNamesTheSlotConfigurationLineIsOnTheLedger()
    {
        string[] naming = SourceFilesNamingTheLine();

        Assert.Equal(LineLedger.Keys, naming);
    }

    /// <summary>
    /// No product type freezes a governed version without the ledger accounting for it.
    /// </summary>
    /// <remarks>
    /// This is the leg that a refactor cannot slip past. A new store that takes a version number of
    /// its own and hands it to the publisher never has to name the governed kind -- the kind can
    /// arrive as a parameter -- so the source scan above would not see it. Freezing, it cannot avoid.
    /// </remarks>
    [Fact]
    public void EveryWriterThatFreezesAGovernedVersionIsOnTheLedger()
    {
        string[] writers = [.. ScannedAssemblies.SelectMany(TypesThatCall(FreezingCalls)).Order(StringComparer.Ordinal)];

        Assert.True(
            writers.SequenceEqual(SnapshotWriters.Keys, StringComparer.Ordinal),
            "The set of product types that freeze a governed version has changed. Every one of them "
            + "consumes a version number, so each has to be accounted for -- and any of them on the "
            + "shared ActiveSlotConfiguration line has to allocate through SlotConfigurationVersionLine. "
            + $"Ledger: {string.Join(", ", SnapshotWriters.Keys)}. Found: {string.Join(", ", writers)}.");
    }

    /// <summary>
    /// Every writer on the shared line allocates through <see cref="SlotConfigurationVersionLine"/>,
    /// and every writer that is not on it does not.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. The first is the rule. The second keeps the ledger honest: a
    /// fourth writer added with <see cref="LineRole.AnotherLine"/> against the facts would have to
    /// stop using the allocator to stay green, and stopping is the thing this guard exists to
    /// notice.
    /// </remarks>
    [Fact]
    public void EveryWriterOnTheSlotConfigurationLineAllocatesThroughTheVersionLine()
    {
        HashSet<string> throughTheAllocator =
            [.. ScannedAssemblies.SelectMany(TypesThatCall(AllocatorCalls))];

        string[] shouldAndDoNot =
        [
            .. SnapshotWriters
                .Where(writer => writer.Value == LineRole.SlotConfiguration
                    && !throughTheAllocator.Contains(writer.Key))
                .Select(writer => writer.Key)
        ];
        string[] shouldNotButDo =
        [
            .. SnapshotWriters
                .Where(writer => writer.Value != LineRole.SlotConfiguration
                    && throughTheAllocator.Contains(writer.Key))
                .Select(writer => writer.Key)
        ];

        Assert.True(
            shouldAndDoNot.Length == 0,
            "These writers freeze versions of the shared ActiveSlotConfiguration line without "
            + "allocating through SlotConfigurationVersionLine, which is how two writers end up "
            + "holding the same version number: " + string.Join(", ", shouldAndDoNot));
        Assert.True(
            shouldNotButDo.Length == 0,
            "The ledger says these writers are not on the shared line, but they use its allocator; "
            + "one of the two is wrong: " + string.Join(", ", shouldNotButDo));
    }

    /// <summary>
    /// Proves the IL scan is not vacuous, by running it over an assembly that really does contain a
    /// writer numbering its own versions: this one.
    /// </summary>
    /// <remarks>
    /// <see cref="UnallocatedVersionWriterSample"/> is the fourth writer this guard exists to catch,
    /// written out in full and never called. Putting it in product code, even temporarily, would be
    /// the violation itself. A fabricated type name would prove less: what has to be shown is that
    /// the scan reports a type whose IL really contains the call.
    /// </remarks>
    [Fact]
    public void TheScanCatchesAWriterThatNumbersItsOwnVersions()
    {
        string[] writers = [.. TypesThatCall(FreezingCalls)("ControlServer.Tests")];
        string[] throughTheAllocator = [.. TypesThatCall(AllocatorCalls)("ControlServer.Tests")];

        string sample = typeof(UnallocatedVersionWriterSample).FullName!;
        Assert.Contains(sample, writers);
        Assert.DoesNotContain(sample, throughTheAllocator);
    }

    /// <summary>
    /// The pinned assembly list still covers everything <c>src/</c> builds.
    /// </summary>
    /// <remarks>
    /// A new project there would otherwise sit outside every assertion above, and nobody would learn
    /// that from a green run. <c>ControlServer.FieldOps</c> is named explicitly rather than
    /// discovered, because <c>tools/</c> holds doubles this guard has no business scanning.
    /// </remarks>
    [Fact]
    public void EverySourceProjectIsInTheScannedSet()
    {
        string[] projects =
        [
            .. Directory.GetDirectories(Path.Combine(RepositoryRoot(), "src"))
                .Select(directory => Path.GetFileName(directory)!)
                .Order(StringComparer.Ordinal)
        ];

        Assert.Empty(projects.Except(ScannedAssemblies, StringComparer.Ordinal));
        Assert.Contains("ControlServer.FieldOps", ScannedAssemblies);
    }

    /// <summary>The two top-level directories that hold product source.</summary>
    private static readonly string[] ProductSourceRoots = ["src", "tools"];

    /// <summary>
    /// The product source files under <c>src/</c> and <c>tools/</c> that name the governed kind,
    /// as repository-relative forward-slash paths.
    /// </summary>
    private static string[] SourceFilesNamingTheLine()
    {
        string root = RepositoryRoot();
        const string named = nameof(GovernedObjectKind) + "." + nameof(GovernedObjectKind.ActiveSlotConfiguration);

        return
        [
            .. ProductSourceRoots
                .SelectMany(top => Directory.EnumerateFiles(
                    Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
                // bin/ and obj/ hold generated copies and build output, not source.
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => File.ReadAllText(file).Contains(named, StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <remarks>
    /// Distinct, because one type reaches the same call from several compiler-generated nested types
    /// -- an async method and each of its lambdas gets a state machine of its own, and all of them
    /// report as the type that contains them.
    /// </remarks>
    private static Func<string, IEnumerable<string>> TypesThatCall(HashSet<string> members) =>
        assembly => CallingTypes(AssemblyPath(assembly), members).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The types in one assembly whose compiled bodies call any of <paramref name="members"/>,
    /// written as <c>Namespace.Type</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read off IL, not source text</b>, for the reason
    /// <see cref="RiotCallAllowlistArchitectureTests"/> gives: a comment that mentions a method is
    /// not a call to it, and a call that arrives through a local function, an iterator or an async
    /// lambda is still a call. The compiler moves all three into nested types, so every result is
    /// attributed to its outermost enclosing type -- which is the type a reviewer would name.
    /// </para>
    /// <para>
    /// <b>The scan looks for a <c>call</c>/<c>callvirt</c> opcode followed by a token it recognises</b>,
    /// rather than decoding every instruction. It cannot miss a call: a real one is exactly those
    /// five bytes. It could in principle report one that is not there, if an operand's bytes happened
    /// to spell the opcode and then one of the two or three tokens this scan knows -- which is about
    /// one chance in ten billion across an assembly this size, and would show up as a name nobody
    /// recognises in a failing ledger rather than as a silent pass.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> CallingTypes(string assemblyPath, HashSet<string> members)
    {
        using FileStream file = File.OpenRead(assemblyPath);
        using PEReader image = new(file);
        MetadataReader metadata = image.GetMetadataReader();
        HashSet<int> tokens = TokensFor(metadata, members);
        if (tokens.Count == 0)
        {
            yield break;
        }

        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            bool calls = false;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }
                if (CallsAnyToken(image.GetMethodBody(method.RelativeVirtualAddress).GetILContent(), tokens))
                {
                    calls = true;
                    break;
                }
            }
            if (calls)
            {
                yield return OutermostTypeName(metadata, handle);
            }
        }
    }

    /// <summary>
    /// The metadata tokens that stand for <paramref name="members"/>, written
    /// <c>DeclaringType.Member</c>.
    /// </summary>
    /// <remarks>
    /// Three tables, because a call reaches a member three ways: a <c>MemberRef</c> for anything in
    /// another assembly, a <c>MethodDef</c> for anything in this one, and a <c>MethodSpec</c> for a
    /// generic method's instantiation -- which is how
    /// <c>SlotConfigurationVersionLine.PublishNextVersionAsync&lt;T&gt;</c> is always called.
    /// </remarks>
    private static HashSet<int> TokensFor(MetadataReader metadata, HashSet<string> members)
    {
        HashSet<int> tokens = [];

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }
            TypeReference parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (members.Contains($"{metadata.GetString(parent.Name)}.{metadata.GetString(member.Name)}"))
            {
                tokens.Add(MetadataTokens.GetToken(handle));
            }
        }

        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            string typeName = metadata.GetString(type.Name);
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (members.Contains($"{typeName}.{metadata.GetString(method.Name)}"))
                {
                    tokens.Add(MetadataTokens.GetToken(methodHandle));
                }
            }
        }

        foreach (MethodSpecificationHandle handle in
            Enumerable.Range(1, metadata.GetTableRowCount(TableIndex.MethodSpec))
                .Select(row => MetadataTokens.MethodSpecificationHandle(row)))
        {
            if (tokens.Contains(MetadataTokens.GetToken(metadata.GetMethodSpecification(handle).Method)))
            {
                tokens.Add(MetadataTokens.GetToken(handle));
            }
        }

        return tokens;
    }

    private const byte Call = 0x28;
    private const byte CallVirt = 0x6F;

    private static bool CallsAnyToken(ImmutableArray<byte> il, HashSet<int> tokens)
    {
        for (int index = 0; index + 4 < il.Length; index++)
        {
            if (il[index] is not (Call or CallVirt))
            {
                continue;
            }
            int token = il[index + 1]
                | (il[index + 2] << 8)
                | (il[index + 3] << 16)
                | (il[index + 4] << 24);
            if (tokens.Contains(token))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The outermost enclosing type's <c>Namespace.Type</c>, so that a lambda's state machine is
    /// reported as the type that contains it.
    /// </summary>
    private static string OutermostTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        while (type.IsNested)
        {
            type = metadata.GetTypeDefinition(type.GetDeclaringType());
        }
        string name = metadata.GetString(type.Name);
        string space = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }

    private static string AssemblyPath(string assemblyName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        Assert.True(File.Exists(path), $"{assemblyName}.dll is not in the test output directory.");
        return path;
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

/// <summary>
/// The fourth writer on the slot-configuration version line, here so that
/// <see cref="SlotConfigurationVersionLineArchitectureTests.TheScanCatchesAWriterThatNumbersItsOwnVersions"/>
/// runs the scan over an assembly that really contains one.
/// </summary>
/// <remarks>
/// It is never called, and it is wrong on purpose: it builds the objectId itself, counts only
/// snapshots, and freezes on the number it computed -- the exact shape of the defect this guard
/// exists to prevent coming back. What matters is that the compiler emits the call into this test
/// assembly, which is the one thing the scan looks for in product code.
/// </remarks>
internal static class UnallocatedVersionWriterSample
{
    internal static Task<GovernedConfigurationSnapshot> PublishAtItsOwnVersionAsync(
        GovernedConfigurationPublisher publisher,
        string agvId,
        string slotModelVersionId,
        long highestVersionSeen,
        CancellationToken cancellationToken) =>
        publisher.PublishVersionAsync(
            GovernedObjectKind.ActiveSlotConfiguration,
            $"{agvId}:{slotModelVersionId}",
            highestVersionSeen + 1,
            "[]",
            "SLOT_IO_BINDING_VERSION_PUBLISHED",
            DateTimeOffset.UnixEpoch,
            cancellationToken);
}
