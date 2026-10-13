using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Mesh.Core;

public class PairingSession
{
    public string? PeerFingerprint { get; set; }
    private X509Certificate2? _localIdentity;

    public async Task StartListener(X509Certificate2 identity, int port)
    {
        _localIdentity = identity;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        using var client = await listener.AcceptTcpClientAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        using var sslStream = new SslStream(client.GetStream(), false,
            (sender, cert, chain, errors) => true); // untrusted at handshake stage, per spec

        await sslStream.AuthenticateAsServerAsync(identity, clientCertificateRequired: true,
            checkCertificateRevocation: false).WaitAsync(TimeSpan.FromSeconds(10));

        var peerCert = sslStream.RemoteCertificate as X509Certificate2
            ?? new X509Certificate2(sslStream.RemoteCertificate!);
        PeerFingerprint = IdentityStore.Fingerprint(peerCert);
    }

    public async Task ConnectTo(X509Certificate2 identity, string ip, int port)
    {
        _localIdentity = identity;
        using var client = new TcpClient();
        await client.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromSeconds(10));

        using var sslStream = new SslStream(client.GetStream(), false,
            (sender, cert, chain, errors) => true); // untrusted at handshake stage, per spec

        var clientCerts = new X509Certificate2Collection(identity);
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = ip,
            ClientCertificates = clientCerts
            // NOTE: do not also set RemoteCertificateValidationCallback here -- the
            // SslStream constructor above already supplies one, and SslStream throws
            // InvalidOperationException ("already set in the SslStream constructor")
            // if AuthenticateAsClientAsync's options try to set it again.
        }).WaitAsync(TimeSpan.FromSeconds(10));

        var peerCert = sslStream.RemoteCertificate as X509Certificate2
            ?? new X509Certificate2(sslStream.RemoteCertificate!);
        PeerFingerprint = IdentityStore.Fingerprint(peerCert);
    }

    public string ComputePin()
    {
        if (_localIdentity == null || PeerFingerprint == null)
            throw new InvalidOperationException("Handshake has not completed yet.");
        var ownFingerprint = IdentityStore.Fingerprint(_localIdentity);
        return PinDerivation.Derive(ownFingerprint, PeerFingerprint);
    }

    public bool IsPeerTrusted(string pairedDevicesPath, string uuid)
    {
        var devices = PairedDeviceStore.Load(pairedDevicesPath);
        var record = devices.FirstOrDefault(d => d.Uuid == uuid);
        return record != null && record.Fingerprint == PeerFingerprint;
    }
}
