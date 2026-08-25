using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControlServer.Domain;

Dictionary<string, string> arguments = ParseArguments(args);
string slice = arguments.GetValueOrDefault("slice")
    ?? throw new ArgumentException("--slice is required.");
string manifestPath = arguments.GetValueOrDefault("manifest")
    ?? throw new ArgumentException("--manifest is required.");
if (!Regex.IsMatch(slice, "^W2G-IS-0[0-7]$", RegexOptions.CultureInvariant))
{
    throw new ArgumentException($"Invalid IntegrationSliceId '{slice}'.");
}
byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath);
string actualHash = LowerHex(SHA256.HashData(manifestBytes));
if (actualHash != ProtocolCandidateIdentity.ManifestSha256)
{
    throw new InvalidDataException(
        $"Protocol manifest hash mismatch: expected {ProtocolCandidateIdentity.ManifestSha256}, actual {actualHash}.");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PREFLIGHT_PASS",
    integrationSliceId = slice,
    protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
    manifestSha256 = actualHash,
    protocolApprovalStatus = ProtocolCandidateIdentity.ApprovalStatus
}));
return 0;

static Dictionary<string, string> ParseArguments(string[] values)
{
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments must use --name value pairs.");
        }
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static string LowerHex(byte[] bytes) => string.Create(bytes.Length * 2, bytes, static (characters, source) =>
{
    const string hex = "0123456789abcdef";
    for (int index = 0; index < source.Length; index++)
    {
        characters[index * 2] = hex[source[index] >> 4];
        characters[(index * 2) + 1] = hex[source[index] & 0x0F];
    }
});
