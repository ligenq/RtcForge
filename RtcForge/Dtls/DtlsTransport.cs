using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace RtcForge.Dtls;

public sealed class DtlsTransport : IDtlsTransport
{
    internal static readonly ProtocolVersion[] SupportedDtlsVersions =
    [
        ProtocolVersion.DTLSv12
    ];

    internal static readonly int[] SupportedDtlsCipherSuites =
    [
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384
    ];

    private DtlsState _state = DtlsState.New;
    private readonly Func<byte[], Task> _sendFunc;
    private Org.BouncyCastle.Tls.DtlsTransport? _dtlsTransport;
    private BouncyCastleDatagramTransport? _bcTransport;
    private SrtpKeys? _srtpKeys;
    private readonly DtlsCertificate _certificate;
    private readonly Lock _sendLock = new();
    private string? _remoteFingerprint;
    private string? _remoteFingerprintAlg;
    private int _disposed;

    public DtlsState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                OnStateChange?.Invoke(this, _state);
            }
        }
    }

    public event EventHandler<DtlsState>? OnStateChange;
    public event EventHandler<byte[]>? OnData;

    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<DtlsTransport>? _logger;

    public DtlsTransport(Func<byte[], Task> sendFunc, DtlsCertificate certificate, ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<DtlsTransport>();
        _sendFunc = sendFunc;
        _certificate = certificate;
    }

    public void SetRemoteFingerprint(string algorithm, string fingerprint)
    {
        _remoteFingerprintAlg = algorithm;
        _remoteFingerprint = fingerprint;
    }

    public void HandleIncomingPacket(byte[] data)
    {
        _bcTransport?.PushReceivedData(data);
    }

    public Task StartAsync(bool isClient)
    {
        return Task.Run(() =>
        {
            try
            {
                State = DtlsState.Connecting;
                _bcTransport = new BouncyCastleDatagramTransport(_sendFunc);
                var crypto = new BcTlsCrypto(new SecureRandom());

                if (isClient)
                {
                    var protocol = new DtlsClientProtocol();
                    var client = new WebRtcTlsClient(crypto, _certificate, _remoteFingerprintAlg, _remoteFingerprint);
                    _dtlsTransport = protocol.Connect(client, _bcTransport);
                    _srtpKeys = client.ExportSrtpKeys();
                }
                else
                {
                    var protocol = new DtlsServerProtocol();
                    var server = new WebRtcTlsServer(crypto, _certificate, _remoteFingerprintAlg, _remoteFingerprint);
                    _dtlsTransport = protocol.Accept(server, _bcTransport);
                    _srtpKeys = server.ExportSrtpKeys();
                }

                State = DtlsState.Connected;
                Task.Run(ReceiveLoop).FireAndForget();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "DTLS handshake failed");
                State = DtlsState.Failed;
            }
        });
    }

    private void ReceiveLoop()
    {
        byte[] buf = new byte[2048];
        try
        {
            while (State == DtlsState.Connected && _dtlsTransport != null)
            {
                int read = _dtlsTransport.Receive(buf, 0, buf.Length, 100);
                if (read > 0)
                {
                    byte[] data = new byte[read];
                    Array.Copy(buf, 0, data, 0, read);
                    OnData?.Invoke(this, data);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DTLS receive loop failed");
            State = DtlsState.Failed;
        }
    }

    public Task SendAsync(byte[] data)
    {
        if (State != DtlsState.Connected || _dtlsTransport == null)
        {
            throw new InvalidOperationException("DTLS not connected");
        }

        lock (_sendLock)
        {
            _dtlsTransport.Send(data, 0, data.Length);
        }

        return Task.CompletedTask;
    }

    public SrtpKeys? GetSrtpKeys() => _srtpKeys;

    internal static void ValidateFingerprint(byte[] der, string? algorithm, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return;
        }

        byte[] hash = algorithm?.ToLowerInvariant() switch
        {
            "sha-256" => SHA256.HashData(der),
            _ => throw new TlsFatalAlert(AlertDescription.bad_certificate)
        };

        string actualFingerprint = BitConverter.ToString(hash).Replace("-", ":").ToUpperInvariant();
        if (!string.Equals(actualFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }
    }

    internal static SrtpKeys? CreateSrtpKeys(TlsContext? context, UseSrtpData? srtpData)
    {
        if (context == null || srtpData == null)
        {
            return null;
        }

        const int totalLen = 2 * (16 + 14);
        byte[] keyingMaterial = context.ExportKeyingMaterial(ExporterLabel.dtls_srtp, null, totalLen);
        return new SrtpKeys
        {
            ProtectionProfile = (ushort)srtpData.ProtectionProfiles[0],
            MasterKey = keyingMaterial
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        State = DtlsState.Closed;
        _dtlsTransport?.Close();
        _bcTransport?.Close();
        GC.SuppressFinalize(this);
    }
}

internal class WebRtcTlsClient : DefaultTlsClient
{
    private UseSrtpData? _srtpData;
    private SrtpKeys? _srtpKeys;
    private readonly DtlsCertificate _certificate;
    private readonly string? _expectedFingerprintAlg;
    private readonly string? _expectedFingerprint;

    public WebRtcTlsClient(TlsCrypto crypto, DtlsCertificate certificate, string? fingerprintAlg, string? fingerprint) : base(crypto)
    {
        _certificate = certificate;
        _expectedFingerprintAlg = fingerprintAlg;
        _expectedFingerprint = fingerprint;
    }

    public SrtpKeys? ExportSrtpKeys()
    {
        return _srtpKeys;
    }

    protected override ProtocolVersion[] GetSupportedVersions()
    {
        return DtlsTransport.SupportedDtlsVersions;
    }

    protected override int[] GetSupportedCipherSuites()
    {
        return DtlsTransport.SupportedDtlsCipherSuites;
    }

    public override IDictionary<int, byte[]> GetClientExtensions()
    {
        var extensions = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetClientExtensions());
        TlsSrtpUtilities.AddUseSrtpExtension(extensions, new UseSrtpData([SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80], TlsUtilities.EmptyBytes));
        return extensions;
    }

    public override void ProcessServerExtensions(IDictionary<int, byte[]> serverExtensions)
    {
        base.ProcessServerExtensions(serverExtensions);
        _srtpData = TlsSrtpUtilities.GetUseSrtpExtension(serverExtensions);
    }

    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        _srtpKeys = DtlsTransport.CreateSrtpKeys(m_context, _srtpData);
    }

    public override TlsAuthentication GetAuthentication() => new WebRtcTlsAuthentication(this);

    private class WebRtcTlsAuthentication : TlsAuthentication
    {
        private readonly WebRtcTlsClient _client;

        public WebRtcTlsAuthentication(WebRtcTlsClient client)
        {
            _client = client;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            if (string.IsNullOrEmpty(_client._expectedFingerprint))
            {
                return;
            }

            var cert = serverCertificate.Certificate.GetCertificateAt(0);
            DtlsTransport.ValidateFingerprint(cert.GetEncoded(), _client._expectedFingerprintAlg, _client._expectedFingerprint);
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest)
        {
            short[]? certificateTypes = certificateRequest.CertificateTypes;
            if (certificateTypes?.Contains(ClientCertificateType.ecdsa_sign) != true)
            {
                return null;
            }

            var signatureAndHashAlgorithm = new SignatureAndHashAlgorithm(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa);
            return new BcDefaultTlsCredentialedSigner(
                new TlsCryptoParameters(_client.m_context),
                (BcTlsCrypto)_client.m_context.Crypto,
                _client._certificate.PrivateKey,
                _client._certificate.TlsCertificate,
                signatureAndHashAlgorithm);
        }
    }
}

