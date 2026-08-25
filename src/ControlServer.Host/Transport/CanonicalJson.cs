using System.Security.Cryptography;
using System.Text.Json;

namespace ControlServer.Host.Transport;

internal static class CanonicalJson
{
    public static string Sha256(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteElement(writer, document.RootElement);
        }
        byte[] hash = SHA256.HashData(stream.ToArray());
        return string.Create(hash.Length * 2, hash, static (characters, bytes) =>
        {
            const string hex = "0123456789abcdef";
            for (int index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = hex[bytes[index] >> 4];
                characters[(index * 2) + 1] = hex[bytes[index] & 0x0F];
            }
        });
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(
                             property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
