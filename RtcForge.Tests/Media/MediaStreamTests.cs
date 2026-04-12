using RtcForge.Media;

namespace RtcForge.Tests.Media;

public class MediaStreamTests
{
    [Fact]
    public void Constructor_Default_HasNoTracks()
    {
        var stream = new MediaStream();

        Assert.Empty(stream.GetTracks());
        Assert.NotNull(stream.Id);
    }

    [Fact]
    public void Constructor_WithTracks_ContainsTracks()
    {
        var audio = new AudioStreamTrack();
        var video = new VideoStreamTrack();

        var stream = new MediaStream(new MediaStreamTrack[] { audio, video });

        Assert.Equal(2, stream.GetTracks().Count());
    }

    [Fact]
    public void AddTrack_IncreasesCount()
    {
        var stream = new MediaStream();
        stream.AddTrack(new AudioStreamTrack());

        Assert.Single(stream.GetTracks());
    }

    [Fact]
    public void RemoveTrack_DecreasesCount()
    {
        var track = new AudioStreamTrack();
        var stream = new MediaStream(new[] { track });

        stream.RemoveTrack(track);

        Assert.Empty(stream.GetTracks());
    }

    [Fact]
    public void RemoveTrack_NonExistent_DoesNothing()
    {
        var stream = new MediaStream(new[] { new AudioStreamTrack() });
        stream.RemoveTrack(new AudioStreamTrack());

        Assert.Single(stream.GetTracks());
    }

    [Fact]
    public void GetAudioTracks_FiltersCorrectly()
    {
        var stream = new MediaStream(new MediaStreamTrack[]
        {
            new AudioStreamTrack(),
            new VideoStreamTrack(),
            new AudioStreamTrack()
        });

        Assert.Equal(2, stream.GetAudioTracks().Count());
    }

    [Fact]
    public void GetVideoTracks_FiltersCorrectly()
    {
        var stream = new MediaStream(new MediaStreamTrack[]
        {
            new AudioStreamTrack(),
            new VideoStreamTrack(),
            new AudioStreamTrack()
        });

        Assert.Single(stream.GetVideoTracks());
    }

    [Fact]
    public void GetAudioTracks_NoAudio_ReturnsEmpty()
    {
        var stream = new MediaStream(new MediaStreamTrack[] { new VideoStreamTrack() });

        Assert.Empty(stream.GetAudioTracks());
    }

    [Fact]
    public void GetVideoTracks_NoVideo_ReturnsEmpty()
    {
        var stream = new MediaStream(new MediaStreamTrack[] { new AudioStreamTrack() });

        Assert.Empty(stream.GetVideoTracks());
    }

    [Fact]
    public void AudioStreamTrack_HasCorrectKind()
    {
        var track = new AudioStreamTrack();

        Assert.Equal("audio", track.Kind);
        Assert.True(track.Enabled);
        Assert.NotNull(track.Id);
    }

    [Fact]
    public void VideoStreamTrack_HasCorrectKind()
    {
        var track = new VideoStreamTrack();

        Assert.Equal("video", track.Kind);
        Assert.True(track.Enabled);
    }
}
