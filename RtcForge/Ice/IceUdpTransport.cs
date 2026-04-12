using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace RtcForge.Ice;

public class IceUdpTransport : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly Channel<UdpPacket> _receiveChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<IceUdpTransport>? _logger;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udpClient.Client.LocalEndPoint!;

    public IceUdpTransport(int port = 0, ILoggerFactory? loggerFactory = null) : this(new IPEndPoint(IPAddress.Any, port), loggerFactory)
    {
    }

    public IceUdpTransport(IPEndPoint localEndPoint, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<IceUdpTransport>();
        _udpClient = new UdpClient(localEndPoint);
        _receiveChannel = Channel.CreateUnbounded<UdpPacket>();

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
                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    var result = await _udpClient.Client.ReceiveFromAsync(buffer, SocketFlags.None, remoteEP, _cts.Token);
                    await _receiveChannel.Writer.WriteAsync(new UdpPacket
                    {
                        Array = buffer,
                        Length = result.ReceivedBytes,
                        RemoteEndPoint = (IPEndPoint)result.RemoteEndPoint
                    }, _cts.Token);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) { }
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
        await _udpClient.SendAsync(data, data.Length, remoteEndPoint);
    }

    public ChannelReader<UdpPacket> GetReader() => _receiveChannel.Reader;

    public void Dispose()
    {
        _cts.Cancel();
        _udpClient.Close(); // Close first to unblock ReceiveAsync
        _udpClient.Dispose();
        _cts.Dispose();
    }
}

public struct UdpPacket
{
    public byte[] Array;
    public int Length;
    public IPEndPoint RemoteEndPoint;

    public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(Array, 0, Length);
    public ReadOnlyMemory<byte> Data => new ReadOnlyMemory<byte>(Array, 0, Length);
}