internal class WebRtcTlsServer : DefaultTlsServer
{
    private readonly DtlsCertificate _certificate;
    private UseSrtpData? _srtpData;
    private SrtpKeys? _srtpKeys;
    private readonly string? _expectedFingerprintAlg;
    private readonly string? _expectedFingerprint;

    public WebRtcTlsServer(TlsCrypto crypto, DtlsCertificate certificate, string? fingerprintAlg, string? fingerprint) : base(crypto)
    {
        _certificate = certificate;
        _expectedFingerprintAlg = fingerprintAlg;
        _expectedFingerprint = fingerprint;
    }

    public SrtpKeys? ExportSrtpKeys()
    {
        return _srtpKeys;
    }

    protected override ProtocolVersion[] GetSupportedVersions()
    {
        return DtlsTransport.SupportedDtlsVersions;
    }

    protected override int[] GetSupportedCipherSuites()
    {
        return DtlsTransport.SupportedDtlsCipherSuites;
    }

    public override IDictionary<int, byte[]> GetServerExtensions()
    {
        var extensions = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetServerExtensions());
        TlsSrtpUtilities.AddUseSrtpExtension(extensions, new UseSrtpData([SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80], TlsUtilities.EmptyBytes));
        return extensions;
    }

    public override void ProcessClientExtensions(IDictionary<int, byte[]> clientExtensions)
    {
        base.ProcessClientExtensions(clientExtensions);
        _srtpData = TlsSrtpUtilities.GetUseSrtpExtension(clientExtensions);
    }

    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        _srtpKeys = DtlsTransport.CreateSrtpKeys(m_context, _srtpData);
    }

    public override CertificateRequest GetCertificateRequest()
    {
        short[] certificateTypes = [ClientCertificateType.ecdsa_sign];
        IList<SignatureAndHashAlgorithm>? serverSigAlgs = null;
        if (TlsUtilities.IsSignatureAlgorithmsExtensionAllowed(m_context.ServerVersion))
        {
            serverSigAlgs = TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context);
        }

        return new CertificateRequest(certificateTypes, serverSigAlgs, null);
    }

    public override void NotifyClientCertificate(Certificate clientCertificate)
    {
        if (clientCertificate?.IsEmpty != false)
        {
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }

        var cert = clientCertificate.GetCertificateAt(0);
        DtlsTransport.ValidateFingerprint(cert.GetEncoded(), _expectedFingerprintAlg, _expectedFingerprint);
    }

    protected override TlsCredentialedSigner GetECDsaSignerCredentials()
    {
        return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), (BcTlsCrypto)Crypto, _certificate.PrivateKey, _certificate.TlsCertificate, new SignatureAndHashAlgorithm(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
    }
}
