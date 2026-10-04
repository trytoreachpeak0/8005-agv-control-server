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

    /// <summary>For a caller that never sees the request line: the answer refuses some message of that type, and is correlated to it.</summary>
    public static void RefusedOfType(string response, string reasonCode, string rejectedMessageType)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        Refused(
            response,
            reasonCode,
            document.RootElement.GetProperty("payload").GetProperty("rejectedMessageId").GetString()!,
            rejectedMessageType);
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
