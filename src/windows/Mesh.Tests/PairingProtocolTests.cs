using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class PairingProtocolTests
{
    [Fact]
    public async Task String_RoundTrips_WithBigEndianLengthPrefix()
    {
        using var ms = new MemoryStream();
        await PairingProtocol.WriteStringAsync(ms, "Pixel 8 — Δ");
        var bytes = ms.ToArray();
        // "Pixel 8 — Δ" is 14 UTF-8 bytes ("—" = 3, "Δ" = 2); header must be 00 00 00 0E.
        Assert.Equal(new byte[] { 0, 0, 0, 14 }, bytes[..4]);
        Assert.Equal(18, bytes.Length);

        ms.Position = 0;
        Assert.Equal("Pixel 8 — Δ", await PairingProtocol.ReadStringAsync(ms));
    }

    [Fact]
    public async Task ReadString_RejectsOversizedLength()
    {
        using var ms = new MemoryStream(new byte[] { 0x00, 0x10, 0x00, 0x00 });
        await Assert.ThrowsAsync<InvalidDataException>(() => PairingProtocol.ReadStringAsync(ms));
    }

    [Fact]
    public async Task ReadBytes_ThrowsOnEarlyEof()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => PairingProtocol.ReadBytesAsync(ms, 4));
    }

    [Fact]
    public void Commitment_IsSha256OfNonce_KnownVector()
    {
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.Equal("be45cb2605bf36bebde684841a28f0fd43c69850a3dce5fedba69928ee3a8991",
            Convert.ToHexString(PairingProtocol.Commit(nonce)).ToLowerInvariant());
    }

    [Fact]
    public void VerifyCommitment_AcceptsMatchingNonce_RejectsOthers()
    {
        var nonce = PairingProtocol.CreateNonce();
        Assert.Equal(16, nonce.Length);
        var commitment = PairingProtocol.Commit(nonce);
        Assert.True(PairingProtocol.VerifyCommitment(commitment, nonce));

        var tampered = (byte[])nonce.Clone();
        tampered[0] ^= 0x01;
        Assert.False(PairingProtocol.VerifyCommitment(commitment, tampered));
        Assert.False(PairingProtocol.VerifyCommitment(commitment, new byte[15]));
    }

    [Fact]
    public void CreateNonce_IsRandom()
    {
        Assert.NotEqual(PairingProtocol.CreateNonce(), PairingProtocol.CreateNonce());
    }
}
