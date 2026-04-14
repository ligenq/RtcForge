namespace RtcForge;

/// <summary>
/// Describes the state of a low-level WebRTC data channel.
/// </summary>
public enum RTCDataChannelState
{
    /// <summary>
    /// The channel has been created but is not ready to send data.
    /// </summary>
    Connecting,

    /// <summary>
    /// The channel is open and can send data.
    /// </summary>
    Open,

    /// <summary>
    /// The channel is closing.
    /// </summary>
    Closing,

    /// <summary>
    /// The channel is closed.
    /// </summary>
    Closed
}

/// <summary>
/// Represents a low-level SCTP-backed WebRTC data channel.
/// </summary>
/// <remarks>
/// Application code should usually use <see cref="IWebRtcDataChannel"/> unless it needs direct access to the lower-level peer connection API.
/// </remarks>
public class RTCDataChannel
{
    /// <summary>
    /// Gets the data channel label.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the SCTP stream identifier used by the channel.
    /// </summary>
    public ushort Id { get; }

    /// <summary>
    /// Gets the current data channel state.
    /// </summary>
    public RTCDataChannelState ReadyState { get; internal set; } = RTCDataChannelState.Connecting;

    /// <summary>
    /// Occurs when the channel opens.
    /// </summary>
    public event EventHandler? OnOpen;

    /// <summary>
    /// Occurs when the channel closes.
    /// </summary>
    public event EventHandler? OnClose;

    /// <summary>
    /// Occurs when a text message is received.
    /// </summary>
    public event EventHandler<string>? OnMessage;

    /// <summary>
    /// Occurs when a binary message is received.
    /// </summary>
    public event EventHandler<byte[]>? OnBinaryMessage;

    private Sctp.SctpAssociation? _association;

    internal RTCDataChannel(string label, ushort id, Sctp.SctpAssociation? association = null)
    {
        Label = label;
        Id = id;
        _association = association;
    }

    internal void SetAssociation(Sctp.SctpAssociation association)
    {
        _association = association;
    }

    /// <summary>
    /// Sends a text message on the channel.
    /// </summary>
    /// <param name="data">The text payload.</param>
    /// <returns>A task that completes when the message has been handed to SCTP.</returns>
    /// <exception cref="InvalidOperationException">The channel is not open.</exception>
    /// <exception cref="ArgumentException">The encoded message exceeds the maximum SCTP message size.</exception>
    public async Task SendAsync(string data)
    {
        if (ReadyState != RTCDataChannelState.Open || _association == null)
        {
            throw new InvalidOperationException("DataChannel not open");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        if (bytes.Length > Sctp.SctpAssociation.MaxMessageSize)
        {
            throw new ArgumentException($"Message size {bytes.Length} exceeds maximum {Sctp.SctpAssociation.MaxMessageSize}.", nameof(data));
        }

        await _association.SendDataAsync(Id, 51, bytes);
    }

    /// <summary>
    /// Sends a binary message on the channel.
    /// </summary>
    /// <param name="data">The binary payload.</param>
    /// <returns>A task that completes when the message has been handed to SCTP.</returns>
    /// <exception cref="InvalidOperationException">The channel is not open.</exception>
    /// <exception cref="ArgumentException">The message exceeds the maximum SCTP message size.</exception>
    public async Task SendAsync(byte[] data)
    {
        if (ReadyState != RTCDataChannelState.Open || _association == null)
        {
            throw new InvalidOperationException("DataChannel not open");
        }

        if (data.Length > Sctp.SctpAssociation.MaxMessageSize)
        {
            throw new ArgumentException($"Message size {data.Length} exceeds maximum {Sctp.SctpAssociation.MaxMessageSize}.", nameof(data));
        }

        await _association.SendDataAsync(Id, 53, data);
    }

    /// <summary>
    /// Closes the channel locally.
    /// </summary>
    public void Close()
    {
        SetClosed();
    }

    internal void HandleIncomingData(uint ppid, byte[] data)
    {
        switch (ppid)
        {
            case 51: OnMessage?.Invoke(this, System.Text.Encoding.UTF8.GetString(data)); break;
            case 53: OnBinaryMessage?.Invoke(this, data); break;
        }
    }

    internal void SetOpen()
    {
        ReadyState = RTCDataChannelState.Open;
        OnOpen?.Invoke(this, EventArgs.Empty);
    }

    internal void SetClosed()
    {
        if (ReadyState == RTCDataChannelState.Closed)
        {
            return;
        }

        ReadyState = RTCDataChannelState.Closed;
        OnClose?.Invoke(this, EventArgs.Empty);
    }
}
