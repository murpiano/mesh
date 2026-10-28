using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Mesh.Core;

/// <summary>
/// The media plane of one cast: a TLS connection that carries nothing but
/// <see cref="CastFrame"/>s, opened for the cast and closed with it. Separate from the session
/// on purpose — a picture must never delay a heartbeat or a chat message.
///
/// It is not a second way into a device. The listener accepts a connection only when the peer
/// certificate's fingerprint is the one the trust store already holds for that device AND the
/// dialer presents the one-time token the receiver handed out over the (already authenticated)
/// session. Mirrors the Android app's CastChannel.kt.
/// </summary>
public sealed class CastChannel : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly SslStream _stream;

    private CastChannel(TcpClient client, SslStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>Sending side: dials the receiver's media listener and presents the token.</summary>
    public static async Task<CastChannel> ConnectAsync(X509Certificate2 identity, string ip, int port,
        string token, CancellationToken ct = default)
    {
        var client = new TcpClient();
        SslStream? stream = null;
        try
        {
            await client.ConnectAsync(ip, port, ct).AsTask().WaitAsync(HandshakeTimeout, ct);
            stream = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = ip,
                ClientCertificates = new X509Certificate2Collection(identity)
            }, ct).WaitAsync(HandshakeTimeout, ct);
            await PairingProtocol.WriteStringAsync(stream, token);
            await stream.FlushAsync(ct);
            return new CastChannel(client, stream);
        }
        catch
        {
            stream?.Dispose();
            client.Dispose();
            throw;
        }
    }

    internal static async Task<CastChannel> AcceptAsync(TcpClient client, X509Certificate2 identity,
        string expectedToken, string expectedFingerprint)
    {
        SslStream? stream = null;
        try
        {
            stream = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await stream.AuthenticateAsServerAsync(identity, clientCertificateRequired: true,
                checkCertificateRevocation: false).WaitAsync(HandshakeTimeout);

            var peerCert = stream.RemoteCertificate as X509Certificate2
                ?? new X509Certificate2(stream.RemoteCertificate!);
            if (IdentityStore.Fingerprint(peerCert) != expectedFingerprint)
                throw new InvalidOperationException("Cast connection from an unexpected certificate.");

            var token = await PairingProtocol.ReadStringAsync(stream).WaitAsync(HandshakeTimeout);
            if (!FixedTimeEquals(token, expectedToken))
                throw new InvalidOperationException("Cast connection with the wrong token.");

            return new CastChannel(client, stream);
        }
        catch
        {
            stream?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public Task WriteFrameAsync(CastFrame frame) => CastFrame.WriteAsync(_stream, frame);

    public Task<CastFrame> ReadFrameAsync() => CastFrame.ReadAsync(_stream);

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>
/// The receiving side's media listener: open only while one cast is being set up or running, on
/// an ephemeral port the receiver tells the sender over the session
/// (<see cref="CastReadyMessage"/>). A connection that fails the fingerprint or token check is
/// closed and the listener goes on listening, so one bad attempt cannot block the real sender.
/// </summary>
public sealed class CastChannelListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _identity;
    private readonly string _expectedToken;
    private readonly string _expectedFingerprint;

    public int Port { get; }

    /// <summary>A fresh token for one cast: 32 bytes of CSPRNG output, base64url, no padding.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private CastChannelListener(TcpListener listener, X509Certificate2 identity, string expectedToken,
        string expectedFingerprint)
    {
        _listener = listener;
        _identity = identity;
        _expectedToken = expectedToken;
        _expectedFingerprint = expectedFingerprint;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static CastChannelListener Start(X509Certificate2 identity, string expectedToken,
        string expectedFingerprint, int port = 0)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        return new CastChannelListener(listener, identity, expectedToken, expectedFingerprint);
    }

    /// <summary>Waits for the sender. Throws when a connection turned up and was refused, so the
    /// caller can log it; the listener itself stays usable.</summary>
    public async Task<CastChannel> AcceptAsync(CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct);
        return await CastChannel.AcceptAsync(client, _identity, _expectedToken, _expectedFingerprint);
    }

    public void Dispose() => _listener.Dispose();
}
