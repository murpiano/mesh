using Mesh.Core;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Mesh.App;

/// <summary>One decoded picture, as BGRA32 rows ready to go into a WriteableBitmap.</summary>
public sealed record CastPicture(byte[] Bgra, int Width, int Height);

/// <summary>
/// Decodes the H.264 the phone sends, using the decoder that is already part of Windows (a
/// Media Foundation Transform) rather than a bundled one: the desktop app ships as a single
/// self-contained .exe, and a bundled decoder would add tens of megabytes to the download.
///
/// NV12 is converted to BGRA here, in managed code, instead of chaining a second transform. It
/// is the simplest thing that works and it is fast enough at the sizes this carries; if it ever
/// shows up as the bottleneck, the Video Processor MFT is the drop-in replacement.
/// </summary>
public sealed class CastDecoder : IDisposable
{
    // MFT_ENUM_FLAG_*, from mfapi.h.
    private const uint EnumFlagSyncMft = 0x00000001;
    private const uint EnumFlagSortAndFilter = 0x00000040;

    // Media Foundation HRESULTs that are part of the normal decode loop, not failures.
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);

    private static int _startups;
    private static readonly object StartupLock = new();

    private IMFTransform? _decoder;
    private int _width;
    private int _height;
    private byte[]? _codecConfig;
    private bool _disposed;

    /// <summary>Raised when the decoder gives up: the window shows this instead of a picture.</summary>
    public event Action<string>? Failed;

    /// <summary>Takes the codec configuration (SPS/PPS) the sender puts in front of its stream.
    /// It is kept and prepended to every keyframe, so a decoder rebuilt mid-cast — or one that
    /// joined after the stream started — has what it needs.</summary>
    public void Configure(ReadOnlySpan<byte> codecConfig) => _codecConfig = codecConfig.ToArray();

    /// <summary>Feeds one frame in and returns the picture that came out, or null when the
    /// decoder needs more input before it can produce one.</summary>
    public CastPicture? Decode(CastFrame frame)
    {
        if (_disposed) return null;
        try
        {
            switch (frame.Type)
            {
                case CastFrameType.VideoConfig:
                    Configure(frame.Payload.Span);
                    Reset();
                    return null;
                case CastFrameType.VideoKey:
                case CastFrameType.VideoDelta:
                    break;
                default:
                    return null; // audio is not this object's business
            }

            var decoder = _decoder ??= CreateDecoder();
            var payload = frame.Type == CastFrameType.VideoKey && _codecConfig is { Length: > 0 }
                ? Concat(_codecConfig, frame.Payload.Span)
                : frame.Payload.ToArray();

            decoder.ProcessInput(0, CreateSample(payload, frame.TimestampUs), 0);
            return ReadPicture(decoder);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
            Reset();
            return null;
        }
    }

    private CastPicture? ReadPicture(IMFTransform decoder)
    {
        while (true)
        {
            var buffers = new OutputDataBuffer[1];
            var result = decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers[0], out _);

            if (result.Code == NeedMoreInput) return null;
            if (result.Code == StreamChange)
            {
                SelectOutputType(decoder);
                continue;
            }
            result.CheckError();

            var sample = buffers[0].Sample;
            if (sample == null) return null;
            try
            {
                return ToPicture(sample);
            }
            finally
            {
                sample.Dispose();
            }
        }
    }

    private IMFTransform CreateDecoder()
    {
        EnsureMediaFoundationStarted();

        using var activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            EnumFlagSyncMft | EnumFlagSortAndFilter,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            null);

        var activate = activates.FirstOrDefault()
            ?? throw new NotSupportedException("Windows has no H.264 decoder available.");
        var decoder = activate.ActivateObject<IMFTransform>();

        var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        decoder.SetInputType(0, input, 0);

        SelectOutputType(decoder);
        decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        return decoder;
    }

    /// <summary>Picks NV12 out of what the decoder offers and reads the frame size it settled
    /// on — which is how a rotation or a resolution change arrives here.</summary>
    private void SelectOutputType(IMFTransform decoder)
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = decoder.GetOutputAvailableType(0, index);
            }
            catch (Exception)
            {
                throw new NotSupportedException("The H.264 decoder offers no NV12 output.");
            }

            if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;

            decoder.SetOutputType(0, candidate, 0);
            var frameSize = candidate.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            _width = (int)(frameSize >> 32);
            _height = (int)(frameSize & 0xFFFFFFFF);
            return;
        }
    }

    private CastPicture? ToPicture(IMFSample sample)
    {
        if (_width <= 0 || _height <= 0) return null;

        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var scan0, out _, out var length);
        try
        {
            var nv12 = new byte[length];
            Marshal.Copy(scan0, nv12, 0, length);
            return new CastPicture(Nv12ToBgra(nv12, _width, _height), _width, _height);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>NV12 is a full-size luma plane followed by a half-size interleaved chroma plane;
    /// this is the usual BT.601 expansion of that into BGRA32.</summary>
    private static byte[] Nv12ToBgra(byte[] nv12, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        var chromaOffset = width * height;
        for (var y = 0; y < height; y++)
        {
            var lumaRow = y * width;
            var chromaRow = chromaOffset + (y / 2) * width;
            for (var x = 0; x < width; x++)
            {
                var luma = nv12[lumaRow + x] - 16;
                var u = nv12[chromaRow + (x & ~1)] - 128;
                var v = nv12[chromaRow + (x & ~1) + 1] - 128;

                var c = 298 * luma;
                var r = (c + 409 * v + 128) >> 8;
                var g = (c - 100 * u - 208 * v + 128) >> 8;
                var b = (c + 516 * u + 128) >> 8;

                var target = (lumaRow + x) * 4;
                bgra[target] = Clamp(b);
                bgra[target + 1] = Clamp(g);
                bgra[target + 2] = Clamp(r);
                bgra[target + 3] = 255;
            }
        }
        return bgra;
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    private static IMFSample CreateSample(byte[] payload, long timestampUs)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(payload.Length);
        buffer.Lock(out var scan0, out _, out _);
        Marshal.Copy(payload, 0, scan0, payload.Length);
        buffer.Unlock();
        buffer.CurrentLength = payload.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestampUs * 10; // Media Foundation counts in 100-nanosecond units
        return sample;
    }

    private static byte[] Concat(byte[] first, ReadOnlySpan<byte> second)
    {
        var joined = new byte[first.Length + second.Length];
        first.CopyTo(joined, 0);
        second.CopyTo(joined.AsSpan(first.Length));
        return joined;
    }

    private static void EnsureMediaFoundationStarted()
    {
        lock (StartupLock)
        {
            if (_startups++ > 0) return;
            MediaFactory.MFStartup(false).CheckError();
        }
    }

    private void Reset()
    {
        _decoder?.Dispose();
        _decoder = null;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }
}
