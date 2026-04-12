using RtcForge.Ice;

namespace RtcForge.Tests.Ice;

public class IceCandidateTests
{
    [Fact]
    public void Parse_HostCandidate_ReturnsCorrectFields()
    {
        var candidate = IceCandidate.Parse("candidate:1 1 udp 2130706431 192.168.1.1 54321 typ host");

        Assert.Equal("1", candidate.Foundation);
        Assert.Equal(1u, candidate.Component);
        Assert.Equal("udp", candidate.Protocol);
        Assert.Equal(2130706431u, candidate.Priority);
        Assert.Equal("192.168.1.1", candidate.Address);
        Assert.Equal(54321, candidate.Port);
        Assert.Equal(IceCandidateType.Host, candidate.Type);
        Assert.Null(candidate.RelatedAddress);
        Assert.Null(candidate.RelatedPort);
    }

    [Fact]
    public void Parse_SrflxCandidate_WithRelatedAddress()
    {
        var candidate = IceCandidate.Parse("candidate:2 1 udp 1694498815 203.0.113.5 9876 typ srflx raddr 192.168.1.1 rport 54321");

        Assert.Equal("2", candidate.Foundation);
        Assert.Equal(IceCandidateType.Srflx, candidate.Type);
        Assert.Equal("203.0.113.5", candidate.Address);
        Assert.Equal(9876, candidate.Port);
        Assert.Equal("192.168.1.1", candidate.RelatedAddress);
        Assert.Equal(54321, candidate.RelatedPort);
    }

    [Fact]
    public void Parse_RelayCandidate()
    {
        var candidate = IceCandidate.Parse("candidate:3 1 udp 16777215 10.0.0.1 5000 typ relay raddr 192.168.1.1 rport 1234");

        Assert.Equal(IceCandidateType.Relay, candidate.Type);
        Assert.Equal("10.0.0.1", candidate.Address);
        Assert.Equal("192.168.1.1", candidate.RelatedAddress);
        Assert.Equal(1234, candidate.RelatedPort);
    }

    [Fact]
    public void Parse_WithoutCandidatePrefix_Works()
    {
        var candidate = IceCandidate.Parse("1 1 udp 100 10.0.0.1 8000 typ host");

        Assert.Equal("1", candidate.Foundation);
        Assert.Equal(IceCandidateType.Host, candidate.Type);
    }

    [Fact]
    public void Parse_WithCandidatePrefix_StripsPrefix()
    {
        var c1 = IceCandidate.Parse("candidate:1 1 udp 100 10.0.0.1 8000 typ host");
        var c2 = IceCandidate.Parse("1 1 udp 100 10.0.0.1 8000 typ host");

        Assert.Equal(c1.Foundation, c2.Foundation);
        Assert.Equal(c1.Address, c2.Address);
        Assert.Equal(c1.Port, c2.Port);
    }

    [Fact]
    public void Parse_TooFewParts_Throws()
    {
        Assert.ThrowsAny<Exception>(() => IceCandidate.Parse("1 1 udp"));
    }

    [Fact]
    public void Parse_InvalidPriority_Throws()
    {
        Assert.ThrowsAny<Exception>(() => IceCandidate.Parse("1 1 udp notanumber 10.0.0.1 8000 typ host"));
    }

    [Fact]
    public void Parse_InvalidType_Throws()
    {
        Assert.ThrowsAny<Exception>(() => IceCandidate.Parse("1 1 udp 100 10.0.0.1 8000 typ invalid_type"));
    }

    [Fact]
    public void ToString_HostCandidate_ProducesCorrectString()
    {
        var candidate = new IceCandidate
        {
            Foundation = "1",
            Component = 1,
            Protocol = "udp",
            Priority = 2130706431,
            Address = "192.168.1.1",
            Port = 54321,
            Type = IceCandidateType.Host
        };

        string result = candidate.ToString();

        Assert.Equal("candidate:1 1 udp 2130706431 192.168.1.1 54321 typ host", result);
    }

    [Fact]
    public void ToString_SrflxCandidate_IncludesRelatedAddress()
    {
        var candidate = new IceCandidate
        {
            Foundation = "2",
            Component = 1,
            Protocol = "udp",
            Priority = 100,
            Address = "203.0.113.5",
            Port = 9876,
            Type = IceCandidateType.Srflx,
            RelatedAddress = "192.168.1.1",
            RelatedPort = 54321
        };

        string result = candidate.ToString();

        Assert.Contains("raddr 192.168.1.1", result);
        Assert.Contains("rport 54321", result);
    }

    [Fact]
    public void ToString_Parse_RoundTrip()
    {
        var original = new IceCandidate
        {
            Foundation = "abc",
            Component = 1,
            Protocol = "udp",
            Priority = 12345,
            Address = "10.0.0.1",
            Port = 5000,
            Type = IceCandidateType.Host
        };

        var parsed = IceCandidate.Parse(original.ToString());

        Assert.Equal(original.Foundation, parsed.Foundation);
        Assert.Equal(original.Component, parsed.Component);
        Assert.Equal(original.Protocol, parsed.Protocol);
        Assert.Equal(original.Priority, parsed.Priority);
        Assert.Equal(original.Address, parsed.Address);
        Assert.Equal(original.Port, parsed.Port);
        Assert.Equal(original.Type, parsed.Type);
    }

    [Fact]
    public void ToString_Parse_RoundTrip_WithRelatedAddress()
    {
        var original = new IceCandidate
        {
            Foundation = "2",
            Component = 1,
            Protocol = "udp",
            Priority = 100,
            Address = "203.0.113.5",
            Port = 9876,
            Type = IceCandidateType.Srflx,
            RelatedAddress = "192.168.1.1",
            RelatedPort = 54321
        };

        var parsed = IceCandidate.Parse(original.ToString());

        Assert.Equal(original.RelatedAddress, parsed.RelatedAddress);
        Assert.Equal(original.RelatedPort, parsed.RelatedPort);
    }
}
