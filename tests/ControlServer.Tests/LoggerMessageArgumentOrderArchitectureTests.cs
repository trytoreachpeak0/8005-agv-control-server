using System.Text;
using System.Text.RegularExpressions;

namespace ControlServer.Tests;

/// <summary>
/// Every <c>LoggerMessage.Define</c> call site hands its arguments over in the order its template names them
/// (control-server#588).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source scan.</b> <c>LoggerMessage.Define</c> binds arguments to placeholders by position; the names in the
/// template take no part in type checking. Two arguments of different types swapped together with the generic
/// arguments compile, and two of the same type swapped compile without even that. The rendered message still reads as
/// a sentence -- event 2112 said "the route graph reached station AGV-01 for vehicle 12" -- and the structured
/// property a search runs on holds the other value. The <c>[LoggerMessage]</c> source generator matches by name and
/// fails the build on a mismatch, so those are not scanned.
/// </para>
/// <para>
/// <b>What counts as a swap.</b> An argument whose name is another placeholder of the same template and not its own:
/// <c>request.AgvId</c> handed to <c>{TargetStationId}</c> while <c>{AgvId}</c> is two places on. An argument whose name
/// is simply unrelated (<c>stop.StationRiotId</c> for <c>{StationId}</c>) is not reported; the name match is the
/// evidence, and without it the scan has nothing to go on. Its own placeholder ending in any segment of the argument
/// is a match (<c>held.VehicleKey</c> for <c>{HolderVehicleKey}</c>, <c>point.StationId</c> for <c>{WaitingPoint}</c>).
/// </para>
/// <para>
/// <b>What it cannot see</b>, by construction (guard, not proof): two swapped arguments whose names match neither
/// placeholder (how many arguments are named unlike any placeholder is printed); an argument expression it cannot
/// name (a method call, arithmetic, a conditional, a literal), whose count is printed and the named count floored
/// below; a delegate called from a file outside its own partial-class group (same directory, same file name up to the
/// first dot), or passed on and called under another name; a template that is not a plain string literal or a
/// <c>+</c> concatenation of them; a <c>Define</c> whose generic arguments are themselves generic
/// (<c>Define&lt;string, IReadOnlyList&lt;int&gt;&gt;</c>), which the definition pattern does not match and so skips
/// without a word; and a swap between two placeholders of one template that end in the same word -- "its own" is any
/// argument segment its placeholder ends with, so <c>reason.Code</c> handed to <c>{StopCode}</c> and <c>stop.Code</c> to
/// <c>{ReasonCode}</c> both count as matching their own places.
/// </para>
/// <para>
/// <b>Two rounds, then stop</b> (control-server#588). Round one names an argument only when it is an identifier or a
/// member chain, and looks for calls in the defining file. Round two also drops a <c>!</c> inside the chain, strips a
/// leading cast, a <c>?? fallback</c>, a trailing <c>.ToString(...)</c>-style call or <c>.Value</c>-style member, and
/// looks for calls in the other files of a partial class. Those strippings apply only to an argument that is not
/// already a plain member chain: <c>deadline.Value</c> is one, so it is named <c>Value</c>, not <c>deadline</c>, and two
/// such arguments swapped go unreported. What is still unnamed after that is counted, not chased.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait</b>: a cross-cutting guard hung off a slice would be deferred with it.
/// </para>
/// </remarks>
public sealed class LoggerMessageArgumentOrderArchitectureTests
{
    private static readonly string[] ProductRoots = ["src", "tools"];

    private static readonly Regex Definition = new(
        @"(?<name>\w+)\s*=\s*LoggerMessage\.Define\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{(?<name>[^{}:,]+)(?:[:,][^{}]*)?\}", RegexOptions.Compiled);

    private static readonly Regex MemberChain = new(
        @"^@?[A-Za-z_]\w*(?:\??\.[A-Za-z_]\w*)*!?$", RegexOptions.Compiled);

    private static readonly Regex LeadingCast = new(@"^\((?:int|long|double|decimal|string|object)\??\)\s*", RegexOptions.Compiled);

    private static readonly Regex Fallback = new(@"\s*\?\?.*$", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TrailingCall = new(
        @"(?:\??\.(?:ToString|ToUpperInvariant|ToLowerInvariant|Trim)\([^()]*\))+$", RegexOptions.Compiled);

    private static readonly Regex TrailingMember = new(
        @"\??\.(?:Value|Count|Length|TotalSeconds|TotalMilliseconds|TotalMinutes)$", RegexOptions.Compiled);

    [Fact]
    public void EveryCallSiteHandsItsArgumentsOverInTheTemplatesOrder()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        Dictionary<string, string> sources = ProductRoots
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);

