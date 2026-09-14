using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlServer.EmergencyDrill;

/// <summary>
/// The run directory. Every file in it is append-only except drill-state.json, whose every version
/// is also appended to state-history.jsonl.
/// </summary>
/// <remarks>
/// Every byte written here goes through <see cref="Redact"/>. Nothing in this tool serializes
/// request headers, so the API key has no path into evidence to begin with; the redaction is the
/// second wall, for an exception message that might one day quote something it should not.
/// </remarks>
internal sealed class DrillEvidence : IDisposable
{
    internal const string StateFileName = "drill-state.json";
    internal const string CommandsFileName = "commands.jsonl";
    internal const string TimelineFileName = "timeline.jsonl";
    internal const string WireFileName = "wire.jsonl";
    internal const string StateHistoryFileName = "state-history.jsonl";
    internal const string SummaryFileName = "SUMMARY.md";
    private const string LockFileName = "drill.lock";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object gate = new();
    private FileStream? lockHandle;

    private DrillEvidence(string directory, string command)
    {
        Directory = directory;
        Command = command;
    }

    /// <summary>The API key's value, replaced wherever it appears. Set once at startup.</summary>
    internal static string? Secret { get; set; }

    public string Directory { get; }

    public string Command { get; }

    internal static DrillEvidence At(string directory, string command) => new(Path.GetFullPath(directory), command);

    internal static string Redact(string text)
    {
        string? secret = Secret;
        return string.IsNullOrEmpty(secret) || secret.Length < 4
            ? text
            : text.Replace(secret, "***REDACTED***", StringComparison.Ordinal);
    }

    /// <summary>
    /// One drill command at a time per run directory. Two terminals racing <c>trigger</c> would
    /// otherwise both read "no trigger recorded" before either wrote it.
    /// </summary>
    internal bool TryLock()
    {
        try
        {
            lockHandle = new FileStream(
                Path.Combine(Directory, LockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal bool HasState => File.Exists(Path.Combine(Directory, StateFileName));

    internal DrillState? LoadState()
    {
        string path = Path.Combine(Directory, StateFileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<DrillState>(File.ReadAllText(path, Utf8), Json)
            : null;
    }

    /// <summary>Replaces drill-state.json durably (flushed to disk before returning) and appends the version.</summary>
    internal void SaveState(DrillState state)
    {
        lock (gate)
        {
            string path = Path.Combine(Directory, StateFileName);
            string temporary = path + ".tmp";
            byte[] bytes = Utf8.GetBytes(Redact(JsonSerializer.Serialize(state, JsonIndented)));
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
            AppendUnlocked(StateHistoryFileName, new { at = DateTimeOffset.Now, command = Command, state });
        }
    }

    internal void AppendCommand(object entry) => Append(CommandsFileName, entry);

    /// <summary>Written immediately before a RIoT write call leaves the process. Counted by summarize.</summary>
    internal void AppendCallIntent(string operation, string target) => Append(
        CommandsFileName,
        new { at = DateTimeOffset.Now, host = Environment.MachineName, command = Command, phase = "SENDING", operation, target });

    internal void AppendTimeline(string kind, object? data) => Append(
        TimelineFileName,
        new { at = DateTimeOffset.Now, command = Command, kind, data });

    internal void AppendWire(object entry) => Append(WireFileName, entry);

    internal void WriteNewFile(string name, string content)
    {
        using FileStream stream = new(Path.Combine(Directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Utf8.GetBytes(Redact(content));
        stream.Write(bytes);
        stream.Flush(true);
    }

    internal IEnumerable<JsonElement> ReadLines(string name)
    {
        string path = Path.Combine(Directory, name);
        if (!File.Exists(path))
        {
            yield break;
        }
        foreach (string line in File.ReadAllLines(path, Utf8))
        {
            if (line.Length == 0)
            {
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private void Append(string name, object entry)
    {
        lock (gate)
        {
            AppendUnlocked(name, entry);
        }
    }

    private void AppendUnlocked(string name, object entry)
    {
        string line = Redact(JsonSerializer.Serialize(entry, Json)) + "\n";
        File.AppendAllText(Path.Combine(Directory, name), line, Utf8);
    }

    public void Dispose()
    {
        lockHandle?.Dispose();
        lockHandle = null;
    }
}
