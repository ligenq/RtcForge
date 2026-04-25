using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls;

namespace RtcForge.Dtls;

internal sealed class BouncyCastleDatagramTransport : DatagramTransport
{
    private readonly BlockingCollection<byte[]> _receiveQueue = [];
    private readonly Channel<byte[]> _sendChannel = Channel.CreateUnbounded<byte[]>();
    private readonly Func<byte[], Task> _sendFunc;
    private readonly ILogger? _logger;
    private int _closed;

    public BouncyCastleDatagramTransport(Func<byte[], Task> sendFunc, ILogger? logger = null)
    {
        _sendFunc = sendFunc;
        _logger = logger;
        Task.Run(ProcessSendsAsync).FireAndForget();
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
                if (_logger?.IsEnabled(LogLevel.Trace) == true)
                {
                    byte first = bytesToCopy > 0 ? buffer[0] : (byte)0;
                    _logger.LogTrace("BC-DTLS Receive (immediate) bytesToCopy={Bytes} queueBacklog={Backlog} first=0x{First:X2}", bytesToCopy, _receiveQueue.Count, first);
                }
                return bytesToCopy;
            }

            if (waitMillis > 0)
            {
                if (_receiveQueue.TryTake(out data, waitMillis))
                {
                    int bytesToCopy = Math.Min(buffer.Length, data.Length);
                    data.AsSpan(0, bytesToCopy).CopyTo(buffer);
                    if (_logger?.IsEnabled(LogLevel.Trace) == true)
                    {
                        byte first = bytesToCopy > 0 ? buffer[0] : (byte)0;
                        _logger.LogTrace("BC-DTLS Receive (waited) bytesToCopy={Bytes} waitMs={Wait} first=0x{First:X2}", bytesToCopy, waitMillis, first);
                    }
                    return bytesToCopy;
                }
                if (_logger?.IsEnabled(LogLevel.Trace) == true)
                {
                    _logger.LogTrace("BC-DTLS Receive timeout waitMs={Wait} queueBacklog={Backlog}", waitMillis, _receiveQueue.Count);
                }
            }
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BC-DTLS Receive error");
            return -1;
        }
    }

    public void Send(byte[] buf, int off, int len)
    {
        Send(buf.AsSpan(off, len));
    }

    public void Send(ReadOnlySpan<byte> buffer)
    {
        if (_logger?.IsEnabled(LogLevel.Trace) == true)
        {
            byte first = buffer.Length > 0 ? buffer[0] : (byte)0;
            _logger.LogTrace("BC-DTLS Send (enqueue) bytes={Bytes} first=0x{First:X2}", buffer.Length, first);
        }
        if (!_sendChannel.Writer.TryWrite(buffer.ToArray()))
        {
            if (_logger?.IsEnabled(LogLevel.Warning) == true)
            {
                _logger.LogWarning("BC-DTLS Send dropped - channel closed bytes={Bytes}", buffer.Length);
            }
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
        try
        {
            await foreach (var data in _sendChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_logger?.IsEnabled(LogLevel.Trace) == true)
                {
                    byte first = data.Length > 0 ? data[0] : (byte)0;
                    _logger.LogTrace("BC-DTLS tx (to wire) bytes={Bytes} first=0x{First:X2}", data.Length, first);
                }
                try
                {
                    await _sendFunc(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Warning) == true)
                    {
                        _logger.LogWarning(ex, "BC-DTLS tx send failed bytes={Bytes}", data.Length);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BC-DTLS send loop crashed");
        }
    }
}
