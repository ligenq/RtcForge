using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace RtcForge.Dtls;

public sealed class DtlsCertificate
{
    public Org.BouncyCastle.Tls.Certificate TlsCertificate { get; }
    public AsymmetricKeyParameter PrivateKey { get; }
    public string Fingerprint { get; }

    private DtlsCertificate(Org.BouncyCastle.Tls.Certificate tlsCertificate, AsymmetricKeyParameter privateKey, string fingerprint)
    {
        TlsCertificate = tlsCertificate;
        PrivateKey = privateKey;
        Fingerprint = fingerprint;
    }

    public static DtlsCertificate Generate(TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var crypto = new BcTlsCrypto(new SecureRandom());
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=RtcForge",
            ecdsa,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var x509Cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        byte[] der = x509Cert.Export(X509ContentType.Cert);
        var tlsCert = new Org.BouncyCastle.Tls.Certificate([crypto.CreateCertificate(der)]);
        var privateKey = PrivateKeyFactory.CreateKey(ecdsa.ExportPkcs8PrivateKey());

        // Calculate fingerprint
        byte[] hash = SHA256.HashData(der);
        string fingerprint = BitConverter.ToString(hash).Replace("-", ":").ToUpper();

        return new DtlsCertificate(tlsCert, privateKey, fingerprint);
    }
}
