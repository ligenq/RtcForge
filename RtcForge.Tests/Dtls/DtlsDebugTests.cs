using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using System.Reflection;
using RtcForge.Dtls;

namespace RtcForge.Tests.Dtls;

public class DtlsDebugTests
{
    [Fact]
    public void InspectDefaultCipherSuites()
    {
        var crypto = new BcTlsCrypto(new SecureRandom());
        var cert = DtlsCertificate.Generate();
        var client = new WebRtcTlsClient(crypto, cert, null, null);

        // Get the default cipher suites via reflection
        var getSupportedMethod = typeof(DefaultTlsClient).GetMethod("GetSupportedCipherSuites",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var getCipherSuitesMethod = typeof(AbstractTlsClient).GetMethod("GetCipherSuites",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        if (getSupportedMethod != null)
        {
            var suites = getSupportedMethod.Invoke(client, null) as int[];
            if (suites != null)
            {
                foreach (var s in suites)
                {
                    Console.WriteLine($"SUPPORTED: 0x{s:X4}");
                }

                Assert.Equal(RtcForge.Dtls.DtlsTransport.SupportedDtlsCipherSuites, suites);
            }
        }

        if (getCipherSuitesMethod != null)
        {
            var suites = getCipherSuitesMethod.Invoke(client, null) as int[];
            if (suites != null)
            {
                foreach (var s in suites)
                {
                    Console.WriteLine($"GETCIPHER: 0x{s:X4}");
                }

                Assert.Equal(RtcForge.Dtls.DtlsTransport.SupportedDtlsCipherSuites, suites);
            }
        }
    }
}
