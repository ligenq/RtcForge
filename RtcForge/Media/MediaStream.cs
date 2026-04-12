namespace RtcForge.Media;

public class MediaStream
{
    public string Id { get; } = Guid.NewGuid().ToString();
    private readonly List<MediaStreamTrack> _tracks = new();

    public MediaStream() { }

    public MediaStream(IEnumerable<MediaStreamTrack> tracks)
    {
        _tracks.AddRange(tracks);
    }

    public void AddTrack(MediaStreamTrack track)
    {
        _tracks.Add(track);
    }

    public void RemoveTrack(MediaStreamTrack track)
    {
        _tracks.Remove(track);
    }

    public IEnumerable<MediaStreamTrack> GetTracks() => _tracks;
    public IEnumerable<AudioStreamTrack> GetAudioTracks() => _tracks.OfType<AudioStreamTrack>();
    public IEnumerable<VideoStreamTrack> GetVideoTracks() => _tracks.OfType<VideoStreamTrack>();
}
