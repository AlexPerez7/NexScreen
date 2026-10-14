using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NexScreen.Core.Security;

/// <summary>
/// Certificado TLS del host para las conexiones por WiFi. Se crea una vez (ECDSA P-256,
/// autofirmado) y se guarda en el almacén de certificados del usuario (CurrentUser\My), que
/// protege la clave privada. Las tablets emparejadas fijan su huella.
/// </summary>
internal static class HostIdentity
{
    private const string FriendlyName = "NexScreen host";
    private const string Subject = "CN=NexScreen host";

    public static X509Certificate2 LoadOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, Subject, validOnly: false)
            .Where(c => c.FriendlyName == FriendlyName && c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        if (existing is not null) return existing;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        // La validez no importa (se fija la huella), pero Schannel rechaza certificados vencidos.
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(50));

        // Schannel no puede usar una clave efímera: hay que importarla como persistente.
        var certificate = new X509Certificate2(
            ephemeral.Export(X509ContentType.Pkcs12), (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        certificate.FriendlyName = FriendlyName;
        store.Add(certificate);
        Log.Info($"Certificado del host creado (huella {Fingerprint.Of(certificate)[..16]}…)");
        return certificate;
    }
}

/// <summary>Huella de un certificado: SHA-256 del DER, en hexadecimal en minúsculas.</summary>
internal static class Fingerprint
{
    public static string Of(X509Certificate certificate) => Of(certificate.GetRawCertData());

    public static string Of(byte[] der) => Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant();
}
