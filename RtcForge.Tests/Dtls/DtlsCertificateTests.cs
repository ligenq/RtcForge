using Org.BouncyCastle.Tls;
using RtcForge.Dtls;

namespace RtcForge.Tests.Dtls;

public class DtlsCertificateTests
{
    [Fact]
    public void Generate_ProducesValidCertificateAndFingerprint()
    {
        // Act
        var cert = DtlsCertificate.Generate();

        // Assert
        Assert.NotNull(cert);
        Assert.NotNull(cert.TlsCertificate);
        Assert.NotNull(cert.PrivateKey);
        Assert.NotNull(cert.Fingerprint);

        // Fingerprint should be a colon-separated hex string (SHA-256 is 32 bytes = 64 chars + 31 colons = 95 chars)
        Assert.NotEmpty(cert.Fingerprint);
        Assert.Contains(":", cert.Fingerprint);
        Assert.Equal(95, cert.Fingerprint.Length);
    }

    [Fact]
    public void ValidateFingerprint_AcceptsMatchingFingerprint()
    {
        var cert = DtlsCertificate.Generate();
        byte[] der = cert.TlsCertificate.GetCertificateAt(0).GetEncoded();

        RtcForge.Dtls.DtlsTransport.ValidateFingerprint(der, "sha-256", cert.Fingerprint);
    }

    [Fact]
    public void ValidateFingerprint_RejectsMismatchedFingerprint()
    {
        var cert = DtlsCertificate.Generate();
        byte[] der = cert.TlsCertificate.GetCertificateAt(0).GetEncoded();

        Assert.Throws<TlsFatalAlert>(() => RtcForge.Dtls.DtlsTransport.ValidateFingerprint(der, "sha-256", new string('0', cert.Fingerprint.Length)));
    }

    [Theory]
    [InlineData(null, "AA")]
    [InlineData("sha-256", null)]
    [InlineData("", "AA")]
    [InlineData("sha-256", "")]
    public void ValidateFingerprint_RejectsMissingFingerprintParameters(string? algorithm, string? fingerprint)
    {
        var cert = DtlsCertificate.Generate();
        byte[] der = cert.TlsCertificate.GetCertificateAt(0).GetEncoded();

        Assert.Throws<TlsFatalAlert>(() => RtcForge.Dtls.DtlsTransport.ValidateFingerprint(der, algorithm, fingerprint));
    }
}
