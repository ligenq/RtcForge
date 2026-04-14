using System.Reflection;
using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class SctpAssociationDisposeTests
{
    [Fact]
    public async Task SctpAssociation_Dispose_CancelsBackgroundLoops()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        await assoc.StartAsync(false);

        assoc.Dispose();

        Assert.Equal(SctpAssociationState.Closed, assoc.State);
    }

    [Fact]
    public void SctpAssociation_Dispose_SetsStateToClosed()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        assoc.Dispose();

        Assert.Equal(SctpAssociationState.Closed, assoc.State);
    }

    [Fact]
    public void SctpAssociation_Dispose_ClosesRegisteredDataChannels()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var dc = new RTCDataChannel("test", 1, assoc);
        assoc.RegisterDataChannel(dc);
        dc.SetOpen();

        assoc.Dispose();

        Assert.Equal(RTCDataChannelState.Closed, dc.ReadyState);
    }
}

public class SctpConcurrentSsnTests
{
    [Fact]
    public async Task SctpAssociation_ConcurrentTsnIncrement_ProducesUniqueValues()
    {
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async data =>
        {
            await assocB.HandlePacketAsync(data);
        });
        assocB = new SctpAssociation(5000, 5000, async data =>
        {
            await assocA.HandlePacketAsync(data);
        });

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        Assert.Equal(SctpAssociationState.Established, assocA.State);

        const int messageCount = 50;
        var tasks = Enumerable.Range(0, messageCount).Select(i =>
            Task.Run(async () =>
            {
                await assocA.SendDataAsync(1, 51, System.Text.Encoding.UTF8.GetBytes($"msg-{i}"));
            })).ToArray();

        await Task.WhenAll(tasks);

        assocA.Dispose();
        assocB.Dispose();
    }

    [Fact]
    public async Task ConcurrentSendOnSameStream_ProducesUniqueSequenceNumbers()
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

        Assert.Equal(SctpAssociationState.Established, assocA.State);

        const int messageCount = 30;
        const ushort streamId = 1;
        var tasks = Enumerable.Range(0, messageCount).Select(i =>
            Task.Run(async () =>
            {
                await assocA.SendDataAsync(streamId, 51,
                    System.Text.Encoding.UTF8.GetBytes($"msg-{i}"));
            })).ToArray();

        await Task.WhenAll(tasks);

        var ssnField = typeof(SctpAssociation)
            .GetField("_outboundSsns", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ssnDict = (System.Collections.Concurrent.ConcurrentDictionary<ushort, ushort>)ssnField.GetValue(assocA)!;
        Assert.True(ssnDict.TryGetValue(streamId, out var finalSsn));
        Assert.Equal(messageCount, finalSsn);

        assocA.Dispose();
        assocB.Dispose();
    }
}

public class SctpBackpressureTests
{
    [Fact]
    public async Task SctpAssociation_BoundedInputChannel_AcceptsPackets()
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

        Assert.Equal(SctpAssociationState.Established, assocA.State);
        Assert.Equal(SctpAssociationState.Established, assocB.State);

        for (int i = 0; i < 10; i++)
        {
            await assocA.SendDataAsync(1, 51, System.Text.Encoding.UTF8.GetBytes($"msg-{i}"));
        }

        assocA.Dispose();
        assocB.Dispose();
    }
}
