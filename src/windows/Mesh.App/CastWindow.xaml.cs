using Mesh.Core;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mesh.App;

/// <summary>
/// The window one cast is shown in — its own window, not a page of the main one, so it can be
/// thrown onto another monitor, made full screen or stretched across everything while Mesh
/// itself stays where it is. Closing it ends the cast.
/// </summary>
public partial class CastWindow : Window
{
    private readonly CastDecoder _decoder = new();
    private WriteableBitmap? _bitmap;
    private CastPlacementMode _mode = CastPlacementMode.Window;

    /// <summary>Raised when the decoder gives up; the session turns this into a stop.</summary>
    public event Action<string>? DecodeFailed;

    public CastWindow(string peerName)
    {
        InitializeComponent();
        Title = $"{peerName} — Mesh";
        ShowState($"Waiting for {peerName}'s screen...");
        _decoder.Failed += reason => Dispatcher.Invoke(() =>
        {
            ShowState(reason);
            DecodeFailed?.Invoke(reason);
        });
        Closed += (_, _) => _decoder.Dispose();
    }

    /// <summary>Hands one frame from the media plane to the decoder and draws whatever comes
    /// out. Safe to call from any thread.</summary>
    public void Push(CastFrame frame)
    {
        var picture = _decoder.Decode(frame);
        if (picture == null) return;
        Dispatcher.Invoke(() => Draw(picture));
    }

    /// <summary>Shows a line instead of a picture: waiting, reconnecting, or why a cast
    /// ended.</summary>
    public void ShowState(string text) => Dispatcher.Invoke(() =>
    {
        StateText.Text = text;
        StateText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    });

    /// <summary>
    /// Puts the window where the settings record says. Returns the placement actually applied,
    /// which differs from the one asked for when it named a monitor that is no longer there —
    /// the caller sends that correction back to the other device.
    /// </summary>
    public CastPlacement ApplyPlacement(CastPlacement placement, IReadOnlyList<CastMonitor> monitors)
    {
        var applied = CastSettings.CorrectPlacement(placement, monitors);
        _mode = applied.Mode;

        switch (applied.Mode)
        {
            case CastPlacementMode.Fullscreen:
                // WPF ignores a Left/Top move while already Maximized, and Maximized->Maximized
                // is a no-op transition: switching monitors while fullscreen would otherwise
                // leave the window on the old one. Drop to Normal first so the move takes, then
                // maximize onto the new bounds.
                WindowState = WindowState.Normal;
                MoveToPhysical(CastMonitors.BoundsOf(applied.MonitorId));
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                break;
            case CastPlacementMode.AllMonitors:
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                MoveToPhysical(CastMonitors.VirtualDesktop());
                break;
            default:
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                if (applied.Width > 0 && applied.Height > 0)
                    MoveToPhysical((applied.X, applied.Y, applied.Width, applied.Height));
                break;
        }

        return applied;
    }

    /// <summary>Where the window is now, in the shape the settings record carries, so moving it
    /// by hand is what the other device sees next.</summary>
    public CastPlacement CurrentPlacement(string monitorId) =>
        new(_mode, monitorId, (int)Left, (int)Top, (int)Width, (int)Height);

    private void Draw(CastPicture picture)
    {
        if (_bitmap == null || _bitmap.PixelWidth != picture.Width || _bitmap.PixelHeight != picture.Height)
        {
            _bitmap = new WriteableBitmap(picture.Width, picture.Height, 96, 96, PixelFormats.Bgra32, null);
            Picture.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, picture.Width, picture.Height), picture.Bgra,
            picture.Width * 4, 0);
        ShowState(string.Empty);
    }

    /// <summary>WPF positions windows in device-independent units; the monitor rectangles are
    /// physical pixels, so they go through the window's own scaling.</summary>
    private void MoveToPhysical((int X, int Y, int Width, int Height) bounds)
    {
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        Left = bounds.X / scaleX;
        Top = bounds.Y / scaleY;
        Width = bounds.Width / scaleX;
        Height = bounds.Height / scaleY;
    }
}
