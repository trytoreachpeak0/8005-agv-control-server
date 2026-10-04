using System.Text.Json;

namespace ControlServer.Tests;

/// <summary>
/// The processor's answer to an inbound message it read and refused (control-server#478): one ProtocolProblem,
/// correlated to that message, carrying the reason code. Until then these were exceptions that ended the connection.
/// </summary>
internal static class ProtocolProblemAssert
{
    /// <summary>Asserts the response refuses <paramref name="requestLine"/>, read for its messageId and messageType.</summary>
    public static void RefusedLine(string response, string reasonCode, string requestLine)
    {
        using JsonDocument request = JsonDocument.Parse(requestLine);
        Refused(
            response,
            reasonCode,
            request.RootElement.GetProperty("messageId").GetString()!,
            request.RootElement.GetProperty("messageType").GetString()!);
    }

    /// <summary>
    /// A refused safety message: the ProtocolProblem first, then exactly <paramref name="followingTypes"/> -- the
    /// SessionReadiness that announces the lowered readiness when it changed, and the SafetyStateSnapshotRequested that asks
    /// for a new baseline (control-server#478, review S2).
    /// </summary>
    public static void RefusedSafety(
        string response, string reasonCode, string rejectedMessageId, string rejectedMessageType,
        params string[] followingTypes)
    {
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Refused(lines[0], reasonCode, rejectedMessageId, rejectedMessageType);
        Assert.Equal(
            followingTypes,
            lines.Skip(1).Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("messageType").GetString()!;
            }).ToArray());
    }

    /// <summary>Asserts the response is exactly that answer.</summary>
    public static void Refused(string response, string reasonCode, string rejectedMessageId, string rejectedMessageType)
    {
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length == 1, $"期望只回一行 ProtocolProblem，实际回了 {lines.Length} 行：{response}");
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        Assert.Equal("ProtocolProblem", root.GetProperty("messageType").GetString());
        Assert.Equal(rejectedMessageId, root.GetProperty("correlationId").GetString());
        JsonElement payload = root.GetProperty("payload");
        Assert.Equal(rejectedMessageId, payload.GetProperty("rejectedMessageId").GetString());
        Assert.Equal(rejectedMessageType, payload.GetProperty("rejectedMessageType").GetString());
        Assert.Equal(reasonCode, payload.GetProperty("problem").GetProperty("reasonCode").GetString());
    }
}
