using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace RtcForge.Dtls;

public class DtlsCertificate
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

    public static DtlsCertificate Generate()
    {
        var crypto = new BcTlsCrypto(new SecureRandom());
        var random = new SecureRandom();
        var kpGen = new ECKeyPairGenerator();
        kpGen.Init(new ECKeyGenerationParameters(Org.BouncyCastle.Asn1.X9.ECNamedCurveTable.GetOid("P-256"), random));
        var kp = kpGen.GenerateKeyPair();

        var gen = new X509V3CertificateGenerator();
        var serialNumber = BigInteger.ProbablePrime(120, random);
        gen.SetSerialNumber(serialNumber);
        gen.SetSubjectDN(new X509Name("CN=RtcForge"));
        gen.SetIssuerDN(new X509Name("CN=RtcForge"));
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(10));
        gen.SetPublicKey(kp.Public);

        var signatureFactory = new Asn1SignatureFactory("SHA256WithECDSA", kp.Private, random);
        var x509Cert = gen.Generate(signatureFactory);

        // Convert to Org.BouncyCastle.Tls.Certificate
        byte[] der = x509Cert.GetEncoded();
        var tlsCert = new Org.BouncyCastle.Tls.Certificate(new TlsCertificate[] { crypto.CreateCertificate(der) });

        // Calculate fingerprint
        byte[] hash = SHA256.HashData(der);
        string fingerprint = BitConverter.ToString(hash).Replace("-", ":").ToUpper();

        return new DtlsCertificate(tlsCert, kp.Private, fingerprint);
    }
}
