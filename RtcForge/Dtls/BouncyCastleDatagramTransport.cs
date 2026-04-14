using System.Collections.Concurrent;
using System.Threading.Channels;
using Org.BouncyCastle.Tls;

namespace RtcForge.Dtls;

internal sealed class BouncyCastleDatagramTransport : DatagramTransport
{
    private readonly BlockingCollection<byte[]> _receiveQueue = [];
    private readonly Channel<byte[]> _sendChannel = Channel.CreateUnbounded<byte[]>();
    private readonly Func<byte[], Task> _sendFunc;
    private readonly Task _sendLoop;
    private int _closed;

    public BouncyCastleDatagramTransport(Func<byte[], Task> sendFunc)
    {
        _sendFunc = sendFunc;
        _sendLoop = Task.Run(ProcessSendsAsync);
    }

    public void PushReceivedData(byte[] data)
    {
        _receiveQueue.TryAdd(data);
    }

    public int GetReceiveLimit() => 1500;
    public int GetSendLimit() => 1500;

    public int Receive(byte[] buf, int off, int len, int waitMillis)
    {
        return Receive(buf.AsSpan(off, len), waitMillis);
    }

    public int Receive(Span<byte> buffer, int waitMillis)
    {
        try
        {
            if (_receiveQueue.TryTake(out var data))
            {
                int bytesToCopy = Math.Min(buffer.Length, data.Length);
                data.AsSpan(0, bytesToCopy).CopyTo(buffer);
                return bytesToCopy;
            }

            if (waitMillis > 0)
            {
                if (_receiveQueue.TryTake(out data, waitMillis))
                {
                    int bytesToCopy = Math.Min(buffer.Length, data.Length);
                    data.AsSpan(0, bytesToCopy).CopyTo(buffer);
                    return bytesToCopy;
                }
            }
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception) { return -1; }
    }

    public void Send(byte[] buf, int off, int len)
    {
        Send(buf.AsSpan(off, len));
    }

    public void Send(ReadOnlySpan<byte> buffer)
    {
        if (!_sendChannel.Writer.TryWrite(buffer.ToArray()))
        {
            throw new InvalidOperationException("DTLS transport is closed");
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _receiveQueue.CompleteAdding();
        _sendChannel.Writer.TryComplete();
    }

    private async Task ProcessSendsAsync()
    {
        await foreach (var data in _sendChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _sendFunc(data).ConfigureAwait(false);
        }
    }
}
