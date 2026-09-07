using System.Reflection;
using System.Text.RegularExpressions;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// The binding between the protocol's closed <c>ErrorCode</c> enumeration and the bytes this
/// server actually emits, made mechanical.
/// </summary>
/// <remarks>
/// <para>
/// Nothing was checking it. Neither end validates against the schema at runtime -- there is no
/// JSON Schema library in either implementation's dependency list -- and
/// <c>CONTROL_SERVER_G2</c> verifies the protocol manifest hash rather than each message against
/// its schema. Three reason codes outside the enumeration therefore lived through the whole MVP
/// cycle and eight green gates before anyone read them off by hand.
/// </para>
/// <para>
/// Reading them off by hand also under-counted. The three were the ones written at a
/// <c>reasonCode =</c> position, where they are visible; the recovery surface returned eleven
/// more from <c>Validate*</c> helpers, which reach <c>Problem.reasonCode</c> and
/// <c>BlockingFact.reasonCode</c> through a local and so appear at no scannable position at all.
/// <see cref="ServerReasonCodes"/> names all of them, which is what lets this test read one place
/// instead of chasing control flow.
/// </para>
/// <para>
/// **The registry is not copied here.** <see cref="ProtocolErrorCodes"/> is the vendored
/// enumeration -- the server is built against a pinned protocol release and does not read its
/// schemas at runtime -- and this test asks it rather than keeping a second list that would not
/// follow a release. What is listed here is the *deviation* set: the codes known to be outside
/// the enumeration, pinned exactly so an unregistered one cannot appear quietly. It is empty as
/// of the v2 candidate, which is the strongest form the comparison takes -- see
/// <see cref="PinnedDeviations"/> for why an empty set is kept rather than removed.
/// </para>
/// </remarks>
public sealed class ProtocolReasonCodeArchitectureTests
{
    /// <summary>
    /// The codes this server emits that the protocol's ErrorCode registry does not contain.
    /// **Empty, and meant to stay that way.**
    /// </summary>
    /// <remarks>
    /// <para>
    /// It held eleven when this test landed. Two had exact counterparts in the enumeration
    /// already, so they were renamed on 2026-09-04 and cost no distinction at all:
    /// <c>RECOVERY_SESSION_CLOSED</c> became <c>RECOVERY_SESSION_NOT_OPEN</c> and
    /// <c>FORCED_RECOVERY_GENERATION_MISMATCH</c> became
    /// <c>FORCED_RECOVERY_GENERATION_STALE</c>.
    /// </para>
    /// <para>
    /// The other nine were not typos, unlike the three renamed the same day --
    /// <c>PROTOCOL_RELEASE_MISMATCH</c> was <c>PROTOCOL_RELEASE_IDENTITY_MISMATCH</c> missing a
    /// word -- but each expressed a distinction the protocol had no vocabulary for. The
    /// enumeration offered one <c>RECOVERY_SCOPE_MISMATCH</c> where the server separates a
    /// mismatched event, demand and operator, and one <c>ACTION_NOT_ALLOWED_IN_STATE</c> where it
    /// separates "already chose an action", "no operation found" and "no proven checkpoint".
    /// Collapsing them onto the enumeration would have lost what the onboard shows the operator,
    /// so v2 appended them instead -- the error surface went from 43 codes to 54 -- and this set
    /// emptied when <see cref="ProtocolErrorCodes"/> was re-synced to that candidate.
    /// </para>
    /// <para>
    /// **It is kept rather than deleted, because empty is itself the assertion.** The test below
    /// compares the deviation set against this one exactly, so an empty set says every code the
    /// server emits is a registry code, and a new one fails on the spot. Deleting the field would
    /// leave nowhere to record a deviation, and a deviation with nowhere to go is how eleven of
    /// them lived through eight green gates. Refilling it is a protocol-side decision first: the
    /// registry is <c>appendOnly</c>, so a genuinely new code belongs in it rather than on a list
    /// of exceptions the server keeps to itself.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> PinnedDeviations = new(StringComparer.Ordinal);