        ScanResult first = Scan(sources, round: 1);
        ScanResult second = Scan(sources, round: 2);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"round 1: {first.Summary}{Environment.NewLine}round 2: {second.Summary}{Environment.NewLine}" +
            $"unnamed after round 2:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", second.Unnamed)}");

        // The floors are what keep a broken parser from passing by scanning nothing. They sit under the 2026-10-13
        // counts (206 definitions, 228 call sites, 712 named arguments, 49 unnamed); lower them only on purpose.
        const string Hint = "; if log messages were removed on purpose, lower this floor in the same change";
        Assert.True(second.Definitions >= 200, $"only {second.Definitions} LoggerMessage.Define definitions found{Hint}");
        Assert.True(second.CallSites >= 220, $"only {second.CallSites} call sites found{Hint}");
        Assert.True(second.NamedArguments >= 700, $"only {second.NamedArguments} arguments named{Hint}");
        Assert.True(
            second.Swaps.Count == 0,
            $"arguments handed to another placeholder's name:{Environment.NewLine}{string.Join(Environment.NewLine, second.Swaps)}");
    }

    [Fact]
    public void TheScanReportsASwapAndPassesTheSameCallInOrder()
    {
        // The defect this guard exists for, in miniature: event 2112 before control-server#588.
        const string Swapped = """
            private static readonly Action<ILogger, string, string, int, Exception?> LogConflict =
                LoggerMessage.Define<string, string, int>(
                    LogLevel.Error,
                    new EventId(2112, nameof(LogConflict)),
                    "Demand {DemandId}: station {TargetStationId} " +
                    "for vehicle {AgvId}.");
            void M() => LogConflict(logger, request.DemandId, request.AgvId, request.TargetStationId, null);
            """;
        string inOrder = Swapped.Replace(
            "request.AgvId, request.TargetStationId", "request.TargetStationId, request.AgvId", StringComparison.Ordinal);

        ScanResult reported = Scan(new Dictionary<string, string> { ["Sample.cs"] = Swapped }, round: 2);
        ScanResult clean = Scan(new Dictionary<string, string> { ["Sample.cs"] = inOrder }, round: 2);

        Assert.Equal(
            ["Sample.cs:7 LogConflict: {TargetStationId} <- request.AgvId (named like {AgvId})",
             "Sample.cs:7 LogConflict: {AgvId} <- request.TargetStationId (named like {TargetStationId})"],
            reported.Swaps);
        Assert.Empty(clean.Swaps);
        Assert.Equal(3, clean.NamedArguments);
    }

    private sealed record ScanResult(
        int Definitions,
        int CallSites,
        int Arguments,
        int NamedArguments,
        int Unrelated,
        IReadOnlyList<string> Unnamed,
        IReadOnlyList<string> Swaps)
    {
        public string Summary =>
            $"{Definitions} definitions, {CallSites} call sites, {Arguments} arguments, {NamedArguments} named, " +
            $"{Arguments - NamedArguments} unnamed, {Unrelated} named unlike any placeholder, {Swaps.Count} swaps";
    }

    private static ScanResult Scan(IReadOnlyDictionary<string, string> sources, int round)
    {
        int definitions = 0, callSites = 0, arguments = 0, named = 0, unrelated = 0;
        List<string> unnamed = [], swaps = [];

        foreach ((string file, string text) in sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string[] callFiles = round == 1
                ? [file]
                : [.. sources.Keys.Where(other => PartialGroup(other) == PartialGroup(file)).Order(StringComparer.Ordinal)];

            foreach (Match definition in Definition.Matches(text))
            {
                definitions++;
                string name = definition.Groups["name"].Value;
                List<string>? defineArguments = SplitArguments(text, definition.Index + definition.Length - 1);
                if (defineArguments is null || defineArguments.Count < 3)
                {
                    unnamed.Add($"{file}:{LineOf(text, definition.Index)} {name}: template not read");
                    continue;
                }

                string template = string.Concat(StringLiteral.Matches(defineArguments[2]).Select(literal => literal.Groups[1].Value))
                    .Replace("{{", string.Empty, StringComparison.Ordinal)
                    .Replace("}}", string.Empty, StringComparison.Ordinal);
                string[] placeholders = [.. Placeholder.Matches(template).Select(match => match.Groups["name"].Value)];

                Regex call = new(@"(?<![\w.])" + Regex.Escape(name) + @"\s*\(");
                foreach (string callFile in callFiles)
                {
                    string callText = sources[callFile];
                    foreach (Match site in call.Matches(callText))
                    {
                        List<string>? callArguments = SplitArguments(callText, site.Index + site.Length - 1);
                        string where = $"{callFile}:{LineOf(callText, site.Index)} {name}";
                        callSites++;
                        if (callArguments is null || callArguments.Count != placeholders.Length + 2)
                        {
                            unnamed.Add($"{where}: argument count does not match {placeholders.Length} placeholders");
                            continue;
                        }

                        // Define's delegate is (logger, args..., exception).
                        for (int position = 0; position < placeholders.Length; position++)
                        {
                            string argument = callArguments[position + 1];
                            arguments++;
                            string[]? segments = NameOf(argument, round);
                            if (segments is null)
                            {
                                unnamed.Add($"{where}: {{{placeholders[position]}}} <- {argument}");
                                continue;
                            }

                            named++;
                            string own = Normalize(placeholders[position]);
                            if (segments.Any(segment => own.EndsWith(segment, StringComparison.Ordinal)))
                            {
                                continue;
                            }

                            int other = Array.FindIndex(
                                placeholders, placeholder => Normalize(placeholder) == segments[^1]);
                            if (other >= 0 && other != position)
                            {
                                swaps.Add($"{where}: {{{placeholders[position]}}} <- {argument} (named like {{{placeholders[other]}}})");
                            }
                            else
                            {
                                unrelated++;
                            }
                        }
                    }
                }
            }
        }

        return new ScanResult(definitions, callSites, arguments, named, unrelated, unnamed, swaps);
    }

    /// <summary>The normalized segments of the member chain an argument names, or null when it names none.</summary>
    private static string[]? NameOf(string argument, int round)
    {
        string expression = argument.Trim();
        if (!MemberChain.IsMatch(expression) && round >= 2)
        {
            expression = expression.Replace("!.", ".", StringComparison.Ordinal);
            expression = LeadingCast.Replace(expression, string.Empty);
            expression = Fallback.Replace(expression, string.Empty);
            expression = TrailingCall.Replace(expression, string.Empty);
            if (expression.Contains('.', StringComparison.Ordinal))
            {
                expression = TrailingMember.Replace(expression, string.Empty);
            }
        }

        return MemberChain.IsMatch(expression)
            ? [.. Regex.Split(expression.TrimEnd('!'), @"\??\.").Select(Normalize)]
            : null;
    }

    private static string Normalize(string name) => name.TrimStart('@', '_').ToLowerInvariant();

    /// <summary>Partial classes here are split as Name.cs, Name.Part.cs in one directory.</summary>
    private static string PartialGroup(string file)
    {
        string directory = Path.GetDirectoryName(file) ?? string.Empty;
        string stem = Path.GetFileName(file).Split('.')[0];
        return directory + "/" + stem;
    }

    private static bool IsBuildOutput(string file)
    {
        string normalized = file.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>
    /// The top-level arguments of the parenthesised list opening at <paramref name="open"/>, or null when it does not
    /// close. String and character literals are skipped whole, so commas and brackets inside them do not count.
    /// </summary>
    private static List<string>? SplitArguments(string text, int open)
    {
        List<string> result = [];
        StringBuilder current = new();
        int depth = 0;
        for (int index = open; index < text.Length; index++)
        {
            char character = text[index];
            if (character is '"' or '\'')
            {
                int end = LiteralEnd(text, index);
                if (end < 0)
                {
                    return null;
                }

                current.Append(text, index, end - index + 1);
                index = end;
                continue;
            }

            if (character is '(' or '[' or '{')
            {
                depth++;
                if (depth == 1)
                {
                    continue;
                }
            }
            else if (character is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    result.Add(current.ToString().Trim());
                    return result;
                }
            }
            else if (character == ',' && depth == 1)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        return null;
    }

    private static int LiteralEnd(string text, int start)
    {
        char quote = text[start];
        bool verbatim = quote == '"' && start > 0 && text[start - 1] == '@';
        for (int index = start + 1; index < text.Length; index++)
        {
            if (verbatim)
            {
                if (text[index] == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        index++;
                        continue;
                    }

                    return index;
                }
            }
            else if (text[index] == '\\')
            {
                index++;
            }
            else if (text[index] == quote)
            {
                return index;
            }
        }

        return -1;
    }
}
