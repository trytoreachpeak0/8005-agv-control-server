using System.Security.Cryptography;
using System.Text;

namespace ControlServer.Host.Transport;

internal static class WireContentHash
{
    public static string Sha256(string wireLine)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(wireLine));
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
}
