namespace RtcForge.Media;

public class RTCTrackEventArgs : EventArgs
{
    public RTCRtpReceiver Receiver { get; }
    public MediaStreamTrack Track { get; }
    public RTCRtpTransceiver Transceiver { get; }

    public RTCTrackEventArgs(RTCRtpReceiver receiver, MediaStreamTrack track, RTCRtpTransceiver transceiver)
    {
        Receiver = receiver;
        Track = track;
        Transceiver = transceiver;
    }
}
