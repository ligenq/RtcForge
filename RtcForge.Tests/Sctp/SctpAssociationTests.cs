using RtcForge.Sctp;
using System.Reflection;

namespace RtcForge.Tests.Sctp;

public class SctpAssociationTests
{
    [Fact]
    public async Task Handshake_Loopback_EstablishesConnection()
    {
        // Arrange
        SctpAssociation peerA = null!;
        SctpAssociation peerB = null!;

        peerA = new SctpAssociation(5000, 5000, async (data) => {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System); // Simulate network
            await peerB.HandlePacketAsync(data);
        });

        peerB = new SctpAssociation(5000, 5000, async (data) => {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System);
            await peerA.HandlePacketAsync(data);
        });

        // Act
        await peerB.StartAsync(false); // Server
        await peerA.StartAsync(true);  // Client

        // Wait for handshake
        for (int i = 0; i < 100; i++)
        {
            if (peerA.State == SctpAssociationState.Established && peerB.State == SctpAssociationState.Established)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        // Assert
        Assert.Equal(SctpAssociationState.Established, peerA.State);
        Assert.Equal(SctpAssociationState.Established, peerB.State);
    }

    [Fact]
    public async Task HandlePacketAsync_CookieEchoWithWrongVerificationTag_DoesNotEstablish()
    {
        var sentPackets = new List<byte[]>();
        using var peer = new SctpAssociation(5000, 5000, data =>
        {
            sentPackets.Add(data);
            return Task.CompletedTask;
        });
        await peer.StartAsync(isClient: false);

        SetPrivateField(peer, "_stateCookie", new byte[] { 1, 2, 3, 4 });
        uint myVerificationTag = GetPrivateField<uint>(peer, "_myVerificationTag");
        byte[] packet = SerializePacket(
            verificationTag: myVerificationTag + 1,
            new SctpCookieEchoChunk { Cookie = [1, 2, 3, 4] });

        await peer.HandlePacketAsync(packet);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System);

        Assert.Equal(SctpAssociationState.Closed, peer.State);
        Assert.Empty(sentPackets);
    }

    [Fact]
    public async Task HandlePacketAsync_CookieEchoWithInvalidCookie_DoesNotEstablish()
    {
        var sentPackets = new List<byte[]>();
        using var peer = new SctpAssociation(5000, 5000, data =>
        {
            sentPackets.Add(data);
            return Task.CompletedTask;
        });
        await peer.StartAsync(isClient: false);

        SetPrivateField(peer, "_stateCookie", new byte[] { 1, 2, 3, 4 });
        uint myVerificationTag = GetPrivateField<uint>(peer, "_myVerificationTag");
        byte[] packet = SerializePacket(
            verificationTag: myVerificationTag,
            new SctpCookieEchoChunk { Cookie = [9, 9, 9, 9] });

        await peer.HandlePacketAsync(packet);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System);

        Assert.Equal(SctpAssociationState.Closed, peer.State);
        Assert.Empty(sentPackets);
    }

    [Fact]
    public void HandleDcepMessage_Open_CreatesRemoteDataChannel()
    {
        var sentPackets = new List<byte[]>();
        using var association = new SctpAssociation(5000, 5000, data =>
        {
            sentPackets.Add(data);
            return Task.CompletedTask;
        });
        SetPrivateField(association, "_state", SctpAssociationState.Established);
        RTCDataChannel? remoteChannel = null;
        association.OnRemoteDataChannel += (_, channel) => remoteChannel = channel;
        var open = new DcepMessage
        {
            Type = DcepMessageType.DataChannelOpen,
            Label = "remote"
        };

        InvokePrivate(association, "HandleDcepMessage", (ushort)7, open.Serialize());

        Assert.NotNull(remoteChannel);
        Assert.Equal("remote", remoteChannel!.Label);
        Assert.Equal((ushort)7, remoteChannel.Id);
        Assert.Equal(RTCDataChannelState.Open, remoteChannel.ReadyState);
    }

    [Fact]
    public void HandleDcepMessage_Ack_OpensRegisteredChannel()
    {
        using var association = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var channel = new RTCDataChannel("local", 3, association);
        association.RegisterDataChannel(channel);

        InvokePrivate(association, "HandleDcepMessage", (ushort)3, new DcepMessage { Type = DcepMessageType.DataChannelAck }.Serialize());

        Assert.Equal(RTCDataChannelState.Open, channel.ReadyState);
    }

    [Fact]
    public void DispatchStreamMessage_ForRegisteredChannel_DeliversUserData()
    {
        using var association = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var channel = new RTCDataChannel("local", 3, association);
        byte[]? received = null;
        channel.OnBinaryMessage += (_, data) => received = data;
        association.RegisterDataChannel(channel);

        InvokePrivate(association, "DispatchStreamMessage", (ushort)3, 53u, new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, received);
    }

    [Fact]
    public void DispatchStreamMessage_ForUnknownChannel_DropsMessage()
    {
        using var association = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);

        InvokePrivate(association, "DispatchStreamMessage", (ushort)99, 53u, new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task HandleSackChunk_GapAckMarksOutstandingChunkAcked()
    {
        var sentPackets = new List<byte[]>();
        using var association = new SctpAssociation(5000, 5000, data =>
        {
            sentPackets.Add(data);
            return Task.CompletedTask;
        });
        SetPrivateField(association, "_state", SctpAssociationState.Established);

        await association.SendDataAsync(1, 53, [1, 2, 3]);
        Assert.True(SctpPacket.TryParse(sentPackets.Single(), out var packet));
        var data = Assert.IsType<SctpDataChunk>(Assert.Single(packet.Chunks));

        InvokePrivate(association, "HandleSackChunk", new SctpSackChunk
        {
            CumulativeTsnAck = data.Tsn - 1,
            GapAckBlocks = { (1, 1) }
        });

        Assert.Equal(0u, GetPrivateField<uint>(association, "_outstandingBytes"));
    }

    private static byte[] SerializePacket(uint verificationTag, SctpChunk chunk)
    {
        var packet = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000,
            VerificationTag = verificationTag
        };
        packet.Chunks.Add(chunk);
        byte[] buffer = new byte[packet.GetSerializedLength()];
        packet.Serialize(buffer);
        return buffer;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        return (T)instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;
    }

    private static void SetPrivateField<T>(object instance, string fieldName, T value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static void InvokePrivate(object instance, string methodName, params object[] args)
    {
        instance.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, args);
    }
}
