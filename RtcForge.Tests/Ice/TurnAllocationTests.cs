using System.Buffers.Binary;
using System.Net;
using RtcForge.Ice;
using RtcForge.Stun;

namespace RtcForge.Tests.Ice;

public class TurnAllocationTests
{
    [Fact]
    public async Task SendToPeerAsync_CreatesPermissionAndChannelBindBeforeChannelData()
    {
        var requests = new List<StunMessageType>();
        byte[]? rawPacket = null;
        using var allocation = new TurnAllocation(
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3478),
            new IPEndPoint(IPAddress.Parse("203.0.113.10"), 50000),
            "user",
            System.Text.Encoding.UTF8.GetBytes("realm"),
            System.Text.Encoding.UTF8.GetBytes("nonce"),
            System.Text.Encoding.UTF8.GetBytes("key"),
            TimeSpan.FromMinutes(10),
            (request, _) =>
            {
                requests.Add(request.Type);
                return Task.FromResult<StunMessage?>(new StunMessage
                {
                    Type = request.Type switch
                    {
                        StunMessageType.CreatePermissionRequest => StunMessageType.CreatePermissionSuccessResponse,
                        StunMessageType.ChannelBindRequest => StunMessageType.ChannelBindSuccessResponse,
                        _ => throw new InvalidOperationException()
                    },
                    TransactionId = request.TransactionId
                });
            },
            payload =>
            {
                rawPacket = payload;
                return Task.CompletedTask;
            });

        await allocation.SendToPeerAsync([1, 2, 3], new IPEndPoint(IPAddress.Parse("198.51.100.20"), 51413));

        Assert.Equal(
            new[] { StunMessageType.CreatePermissionRequest, StunMessageType.ChannelBindRequest },
            requests);
        Assert.NotNull(rawPacket);
        Assert.Equal(0x4000, BinaryPrimitives.ReadUInt16BigEndian(rawPacket!.AsSpan(0, 2)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(rawPacket.AsSpan(2, 2)));
        Assert.Equal(new byte[] { 1, 2, 3 }, rawPacket.AsSpan(4, 3).ToArray());
    }

