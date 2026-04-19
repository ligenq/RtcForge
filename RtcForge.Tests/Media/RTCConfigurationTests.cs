using Microsoft.Extensions.Logging.Abstractions;
using RtcForge.Media;

namespace RtcForge.Tests.Media;

public class RTCConfigurationTests
{
    [Fact]
    public void Constructor_UsesExpectedDefaults()
    {
        var configuration = new RTCConfiguration();

        Assert.Empty(configuration.IceServers);
        Assert.Equal(RTCIceTransportPolicy.All, configuration.IceTransportPolicy);
        Assert.Null(configuration.LoggerFactory);
        Assert.Null(configuration.TimeProvider);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.ConnectionTimeout);
    }

    [Fact]
    public void Properties_AreMutable()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var timeProvider = TimeProvider.System;
        var iceServer = new RTCIceServer
        {
            Urls = { "stun:127.0.0.1:3478" },
            Username = "user",
            Credential = "pass"
        };

        var configuration = new RTCConfiguration
        {
            IceServers = [iceServer],
            IceTransportPolicy = RTCIceTransportPolicy.Relay,
            LoggerFactory = loggerFactory,
            TimeProvider = timeProvider,
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };

        Assert.Same(iceServer, Assert.Single(configuration.IceServers));
        Assert.Equal(RTCIceTransportPolicy.Relay, configuration.IceTransportPolicy);
        Assert.Same(loggerFactory, configuration.LoggerFactory);
        Assert.Same(timeProvider, configuration.TimeProvider);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.ConnectionTimeout);
        Assert.Equal("stun:127.0.0.1:3478", Assert.Single(iceServer.Urls));
        Assert.Equal("user", iceServer.Username);
        Assert.Equal("pass", iceServer.Credential);
    }
}
