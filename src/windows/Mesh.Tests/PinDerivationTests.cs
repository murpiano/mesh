using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

/// <summary>
/// Test vectors are shared verbatim with android/app/src/test/java/com/mesh/core/PinDerivationTest.kt
/// and were independently cross-checked with a standalone Python implementation of the
/// algorithm documented on <see cref="PinDerivation"/>.
/// </summary>
public class PinDerivationTests
{
    private const string FpA = "a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5";
    private const string FpB = "0123456789abcdef110123456789abcdef110123456789abcdef110123456789";
    private const string FpF = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string FpZero = "0000000000000000000000000000000000000000000000000000000000000000";

    // 0x00..0x0f
    private static readonly byte[] Nonce1 = Enumerable.Range(0x00, 16).Select(i => (byte)i).ToArray();
    // 0xf0..0xff
    private static readonly byte[] Nonce2 = Enumerable.Range(0xf0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] NonceZero = new byte[16];
    private static readonly byte[] NonceFf = Enumerable.Repeat((byte)0xff, 16).ToArray();

    [Fact]
    public void Derive_MatchesKnownVector_AndIsOrderIndependent()
    {
        Assert.Equal("016425", PinDerivation.Derive(FpA, FpB, Nonce1, Nonce2));
        Assert.Equal("016425", PinDerivation.Derive(FpB, FpA, Nonce2, Nonce1));
        // Fingerprints and nonces are sorted independently, so pairing a nonce with
        // "the other" fingerprint must not change the result either.
        Assert.Equal("016425", PinDerivation.Derive(FpA, FpB, Nonce2, Nonce1));
    }

    [Fact]
    public void Derive_MatchesSecondKnownVector()
    {
        Assert.Equal("250919", PinDerivation.Derive(FpF, FpZero, NonceZero, NonceFf));
    }

    [Fact]
    public void Derive_IsSensitiveToFullFingerprint_NotJustAPrefix()
    {
        // Same first 63 hex chars, differing only in the very last one.
        var fpA2 = FpA[..63] + "6";
        Assert.Equal("122587", PinDerivation.Derive(fpA2, FpB, Nonce1, Nonce2));
        Assert.NotEqual(PinDerivation.Derive(FpA, FpB, Nonce1, Nonce2),
                        PinDerivation.Derive(fpA2, FpB, Nonce1, Nonce2));
    }

    [Fact]
    public void Derive_IsSensitiveToNonces()
    {
        var nonce1b = (byte[])Nonce1.Clone();
        nonce1b[15] = 0x10; // only the last byte differs
        Assert.Equal("913817", PinDerivation.Derive(FpA, FpB, nonce1b, Nonce2));
        Assert.NotEqual(PinDerivation.Derive(FpA, FpB, Nonce1, Nonce2),
                        PinDerivation.Derive(FpA, FpB, nonce1b, Nonce2));
    }

    [Fact]
    public void Derive_IsAlwaysSixDigits()
    {
        Assert.Equal(6, PinDerivation.Derive(FpA, FpF, Nonce1, Nonce2).Length);
    }

    [Fact]
    public void Derive_RejectsWrongNonceLength()
    {
        Assert.Throws<ArgumentException>(() => PinDerivation.Derive(FpA, FpB, new byte[15], Nonce2));
    }
}
