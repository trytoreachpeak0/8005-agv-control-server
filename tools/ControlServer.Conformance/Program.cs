using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControlServer.Domain;

Dictionary<string, string> arguments = ParseArguments(args);
string slice = arguments.GetValueOrDefault("slice")
    ?? throw new ArgumentException("--slice is required.");
string manifestPath = arguments.GetValueOrDefault("manifest")
    ?? throw new ArgumentException("--manifest is required.");
// The v2 family is FP-IS-NN and replaces W2G-IS-00 through 07 outright: the scope specification's
// section 7.1 rejected coexistence, so a W2G id reaching this preflight is a caller still pointed
// at the superseded family rather than a slice to run.
//
// The pattern is the protocol's own, character for character -- it is
// schemas/governance/integration-slice-index.schema.json's, and section 6.6 item 4 calls this the
// second copy of that regex. A tighter one here (^FP-IS-(0[0-9]|1[0-5])$, say) would say something
// the protocol does not: how many slices exist is a fact of the index, and checking an id against
// the index is scripts/test-wire-to-gate.ps1's job, which it does by looking the slice up in the
// vendored copy and refusing when it is absent. Shape here, membership there.
if (!Regex.IsMatch(slice, "^FP-IS-[0-9]{2}$", RegexOptions.CultureInvariant))
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
