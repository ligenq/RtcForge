using RtcForge.Sdp;
using System.Reflection;
using RtcForge.Ice;

namespace RtcForge.Tests.Integration;

public class SignalingStateTests
{
    [Fact]
    public async Task SignalingState_Transitions_Correctly()
    {
        var pc = new RTCPeerConnection();
        Assert.Equal(SignalingState.Stable, pc.SignalingState);

        var offer = new SdpMessage { SessionName = "Offer" };
        await pc.SetLocalDescriptionAsync(offer);
        Assert.Equal(SignalingState.HaveLocalOffer, pc.SignalingState);

        var answer = new SdpMessage { SessionName = "Answer" };
        await pc.SetRemoteDescriptionAsync(answer);
        Assert.Equal(SignalingState.Stable, pc.SignalingState);
    }

    [Fact]
    public async Task RemoteOffer_Transitions_Correctly()
    {
        var pc = new RTCPeerConnection();
        var offer = new SdpMessage { SessionName = "Offer" };

        await pc.SetRemoteDescriptionAsync(offer);
        Assert.Equal(SignalingState.HaveRemoteOffer, pc.SignalingState);

        var answer = new SdpMessage { SessionName = "Answer" };
        await pc.SetLocalDescriptionAsync(answer);
        Assert.Equal(SignalingState.Stable, pc.SignalingState);
    }

    [Fact]
    public async Task ResolveDtlsClientRole_UsesNegotiatedSetupAttributes()
    {
        using var offerer = new RTCPeerConnection();
        using var answerer = new RTCPeerConnection();

        var offer = await offerer.CreateOfferAsync();
        await offerer.SetLocalDescriptionAsync(offer);
        await answerer.SetRemoteDescriptionAsync(offer);

        var answer = await answerer.CreateAnswerAsync();
        await answerer.SetLocalDescriptionAsync(answer);
        await offerer.SetRemoteDescriptionAsync(answer);

        Assert.True(offerer.ResolveDtlsClientRole());
        Assert.False(answerer.ResolveDtlsClientRole());
    }

    [Fact]
    public async Task Answerer_RemainsIceControlled_AfterSettingLocalAnswer()
    {
        using var offerer = new RTCPeerConnection();
        using var answerer = new RTCPeerConnection();

        var offer = await offerer.CreateOfferAsync();
        await answerer.SetRemoteDescriptionAsync(offer);

        var answer = await answerer.CreateAnswerAsync();
        await answerer.SetLocalDescriptionAsync(answer);

        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(answerer)!;

        Assert.False(iceAgent.IsControlling);
    }
}
