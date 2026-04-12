using System.Threading.Channels;
using Org.BouncyCastle.Tls;

namespace RtcForge.Dtls;

internal sealed class BouncyCastleDatagramTransport : DatagramTransport
{
    private readonly Channel<byte[]> _receiveChannel;
    private readonly Func<byte[], Task> _sendFunc;

    public BouncyCastleDatagramTransport(Func<byte[], Task> sendFunc)
    {
        _sendFunc = sendFunc;
        _receiveChannel = Channel.CreateUnbounded<byte[]>();
    }

    public void PushReceivedData(byte[] data)
    {
        _receiveChannel.Writer.TryWrite(data);
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
            if (_receiveChannel.Reader.TryRead(out var data))
            {
                int bytesToCopy = Math.Min(buffer.Length, data.Length);
                data.AsSpan(0, bytesToCopy).CopyTo(buffer);
                return bytesToCopy;
            }

            if (waitMillis > 0)
            {
                using var cts = new CancellationTokenSource(waitMillis);
                if (_receiveChannel.Reader.WaitToReadAsync(cts.Token).AsTask().GetAwaiter().GetResult())
                {
                    if (_receiveChannel.Reader.TryRead(out data))
                    {
                        int bytesToCopy = Math.Min(buffer.Length, data.Length);
                        data.AsSpan(0, bytesToCopy).CopyTo(buffer);
                        return bytesToCopy;
                    }
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
        _sendFunc(buffer.ToArray()).GetAwaiter().GetResult();
    }

    public void Close()
    {
        _receiveChannel.Writer.TryComplete();
    }
}
