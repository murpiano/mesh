using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mesh.Core;

/// <summary>
/// Post-TLS-handshake pairing protocol. MUST stay wire-compatible with
/// android/app/src/main/java/com/mesh/core/PairingProtocol.kt.
///
/// Both peers run the same symmetric sequence over the open TLS stream. Every step is
/// "send own value, then read peer's value", so neither side has to know whether it is
/// the TCP initiator or the listener:
///
///   1. uuid        string: u32 big-endian byte length + UTF-8 bytes (max 1024 bytes)
///   2. name        string: same encoding
///   3. trusted     1 byte: 0x01 if this side has a stored record for the peer's uuid whose
///                  fingerprint equals the live TLS peer fingerprint, else 0x00.
///                  If BOTH sides sent 0x01 the session is a trusted reconnect and the
///                  protocol ends here (no PIN). Otherwise continue with full pairing.
///   4. commitment  32 raw bytes: SHA-256(own 16-byte random nonce)
///   5. nonce       16 raw bytes: the nonce itself. Sent only after the peer's commitment
///                  has been received. Receiver verifies SHA-256(nonce) == commitment and
///                  aborts pairing on mismatch.
///   -- both sides compute the PIN (<see cref="PinDerivation"/>) and ask their user --
///   6. confirm     1 byte: 0x01 = user tapped Match, 0x00 = Not matching / declined /
///                  timed out. Pairing succeeds only if both sides sent 0x01.
///
/// Steps 1-5 use the 10 s network timeout; step 6 uses a separate 60 s user-interaction
/// budget for the local prompt plus 60 s for waiting on the peer's byte.
/// </summary>
public static class PairingProtocol
{
    public const int MaxStringBytes = 1024;
    public const int CommitmentLength = 32;
    public const byte Yes = 0x01;
    public const byte No = 0x00;

    public static async Task WriteStringAsync(Stream stream, string value, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
            throw new ArgumentException($"String exceeds {MaxStringBytes} bytes.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string> ReadStringAsync(Stream stream, CancellationToken ct = default)
    {
        var header = await ReadBytesAsync(stream, 4, ct);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxStringBytes)
            throw new InvalidDataException($"Peer sent a string of {length} bytes (max {MaxStringBytes}).");
        var bytes = await ReadBytesAsync(stream, (int)length, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    public static async Task WriteBytesAsync(Stream stream, byte[] bytes, CancellationToken ct = default)
    {
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadBytesAsync(Stream stream, int count, CancellationToken ct = default)
    {
        var buffer = new byte[count];
        // Throws EndOfStreamException if the peer closes early.
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    public static Task WriteByteAsync(Stream stream, byte value, CancellationToken ct = default) =>
        WriteBytesAsync(stream, new[] { value }, ct);

    public static async Task<byte> ReadByteAsync(Stream stream, CancellationToken ct = default) =>
        (await ReadBytesAsync(stream, 1, ct))[0];

    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(PinDerivation.NonceLength);

    public static byte[] Commit(byte[] nonce) => SHA256.HashData(nonce);

    public static bool VerifyCommitment(byte[] commitment, byte[] revealedNonce) =>
        revealedNonce.Length == PinDerivation.NonceLength &&
        CryptographicOperations.FixedTimeEquals(commitment, Commit(revealedNonce));
}
