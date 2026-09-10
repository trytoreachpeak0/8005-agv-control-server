using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.FakeOnboard;

/// <summary>
/// Records every protocol line this peer sends, for the L2 runner to hand to
/// tools/ControlServer.SchemaConformance after the scenario (ADR-cross-0058 map, #33 / #35).
/// </summary>
/// <remarks>
/// <para>
/// This is where the synthetic peer's lines get checked, not <c>dotnet test</c>: the test project does
/// not reference this assembly, so no G2 run ever produces one of them. A double that sends a line the
/// contract forbids lets the server be built against the wrong contract while every gate stays green.
/// </para>
/// <para>
/// One record per line, appended as it is built, so a peer killed mid-scenario (StopComponent) still
/// leaves everything it sent. The shape is the validator's input: messageType, origin, site, line.
/// </para>
/// </remarks>
internal static class SchemaRecorder
{
    private static readonly object Gate = new();

    public static void Install(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, string.Empty);
        ProtocolEnvelope.OutboundObserver = (messageType, line) =>
        {
            string record = JsonSerializer.Serialize(
                new { messageType, origin = "synthetic-peer", site = Locate(new StackTrace(1, false)), line },
                ProtocolEnvelope.SerializerOptions);
            lock (Gate)
            {
                File.AppendAllText(path, record + "\n");
            }
        };
    }

    // The sending method, not the observation point: the first two frames of this assembly above the
    // shared Envelope helper, which every send goes through and so names nothing.
    private static string Locate(StackTrace trace)
    {
        List<string> site = [];
        foreach (StackFrame frame in trace.GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            if (method is null || type is null || type.Assembly != typeof(SchemaRecorder).Assembly ||
                type == typeof(SchemaRecorder) || method.Name == "Envelope")
            {
                continue;
            }
            // An async method shows up twice in a row: its kickoff stub, then its state machine.
            string name = Describe(method, type);
            if (site.Count == 0 || site[^1] != name)
            {
                site.Add(name);
            }
            if (site.Count == 2)
            {
                break;
            }
        }
        return site.Count == 0 ? "unknown" : string.Join(" <- ", site);
    }

    // Async bodies run as MoveNext on a compiler-generated <Name>d__N type; name the method a reader
    // would search for. Same unmangling as tests/ControlServer.Tests/OutboundSchemaConformance.cs.
    private static string Describe(MethodBase method, Type type)
    {
        string generated = type.Name.StartsWith('<') && method.Name == "MoveNext" ? type.Name : method.Name;
        string trimmed = generated.TrimStart('<');
        int close = trimmed.IndexOf('>', StringComparison.Ordinal);
        string name = close > 0 ? trimmed[..close] : generated;
        while (type.Name.StartsWith('<') && type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }
        return type.Name + "." + name;
    }
}
