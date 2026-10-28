using System.Buffers.Binary;

namespace Mesh.Core;

/// <summary>Where the receiving device puts the cast window.</summary>
public enum CastPlacementMode
{
    /// <summary>An ordinary window, at <see cref="CastPlacement.X"/>/<see cref="CastPlacement.Y"/>.</summary>
    Window = 1,
    /// <summary>Filling the monitor named by <see cref="CastPlacement.MonitorId"/>.</summary>
    Fullscreen = 2,
    /// <summary>Spanning every monitor the receiver has.</summary>
    AllMonitors = 3
}

/// <summary>One monitor of the receiving device, as the sender sees it in its own controls. The
/// receiver is the authority on this list — it sends it, and it corrects a placement that names
/// a monitor that has since gone away.</summary>
public sealed record CastMonitor(string Id, string Name, int Width, int Height, bool IsPrimary);

/// <summary>Where the window sits. <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see
/// cref="Height"/> only mean anything in <see cref="CastPlacementMode.Window"/>; the other modes
/// carry the last known rectangle so going back to a window restores it.</summary>
public sealed record CastPlacement(CastPlacementMode Mode, string MonitorId, int X, int Y, int Width, int Height);

/// <summary>
/// Everything about a running cast that either device can change: how the picture is encoded,
/// whether audio comes with it, and where the receiving device shows it. The whole record is
/// replicated on every change from whichever side made it (last write wins) — it is small and
/// changes only when a human moves a control, so there is nothing to merge.
///
/// Mirrors the Android app's CastSettings.kt byte for byte.
/// </summary>
public sealed record CastSettings(
    int Quality,
    bool Audio,
    CastPlacement Placement,
    IReadOnlyList<CastMonitor> Monitors)
{
    /// <summary>0 is the smooth end (high frame rate, scaled down), 100 the sharp end (native
    /// resolution, few frames). Clamped on read so a peer can never push it out of range.</summary>
    public const int MinQuality = 0;
    public const int MaxQuality = 100;

    /// <summary>More monitors than any desk has. A longer list is a protocol error, not a
    /// truncation, so the two sides never disagree about what was sent.</summary>
    public const int MaxMonitors = 16;

    public static async Task WriteAsync(Stream stream, CastSettings settings)
    {
        if (settings.Monitors.Count > MaxMonitors)
            throw new ArgumentException($"Cast settings carry {settings.Monitors.Count} monitors (max {MaxMonitors}).");

        await PairingProtocol.WriteByteAsync(stream, (byte)Math.Clamp(settings.Quality, MinQuality, MaxQuality));
        await PairingProtocol.WriteByteAsync(stream, settings.Audio ? PairingProtocol.Yes : PairingProtocol.No);

        await PairingProtocol.WriteByteAsync(stream, (byte)settings.Placement.Mode);
        await PairingProtocol.WriteStringAsync(stream, settings.Placement.MonitorId);
        await WriteInt32Async(stream, settings.Placement.X);
        await WriteInt32Async(stream, settings.Placement.Y);
        await WriteInt32Async(stream, settings.Placement.Width);
        await WriteInt32Async(stream, settings.Placement.Height);

        await PairingProtocol.WriteByteAsync(stream, (byte)settings.Monitors.Count);
        foreach (var monitor in settings.Monitors)
        {
            await PairingProtocol.WriteStringAsync(stream, monitor.Id);
            await PairingProtocol.WriteStringAsync(stream, monitor.Name);
            await WriteInt32Async(stream, monitor.Width);
            await WriteInt32Async(stream, monitor.Height);
            await PairingProtocol.WriteByteAsync(stream, monitor.IsPrimary ? PairingProtocol.Yes : PairingProtocol.No);
        }
    }

    public static async Task<CastSettings> ReadAsync(Stream stream)
    {
        var quality = Math.Clamp((int)await PairingProtocol.ReadByteAsync(stream), MinQuality, MaxQuality);
        var audio = await PairingProtocol.ReadByteAsync(stream) == PairingProtocol.Yes;

        var mode = (CastPlacementMode)await PairingProtocol.ReadByteAsync(stream);
        if (!Enum.IsDefined(mode)) throw new InvalidDataException($"Peer sent an unknown placement mode: {(int)mode}.");
        var placement = new CastPlacement(
            mode,
            await PairingProtocol.ReadStringAsync(stream),
            await ReadInt32Async(stream),
            await ReadInt32Async(stream),
            await ReadInt32Async(stream),
            await ReadInt32Async(stream));

        var count = await PairingProtocol.ReadByteAsync(stream);
        if (count > MaxMonitors)
            throw new InvalidDataException($"Peer sent {count} monitors (max {MaxMonitors}).");
        var monitors = new List<CastMonitor>(count);
        for (var i = 0; i < count; i++)
        {
            monitors.Add(new CastMonitor(
                await PairingProtocol.ReadStringAsync(stream),
                await PairingProtocol.ReadStringAsync(stream),
                await ReadInt32Async(stream),
                await ReadInt32Async(stream),
                await PairingProtocol.ReadByteAsync(stream) == PairingProtocol.Yes));
        }

        return new CastSettings(quality, audio, placement, monitors);
    }

    private static async Task WriteInt32Async(Stream stream, int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        await PairingProtocol.WriteBytesAsync(stream, bytes);
    }

    private static async Task<int> ReadInt32Async(Stream stream) =>
        BinaryPrimitives.ReadInt32BigEndian(await PairingProtocol.ReadBytesAsync(stream, 4));
}
