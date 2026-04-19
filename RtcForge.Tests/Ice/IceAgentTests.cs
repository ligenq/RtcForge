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

    [Fact]
    public async Task TryNominateDiscoveredPairAsync_WhenPairAlreadySelected_DoesNotReNominate()
    {
        // Arrange: create agent with an already-selected pair
        using var agent = new IceAgent();
        agent.IsControlling = true;
        agent.SetRemoteCredentials("remoteUfrag", "remotePwd");

        var transport = new IceUdpTransport();
        SetPrivateField(agent, "_transports", new List<IceUdpTransport> { transport });

        var selectedLocal = new IceCandidate
        {
            Foundation = "1",
            Component = 1,
            Protocol = "udp",
            Priority = 2130706431,
            Address = IPAddress.Loopback.ToString(),
            Port = transport.LocalEndPoint.Port,
            Type = IceCandidateType.Host
        };
        var selectedRemote = new IceCandidate
        {
            Foundation = "2",
            Component = 1,
            Protocol = "udp",
            Priority = 2130706431,
            Address = "192.168.1.100",
            Port = 30000,
            Type = IceCandidateType.Host
        };

        // Simulate that a pair has already been selected (ICE connected)
        SetPrivateField(agent, "_selectedLocalCandidate", selectedLocal);
        SetPrivateField(agent, "_selectedRemoteCandidate", selectedRemote);
        SetPrivateField(agent, "_selectedTransport", transport);

        // Set state to Completed (as it would be after nomination)
        SetPrivateField(agent, "_state", IceState.Completed);

        var stateTransitions = new List<IceState>();
        agent.OnStateChange += (_, state) => stateTransitions.Add(state);

        // Act: invoke TryNominateDiscoveredPairAsync with a different pair
        var differentLocal = new IceCandidate
        {
            Foundation = "3",
            Component = 1,
            Protocol = "udp",
            Priority = 1694498815,
            Address = "10.0.0.1",
            Port = 50000,
            Type = IceCandidateType.Host
        };
        var differentRemote = new IceCandidate
        {
            Foundation = "4",
            Component = 1,
            Protocol = "udp",
            Priority = 1694498815,
            Address = "192.168.1.200",
            Port = 30001,
            Type = IceCandidateType.Host
        };

        var method = typeof(IceAgent).GetMethod("TryNominateDiscoveredPairAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(agent, [transport, differentLocal, differentRemote])!;
        await task;

        // Assert: no state transitions should have occurred (no regression to Checking)
        Assert.Empty(stateTransitions);
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

    private static UdpClient StartFakeStunServer(Func<UdpReceiveResult, bool> shouldRespond, CancellationToken cancellationToken, IPAddress? reportedAddress, out int port)
    {
        var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        int localPort = port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    UdpReceiveResult received;
                    try
                    {
                        received = await server.ReceiveAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    if (!StunMessage.TryParse(received.Buffer, out var request) || request.Type != StunMessageType.BindingRequest)
                    {
                        continue;
                    }

                    if (!shouldRespond(received))
                    {
                        continue;
                    }

                    var mappedEndpoint = reportedAddress != null
                        ? new IPEndPoint(reportedAddress, received.RemoteEndPoint.Port)
                        : received.RemoteEndPoint;

                    var response = new StunMessage
                    {
                        Type = StunMessageType.BindingSuccessResponse,
                        TransactionId = request.TransactionId
                    };
                    response.Attributes.Add(StunAttribute.CreateXorMappedAddress(mappedEndpoint, request.TransactionId));
                    response.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });

                    byte[] buffer = new byte[response.GetSerializedLength()];
                    response.Serialize(buffer);
                    try
                    {
                        await server.SendAsync(buffer, buffer.Length, received.RemoteEndPoint);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);

        return server;
    }

    [Fact]
    public async Task StartGatheringAsync_WithDeadStunServer_DoesNotStallPastTimeout()
    {
        // Arrange: configure a STUN server URL that points at a closed UDP port on loopback.
        // Before the fix, the plain STUN binding request would burn the full
        // 15.5-second retransmission budget. With the per-request timeout it must return
        // well under that bound.
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int deadPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close(); // free the port so nothing responds there

        using var agent = new IceAgent();
        agent.SetIceServers(new[]
        {
            new RtcForge.Media.RTCIceServer { Urls = { $"stun:127.0.0.1:{deadPort}" } }
        });

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await agent.StartGatheringAsync();
        stopwatch.Stop();

        // Assert: must bail out under the per-request cap, not the full RFC budget.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6),
            $"gathering took {stopwatch.Elapsed.TotalSeconds:F1}s against a dead STUN server; expected <6s");
    }

    [Fact]
    public async Task StartGatheringAsync_MultipleStunServers_QueriedInParallel()
    {
        // Arrange: two STUN servers, one slow (delayed response), one fast. With sequential
        // gathering the total time would be slow+fast; with parallel gathering it should
        // be roughly max(slow, fast).
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int slowPort, fastPort;

        using var slowServer = StartFakeStunServer(
            _ => { slowGate.Task.Wait(serverCts.Token); return true; },
            serverCts.Token,
            reportedAddress: IPAddress.Parse("203.0.113.10"),
            out slowPort);

        using var fastServer = StartFakeStunServer(
            _ => true,
            serverCts.Token,
            reportedAddress: IPAddress.Parse("203.0.113.20"),
            out fastPort);

        using var agent = new IceAgent();
        var srflxCount = 0;
        var firstSrflx = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnLocalCandidate += (_, c) =>
        {
            if (c.Type == IceCandidateType.Srflx)
            {
                Interlocked.Increment(ref srflxCount);
                firstSrflx.TrySetResult();
            }
        };

        agent.SetIceServers(new[]
        {
            new RtcForge.Media.RTCIceServer { Urls = { $"stun:127.0.0.1:{slowPort}" } },
            new RtcForge.Media.RTCIceServer { Urls = { $"stun:127.0.0.1:{fastPort}" } }
        });

        var gatherTask = agent.StartGatheringAsync();

        // The fast server's srflx candidate should arrive while the slow one is still
        // blocked, proving the servers are being queried concurrently.
        var winner = await Task.WhenAny(firstSrflx.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(firstSrflx.Task, winner);

        // Release the slow server and let gathering finish.
        slowGate.SetResult();
        await gatherTask;
        serverCts.Cancel();

        Assert.True(srflxCount >= 1);
    }

    [Fact]
    public async Task StartGatheringAsync_DuplicateSrflxFromMultipleServers_Deduped()
    {
        // Arrange: two STUN servers that report the same public mapped address. The agent
        // should only surface one srflx candidate per (host, mapped) pair, not one per server.
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int port1, port2;
        var reportedIp = IPAddress.Parse("198.51.100.7");

        using var server1 = StartFakeStunServer(_ => true, serverCts.Token, reportedIp, out port1);
        using var server2 = StartFakeStunServer(_ => true, serverCts.Token, reportedIp, out port2);

        using var agent = new IceAgent();
        var srflxCandidates = new List<IceCandidate>();
        agent.OnLocalCandidate += (_, c) =>
        {
            if (c.Type == IceCandidateType.Srflx)
            {
                lock (srflxCandidates) { srflxCandidates.Add(c); }
            }
        };

        agent.SetIceServers(new[]
        {
            new RtcForge.Media.RTCIceServer { Urls = { $"stun:127.0.0.1:{port1}" } },
            new RtcForge.Media.RTCIceServer { Urls = { $"stun:127.0.0.1:{port2}" } }
        });

        // Act
        await agent.StartGatheringAsync();

        // Give the second-server response a brief window to race in and be deduped.
        await Task.Delay(200);
        serverCts.Cancel();

        // Assert: each host candidate should yield at most one srflx for the reported mapped address.
        lock (srflxCandidates)
        {
            var mappedDistinct = srflxCandidates
                .Select(c => (c.Address, c.Port, c.RelatedAddress, c.RelatedPort))
                .Distinct()
                .Count();
            Assert.Equal(mappedDistinct, srflxCandidates.Count);
            Assert.Contains(srflxCandidates, c => c.Address == reportedIp.ToString());
        }
    }

    [Fact]
    public async Task StartGatheringAsync_WithStunServer_GathersSrflxCandidate()
    {
        // Arrange: spin up a minimal in-process STUN server that echoes a XOR-MAPPED-ADDRESS
        // back to any BindingRequest. This verifies that srflx gathering sends a valid,
        // plain RFC 5389 Binding request (no ICE short-term credential) and parses the response.
        using var stunServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int stunPort = ((IPEndPoint)stunServer.Client.LocalEndPoint!).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await stunServer.ReceiveAsync(serverCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!StunMessage.TryParse(received.Buffer, out var request) || request.Type != StunMessageType.BindingRequest)
                {
                    continue;
                }

                var response = new StunMessage
                {
                    Type = StunMessageType.BindingSuccessResponse,
                    TransactionId = request.TransactionId
                };
                response.Attributes.Add(StunAttribute.CreateXorMappedAddress(received.RemoteEndPoint, request.TransactionId));
                response.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });

                byte[] buffer = new byte[response.GetSerializedLength()];
                response.Serialize(buffer);
                await stunServer.SendAsync(buffer, buffer.Length, received.RemoteEndPoint);
            }
        }, serverCts.Token);

        using var agent = new IceAgent();
        var candidates = new List<IceCandidate>();
        var gotSrflx = new TaskCompletionSource<IceCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnLocalCandidate += (_, c) =>
        {
            lock (candidates) { candidates.Add(c); }
            if (c.Type == IceCandidateType.Srflx)
            {
                gotSrflx.TrySetResult(c);
            }
        };

        agent.SetIceServers(new[]
        {
            new RtcForge.Media.RTCIceServer { Urls = { $"stun:127.0.0.1:{stunPort}" } }
        });

        // Act
        await agent.StartGatheringAsync();

        // Assert: the awaited gather loop should have produced at least one srflx candidate.
        var winner = await Task.WhenAny(gotSrflx.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(gotSrflx.Task, winner);

        var srflx = await gotSrflx.Task;
        Assert.Equal(IceCandidateType.Srflx, srflx.Type);
        Assert.Equal("127.0.0.1", srflx.Address);
        Assert.True(srflx.Port > 0);
        Assert.NotNull(srflx.RelatedAddress);

        lock (candidates)
        {
            Assert.Contains(candidates, c => c.Type == IceCandidateType.Host);
            Assert.Contains(candidates, c => c.Type == IceCandidateType.Srflx);
        }

        serverCts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ConnectAsync_PrefersHigherPriorityPairThatSucceedsWithinNominationGrace()
    {
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int lowPort, highPort;
        using var lowServer = StartFakeStunServer(_ => true, serverCts.Token, reportedAddress: null, out lowPort);
        using var highServer = StartFakeStunServer(
            _ =>
            {
                Thread.Sleep(100);
                return true;
            },
            serverCts.Token,
            reportedAddress: null,
            out highPort);

        using var agent = new IceAgent();
        agent.IsControlling = true;
        agent.SetRemoteCredentials("remoteUfrag", "remotePwd");
        await agent.StartGatheringAsync();

        uint highPriority = (126u << 24) | (65535u << 8) | 255u;
        uint lowPriority = (100u << 24) | (65535u << 8) | 255u;
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "low",
            Component = 1,
            Protocol = "udp",
            Priority = lowPriority,
            Address = IPAddress.Loopback.ToString(),
            Port = lowPort,
            Type = IceCandidateType.Srflx
        });
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "high",
            Component = 1,
            Protocol = "udp",
            Priority = highPriority,
            Address = IPAddress.Loopback.ToString(),
            Port = highPort,
            Type = IceCandidateType.Host
        });

        Assert.True(await agent.ConnectAsync());
        serverCts.Cancel();

        var selectedRemote = (IceCandidate?)typeof(IceAgent)
            .GetField("_selectedRemoteCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent);
        Assert.NotNull(selectedRemote);
        Assert.Equal(highPort, selectedRemote.Port);
    }

    [Fact]
    public async Task ConnectAsync_UnreachableHighPriorityPairs_DoNotBlockReachableLowPriorityPair()
    {
        // Arrange: one reachable remote that always responds, plus several dead remote ports
        // at higher priority. Sequential connectivity checks would burn the full ~15.5s STUN
        // retransmission budget on every dead pair before reaching the reachable one; parallel
        // checks must pick the reachable pair within a few hundred milliseconds.
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int livePort;
        using var liveServer = StartFakeStunServer(_ => true, serverCts.Token, reportedAddress: null, out livePort);

        // Allocate several dead UDP ports on loopback and immediately release them so sends
        // to those endpoints get ICMP-unreachable (effectively: no response at all).
        var deadPorts = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            deadPorts.Add(((IPEndPoint)probe.Client.LocalEndPoint!).Port);
        }

        using var agent = new IceAgent();
        agent.IsControlling = true;
        agent.SetRemoteCredentials("remoteUfrag", "remotePwd");
        await agent.StartGatheringAsync();

        // High-priority dead pairs (typPref 126 == host).
        uint highPriority = (126u << 24) | (65535u << 8) | 255u;
        uint lowPriority = (100u << 24) | (65535u << 8) | 255u;
        foreach (var deadPort in deadPorts)
        {
            agent.AddRemoteCandidate(new IceCandidate
            {
                Foundation = $"dead{deadPort}",
                Component = 1,
                Protocol = "udp",
                Priority = highPriority,
                Address = IPAddress.Loopback.ToString(),
                Port = deadPort,
                Type = IceCandidateType.Host
            });
        }

        // One reachable low-priority pair.
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "live",
            Component = 1,
            Protocol = "udp",
            Priority = lowPriority,
            Address = IPAddress.Loopback.ToString(),
            Port = livePort,
            Type = IceCandidateType.Srflx
        });

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool connected = await agent.ConnectAsync();
        sw.Stop();
        serverCts.Cancel();

        // Assert: must succeed, and must do so well before the sequential budget (15.5s per pair).
        Assert.True(connected, $"ConnectAsync returned false (elapsed {sw.Elapsed})");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"ConnectAsync took {sw.Elapsed} — parallel checks regressed");
    }
}