    [Fact]
    public void TryTranslateIncoming_DataIndication_ReturnsPeerPacket()
    {
        using var allocation = CreateAllocation((_, _) => Task.FromResult<StunMessage?>(null), _ => Task.CompletedTask);
        var peerEndPoint = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 51413);
        var message = new StunMessage
        {
            Type = StunMessageType.DataIndication,
            TransactionId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
            Attributes =
            {
                StunAttribute.CreateXorPeerAddress(peerEndPoint, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]),
                new StunAttribute { Type = StunAttributeType.Data, Value = [9, 8, 7] }
            }
        };
        byte[] buffer = new byte[message.GetSerializedLength()];
        message.Serialize(buffer);

        bool translated = allocation.TryTranslateIncoming(
            new UdpPacket
            {
                Array = buffer,
                Length = buffer.Length,
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3478)
            },
            out var packet);

        Assert.True(translated);
        Assert.Equal(peerEndPoint, packet.RemoteEndPoint);
        Assert.Equal(new byte[] { 9, 8, 7 }, packet.Span.ToArray());
    }

    [Fact]
    public async Task TryTranslateIncoming_ChannelData_ReturnsBoundPeerPacket()
    {
        using var allocation = CreateAllocation(
            (request, _) => Task.FromResult<StunMessage?>(new StunMessage
            {
                Type = request.Type switch
                {
                    StunMessageType.CreatePermissionRequest => StunMessageType.CreatePermissionSuccessResponse,
                    StunMessageType.ChannelBindRequest => StunMessageType.ChannelBindSuccessResponse,
                    _ => throw new InvalidOperationException()
                },
                TransactionId = request.TransactionId
            }),
            _ => Task.CompletedTask);

        var peerEndPoint = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 51413);
        await allocation.SendToPeerAsync([1], peerEndPoint);

        byte[] channelPacket = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(channelPacket.AsSpan(0, 2), 0x4000);
        BinaryPrimitives.WriteUInt16BigEndian(channelPacket.AsSpan(2, 2), 3);
        channelPacket[4] = 5;
        channelPacket[5] = 6;
        channelPacket[6] = 7;

        bool translated = allocation.TryTranslateIncoming(
            new UdpPacket
            {
                Array = channelPacket,
                Length = channelPacket.Length,
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3478)
            },
            out var packet);

        Assert.True(translated);
        Assert.Equal(peerEndPoint, packet.RemoteEndPoint);
        Assert.Equal(new byte[] { 5, 6, 7 }, packet.Span.ToArray());
    }

    [Fact]
    public async Task RefreshAllocationAsync_UpdatesLifetimeFromResponse()
    {
        using var allocation = CreateAllocation(
            (request, _) => Task.FromResult<StunMessage?>(new StunMessage
            {
                Type = request.Type switch
                {
                    StunMessageType.RefreshRequest => StunMessageType.RefreshSuccessResponse,
                    _ => throw new InvalidOperationException()
                },
                Attributes =
                {
                    CreateUInt32Attribute(StunAttributeType.Lifetime, 90u)
                }
            }),
            _ => Task.CompletedTask);

        await allocation.RefreshAllocationAsync();

        Assert.Equal(TimeSpan.FromSeconds(90), allocation.AllocationLifetime);
    }

    [Fact]
    public async Task RefreshAllocationAsync_RetriesWhenTurnServerReturnsStaleNonce()
    {
        int attempts = 0;
        byte[] latestNonce = System.Text.Encoding.UTF8.GetBytes("nonce-b");
        using var allocation = CreateAllocation(
            (request, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult<StunMessage?>(new StunMessage
                    {
                        Type = StunMessageType.RefreshErrorResponse,
                        Attributes =
                        {
                            CreateErrorCodeAttribute(438, "Stale Nonce"),
                            new StunAttribute { Type = StunAttributeType.Nonce, Value = latestNonce },
                            new StunAttribute { Type = StunAttributeType.Realm, Value = System.Text.Encoding.UTF8.GetBytes("realm-b") }
                        }
                    });
                }

                return Task.FromResult<StunMessage?>(new StunMessage
                {
                    Type = StunMessageType.RefreshSuccessResponse,
                    Attributes =
                    {
                        CreateUInt32Attribute(StunAttributeType.Lifetime, 120u)
                    }
                });
            },
            _ => Task.CompletedTask);

        await allocation.RefreshAllocationAsync();

        Assert.Equal(2, attempts);
        Assert.Equal("nonce-b", System.Text.Encoding.UTF8.GetString(allocation.Nonce));
        Assert.Equal("realm-b", System.Text.Encoding.UTF8.GetString(allocation.Realm));
        Assert.Equal(TimeSpan.FromSeconds(120), allocation.AllocationLifetime);
    }

    [Fact]
    public async Task SendToPeerAsync_ReusesExistingBindingForSamePeer()
    {
        var requests = new List<StunMessageType>();
        var rawPackets = new List<byte[]>();
        using var allocation = CreateAllocation(
            (request, _) =>
            {
                requests.Add(request.Type);
                return Task.FromResult<StunMessage?>(new StunMessage
                {
                    Type = request.Type switch
                    {
                        StunMessageType.CreatePermissionRequest => StunMessageType.CreatePermissionSuccessResponse,
                        StunMessageType.ChannelBindRequest => StunMessageType.ChannelBindSuccessResponse,
                        _ => throw new InvalidOperationException()
                    },
                    TransactionId = request.TransactionId
                });
            },
            packet =>
            {
                rawPackets.Add(packet);
                return Task.CompletedTask;
            });
        var peer = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 51413);

        await allocation.SendToPeerAsync([1], peer);
        await allocation.SendToPeerAsync([2], peer);

        Assert.Equal([StunMessageType.CreatePermissionRequest, StunMessageType.ChannelBindRequest], requests);
        Assert.Equal(2, rawPackets.Count);
        Assert.Equal(0x4000, BinaryPrimitives.ReadUInt16BigEndian(rawPackets[0].AsSpan(0, 2)));
        Assert.Equal(0x4000, BinaryPrimitives.ReadUInt16BigEndian(rawPackets[1].AsSpan(0, 2)));
    }

    [Fact]
    public async Task SendToPeerAsync_CanceledToken_ThrowsBeforeSending()
    {
        using var allocation = CreateAllocation((_, _) => throw new InvalidOperationException("should not send"), _ => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => allocation.SendToPeerAsync([1], new IPEndPoint(IPAddress.Loopback, 1234), cts.Token));
    }

    [Fact]
    public async Task RefreshAllocationAsync_ErrorWithoutStaleNonce_ThrowsReason()
    {
        using var allocation = CreateAllocation(
            (request, _) => Task.FromResult<StunMessage?>(new StunMessage
            {
                Type = StunMessageType.RefreshErrorResponse,
                Attributes = { CreateErrorCodeAttribute(401, "Unauthorized") }
            }),
            _ => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => allocation.RefreshAllocationAsync());

        Assert.Equal("Unauthorized", ex.Message);
    }

    [Fact]
    public async Task RefreshAllocationAsync_NullOrUnexpectedResponse_Throws()
    {
        using var noResponse = CreateAllocation((_, _) => Task.FromResult<StunMessage?>(null), _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noResponse.RefreshAllocationAsync());

        using var unexpected = CreateAllocation(
            (_, _) => Task.FromResult<StunMessage?>(new StunMessage { Type = StunMessageType.BindingSuccessResponse }),
            _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unexpected.RefreshAllocationAsync());
    }

    [Fact]
    public async Task RefreshAllocationAsync_StaleNonceWithoutNonce_DoesNotRetry()
    {
        using var allocation = CreateAllocation(
            (_, _) => Task.FromResult<StunMessage?>(new StunMessage
            {
                Type = StunMessageType.RefreshErrorResponse,
                Attributes = { CreateErrorCodeAttribute(438, "Stale Nonce") }
            }),
            _ => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => allocation.RefreshAllocationAsync());

        Assert.Equal("Stale Nonce", ex.Message);
    }

    [Fact]
    public void TryTranslateIncoming_NonServerOrMalformedPackets_ReturnFalse()
    {
        using var allocation = CreateAllocation((_, _) => Task.FromResult<StunMessage?>(null), _ => Task.CompletedTask);

        Assert.False(allocation.TryTranslateIncoming(new UdpPacket
        {
            Array = [0x40, 0, 0, 0],
            Length = 4,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 3478)
        }, out _));

        Assert.False(allocation.TryTranslateIncoming(new UdpPacket
        {
            Array = [0x40, 0, 0, 1, 9],
            Length = 5,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3478)
        }, out _));

        Assert.False(allocation.TryTranslateIncoming(new UdpPacket
        {
            Array = [0x40, 0, 0, 1, 9],
            Length = 5,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3478)
        }, out _));

        Assert.False(allocation.TryTranslateIncoming(new UdpPacket
        {
            Array = [0x00, 0x01, 0, 0],
            Length = 4,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3478)
        }, out _));
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var allocation = CreateAllocation((_, _) => Task.FromResult<StunMessage?>(null), _ => Task.CompletedTask);

        allocation.Dispose();
        allocation.Dispose();
    }

    private static TurnAllocation CreateAllocation(
        Func<StunMessage, byte[]?, Task<StunMessage?>> sendRequestAsync,
        Func<byte[], Task> sendRawAsync)
    {
        return new TurnAllocation(
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3478),
            new IPEndPoint(IPAddress.Parse("203.0.113.10"), 50000),
            "user",
            System.Text.Encoding.UTF8.GetBytes("realm"),
            System.Text.Encoding.UTF8.GetBytes("nonce"),
            System.Text.Encoding.UTF8.GetBytes("key"),
            TimeSpan.FromMinutes(10),
            sendRequestAsync,
            sendRawAsync);
    }

    private static StunAttribute CreateUInt32Attribute(StunAttributeType type, uint value)
    {
        byte[] buffer = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return new StunAttribute { Type = type, Value = buffer };
    }

    private static StunAttribute CreateErrorCodeAttribute(int code, string reason)
    {
        byte[] reasonBytes = System.Text.Encoding.UTF8.GetBytes(reason);
        byte[] value = new byte[4 + reasonBytes.Length];
        value[2] = (byte)(code / 100);
        value[3] = (byte)(code % 100);
        reasonBytes.CopyTo(value, 4);
        return new StunAttribute { Type = StunAttributeType.ErrorCode, Value = value };
    }
}
