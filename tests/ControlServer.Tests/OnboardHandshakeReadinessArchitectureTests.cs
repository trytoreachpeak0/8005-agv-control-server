using System.Text.RegularExpressions;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on where an answer may take a SessionReadiness line after it: in
/// <c>OnboardMessageProcessor.AnswerWithReadiness</c>, which refuses to inside the reconnect handshake, and in the
/// recovery report's answer, which ends the handshake -- nowhere else (control-server#340).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a guard and not one more check per site.</b> The rule is control-server#202's: until its recovery report is
/// answered, the vehicle reads exactly one answer for each line it sends, and a readiness line after an answer is read
/// in place of the next one. #202 applied it to the recovery sends and the safety snapshot request; five sites that
/// appended readiness themselves were left out, and onboard-hmi#204 met one of them in the field. The fix put the
/// decision in one function and routed the five through it. What keeps that sufficient is that no sixth site appends
/// a readiness line on its own, and that is what these tests pin.
/// </para>
/// <para>
/// <b>What this class cannot see, by construction.</b> It reads text; it does not follow values. It counts calls of
/// <c>SessionReadinessLine</c> and occurrences of the literal <c>"SessionReadiness"</c>, with whole-line comments
/// removed. So these would pass it: a readiness envelope built from a message type that is not spelled as that
/// literal (a constant defined elsewhere, <c>nameof</c> of the enum of the same name), a readiness line copied out of
/// a stored response and appended, and a second class that answers vehicle lines. It is a regression guard against
/// the one shape this defect had -- a site composing <c>{answer}\n{readiness}</c> itself -- not a proof. The
/// guarantee rests on the construction: one builder, <c>SessionReadinessLine</c>, private to the processor, and one
/// caller of it that answers inside a handshake, <c>AnswerWithReadiness</c>. That the caller does refuse inside the
/// handshake is not pinned here but by behaviour, in <c>RecoveryStateMachineG2Tests</c>: a test per site, each red if
/// the check is taken away.
/// </para>
/// <para>
/// If this goes red on a new site that needs to tell the vehicle its readiness, route it through
/// <c>AnswerWithReadiness</c> rather than adding it to the ledger below. The ledger has one exception, and that
/// exception exists only because it is the answer that ends the handshake.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait</b>, for the reason <see cref="RiotCallAllowlistArchitectureTests"/> gives: a
/// cross-cutting guard hung off a slice goes unguarded whenever that slice is deferred.
/// </para>
/// </remarks>
public sealed class OnboardHandshakeReadinessArchitectureTests
{
    private const string Processor = "src/ControlServer.Host/Transport/OnboardMessageProcessor.cs";

    /// <summary>
    /// The literal that names a readiness envelope appears in product code in the processor alone: once where the one
    /// line is built, once where a stored first response is read back.
    /// </summary>
    [Fact]
    public void AReadinessEnvelopeIsBuiltInOneMethodOnly()
    {
        string[] naming = [.. ProductSourceFiles()
            .SelectMany(file => Members(WithoutWholeLineComments(File.ReadAllText(file)))
                .SelectMany(member => Enumerable.Repeat(
                    $"{Relative(file)}#{member.Name}",
                    Regex.Count(member.Body, "\"SessionReadiness\""))))];

        Assert.Equal(
            [$"{Processor}#SessionReadinessLine", $"{Processor}#RestoreAcceptedSnapshotVersions"],
            naming);
    }

    /// <summary>
    /// <c>SessionReadinessLine</c> is called twice: by the combining function, and by the recovery report's answer
    /// after that report has marked the handshake done.
    /// </summary>
    [Fact]
    public void OnlyTheCombiningFunctionAndTheRecoveryReportsAnswerAppendAReadinessLine()
    {
        List<Member> members = Members(WithoutWholeLineComments(
            File.ReadAllText(Path.Combine(RepositoryRoot(), Processor))));
        Assert.Contains(members, member => member.Name == "AnswerWithReadiness");

        string[] calling = [.. members
            .Where(member => member.Name != "SessionReadinessLine")
            .SelectMany(member => Enumerable.Repeat(member.Name, Regex.Count(member.Body, @"\bSessionReadinessLine\(")))];
        Assert.Equal(["ProcessCurrentSessionMessageAsync", "AnswerWithReadiness"], calling);

        string dispatch = members.Single(member => member.Name == "ProcessCurrentSessionMessageAsync").Body;
        Match report = Regex.Match(dispatch, "case \"RecoveryStateReport\":(?<body>.*?)\\n\\s*case \"", RegexOptions.Singleline);
        Assert.True(report.Success, "The recovery report's case is gone from the processor's dispatch.");
        string body = report.Groups["body"].Value;
        int done = body.IndexOf("state.HandshakeCompleted = true;", StringComparison.Ordinal);
        int line = body.IndexOf("SessionReadinessLine(", StringComparison.Ordinal);
        Assert.True(line >= 0, "The recovery report's answer no longer carries readiness; the handshake's end depends on it.");
        Assert.True(
            done >= 0 && done < line,
            "The recovery report's answer builds its readiness line before marking the handshake done.");
    }

    private sealed record Member(string Name, string Body);

    /// <summary>
    /// The file cut at each member declared at class level (four spaces in), each piece named after the member it
    /// starts with. A member's piece runs to the next declaration, so the doc comment of that next one lands in it --
    /// which is why whole-line comments are removed first.
    /// </summary>
    private static List<Member> Members(string source)
    {
        MatchCollection declarations = Regex.Matches(
            source, @"^    (?:private|public|internal|protected)\b[^\n(=]*?\b(?<name>\w+)\s*\(", RegexOptions.Multiline);
        List<Member> members = [];
        for (int i = 0; i < declarations.Count; i++)
        {
            int start = declarations[i].Index;
            int end = i + 1 < declarations.Count ? declarations[i + 1].Index : source.Length;
            members.Add(new Member(declarations[i].Groups["name"].Value, source[start..end]));
        }
        return members;
    }

    private static string WithoutWholeLineComments(string source) =>
        Regex.Replace(source, @"^[ \t]*//.*$", string.Empty, RegexOptions.Multiline);

    private static string[] ProductSourceFiles()
    {
        string root = Path.Combine(RepositoryRoot(), "src");
        string[] files = [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
        Assert.Contains(files, file => Relative(file) == Processor);
        return files;
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(RepositoryRoot(), file).Replace(Path.DirectorySeparatorChar, '/');

    private static string RepositoryRoot() => ProtocolIdentityArchitectureTests.RepositoryRoot();
}
