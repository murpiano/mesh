using Mesh.Core;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Mesh.App;

/// <summary>
/// One cast being received: the media listener the sender dials, the window the picture goes
/// into, and the loop that carries frames from one to the other. The control messages that
/// start and stop it live in <see cref="MainWindow"/>; everything the cast itself owns is here,
/// so ending it is one Dispose.
/// </summary>
public sealed class CastSession : IDisposable
{
    private readonly CastChannelListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private CastChannel? _channel;
    private bool _disposed;

    public string CastId { get; }
    public string PeerUuid { get; }
    public CastWindow Window { get; }
    public int Port => _listener.Port;
    public string Token { get; }

    /// <summary>The settings as both devices last agreed them. Replaced whole, never edited
    /// field by field — see <see cref="CastSettings"/>.</summary>
    public CastSettings Settings { get; private set; }

    /// <summary>Raised when this side had to change what the other device asked for (a monitor
    /// that is gone): the caller sends the corrected record back.</summary>
    public event Action<CastSettings>? SettingsCorrected;

    /// <summary>Raised once when the cast ends by itself — the window was closed, the sender
    /// vanished, the decoder gave up. Not raised by <see cref="Dispose"/>.</summary>
    public event Action<string>? Ended;

    private CastSession(string castId, string peerUuid, CastChannelListener listener, string token,
        CastWindow window, CastSettings settings)
    {
        CastId = castId;
        PeerUuid = peerUuid;
        _listener = listener;
        Token = token;
        Window = window;
        Settings = settings;
    }

    /// <summary>
    /// Opens the media listener and the window, ready for the sender to dial in. The caller
    /// answers the peer's CastStart with <see cref="Port"/> and <see cref="Token"/>.
    /// </summary>
    public static CastSession Create(string castId, string peerUuid, string peerName,
        string expectedFingerprint, X509Certificate2 identity)
    {
        var ticket = CastChannelListener.NewToken();
        var listener = CastChannelListener.Start(identity, ticket, expectedFingerprint);
        var window = new CastWindow(peerName);
        var monitors = CastMonitors.Current();
        var primary = monitors.Count > 0 ? monitors[0].Id : string.Empty;
        var settings = new CastSettings(
            Quality: 50,
            Audio: false,
            Placement: new CastPlacement(CastPlacementMode.Window, primary, 0, 0, 0, 0),
            Monitors: monitors);
        return new CastSession(castId, peerUuid, listener, ticket, window, settings);
    }

    /// <summary>Waits for the sender and pumps its frames into the window until the cast
    /// ends.</summary>
    public void Start()
    {
        Window.Closed += (_, _) => End("The window was closed.");
        Window.DecodeFailed += reason => End(reason);
        Window.Show();
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                _channel = await _listener.AcceptAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A refused or broken attempt: keep listening, the sender retries.
                DebugLog.Write($"CastSession: refused a media connection ({ex.Message})");
                continue;
            }

            Window.ShowState(string.Empty);
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var frame = await _channel.ReadFrameAsync();
                    Window.Push(frame);
                }
            }
            catch (Exception ex)
            {
                if (_lifetime.IsCancellationRequested) return;
                DebugLog.Write($"CastSession: media plane dropped ({ex.Message})");
                Window.ShowState("Reconnecting...");
            }
            finally
            {
                _channel?.Dispose();
                _channel = null;
            }
        }
    }

    /// <summary>
    /// Applies a settings record that came from the other device and returns what was actually
    /// applied. A placement naming a monitor that is gone is corrected here, and
    /// <see cref="SettingsCorrected"/> tells the caller to send the correction back.
    /// </summary>
    public CastSettings Apply(CastSettings settings)
    {
        var monitors = CastMonitors.Current();
        var placement = CastSettings.CorrectPlacement(settings.Placement, monitors);
        var applied = settings with { Placement = placement, Monitors = monitors };

        Window.Dispatcher.Invoke(() => Window.ApplyPlacement(applied.Placement, monitors));
        Settings = applied;

        if (!Equals(placement, settings.Placement) || !MonitorsMatch(settings.Monitors, monitors))
            SettingsCorrected?.Invoke(applied);
        return applied;
    }

    private static bool MonitorsMatch(IReadOnlyList<CastMonitor> a, IReadOnlyList<CastMonitor> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }

    private void End(string reason)
    {
        if (_disposed) return;
        Dispose();
        Ended?.Invoke(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _channel?.Dispose();
        _listener.Dispose();
        Window.Dispatcher.Invoke(() =>
        {
            if (Window.IsVisible) Window.Close();
        });
    }
}
