namespace RtcForge.Ice;

public class IceCandidatePair
{
    public IceCandidate Local { get; }
    public IceCandidate Remote { get; }
    public ulong Priority { get; }
    public IceState State { get; internal set; } = IceState.New;

    public IceCandidatePair(IceCandidate local, IceCandidate remote)
    {
        Local = local;
        Remote = remote;
        Priority = CalculatePriority(local.Priority, remote.Priority);
    }

    private static ulong CalculatePriority(uint localPriority, uint remotePriority)
    {
        // RFC 8445 Section 6.1.2.3
        uint g = localPriority;
        uint e = remotePriority;
        return ((ulong)Math.Min(g, e) << 32) + (2 * (ulong)Math.Max(g, e)) + (ulong)(g > e ? 1 : 0);
    }
}

public class IceCheckScheduler
{
    private readonly List<IceCandidatePair> _pairs = new();
    private readonly IIceAgent _agent;
    private readonly Timer _timer;
    private int _currentIndex = 0;

    public IceCheckScheduler(IIceAgent agent)
    {
        _agent = agent;
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void AddPair(IceCandidatePair pair)
    {
        _pairs.Add(pair);
        _pairs.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    public void Start()
    {
        _timer.Change(0, 20); // 20ms Ta interval (default)
    }

    private void OnTimer(object? state)
    {
        if (_currentIndex >= _pairs.Count)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var pair = _pairs[_currentIndex++];
        PerformCheckAsync(pair).FireAndForget();
    }

    private async Task PerformCheckAsync(IceCandidatePair pair)
    {
        pair.State = IceState.Checking;
        // The agent will handle the actual STUN transaction
        // bool success = await _agent.SendConnectivityCheckAsync(pair);
        // pair.State = success ? IceState.Connected : IceState.Failed;
    }
}
