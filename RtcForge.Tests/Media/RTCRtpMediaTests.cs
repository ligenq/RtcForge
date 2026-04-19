using RtcForge.Media;
using RtcForge.Rtp;

namespace RtcForge.Tests.Media;

public class RTCRtpMediaTests
{
    [Fact]
    public async Task Sender_SendRtpAsync_StoresInHistory()
    {
        var sentPackets = new List<RtpPacket>();
        var sender = new RTCRtpSender(new AudioStreamTrack(), p => { sentPackets.Add(p); return Task.CompletedTask; });

        var packet = new RtpPacket { SequenceNumber = 42, PayloadType = 111 };
        await sender.SendRtpAsync(packet);

        Assert.Single(sentPackets);
        Assert.Equal((ushort)42, sentPackets[0].SequenceNumber);
    }

    [Fact]
    public async Task Sender_HandleNack_RetransmitsFromHistory()
    {
        var sentPackets = new List<RtpPacket>();
        var sender = new RTCRtpSender(new AudioStreamTrack(), p => { sentPackets.Add(p); return Task.CompletedTask; });

        // Send original packets
        await sender.SendRtpAsync(new RtpPacket { SequenceNumber = 10 });
        await sender.SendRtpAsync(new RtpPacket { SequenceNumber = 11 });
        await sender.SendRtpAsync(new RtpPacket { SequenceNumber = 12 });

        sentPackets.Clear();

        // NACK for seq 11
        var nack = new RtcpNackPacket { MediaSsrc = 0, LostSequenceNumbers = [11] };
        await sender.HandleNackAsync(nack);

        Assert.Single(sentPackets);
        Assert.Equal((ushort)11, sentPackets[0].SequenceNumber);
    }

    [Fact]
    public async Task Sender_HandleNack_UnknownSequence_DoesNotSend()
    {
        var sentPackets = new List<RtpPacket>();
        var sender = new RTCRtpSender(new AudioStreamTrack(), p => { sentPackets.Add(p); return Task.CompletedTask; });

        var nack = new RtcpNackPacket { MediaSsrc = 0, LostSequenceNumbers = [999] };
        await sender.HandleNackAsync(nack);

        Assert.Empty(sentPackets);
    }

    [Fact]
    public async Task Sender_HandlePli_FiresOnPictureLoss()
    {
        bool fired = false;
        var sender = new RTCRtpSender(new VideoStreamTrack(), _ => Task.CompletedTask);
        sender.OnPictureLoss += (_, _) => fired = true;

        await sender.HandlePliAsync(new RtcpPliPacket());

        Assert.True(fired);
    }

    [Fact]
    public void Sender_ReplaceTrack_ChangesTrack()
    {
        var original = new AudioStreamTrack();
        var replacement = new AudioStreamTrack();
        var sender = new RTCRtpSender(original, _ => Task.CompletedTask);

        Assert.Same(original, sender.Track);

        sender.ReplaceTrack(replacement);

        Assert.Same(replacement, sender.Track);
    }

    [Fact]
    public void Sender_ReplaceTrack_WithNull_ClearsTrack()
    {
        var sender = new RTCRtpSender(new AudioStreamTrack(), _ => Task.CompletedTask);

        sender.ReplaceTrack(null);

        Assert.Null(sender.Track);
    }

    [Fact]
    public async Task Sender_HistoryEvictsOldest_WhenOverLimit()
    {
        var sentPackets = new List<RtpPacket>();
        var sender = new RTCRtpSender(new AudioStreamTrack(), p => { sentPackets.Add(p); return Task.CompletedTask; });

        // Send 101 packets (history limit is 100)
        for (ushort i = 0; i <= 100; i++)
        {
            await sender.SendRtpAsync(new RtpPacket { SequenceNumber = i });
        }

        sentPackets.Clear();

        // Packet 0 should have been evicted
        var nack = new RtcpNackPacket { LostSequenceNumbers = [0] };
        await sender.HandleNackAsync(nack);
        Assert.Empty(sentPackets);

        // Packet 100 should still be in history
        nack = new RtcpNackPacket { LostSequenceNumbers = [100] };
        await sender.HandleNackAsync(nack);
        Assert.Single(sentPackets);
    }

