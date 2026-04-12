namespace RtcForge.Media;

public class RTCTrackEvent : EventArgs
{
    public RTCRtpReceiver Receiver { get; }
    public MediaStreamTrack Track { get; }
    public RTCRtpTransceiver Transceiver { get; }

    public RTCTrackEvent(RTCRtpReceiver receiver, MediaStreamTrack track, RTCRtpTransceiver transceiver)
    {
        Receiver = receiver;
        Track = track;
        Transceiver = transceiver;
    }
}
