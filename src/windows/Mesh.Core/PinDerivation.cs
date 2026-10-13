using System.Security.Cryptography;
using System.Text;

namespace Mesh.Core;

public static class PinDerivation
{
    public static string Derive(string fingerprintA, string fingerprintB)
    {
        var ordered = new[] { fingerprintA, fingerprintB };
        Array.Sort(ordered, StringComparer.Ordinal);
        var combined = ordered[0] + ordered[1];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        var num = ((hash[0] << 16) | (hash[1] << 8) | hash[2]) % 1_000_000;
        return num.ToString("D6");
    }
}