    [Fact]
    public async Task Receiver_HandleRtpPacket_AddsToJitterBuffer()
    {
        var rtcpSent = new List<RtcpPacket>();
        var receiver = new RTCRtpReceiver(new AudioStreamTrack(), p => { rtcpSent.Add(p); return Task.CompletedTask; });

        await receiver.HandleRtpPacketAsync(new RtpPacket { SequenceNumber = 1 });

        var packet = receiver.GetNextPacket();
        Assert.NotNull(packet);
        Assert.Equal((ushort)1, packet.SequenceNumber);
    }

    [Fact]
    public async Task Receiver_HandleRtpPacket_Gap_SendsNack()
    {
        var rtcpSent = new List<RtcpPacket>();
        var receiver = new RTCRtpReceiver(new AudioStreamTrack(), p => { rtcpSent.Add(p); return Task.CompletedTask; });

        await receiver.HandleRtpPacketAsync(new RtpPacket { SequenceNumber = 1 });
        await receiver.HandleRtpPacketAsync(new RtpPacket { SequenceNumber = 4 }); // gap: 2, 3 missing

        Assert.Single(rtcpSent);
        var nack = Assert.IsType<RtcpNackPacket>(rtcpSent[0]);
        Assert.Contains((ushort)2, nack.LostSequenceNumbers);
        Assert.Contains((ushort)3, nack.LostSequenceNumbers);
    }

    [Fact]
    public async Task Receiver_HandleRtpPacket_NoGap_NoNack()
    {
        var rtcpSent = new List<RtcpPacket>();
        var receiver = new RTCRtpReceiver(new AudioStreamTrack(), p => { rtcpSent.Add(p); return Task.CompletedTask; });

        await receiver.HandleRtpPacketAsync(new RtpPacket { SequenceNumber = 1 });
        await receiver.HandleRtpPacketAsync(new RtpPacket { SequenceNumber = 2 });

        Assert.Empty(rtcpSent);
    }

    [Fact]
    public async Task Receiver_RequestKeyFrameAsync_SendsPli()
    {
        var rtcpSent = new List<RtcpPacket>();
        var receiver = new RTCRtpReceiver(new VideoStreamTrack(), p => { rtcpSent.Add(p); return Task.CompletedTask; });

        await receiver.RequestKeyFrameAsync();

        Assert.Single(rtcpSent);
        Assert.IsType<RtcpPliPacket>(rtcpSent[0]);
    }

    [Fact]
    public void Transceiver_Stop_SetsDirectionInactive()
    {
        var sender = new RTCRtpSender(new AudioStreamTrack(), _ => Task.CompletedTask);
        var receiver = new RTCRtpReceiver(new AudioStreamTrack(), _ => Task.CompletedTask);
        var transceiver = new RTCRtpTransceiver(sender, receiver)
        {
            Direction = RTCRtpTransceiverDirection.SendRecv
        };

        transceiver.Stop();

        Assert.Equal(RTCRtpTransceiverDirection.Inactive, transceiver.Direction);
    }

    [Fact]
    public void Transceiver_DefaultDirection_IsSendRecv()
    {
        var sender = new RTCRtpSender(null, _ => Task.CompletedTask);
        var receiver = new RTCRtpReceiver(new AudioStreamTrack(), _ => Task.CompletedTask);
        var transceiver = new RTCRtpTransceiver(sender, receiver);

        Assert.Equal(RTCRtpTransceiverDirection.SendRecv, transceiver.Direction);
    }

    [Fact]
    public void RTCTrackEvent_ExposesConstructorArguments()
    {
        var sender = new RTCRtpSender(new AudioStreamTrack(), _ => Task.CompletedTask);
        var receiver = new RTCRtpReceiver(new VideoStreamTrack(), _ => Task.CompletedTask);
        var transceiver = new RTCRtpTransceiver(sender, receiver);

        var trackEvent = new RTCTrackEvent(receiver, receiver.Track, transceiver);

        Assert.Same(receiver, trackEvent.Receiver);
        Assert.Same(receiver.Track, trackEvent.Track);
        Assert.Same(transceiver, trackEvent.Transceiver);
    }
}
