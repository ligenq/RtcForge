using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace RtcForge.Ice;

public class IceUdpTransport : IDisposable
{
    private const int MaxDatagramSize = 65536;
    private const int ReceiveQueueCapacity = 1024;

    private readonly UdpClient _udpClient;
    private readonly Channel<UdpPacket> _receiveChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<IceUdpTransport>? _logger;
    private int _disposed;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udpClient.Client.LocalEndPoint!;

    public IceUdpTransport(int port = 0, ILoggerFactory? loggerFactory = null) : this(new IPEndPoint(IPAddress.Any, port), loggerFactory)
    {
    }

    public IceUdpTransport(IPEndPoint localEndPoint, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<IceUdpTransport>();
        _udpClient = new UdpClient(localEndPoint);
        _receiveChannel = Channel.CreateBounded<UdpPacket>(new BoundedChannelOptions(ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        // Start the receive loop
        Task.Run(ReceiveLoopAsync).FireAndForget();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            EndPoint remoteEP = _udpClient.Client.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);
            while (!_cts.IsCancellationRequested)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
                try
                {
                    var result = await _udpClient.Client.ReceiveFromAsync(buffer, SocketFlags.None, remoteEP, _cts.Token);
                    if (_logger?.IsEnabled(LogLevel.Trace) == true)
                    {
                        byte first = result.ReceivedBytes > 0 ? buffer[0] : (byte)0;
                        _logger.LogTrace(
                            "IceUdpTransport {Local} rx bytes={Bytes} from={Remote} first=0x{First:X2} class={Class}",
                            LocalEndPoint, result.ReceivedBytes, result.RemoteEndPoint, first, ClassifyFirstByte(first));
                    }
                    await _receiveChannel.Writer.WriteAsync(new UdpPacket
                    {
                        Array = buffer,
                        Length = result.ReceivedBytes,
                        RemoteEndPoint = (IPEndPoint)result.RemoteEndPoint
                    }, _cts.Token);
                }
                catch (SocketException ex) when (IsIgnorableUdpReceiveError(ex) && !_cts.IsCancellationRequested)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the transport is disposed.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ReceiveLoop Error on endpoint {LocalEndPoint}", LocalEndPoint);
        }
        finally
        {
            _receiveChannel.Writer.Complete();
        }
    }

    public async Task SendAsync(byte[] data, IPEndPoint remoteEndPoint)
    {
        await SendAsync(data.AsMemory(), remoteEndPoint).ConfigureAwait(false);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remoteEndPoint)
    {
        await _udpClient.Client.SendToAsync(data, SocketFlags.None, remoteEndPoint, _cts.Token).ConfigureAwait(false);
    }

    public ChannelReader<UdpPacket> GetReader() => _receiveChannel.Reader;

    private static bool IsIgnorableUdpReceiveError(SocketException exception)
    {
        return exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused;
    }

    private static string ClassifyFirstByte(byte b) => b switch
    {
        <= 3 => "STUN",
        >= 20 and <= 63 => "DTLS",
        >= 128 and <= 191 => "RTP/RTCP",
        _ => "UNKNOWN"
    };

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!disposing)
        {
            return;
        }

        _cts.Cancel();
        _udpClient.Close(); // Close first to unblock ReceiveAsync
        _udpClient.Dispose();
        _cts.Dispose();
    }
}

public struct UdpPacket
{
    public byte[] Array { get; set; }
    public int Length { get; set; }
    public IPEndPoint RemoteEndPoint { get; set; }

    public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(Array, 0, Length);
    public ReadOnlyMemory<byte> Data => new ReadOnlyMemory<byte>(Array, 0, Length);
}
