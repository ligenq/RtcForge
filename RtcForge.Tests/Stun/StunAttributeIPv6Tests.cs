using System.Net;
using System.Net.Sockets;
using RtcForge.Stun;

namespace RtcForge.Tests.Stun;

public class StunAttributeIPv6Tests
{
    [Fact]
    public void GetXorMappedAddress_IPv4_ReturnsCorrectEndpoint()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 12345);
        var transactionId = new byte[12];
        for (int i = 0; i < 12; i++) transactionId[i] = (byte)i;

        var attr = StunAttribute.CreateXorMappedAddress(endpoint, transactionId);
        var decoded = attr.GetXorMappedAddress(transactionId);

        Assert.NotNull(decoded);
        Assert.Equal(endpoint.Address, decoded!.Address);
        Assert.Equal(endpoint.Port, decoded.Port);
    }

    [Fact]
    public void GetXorMappedAddress_IPv6_ReturnsCorrectEndpoint()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 54321);
        var transactionId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

        var attr = StunAttribute.CreateXorMappedAddress(endpoint, transactionId);

        Assert.Equal(20, attr.Value.Length);
        Assert.Equal(0x02, attr.Value[1]);

        var decoded = attr.GetXorMappedAddress(transactionId);

        Assert.NotNull(decoded);
        Assert.Equal(AddressFamily.InterNetworkV6, decoded!.AddressFamily);
        Assert.Equal(endpoint.Address, decoded.Address);
        Assert.Equal(endpoint.Port, decoded.Port);
    }

    [Fact]
    public void CreateXorPeerAddress_IPv6_RoundTrips()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("fe80::1"), 9999);
        var transactionId = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };

        var attr = StunAttribute.CreateXorPeerAddress(endpoint, transactionId);
        var decoded = attr.GetXorMappedAddress(transactionId);

        Assert.NotNull(decoded);
        Assert.Equal(endpoint.Address, decoded!.Address);
        Assert.Equal(endpoint.Port, decoded.Port);
    }

    [Fact]
    public void GetXorMappedAddress_IPv6_FullAddress_RoundTrips()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("2001:0db8:85a3:0000:0000:8a2e:0370:7334"), 443);
        var transactionId = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        var attr = StunAttribute.CreateXorMappedAddress(endpoint, transactionId);
        var decoded = attr.GetXorMappedAddress(transactionId);

        Assert.NotNull(decoded);
        Assert.Equal(endpoint.Address, decoded!.Address);
        Assert.Equal(443, decoded.Port);
    }

    [Fact]
    public void GetXorMappedAddress_InvalidFamily_ReturnsNull()
    {
        var attr = new StunAttribute
        {
            Type = StunAttributeType.XorMappedAddress,
            Value = [0, 0x03, 0, 0, 0, 0, 0, 0] // family 0x03 is invalid
        };

        var result = attr.GetXorMappedAddress(new byte[12]);

        Assert.Null(result);
    }

    [Fact]
    public void GetXorMappedAddress_TooShortForIPv6_ReturnsNull()
    {
        var attr = new StunAttribute
        {
            Type = StunAttributeType.XorMappedAddress,
            Value = [0, 0x02, 0, 0, 0, 0, 0, 0, 0, 0] // IPv6 but only 10 bytes
        };

        var result = attr.GetXorMappedAddress(new byte[12]);

        Assert.Null(result);
    }
}
