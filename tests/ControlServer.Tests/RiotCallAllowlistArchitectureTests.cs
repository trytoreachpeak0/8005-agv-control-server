using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the RIoT call allowlist: product code reaches RIoT only through named
/// Facade methods, and every method it calls is on the approved list.
/// </summary>
/// <remarks>
/// <para>
/// The allowlist was a document and nothing else. Compile time, run time and test time all had
/// nothing to say about it, so a <c>.Raw</c> escape or a call to an unapproved endpoint would
/// have shipped without anyone noticing. This class is the first thing that reads the list and
/// compares it against what the server actually does.
/// </para>
/// <para>
/// <b>The comparison is made against compiled IL, not against source text.</b> A text scan cannot
/// do this job: <c>src/</c> contains three comments that mention <c>.Raw</c> -- all three saying
/// not to use it -- so a naive grep opens with three false reds, and the word "session" appears
/// on some sixty lines that have nothing to do with RIoT. Reading the assembly reference tables
/// instead answers the actual question. A member reference into <c>RIoT.Sdk.Facade</c> is a call
/// the compiler emitted; a comment is not one.
/// </para>
/// <para>
/// <b>The list is not copied here.</b> <c>vendor/8005-agv-program/docs/riot-call-allowlist.md</c>
/// is the approved document byte for byte, and <see cref="ApprovedAllowlistSha256"/> pins it. The
/// document lives in another repository and CI checks out one repository, so a sibling directory
/// is not reachable from the headless runner -- the copy is how the list gets here at all, and the
/// hash is what keeps it from becoming a second, quietly diverging list. Refreshing it is
/// <c>vendor/8005-agv-program/README.md</c>.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster.</b> Every other test in this project
/// carries one; this one deliberately does not. It is a cross-cutting guard rather than evidence
/// for a slice, and hanging it off <c>FP-C4</c> would mean the whole RIoT call surface goes
/// unguarded for as long as that cluster is deferred.
/// </para>
/// </remarks>
public sealed class RiotCallAllowlistArchitectureTests
{
    /// <summary>
    /// SHA-256 of the approved allowlist document, over the file's bytes. Taken from
    /// <c>8005-agv-program</c> commit <c>3bc055c450c8a1d8d395d7d036042d1c890e383f</c> on
    /// 2026-09-08.
    /// </summary>
    private const string ApprovedAllowlistSha256 =
        "dec0bc1046f3f7c969ca53bd332708fe4668ded5370aa308c584305222d7bcee";

    private const string FacadeAssembly = "RIoT.Sdk.Facade";
    private const string GeneratedAssembly = "RIoT.Sdk.Generated";

    /// <summary>
    /// Everything built out of <c>src/</c>. <c>tools/</c> is out of scope on purpose -- the fake
    /// RIoT and the test doubles are not product code.
    /// </summary>
    /// <remarks>
    /// Pinned rather than discovered, and
    /// <see cref="EverySourceProjectIsInTheScannedSet"/> checks the pin against
    /// <c>src/</c> itself. A new project added there would otherwise sit outside every assertion
    /// below and nobody would learn that from a green run.
    /// </remarks>
    private static readonly string[] ProductAssemblies =
    [
        "ControlServer.Domain",
        "ControlServer.Application",
        "ControlServer.Infrastructure",
        "ControlServer.Host"
    ];

    /// <summary>
    /// The <c>RIoT.Sdk.Facade</c> members that are not calls to RIoT: constructing a session,
    /// navigating to one of its clients, disposing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subset check is default-deny, matching the allowlist itself -- anything referenced that
    /// is neither on this list nor on the approved list is reported. So this list holds what
    /// product code references today and nothing more: pre-listing members nobody uses would be
    /// waiving a check in advance for a call nobody has thought about yet. The first use of
    /// <c>get_Device</c> or <c>DisposeAsync</c> costs one line here, and that red is the intended
    /// moment for someone to look at what is reaching into the session.
    /// </para>
    /// <para>
    /// <b><c>get_Raw</c> will never belong here.</b> It is a property getter like
    /// <c>get_Maps</c>, so a rule of "ignore property accessors" would let the one escape hatch
    /// the allowlist actually forbids slip through the subset check. It fails here as well as in
    /// <see cref="ProductCodeReachesRiotOnlyThroughNamedFacades"/>.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> NavigationMembers = new(StringComparer.Ordinal)
    {
        ".ctor",
        "get_Maps",
        "get_Order",
        "get_Tasks"
    };

