using RtcForge.Ice;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Buffers.Binary;
using RtcForge.Stun;

namespace RtcForge.Tests.Ice;

public class IceAgentTests
{
    [Fact]
    public async Task StartGatheringAsync_GathersLocalCandidates()
    {
        // Arrange
        var agent = new IceAgent();
        var candidates = new List<IceCandidate>();
        agent.OnLocalCandidate += (s, c) => candidates.Add(c);

        // Act
        await agent.StartGatheringAsync();

        // Assert
        Assert.NotEmpty(candidates);
        Assert.Contains(candidates, c => c.Type == IceCandidateType.Host);
        Assert.Equal(IceState.Complete, agent.State);
    }

    [Fact]
    public async Task ConnectAsync_WithoutRemoteCandidates_Fails()
    {
        // Arrange
        var agent = new IceAgent();
        agent.SetRemoteCredentials("remoteUfrag", "remotePwd");
        await agent.StartGatheringAsync();

        // Act
        bool result = await agent.ConnectAsync();

        // Assert
        Assert.False(result);
        Assert.Equal(IceState.Failed, agent.State);
    }

    [Fact]
    public async Task SendDataAsync_UsesSelectedCandidatePair()
    {
        using var agent = new IceAgent();
        var transport1 = new IceUdpTransport();
        var transport2 = new IceUdpTransport();
        using var receiver1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var receiver2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        var remote1 = new IceCandidate
        {
            Foundation = "1",
            Component = 1,
            Protocol = "udp",
            Priority = 1,
            Address = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)receiver1.Client.LocalEndPoint!).Port,
            Type = IceCandidateType.Host
        };
        var remote2 = new IceCandidate
        {
            Foundation = "2",
            Component = 1,
            Protocol = "udp",
            Priority = 2,
            Address = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)receiver2.Client.LocalEndPoint!).Port,
            Type = IceCandidateType.Host
        };

        SetPrivateField(agent, "_transports", new List<IceUdpTransport> { transport1, transport2 });
        SetPrivateField(agent, "_remoteCandidates", new List<IceCandidate> { remote1, remote2 });
        SetPrivateField(agent, "_selectedLocalCandidate", new IceCandidate
        {
            Foundation = "host",
            Component = 1,
            Protocol = "udp",
            Priority = 2,
            Address = IPAddress.Loopback.ToString(),
            Port = transport2.LocalEndPoint.Port,
            Type = IceCandidateType.Host
        });
        SetPrivateField(agent, "_selectedTransport", transport2);
        SetPrivateField(agent, "_selectedRemoteCandidate", remote2);

        byte[] payload = [1, 2, 3, 4];
        await agent.SendDataAsync(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = await receiver2.ReceiveAsync(cts.Token);

        Assert.Equal(payload, received.Buffer);
        Assert.False(receiver1.Available > 0);
    }

    [Fact]
    public void TryResolveRoleConflict_RemoteControllingWithHigherTieBreaker_SwitchesToControlled()
    {
        using var agent = new IceAgent
        {
            IsControlling = true
        };

        ulong localTieBreaker = (ulong)typeof(IceAgent)
            .GetField("_tieBreaker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;

        var request = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            Attributes =
            {
                CreateUInt64Attribute(StunAttributeType.IceControlling, localTieBreaker + 1)
            }
        };

        bool shouldReject = agent.TryResolveRoleConflict(request);

        Assert.False(shouldReject);
        Assert.False(agent.IsControlling);
    }

    [Fact]
    public void TryResolveRoleConflict_RemoteControlledWithLowerTieBreaker_SwitchesToControlling()
    {
        using var agent = new IceAgent
        {
            IsControlling = false
        };

        ulong localTieBreaker = (ulong)typeof(IceAgent)
            .GetField("_tieBreaker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;

        var request = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            Attributes =
            {
                CreateUInt64Attribute(StunAttributeType.IceControlled, localTieBreaker - 1)
            }
        };

        bool shouldReject = agent.TryResolveRoleConflict(request);

        Assert.False(shouldReject);
        Assert.True(agent.IsControlling);
    }

    [Fact]
    public void GetOrCreateRemoteCandidate_LearnsPeerReflexiveCandidateFromBindingRequest()
    {
        using var agent = new IceAgent();
        var endPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49152);
        var request = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            Attributes =
            {
                CreateUInt32Attribute(StunAttributeType.Priority, 1234u)
            }
        };

        IceCandidate? candidate = agent.GetOrCreateRemoteCandidate(endPoint, request);

        Assert.NotNull(candidate);
        Assert.Equal(IceCandidateType.Prflx, candidate!.Type);
        Assert.Equal("127.0.0.1", candidate.Address);
        Assert.Equal(49152, candidate.Port);
        Assert.Equal(1234u, candidate.Priority);
    }

    [Fact]
    public void SetRemoteCredentials_IceRestartClearsRemoteCandidatesAndSelectedPair()
    {
        using var agent = new IceAgent();
        SetPrivateField(agent, "_remoteCandidates", new List<IceCandidate>
        {
            new() { Foundation = "1", Component = 1, Protocol = "udp", Priority = 1, Address = "127.0.0.1", Port = 5000, Type = IceCandidateType.Host }
        });
        SetPrivateField(agent, "_selectedRemoteCandidate", new IceCandidate
        {
            Foundation = "2", Component = 1, Protocol = "udp", Priority = 2, Address = "127.0.0.1", Port = 5001, Type = IceCandidateType.Host
        });

        agent.SetRemoteCredentials("ufrag1", "pwd1");
        agent.SetRemoteCredentials("ufrag2", "pwd2");

        var remoteCandidates = (List<IceCandidate>)typeof(IceAgent)
            .GetField("_remoteCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
        var selectedRemote = typeof(IceAgent)
            .GetField("_selectedRemoteCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent);

        Assert.Empty(remoteCandidates);
        Assert.Null(selectedRemote);
        Assert.Equal(IceState.New, agent.State);
    }

    [Fact]
    public async Task AddRemoteCandidate_AfterFailure_RetriesConnectivityChecks()
    {
        using var agent = new IceAgent();
        var stateTransitions = new List<IceState>();
        agent.OnStateChange += (_, state) => stateTransitions.Add(state);

        agent.SetRemoteCredentials("remoteUfrag", "remotePwd");
        await agent.StartGatheringAsync();
        bool firstResult = await agent.ConnectAsync();

        Assert.False(firstResult);
        Assert.Equal(IceState.Failed, agent.State);

        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "1",
            Component = 1,
            Protocol = "udp",
            Priority = 1,
            Address = IPAddress.Loopback.ToString(),
            Port = 59999,
            Type = IceCandidateType.Host
        });

        for (int i = 0; i < 20 && !stateTransitions.Contains(IceState.Checking); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System);
        }

        Assert.Contains(IceState.Checking, stateTransitions);
    }

    [Fact]
    public async Task ResolveRemoteEndPointAsync_ResolvesLocalhost()
    {
        var candidate = new IceCandidate
        {
            Address = "localhost",
            Port = 3478
        };

        var agent = new IceAgent();
        var resolved = await agent.ResolveRemoteEndPointAsync(candidate, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(3478, resolved!.Port);
    }

    private static void SetPrivateField<T>(object instance, string fieldName, T value)
    {
        typeof(IceAgent)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static StunAttribute CreateUInt32Attribute(StunAttributeType type, uint value)
    {
        byte[] buffer = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return new StunAttribute { Type = type, Value = buffer };
    }

    private static StunAttribute CreateUInt64Attribute(StunAttributeType type, ulong value)
    {
        byte[] buffer = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        return new StunAttribute { Type = type, Value = buffer };
    }
}
