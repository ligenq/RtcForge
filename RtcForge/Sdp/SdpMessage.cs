using System.Text;

namespace RtcForge.Sdp;

public class SdpMessage
{
    public int Version { get; set; }
    public SdpOrigin Origin { get; set; } = new();
    public string SessionName { get; set; } = "-";
    public string Connection { get; set; } = "IN IP4 0.0.0.0";
    public SdpTiming Timing { get; set; } = new();
    public List<SdpAttribute> Attributes { get; set; } = [];
    public List<SdpMediaDescription> MediaDescriptions { get; set; } = [];

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"v={Version}\r\n");
        sb.Append($"o={Origin}\r\n");
        sb.Append($"s={SessionName}\r\n");
        sb.Append($"c={Connection}\r\n");
        sb.Append($"t={Timing}\r\n");
        foreach (var attr in Attributes)
        {
            sb.Append($"a={attr}\r\n");
        }
        foreach (var md in MediaDescriptions)
        {
            sb.Append(md.ToString());
        }
        return sb.ToString();
    }

    public static bool TryParse(string sdp, out SdpMessage? message)
    {
        message = new SdpMessage();
        var lines = sdp.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        SdpMediaDescription? currentMedia = null;

        foreach (var line in lines)
        {
            if (line.Length < 3 || line[1] != '=')
            {
                continue;
            }

            char type = line[0];
            string value = line[2..];

            switch (type)
            {
                case 'v':
                    if (!int.TryParse(value, out int version))
                    {
                        message = null;
                        return false;
                    }
                    message.Version = version;
                    break;
                case 'o':
                    if (!SdpOrigin.TryParse(value, out var origin))
                    {
                        message = null;
                        return false;
                    }
                    message.Origin = origin!;
                    break;
                case 's': message.SessionName = value; break;
                case 'c':
                    if (currentMedia != null)
                    {
                        currentMedia.Connection = value;
                    }
                    else
                    {
                        message.Connection = value;
                    }

                    break;
                case 't':
                    if (!SdpTiming.TryParse(value, out var timing))
                    {
                        message = null;
                        return false;
                    }
                    message.Timing = timing!;
                    break;
                case 'm':
                    if (!SdpMediaDescription.TryParse(value, out var md))
                    {
                        message = null;
                        return false;
                    }
                    currentMedia = md;
                    message.MediaDescriptions.Add(currentMedia!);
                    break;
                case 'a':
                    var attr = SdpAttribute.Parse(value);
                    if (currentMedia != null)
                    {
                        currentMedia.Attributes.Add(attr);
                    }
                    else
                    {
                        message.Attributes.Add(attr);
                    }

                    break;
            }
        }

        return true;
    }

    public static SdpMessage Parse(string sdp)
    {
        if (!TryParse(sdp, out var message))
        {
            throw new FormatException("Invalid SDP message.");
        }
        return message!;
    }
}

public class SdpOrigin
{
    public string Username { get; set; } = "-";
    public ulong SessionId { get; set; }
    public ulong SessionVersion { get; set; }
    public string NetType { get; set; } = "IN";
    public string AddrType { get; set; } = "IP4";
    public string UnicastAddress { get; set; } = "127.0.0.1";

    public override string ToString() => $"{Username} {SessionId} {SessionVersion} {NetType} {AddrType} {UnicastAddress}";

    public static bool TryParse(string value, out SdpOrigin? origin)
    {
        origin = null;
        var parts = value.Split(' ');
        if (parts.Length < 6)
        {
            return false;
        }
        if (!ulong.TryParse(parts[1], out var sessionId) || !ulong.TryParse(parts[2], out var sessionVersion))
        {
            return false;
        }
        origin = new SdpOrigin
        {
            Username = parts[0],
            SessionId = sessionId,
            SessionVersion = sessionVersion,
            NetType = parts[3],
            AddrType = parts[4],
            UnicastAddress = parts[5]
        };
        return true;
    }

    public static SdpOrigin Parse(string value)
    {
        if (!TryParse(value, out var origin))
        {
            throw new FormatException($"Invalid SDP origin: {value}");
        }
        return origin!;
    }
}

public class SdpTiming
{
    public ulong StartTime { get; set; }
    public ulong StopTime { get; set; }

    public override string ToString() => $"{StartTime} {StopTime}";

    public static bool TryParse(string value, out SdpTiming? timing)
    {
        timing = null;
        var parts = value.Split(' ');
        if (parts.Length < 2)
        {
            return false;
        }
        if (!ulong.TryParse(parts[0], out var startTime) || !ulong.TryParse(parts[1], out var stopTime))
        {
            return false;
        }
        timing = new SdpTiming
        {
            StartTime = startTime,
            StopTime = stopTime
        };
        return true;
    }

    public static SdpTiming Parse(string value)
    {
        if (!TryParse(value, out var timing))
        {
            throw new FormatException($"Invalid SDP timing: {value}");
        }
        return timing!;
    }
}

public class SdpAttribute
{
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }

    public override string ToString() => Value != null ? $"{Name}:{Value}" : Name;

    public static SdpAttribute Parse(string value)
    {
        int colonIndex = value.IndexOf(':');
        if (colonIndex == -1)
        {
            return new SdpAttribute { Name = value };
        }

        return new SdpAttribute
        {
            Name = value[..colonIndex],
            Value = value[(colonIndex + 1)..]
        };
    }
}

public class SdpMediaDescription
{
    public string Media { get; set; } = "audio";
    public int Port { get; set; }
    public string Proto { get; set; } = "RTP/AVP";
    public string? Connection { get; set; }
    public List<string> Formats { get; set; } = [];
    public List<SdpAttribute> Attributes { get; set; } = [];

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"m={Media} {Port} {Proto} {string.Join(" ", Formats)}\r\n");
        if (!string.IsNullOrWhiteSpace(Connection))
        {
            sb.Append($"c={Connection}\r\n");
        }
        foreach (var attr in Attributes)
        {
            sb.Append($"a={attr}\r\n");
        }
        return sb.ToString();
    }

    public static bool TryParse(string value, out SdpMediaDescription? md)
    {
        md = null;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }
        if (!int.TryParse(parts[1], out var port))
        {
            return false;
        }
        md = new SdpMediaDescription
        {
            Media = parts[0],
            Port = port,
            Proto = parts[2]
        };
        for (int i = 3; i < parts.Length; i++)
        {
            md.Formats.Add(parts[i]);
        }
        return true;
    }

    public static SdpMediaDescription Parse(string value)
    {
        if (!TryParse(value, out var md))
        {
            throw new FormatException($"Invalid SDP media description: {value}");
        }
        return md!;
    }
}
