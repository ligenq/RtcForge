using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class MessageSizeLimitTests
{
    [Fact]
    public async Task RTCDataChannel_SendAsync_String_RejectsOversizedMessage()
    {
        var assocA = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var dc = new RTCDataChannel("test", 1, assocA);
        dc.SetOpen();

        string oversized = new('A', SctpAssociation.MaxMessageSize + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => dc.SendAsync(oversized));
        Assert.Contains("exceeds maximum", ex.Message);
    }

    [Fact]
    public async Task RTCDataChannel_SendAsync_Bytes_RejectsOversizedMessage()
    {
        var assocA = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var dc = new RTCDataChannel("test", 1, assocA);
        dc.SetOpen();

        byte[] oversized = new byte[SctpAssociation.MaxMessageSize + 1];

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => dc.SendAsync(oversized));
        Assert.Contains("exceeds maximum", ex.Message);
    }

    [Fact]
    public async Task RTCDataChannel_SendAsync_Bytes_AllowsExactMaxSize()
    {
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async data => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async data => await assocA.HandlePacketAsync(data));

        var dcA = new RTCDataChannel("test", 1, assocA);
        var dcB = new RTCDataChannel("test", 1, assocB);
        assocA.RegisterDataChannel(dcA);
        assocB.RegisterDataChannel(dcB);

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        dcA.SetOpen();
        dcB.SetOpen();

        byte[] maxSize = new byte[SctpAssociation.MaxMessageSize];
        await dcA.SendAsync(maxSize);

        assocA.Dispose();
        assocB.Dispose();
    }

    [Fact]
    public async Task SctpAssociation_SendDataAsync_RejectsOversizedMessage()
    {
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async data => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async data => await assocA.HandlePacketAsync(data));

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        byte[] oversized = new byte[SctpAssociation.MaxMessageSize + 1];

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => assocA.SendDataAsync(1, 53, oversized));
        Assert.Contains("exceeds maximum", ex.Message);

        assocA.Dispose();
        assocB.Dispose();
    }

    [Fact]
    public async Task SctpAssociation_SendDataAsync_WhenNotEstablished_Throws()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => assoc.SendDataAsync(1, 53, new byte[] { 1, 2, 3 }));

        assoc.Dispose();
    }

    [Fact]
    public void MaxMessageSize_IsExpectedValue()
    {
        Assert.Equal(262144, SctpAssociation.MaxMessageSize);
    }
}