    /// <summary>
    /// An approved row: first cell exactly an HTTP method, second cell a backtick-quoted path.
    /// The document's section 4 states this format as a contract; the "not approved" table in
    /// section 2 writes its first cell as free text and so cannot match.
    /// </summary>
    private static readonly Regex ApprovedRow = new(
        @"^\|\s*(?:GET|POST|PUT|DELETE|PATCH)\s*\|(?<path>[^|]*)\|(?<facade>[^|]*)\|",
        RegexOptions.Compiled);

    private static readonly Regex QuotedPath = new(@"`(?<path>/[^`]*)`", RegexOptions.Compiled);

    /// <summary>
    /// A Facade method named in the third cell. The optional type prefix covers
    /// <c>RiotSession.LoginAsync</c>, written that way because the two auth calls hang off the
    /// session rather than off a client.
    /// </summary>
    private static readonly Regex QuotedFacadeMethod = new(
        @"`(?:[A-Za-z][A-Za-z0-9_]*\.)?(?<method>[A-Za-z][A-Za-z0-9_]*Async)`",
        RegexOptions.Compiled);

    private sealed record RiotReference(string Assembly, string DeclaringType, string Member);

    private sealed record ApprovedCall(string Path, IReadOnlyList<string> FacadeMethods);

    [Fact]
    public void TheVendoredAllowlistIsTheApprovedDocumentByteForByte()
    {
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AllowlistPath())))
            .ToLowerInvariant();

        Assert.Equal(ApprovedAllowlistSha256, actual);
    }

    /// <summary>
    /// The parse of section 1, checked against what the document says it contains.
    /// </summary>
    /// <remarks>
    /// The two negatives are the load-bearing half. <c>dynamicRouteCost</c> is in section 2's
    /// "not approved" table and <c>queryVehicleNotAssignOrder</c> is in section 3.2's prose -- both
    /// are real paths written elsewhere in the same file, so finding either here would mean the
    /// scan had stopped honouring the section boundary and the list had silently widened.
    /// </remarks>
    [Fact]
    public void TheAllowlistParsesIntoTheTwentySixApprovedCalls()
    {
        List<ApprovedCall> approved = ApprovedCalls();
        HashSet<string> paths = new(approved.Select(call => call.Path), StringComparer.Ordinal);

        Assert.Equal(26, approved.Count);
        Assert.Contains("/api/order/v1/orderRecord", paths);
        Assert.Contains("/api/device/v1/command/sync/service/{deviceKey}/{serviceId}", paths);
        Assert.Contains("LoginAsync", approved.SelectMany(call => call.FacadeMethods));
        Assert.DoesNotContain("/api/task/v1/route/dynamicRouteCost", paths);
        Assert.DoesNotContain(
            "/api/task/vehicles/queryVehicleNotAssignOrder/{deviceKey}/{orderKey}",
            paths);
    }

    /// <summary>
    /// No product assembly reaches the generated Kiota client: not through <c>.Raw</c>, the SDK's
    /// escape from the named Facade onto any URL at all, and not by naming its types at all.
    /// REQ-0309 requires the named Facade; section 2 of the allowlist denies the generated client
    /// directly, on the ground that regenerating the SDK does not widen what is approved.
    /// </summary>
    /// <remarks>
    /// Two legs, because they fail in different places. <c>get_Raw</c> catches the escape even
    /// when its result is discarded and no generated type is ever named; the assembly reference
    /// catches a generated type reached some other way. A call to <c>ListDevicesAsync</c> trips
    /// the second on its own -- correctly, since it returns a generated model and section 2
    /// denies it -- while the subset check names it as the unapproved call it is.
    /// </remarks>
    [Fact]
    public void ProductCodeReachesRiotOnlyThroughNamedFacades()
    {
        string[] escapes = [.. ProductAssemblies.SelectMany(RawEscapes)];

        Assert.True(
            escapes.Length == 0,
            "Product code reaches RIoT outside the named Facade surface, which REQ-0309 and "
            + "section 2 of the allowlist forbid: " + string.Join("; ", escapes));
    }

    /// <summary>
    /// Every RIoT call product code makes is on the allowlist.
    /// </summary>
    [Fact]
    public void EveryRiotCallProductCodeMakesIsOnTheAllowlist()
    {
        string[] called = CalledFacadeMethods();
        Assert.NotEmpty(called);

        string[] unapproved = NotOnTheAllowlist(called);

        Assert.True(
            unapproved.Length == 0,
            "Product code calls RIoT Facade methods that section 1 of the allowlist does not "
            + "approve: " + string.Join(", ", unapproved));
    }

    /// <summary>
    /// Proves the comparison above is not vacuous, with a method the allowlist denies by name.
    /// </summary>
    /// <remarks>
    /// <c>ListDevicesAsync</c> (<c>GET /api/device/v1/devices</c>) is a real Facade method that
    /// product code could call today, and section 2 lists it among the denials -- being in the
    /// Facade was never the authorisation. A fabricated method name would prove less: it would
    /// leave open whether the check reports something that genuinely exists.
    /// </remarks>
    [Fact]
    public void TheAllowlistComparisonCatchesACallNobodyApproved()
    {
        string[] withADeniedCall = [.. CalledFacadeMethods(), "ListDevicesAsync"];

        string[] unapproved = NotOnTheAllowlist(withADeniedCall);

        Assert.Contains("ListDevicesAsync", unapproved);
    }

    /// <summary>
    /// Proves the <c>.Raw</c> scan is not vacuous, by running it over an assembly that really
    /// does escape: this one. <see cref="RawEscapeSample"/> is the only <c>.Raw</c> in the
    /// repository and exists for this test.
    /// </summary>
    [Fact]
    public void TheRawScanCatchesAnEscapeThatIsReallyThere()
    {
        string[] escapes = [.. RawEscapes("ControlServer.Tests")];

        Assert.Contains(escapes, escape => escape.Contains("MapClient.Raw", StringComparison.Ordinal));
        Assert.Contains(escapes, escape => escape.Contains(GeneratedAssembly, StringComparison.Ordinal));
    }

    /// <summary>
    /// The pinned assembly list still covers everything <c>src/</c> builds.
    /// </summary>
    [Fact]
    public void EverySourceProjectIsInTheScannedSet()
    {
        string[] projects =
        [
            .. Directory.GetDirectories(Path.Combine(RepositoryRoot(), "src"))
                .Select(directory => Path.GetFileName(directory)!)
                .Order(StringComparer.Ordinal)
        ];

        Assert.Equal(ProductAssemblies.Order(StringComparer.Ordinal), projects);
    }

    private static string[] NotOnTheAllowlist(IEnumerable<string> facadeMethods)
    {
        HashSet<string> approved = new(
            ApprovedCalls().SelectMany(call => call.FacadeMethods),
            StringComparer.Ordinal);

        return
        [
            .. facadeMethods
                .Where(method => !approved.Contains(method))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// The Facade methods the product assemblies call, read off their member reference tables.
    /// </summary>
    private static string[] CalledFacadeMethods() =>
    [
        .. ProductAssemblies
            .SelectMany(assembly => RiotReferences(AssemblyPath(assembly)))
            .Where(reference => reference.Assembly == FacadeAssembly
                && !NavigationMembers.Contains(reference.Member))
            .Select(reference => reference.Member)
            .Distinct(StringComparer.Ordinal)
    ];

    private static IEnumerable<string> RawEscapes(string assemblyName)
    {
        string path = AssemblyPath(assemblyName);

        foreach (RiotReference reference in RiotReferences(path))
        {
            if (reference.Assembly == GeneratedAssembly)
            {
                yield return
                    $"{assemblyName} calls {reference.DeclaringType}.{reference.Member} on the "
                    + "generated client rather than a named Facade";
            }
            else if (reference.Member == "get_Raw")
            {
                yield return $"{assemblyName} reads {reference.DeclaringType}.Raw";
            }
        }

        // The second leg, and a rule in its own right: section 2 denies calling the generated
        // client directly, so product code has no business naming its types at all. Every
        // approved Facade method returns RIoT.Sdk.Core types -- the only members of the Facade
        // that put a generated type in a caller's signature are the three Raw properties and the
        // two DeviceClient methods section 2 denies by name. None of the four product assemblies
        // references the assembly today.
        if (AssemblyReferences(path).Contains(GeneratedAssembly, StringComparer.Ordinal))
        {
            yield return
                $"{assemblyName} references {GeneratedAssembly}, the generated client, which "
                + "section 2 of the allowlist denies product code";
        }
    }

    private static List<ApprovedCall> ApprovedCalls()
    {
        List<ApprovedCall> approved = [];
        foreach (string line in SectionOne(File.ReadAllText(AllowlistPath())))
        {
            Match row = ApprovedRow.Match(line);
            if (!row.Success)
            {
                continue;
            }

            Match path = QuotedPath.Match(row.Groups["path"].Value);
            if (!path.Success)
            {
                continue;
            }

            approved.Add(new ApprovedCall(
                path.Groups["path"].Value.Split('?', 2)[0],
                [
                    .. QuotedFacadeMethod.Matches(row.Groups["facade"].Value)
                        .Select(match => match.Groups["method"].Value)
                ]));
        }

        return approved;
    }

    /// <summary>
    /// Section 1 of the document and nothing else. Its own section 4 says so: "the list is every
    /// row in section 1 whose first cell is an HTTP method".
    /// </summary>
    private static IEnumerable<string> SectionOne(string document)
    {
        bool inside = false;
        foreach (string line in document.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = line.StartsWith("## 一、", StringComparison.Ordinal);
                continue;
            }

            if (inside)
            {
                yield return line;
            }
        }
    }

    private static List<RiotReference> RiotReferences(string assemblyPath)
    {
        using FileStream file = File.OpenRead(assemblyPath);
        using PEReader image = new(file);
        MetadataReader metadata = image.GetMetadataReader();
        List<RiotReference> references = [];

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

            string assembly = metadata.GetString(metadata
                .GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
            if (assembly is not (FacadeAssembly or GeneratedAssembly))
            {
                continue;
            }

            references.Add(new RiotReference(
                assembly,
                metadata.GetString(type.Name),
                metadata.GetString(member.Name)));
        }

        return references;
    }

    private static string[] AssemblyReferences(string assemblyPath)
    {
        using FileStream file = File.OpenRead(assemblyPath);
        using PEReader image = new(file);
        MetadataReader metadata = image.GetMetadataReader();

        return
        [
            .. metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
        ];
    }

    private static string AssemblyPath(string assemblyName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        Assert.True(File.Exists(path), $"{assemblyName}.dll is not in the test output directory.");
        return path;
    }

    private static string AllowlistPath() => Path.Combine(
        RepositoryRoot(), "vendor", "8005-agv-program", "docs", "riot-call-allowlist.md");

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
/// The one <c>.Raw</c> escape in this repository, here so that
/// <see cref="RiotCallAllowlistArchitectureTests.TheRawScanCatchesAnEscapeThatIsReallyThere"/>
/// runs the scan over an assembly that really contains one.
/// </summary>
/// <remarks>
/// It is never called. What matters is that the compiler emits the member reference and the
/// reference to <c>RIoT.Sdk.Generated</c> into this test assembly -- the same two things the scan
/// looks for in <c>src/</c>. Writing the sample into product code instead, even temporarily,
/// would be the violation itself.
/// </remarks>
internal static class RawEscapeSample
{
    internal static object MapRawEscape(MapClient maps) => maps.Raw;
}
