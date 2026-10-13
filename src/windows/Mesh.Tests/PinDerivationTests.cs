using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class PinDerivationTests
{
    private const string FpA = "a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5";
    private const string FpB = "0123456789abcdef110123456789abcdef110123456789abcdef110123456789";
    private const string FpF = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string FpZero = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Derive_IsOrderIndependent_ForKnownVector()
    {
        Assert.Equal("936941", PinDerivation.Derive(FpA, FpB));
        Assert.Equal("936941", PinDerivation.Derive(FpB, FpA));
    }

    [Fact]
    public void Derive_MatchesSecondKnownVector()
    {
        Assert.Equal("859565", PinDerivation.Derive(FpF, FpZero));
    }

    [Fact]
    public void Derive_DiffersForDifferentFingerprints()
    {
        var pin1 = PinDerivation.Derive(FpA, FpB);
        var pin2 = PinDerivation.Derive(FpA, FpF);
        Assert.NotEqual(pin1, pin2);
    }

    [Fact]
    public void Derive_IsAlwaysSixDigits()
    {
        var pin = PinDerivation.Derive(FpA, FpF);
        Assert.Equal(6, pin.Length);
    }
}
