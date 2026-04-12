# RtcForge

RtcForge is an experimental, managed C# WebRTC toolkit for .NET.

It provides protocol building blocks for peer-to-peer RTC work, including SDP, ICE, STUN/TURN, DTLS, SRTP, SCTP, RTP/RTCP, data channels, and a small `RTCPeerConnection`-style API. The project is currently intended for experimentation, interop work, and library integration scenarios where a managed WebRTC stack is useful.

RtcForge is not yet a browser-grade, production-hardened replacement for mature WebRTC stacks.

## Status

The current implementation has working coverage for:

- SDP offer/answer parsing and generation.
- Data channel negotiation over an `m=application` SDP section with SCTP.
- ICE host candidate gathering and connectivity checks.
- ICE controlling/controlled roles, role conflict handling, selected-pair tracking, consent freshness, restart reset handling, and IPv4/IPv6 candidate-pair filtering.
- STUN parsing, serialization, message integrity, fingerprint validation, and XOR address attributes.
- TURN allocation, permissions, channel binding, and relay channel-data translation.
- DTLS certificate generation and fingerprint validation.
- SCTP association setup, retransmission handling, DCEP, and data channel messaging.
- SRTP key derivation and RTP/RTCP protection/unprotection.
- RTP/RTCP packet parsing, packetization helpers, NACK, PLI, FIR, and a small jitter buffer.
- A compact `WebRtcConnection` abstraction for SDP/candidate exchange and data channels.
- A demo app with loopback, file-signaled interop, and local browser harness modes.

Known limitations:

- Browser interoperability is still experimental and should be tested for each target browser/version.
- ICE is not a complete RFC 8445 implementation. Advanced nomination, broader trickle interoperability, and long-haul edge cases still need work.
- TURN support exists, but production behavior such as allocation refresh and stale-nonce recovery needs more hardening.
- SDP coverage is intentionally narrow and focused on the scenarios currently exercised by the tests/demo.
- Media support is limited to low-level RTP/RTCP plumbing and helper types, not a full capture/encode/decode pipeline.
- The public surface is still young and may change before a stable release.

## Install

After the package is published:

```powershell
dotnet add package RtcForge --prerelease
```

For local development, reference the project directly:

```xml
<ProjectReference Include="..\RtcForge\RtcForge\RtcForge.csproj" />
```

## Quick Start

This example shows the high-level data-channel flow. Signaling is intentionally left to the host application.

```csharp
using RtcForge;

await using var local = new WebRtcConnection();
await using var remote = new WebRtcConnection();

local.IceCandidateReady += async (_, candidate) =>
    await remote.AddRemoteIceCandidateAsync(candidate);

remote.IceCandidateReady += async (_, candidate) =>
    await local.AddRemoteIceCandidateAsync(candidate);

remote.DataChannelOpened += (_, channel) =>
{
    channel.MessageReceived += data =>
        Console.WriteLine($"received {data.Length} bytes");
};

var channel = local.CreateDataChannel("chat");

var offer = await local.CreateOfferAsync();
await local.SetLocalDescriptionAsync(offer);
await remote.SetRemoteDescriptionAsync(offer);

var answer = await remote.CreateAnswerAsync();
await remote.SetLocalDescriptionAsync(answer);
await local.SetRemoteDescriptionAsync(answer);

await Task.WhenAll(local.ConnectAsync(), remote.ConnectAsync());
await channel.SendAsync("hello"u8.ToArray());
```

For lower-level work, the protocol pieces are available under namespaces such as `RtcForge.Ice`, `RtcForge.Stun`, `RtcForge.Dtls`, `RtcForge.Sctp`, `RtcForge.Srtp`, `RtcForge.Rtp`, `RtcForge.Sdp`, and `RtcForge.Media`.

## Repository Layout

- `RtcForge/` - library source.
- `RtcForge.Tests/` - unit and integration tests.
- `RtcForge.Demo/` - console demo and browser harness.

## Build And Test

```powershell
dotnet build RtcForge\RtcForge.csproj
dotnet test RtcForge.Tests\RtcForge.Tests.csproj
dotnet pack RtcForge\RtcForge.csproj
```

The latest local verification for this README:

```text
dotnet build RtcForge\RtcForge.csproj -v minimal -nr:false
Build succeeded

dotnet test RtcForge.Tests\RtcForge.Tests.csproj -v minimal
Passed: 188

dotnet pack RtcForge\RtcForge.csproj -v minimal
Created RtcForge.0.1.0-alpha.1.nupkg
```

## Demo

Run a local loopback demo:

```powershell
dotnet run --project RtcForge.Demo\RtcForge.Demo.csproj -- loopback
```

Run a file-signaled interop pair:

```powershell
dotnet run --project RtcForge.Demo\RtcForge.Demo.csproj -- offer .\signal
dotnet run --project RtcForge.Demo\RtcForge.Demo.csproj -- answer .\signal
```

The file-signaled mode writes `offer.sdp`, `answer.sdp`, and candidate files into the chosen directory. This is useful when bridging signaling manually to another process or browser harness.

Run the local browser harness:

```powershell
dotnet run --project RtcForge.Demo\RtcForge.Demo.csproj -- serve
```

Then open the printed local URL in a browser and use the page to negotiate with the local RtcForge peer.

## Tracing

Set `RTCFORGE_TRACE_ICE=1` to enable additional ICE tracing from the ICE agent.

```powershell
$env:RTCFORGE_TRACE_ICE = "1"
dotnet run --project RtcForge.Demo\RtcForge.Demo.csproj -- loopback
```

## Packaging Notes

The project currently targets `net8.0` and `net10.0`, and packs as the experimental prerelease package `RtcForge`.

Before publishing a stable package, the main remaining cleanup items are:

- Review public API names and compatibility guarantees.
- Add XML documentation for the public surface currently covered by the prerelease API.
- Expand browser and TURN interop testing.

## License

MIT.
