using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Mesh.Core;

/// <summary>
/// Long-lived TCP listener for incoming pairing connections. Binds once (port 0 = OS picks
/// a free ephemeral port; advertise <see cref="Port"/> via mDNS) and stays bound for the
/// app's lifetime. Each accepted connection is handled and closed independently, so a
/// failed/timed-out attempt never leaks or blocks the listening port.
/// </summary>
public sealed class PairingListener : IDisposable
{
    private readonly TcpListener _listener;

    public int Port { get; }

    public PairingListener(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Waits (indefinitely, until <paramref name="ct"/>) for one incoming connection and
    /// performs the TLS handshake (bounded by 10 s). The returned session is owned by the
    /// caller. If the handshake fails the accepted socket is closed before rethrowing.
    /// </summary>
    public async Task<PairingSession> AcceptAsync(X509Certificate2 identity, CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct);
        return await PairingSession.AcceptAsync(client, identity);
    }

    /// <summary>
    /// Accept loop: handles one incoming connection at a time via <paramref name="handler"/>
    /// and then goes back to listening, until <paramref name="ct"/> is cancelled or the
    /// listener is disposed. Handshake failures from a peer are reported through
    /// <paramref name="onError"/> and do not stop the loop. The session passed to the
    /// handler is disposed by the loop after the handler returns.
    /// </summary>
    public async Task RunAcceptLoopAsync(X509Certificate2 identity, Func<PairingSession, Task> handler,
        Action<Exception>? onError = null, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            PairingSession session;
            try
            {
                session = await AcceptAsync(identity, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return; // listener stopped
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex); // e.g. TLS handshake timeout from a stalled peer
                continue;
            }

            using (session)
            {
                try
                {
                    await handler(session);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
            }
        }
    }

    public void Dispose() => _listener.Stop();
}
