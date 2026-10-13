using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mesh.Core;

public static class IdentityStore
{
    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            return new X509Certificate2(pfxPath, (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=MeshDevice",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        var exportable = new X509Certificate2(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, exportable.Export(X509ContentType.Pfx));
        return exportable;
    }

    public static string Fingerprint(X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