    /// <summary>
    /// The positions a reason code is written at. Each is exact rather than approximate: a scan
    /// for "any SCREAMING_SNAKE literal that happens to match a code" reports the vehicle's own
    /// <c>SafetySummary.reasonCodes</c> values, which this server reads rather than sends, and
    /// the internal session reason that <see cref="ProtocolErrorCodes.ToSessionReadinessReasonCode"/>
    /// maps before it reaches the wire.
    /// </summary>
    private static readonly Regex[] WritingPositions =
    [
        new(@"reasonCode\s*=\s*""(?<code>[A-Z][A-Z0-9_]*)""", RegexOptions.Compiled),
        new(@"Problem\(\s*""(?<code>[A-Z][A-Z0-9_]*)""", RegexOptions.Compiled),
        new(@"ProblemReasonCode\s*=\s*""(?<code>[A-Z][A-Z0-9_]*)""", RegexOptions.Compiled)
    ];

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void EveryReasonCodeTheServerEmitsIsInTheRegistryOrAPinnedDeviation()
    {
        string[] emitted = EmittedReasonCodes();
        Assert.NotEmpty(emitted);

        string[] deviations = Deviations(emitted);

        Assert.Equal(
            PinnedDeviations.OrderBy(code => code, StringComparer.Ordinal),
            deviations.OrderBy(code => code, StringComparer.Ordinal));
    }

    /// <summary>
    /// Proves the comparison above is not vacuous: a code nobody pinned is reported, and one that
    /// was pinned but is no longer emitted is reported too.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void ThePinnedDeviationComparisonCatchesACodeNobodyRegistered()
    {
        string[] withAnExtraCode = [.. EmittedReasonCodes(), "A_CODE_NOBODY_REGISTERED"];

        string[] deviations = Deviations(withAnExtraCode);

        Assert.Contains("A_CODE_NOBODY_REGISTERED", deviations);
        Assert.DoesNotContain("A_CODE_NOBODY_REGISTERED", PinnedDeviations);
    }

    /// <summary>
    /// Nothing may write a reason code as a bare literal at one of the writing positions.
    /// </summary>
    /// <remarks>
    /// <see cref="ServerReasonCodes"/> is the one place a reason code is spelled, so the previous
    /// test covers every code that goes through it. This one closes the way around it: a literal
    /// written straight into a <c>Problem(</c> call or a <c>reasonCode =</c> initialiser would
    /// never appear in that class and so would never be compared against the registry.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void NoReasonCodeIsWrittenAsABareLiteral()
    {
        string sourceRoot = Path.Combine(RepositoryRoot(), "src");
        List<string> offences = [];
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            offences.AddRange(BareReasonCodeLiterals(File.ReadAllText(file))
                .Select(code => $"{Path.GetRelativePath(sourceRoot, file)}: {code}"));
        }

        Assert.True(
            offences.Count == 0,
            "A reason code is written as a bare literal instead of a ServerReasonCodes constant, "
            + "so nothing compares it against the protocol registry: "
            + string.Join("; ", offences));
    }

    /// <summary>
    /// Proves the scan above is not vacuous: it is run over source that does write a bare literal.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void TheBareLiteralScanCatchesEachWritingPosition()
    {
        const string source = """
            var rejected = new { reasonCode = "SOMETHING_UNREGISTERED", fieldPath = "payload" };
            var problem = Problem("ANOTHER_UNREGISTERED", "payload", "message");
            row.ProblemReasonCode = "A_THIRD_UNREGISTERED";
            var fine = Problem(ServerReasonCodes.ActionNotAllowedInState, "payload", "message");
            """;

        string[] found = BareReasonCodeLiterals(source);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "SOMETHING_UNREGISTERED", "ANOTHER_UNREGISTERED", "A_THIRD_UNREGISTERED"
            },
            new HashSet<string>(found, StringComparer.Ordinal));
    }

    private static string[] EmittedReasonCodes() =>
        [.. typeof(ServerReasonCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)];

    private static string[] Deviations(IEnumerable<string> codes) =>
        [.. codes.Where(code => !ProtocolErrorCodes.Contains(code)).Distinct(StringComparer.Ordinal)];

    private static string[] BareReasonCodeLiterals(string source) =>
        [.. WritingPositions
            .SelectMany(pattern => pattern.Matches(source).Select(match => match.Groups["code"].Value))];

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
