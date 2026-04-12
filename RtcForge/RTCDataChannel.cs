namespace RtcForge;

public enum RTCDataChannelState
{
    Connecting,
    Open,
    Closing,
    Closed
}

public class RTCDataChannel
{
    public string Label { get; }
    public ushort Id { get; }
    public RTCDataChannelState ReadyState { get; internal set; } = RTCDataChannelState.Connecting;

    public event EventHandler? OnOpen;
    public event EventHandler? OnClose;
    public event EventHandler<string>? OnMessage;
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

    public async Task SendAsync(string data)
    {
        if (ReadyState != RTCDataChannelState.Open || _association == null)
        {
            throw new InvalidOperationException("DataChannel not open");
        }

        await _association.SendDataAsync(Id, 51, System.Text.Encoding.UTF8.GetBytes(data));
    }

    public async Task SendAsync(byte[] data)
    {
        if (ReadyState != RTCDataChannelState.Open || _association == null)
        {
            throw new InvalidOperationException("DataChannel not open");
        }

        await _association.SendDataAsync(Id, 53, data);
    }

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
