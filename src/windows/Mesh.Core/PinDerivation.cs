using System.Security.Cryptography;
using System.Text;

namespace Mesh.Core;

/// <summary>
/// 6-digit pairing PIN derivation. MUST stay byte-for-byte identical to
/// android/app/src/main/java/com/mesh/core/PinDerivation.kt (see plan Global Constraints).
///
/// Inputs: both peers' certificate SHA-256 fingerprints (64-char lowercase hex, no
/// separators) and both peers' 16-byte pairing nonces (exchanged via commit-then-reveal,
/// see <see cref="PairingProtocol"/>).
///
/// Algorithm:
///   1. Sort the two fingerprint strings ascending (ordinal) -> f0, f1.
///   2. Hex-encode each nonce as 32 lowercase hex chars, sort those two strings ascending
///      (ordinal) -> n0, n1. Fingerprints and nonces are sorted independently.
///   3. message = UTF-8(f0 + f1 + n0 + n1)  (64 + 64 + 32 + 32 = 192 ASCII bytes).
///   4. hash = SHA-256(message).
///   5. num = ((hash[0] &lt;&lt; 16) | (hash[1] &lt;&lt; 8) | hash[2]) % 1_000_000.
///   6. PIN = num formatted as 6-digit zero-padded decimal.
///
/// Mixing in the committed nonces means an active man-in-the-middle cannot pre-compute a
/// certificate whose PIN collides with both honest peers' PINs: each honest side's nonce is
/// unknown to the attacker at the time the attacker must commit to its own.
/// </summary>
public static class PinDerivation
{
    public const int NonceLength = 16;

    public static string Derive(string fingerprintA, string fingerprintB, byte[] nonceA, byte[] nonceB)
    {
        if (nonceA.Length != NonceLength || nonceB.Length != NonceLength)
            throw new ArgumentException($"Nonces must be exactly {NonceLength} bytes.");

        var fps = new[] { fingerprintA, fingerprintB };
        Array.Sort(fps, StringComparer.Ordinal);
        var nonces = new[]
        {
            Convert.ToHexString(nonceA).ToLowerInvariant(),
            Convert.ToHexString(nonceB).ToLowerInvariant()
        };
        Array.Sort(nonces, StringComparer.Ordinal);

        var combined = fps[0] + fps[1] + nonces[0] + nonces[1];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        var num = ((hash[0] << 16) | (hash[1] << 8) | hash[2]) % 1_000_000;
        return num.ToString("D6");
    }
}
