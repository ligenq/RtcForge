using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RtcForge.Media;
using RtcForge.Rtp;
using RtcForge.Sdp;

namespace RtcForge.Demo;

internal static class Program
{
    private const string OfferFile = "offer.sdp";
    private const string AnswerFile = "answer.sdp";
    private const string OfferCandidatesFile = "offer.candidates";
    private const string AnswerCandidatesFile = "answer.candidates";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task Main(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("loopback", StringComparison.OrdinalIgnoreCase))
        {
            await RunLoopbackAsync();
            return;
        }

        string mode = args[0].ToLowerInvariant();
        switch (mode)
        {
            case "offer" when args.Length == 2:
                Directory.CreateDirectory(args[1]);
                await RunFileSignaledAsync(args[1], offerer: true);
                return;
            case "answer" when args.Length == 2:
                Directory.CreateDirectory(args[1]);
                await RunFileSignaledAsync(args[1], offerer: false);
                return;
            case "serve":
            {
                string prefix = args.Length >= 2 ? args[1] : "http://localhost:8080/";
                await RunBrowserHarnessAsync(prefix);
                return;
            }
            default:
                PrintUsage();
                return;
        }
    }

    private static async Task RunLoopbackAsync()
    {
        Console.WriteLine("RtcForge Demo starting in loopback mode.");

        using var pcA = new RTCPeerConnection();
        using var pcB = new RTCPeerConnection();

        pcA.OnIceCandidate += (_, candidate) => pcB.AddIceCandidate(candidate);
        pcB.OnIceCandidate += (_, candidate) => pcA.AddIceCandidate(candidate);

        pcB.OnDataChannel += (_, dc) =>
        {
            Console.WriteLine($"pcB: Received Data Channel '{dc.Label}'");
            dc.OnMessage += (_, msg) => Console.WriteLine($"pcB DataChannel: {msg}");
        };

        pcB.OnTrack += (_, ev) => Console.WriteLine($"pcB: Received Track of kind '{ev.Track.Kind}'");

        var dcA = pcA.CreateDataChannel("chat");
        dcA.OnOpen += (_, _) => Console.WriteLine("pcA: Data Channel Opened");

        pcA.AddTrack(new AudioStreamTrack());

        Console.WriteLine("Creating Offer...");
        var offer = await pcA.CreateOfferAsync();
        await pcA.SetLocalDescriptionAsync(offer);
        await pcB.SetRemoteDescriptionAsync(offer);

        Console.WriteLine("Creating Answer...");
        var answer = await pcB.CreateAnswerAsync();
        await pcB.SetLocalDescriptionAsync(answer);
        await pcA.SetRemoteDescriptionAsync(answer);

        Console.WriteLine("Connecting...");
        await Task.WhenAll(pcA.ConnectAsync(), pcB.ConnectAsync());

        Console.WriteLine($"pcA Connection State: {pcA.ConnectionState}");
        Console.WriteLine($"pcB Connection State: {pcB.ConnectionState}");

        await Task.Delay(1000);

        if (pcA.ConnectionState == PeerConnectionState.Connected)
        {
            Console.WriteLine("Sending message over Data Channel...");
            try
            {
                await dcA.SendAsync("Hello from pcA!");
            }
            catch
            {
                Console.WriteLine("Data Channel not yet ready for sending.");
            }

            Console.WriteLine("Sending RTP packet...");
            var sender = pcA.GetTransceivers().First().Sender;
            await sender.SendRtpAsync(new RtpPacket
            {
                PayloadType = 111,
                SequenceNumber = 1,
                Payload = new byte[] { 1, 2, 3, 4 }.AsMemory()
            });
        }

        Console.WriteLine("Demo finished.");
    }

    private static async Task RunFileSignaledAsync(string signalingDirectory, bool offerer)
    {
        string localSdpFile = Path.Combine(signalingDirectory, offerer ? OfferFile : AnswerFile);
        string remoteSdpFile = Path.Combine(signalingDirectory, offerer ? AnswerFile : OfferFile);
        string localCandidatesFile = Path.Combine(signalingDirectory, offerer ? OfferCandidatesFile : AnswerCandidatesFile);
        string remoteCandidatesFile = Path.Combine(signalingDirectory, offerer ? AnswerCandidatesFile : OfferCandidatesFile);

        Console.WriteLine($"RtcForge Demo starting in {(offerer ? "offerer" : "answerer")} interop mode.");
        Console.WriteLine($"Signaling directory: {signalingDirectory}");
        Console.WriteLine("Exchange the generated files with the remote peer or browser harness.");

        if (offerer)
        {
            SafeDelete(localSdpFile);
            SafeDelete(localCandidatesFile);
        }

        var loggerFactory = LoggerFactory.Create(builder => { builder.AddConsole(); builder.SetMinimumLevel(LogLevel.Debug); });
        var config = new RTCConfiguration { LoggerFactory = loggerFactory };
        using var pc = new RTCPeerConnection(config);
        var processedCandidates = new ConcurrentDictionary<string, byte>();
        var dataChannelReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pc.OnConnectionStateChange += (_, state) => Console.WriteLine($"Connection State: {state}");
        pc.OnDataChannel += (_, channel) =>
        {
            Console.WriteLine($"Remote opened data channel '{channel.Label}'");
            channel.OnMessage += (_, message) => Console.WriteLine($"DataChannel message: {message}");
            channel.OnOpen += (_, _) => dataChannelReady.TrySetResult();
        };
        pc.OnIceCandidate += (_, candidate) => File.AppendAllLines(localCandidatesFile, new[] { candidate.ToString() });

        RTCDataChannel? localChannel = null;
        if (offerer)
        {
            localChannel = pc.CreateDataChannel("interop");
            localChannel.OnOpen += (_, _) =>
            {
                Console.WriteLine("Local data channel opened.");
                dataChannelReady.TrySetResult();
            };
        }

        var candidatePump = PumpRemoteCandidatesAsync(pc, remoteCandidatesFile, processedCandidates);

        if (offerer)
        {
            var offer = await pc.CreateOfferAsync();
            await pc.SetLocalDescriptionAsync(offer);
            await File.WriteAllTextAsync(localSdpFile, offer.ToString());
            Console.WriteLine($"Wrote offer SDP to {localSdpFile}");

            var answerSdp = await WaitForFileTextAsync(remoteSdpFile);
            await pc.SetRemoteDescriptionAsync(SdpMessage.Parse(answerSdp));
        }
        else
        {
            var offerSdp = await WaitForFileTextAsync(remoteSdpFile);
            var offer = SdpMessage.Parse(offerSdp);
            await pc.SetRemoteDescriptionAsync(offer);

            var answer = await pc.CreateAnswerAsync();
            await pc.SetLocalDescriptionAsync(answer);
            await File.WriteAllTextAsync(localSdpFile, answer.ToString());
            Console.WriteLine($"Wrote answer SDP to {localSdpFile}");
        }

        bool connected = await pc.ConnectAsync();
        Console.WriteLine($"ConnectAsync returned {connected}");

        await Task.WhenAny(dataChannelReady.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (dataChannelReady.Task.IsCompletedSuccessfully && localChannel != null)
        {
            await localChannel.SendAsync("Hello from RtcForge interop harness");
        }

        await Task.Delay(TimeSpan.FromSeconds(5));
        await candidatePump;
    }

    private static async Task RunBrowserHarnessAsync(string prefix)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        Console.WriteLine($"Browser harness listening at {prefix}");
        Console.WriteLine("Open the URL in a browser and press Connect.");

        using var service = new BrowserHarnessService();
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => service.HandleAsync(context));
        }
    }

    private static async Task PumpRemoteCandidatesAsync(
        RTCPeerConnection peerConnection,
        string candidatesFile,
        ConcurrentDictionary<string, byte> processedCandidates)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < timeout)
        {
            if (File.Exists(candidatesFile))
            {
                foreach (var line in await File.ReadAllLinesAsync(candidatesFile))
                {
                    if (string.IsNullOrWhiteSpace(line) || !processedCandidates.TryAdd(line, 0))
                    {
                        continue;
                    }

                    if (line.Equals("end-of-candidates", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    peerConnection.AddIceCandidate(Ice.IceCandidate.Parse(line));
                }
            }

            await Task.Delay(250);
        }
    }

    private static async Task<string> WaitForFileTextAsync(string path)
    {
        while (true)
        {
            if (File.Exists(path))
            {
                string text = await File.ReadAllTextAsync(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            await Task.Delay(250);
        }
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project RtcForge.Demo");
        Console.WriteLine("  dotnet run --project RtcForge.Demo -- loopback");
        Console.WriteLine("  dotnet run --project RtcForge.Demo -- offer <signaling-directory>");
        Console.WriteLine("  dotnet run --project RtcForge.Demo -- answer <signaling-directory>");
        Console.WriteLine("  dotnet run --project RtcForge.Demo -- serve [http-prefix]");
    }

    private sealed class BrowserHarnessService : IDisposable
    {
        private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();
        private readonly string _indexHtml;

        public BrowserHarnessService()
        {
            string root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _indexHtml = File.ReadAllText(Path.Combine(root, "index.html"));
        }

        public async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "/";
                if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(context.Response, "text/html; charset=utf-8", _indexHtml);
                    return;
                }

                if (path.Equals("/api/session", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
                {
                    await HandleCreateSessionAsync(context.Response);
                    return;
                }

                if (path.StartsWith("/api/session/", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        string sessionId = parts[2];
                        if (!_sessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await WriteJsonAsync(context.Response, new { error = "session not found" });
                            return;
                        }

                        if (parts.Length == 4 && parts[3].Equals("answer", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
                        {
                            var payload = await ReadJsonAsync<AnswerRequest>(context.Request);
                            await session.ApplyAnswerAsync(payload?.Sdp ?? string.Empty);
                            await WriteJsonAsync(context.Response, new { ok = true });
                            return;
                        }

                        if (parts.Length == 4 && parts[3].Equals("candidate", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
                        {
                            var payload = await ReadJsonAsync<CandidateRequest>(context.Request);
                            if (!string.IsNullOrWhiteSpace(payload?.Candidate))
                            {
                                session.AddRemoteCandidate(payload.Candidate);
                            }

                            await WriteJsonAsync(context.Response, new { ok = true });
                            return;
                        }

                        if (parts.Length == 4 && parts[3].Equals("candidates", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET")
                        {
                            int since = TryParseInt(context.Request.QueryString["since"]);
                            var page = session.GetLocalCandidates(since);
                            await WriteJsonAsync(context.Response, page);
                            return;
                        }

                        if (parts.Length == 4 && parts[3].Equals("state", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET")
                        {
                            await WriteJsonAsync(context.Response, session.GetState());
                            return;
                        }

                        if (parts.Length == 4 && parts[3].Equals("message", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
                        {
                            var payload = await ReadJsonAsync<MessageRequest>(context.Request);
                            await session.SendMessageAsync(payload?.Message ?? string.Empty);
                            await WriteJsonAsync(context.Response, new { ok = true });
                            return;
                        }
                    }
                }

                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteJsonAsync(context.Response, new { error = "not found" });
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteJsonAsync(context.Response, new { error = ex.Message });
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
        }

        private async Task HandleCreateSessionAsync(HttpListenerResponse response)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            var session = new BrowserSession(sessionId);
            if (!_sessions.TryAdd(sessionId, session))
            {
                session.Dispose();
                throw new InvalidOperationException("Failed to create session.");
            }

            string offerSdp = await session.CreateOfferAsync();
            await WriteJsonAsync(response, new CreateSessionResponse(sessionId, offerSdp));
        }

        private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, JsonOptions);
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
        {
            response.ContentType = "application/json; charset=utf-8";
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, string contentType, string text)
        {
            response.ContentType = contentType;
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
        }

        private static int TryParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private sealed class BrowserSession : IDisposable
    {
        private readonly RTCPeerConnection _peerConnection;
        private readonly RTCDataChannel _serverChannel;
        private readonly List<string> _localCandidates = new();
        private readonly object _candidateLock = new();
        private readonly List<string> _messages = new();
        private RTCDataChannel? _activeChannel;

        public BrowserSession(string id)
        {
            Id = id;
            var loggerFactory = LoggerFactory.Create(builder => { builder.AddConsole(); builder.SetMinimumLevel(LogLevel.Debug); });
            var config = new RTCConfiguration { LoggerFactory = loggerFactory };
            _peerConnection = new RTCPeerConnection(config);
            _serverChannel = _peerConnection.CreateDataChannel("server-chat");
            _activeChannel = _serverChannel;

            _peerConnection.OnIceCandidate += (_, candidate) =>
            {
                lock (_candidateLock)
                {
                    _localCandidates.Add(candidate.ToString());
                }
            };

            _peerConnection.OnConnectionStateChange += (_, state) =>
            {
                ConnectionState = state.ToString();
                Console.WriteLine($"[{Id}] Connection State: {state}");
            };

            _peerConnection.OnDataChannel += (_, channel) =>
            {
                _activeChannel = channel;
                WireChannel(channel);
            };

            WireChannel(_serverChannel);
        }

        public string Id { get; }
        public string ConnectionState { get; private set; } = PeerConnectionState.New.ToString();

        public async Task<string> CreateOfferAsync()
        {
            var offer = await _peerConnection.CreateOfferAsync();
            await _peerConnection.SetLocalDescriptionAsync(offer);
            return offer.ToString();
        }

        public async Task ApplyAnswerAsync(string sdp)
        {
            await _peerConnection.SetRemoteDescriptionAsync(SdpMessage.Parse(sdp));
            _ = Task.Run(async () =>
            {
                bool connected = await _peerConnection.ConnectAsync();
                Console.WriteLine($"[{Id}] ConnectAsync returned {connected}");
            });
        }

        public void AddRemoteCandidate(string candidate)
        {
            _peerConnection.AddIceCandidate(Ice.IceCandidate.Parse(candidate));
        }

        public CandidatePage GetLocalCandidates(int since)
        {
            lock (_candidateLock)
            {
                var items = _localCandidates.Skip(Math.Max(0, since)).ToArray();
                return new CandidatePage(items, _localCandidates.Count);
            }
        }

        public SessionState GetState()
        {
            lock (_messages)
            {
                return new SessionState(ConnectionState, _messages.ToArray());
            }
        }

        public async Task SendMessageAsync(string message)
        {
            if (_activeChannel?.ReadyState != RTCDataChannelState.Open)
            {
                throw new InvalidOperationException("Data channel is not open.");
            }

            await _activeChannel.SendAsync(message);
        }

        public void Dispose()
        {
            _peerConnection.Dispose();
        }

        private void WireChannel(RTCDataChannel channel)
        {
            channel.OnOpen += async (_, _) =>
            {
                Console.WriteLine($"[{Id}] Data channel '{channel.Label}' opened.");
                try
                {
                    await channel.SendAsync("Hello from RtcForge server");
                }
                catch
                {
                }
            };

            channel.OnMessage += (_, message) =>
            {
                Console.WriteLine($"[{Id}] Browser says: {message}");
                lock (_messages)
                {
                    _messages.Add(message);
                }
            };
        }
    }

    private sealed record CreateSessionResponse(string SessionId, string OfferSdp);
    private sealed record AnswerRequest(string Sdp);
    private sealed record CandidateRequest(string Candidate);
    private sealed record MessageRequest(string Message);
    private sealed record CandidatePage(string[] Items, int Next);
    private sealed record SessionState(string ConnectionState, string[] Messages);
}
