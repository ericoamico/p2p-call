# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

WPF (.NET 9, `net9.0-windows`) desktop client for P2P voice calls with screen sharing. It must stay wire-compatible with an existing **web client** (`client.js`, not in this repo) that uses the same Socket.IO signaling server — web and desktop participants mix in the same room. Code comments, UI strings and commit messages are in Brazilian Portuguese; keep that convention.

## Commands

```bash
dotnet build P2PCall.sln
dotnet run --project src/P2PCall.Client -- --sala minhaSala [--server http://localhost:3000]
```

Default server is `https://voz.tempsharing.com.br`. Local signaling server for testing:

```bash
cd server && npm install && npm start   # listens on PORT or 3000
```

There are no test projects and no linter configured.

## Architecture

- **Signaling** (`Call/CallManager.cs`): Socket.IO events `join`, `user-joined`, `offer`, `answer`, `ice-candidate`, `user-disconnected`. Payload shapes must match `server/server.js` and the web client (e.g. `ice-candidate` sends both `target` and `to`). The existing participant offers to each newcomer (`OnUserJoined`). All signaling handlers are serialized through `_signalingLock`; ICE candidates arriving before the peer exists are buffered in `_earlyCandidates`, and per-peer ones before the remote description in `PeerSession`.
- **One `RTCPeerConnection` (SIPSorcery) per remote participant**, wrapped in `PeerSession` (per-track Opus decoders/audio sinks, VP8 decoder, DTLS role, pending candidates).
- **Web-compatibility rules in SDP negotiation** (the source of most past bugs):
  - The answer must mirror the offer's m-lines exactly (count, order). `OnOffer` counts `m=audio` lines and only adds video if the offer has `m=video`. Web clients offer audio only (sometimes two audio tracks when sharing PC audio), so they never receive the screen.
  - `SdpDirections.MatchOffer` rewrites answer directions because SIPSorcery answers `sendrecv` even when the browser offered `recvonly`.
  - Renegotiations from the web reuse the existing connection; the DTLS `a=setup` role from the first negotiation is forced on later answers.
  - Between desktop clients the VP8 video track is created with the connection up front, so toggling screen share needs no renegotiation; video is only sent to peers with a remote video track.
- **Audio** (`Audio/AudioEngine.cs`): NAudio `WaveIn` mono capture → Concentus Opus encoder (48 kHz, 20 ms / 960-sample frames, FEC) → `EncodedFrameReady` → `CallManager.SendAudioToPeers`. Remote tracks each get a `RemoteAudioSink` feeding a `MixingSampleProvider` → `WaveOut`. `RecordingTap` sits on the mixer and also takes mic samples to write the call to WAV. No echo cancellation/noise suppression/AGC by design (matches web raw audio).
- **Video** (`Video/`): `ScreenSharer` captures the primary screen via GDI and encodes VP8 (max 1600 px, 12 fps, cursor drawn); `RemoteScreen` displays decoded frames. `MainWindow` removes a remote screen ~3 s after frames stop (`_screenWatchdog`).
- **UI** (`MainWindow.xaml(.cs)`): `CallManager` events fire on background threads and are marshalled with `Dispatcher.BeginInvoke`. Also keeps the PC awake during a call via `SetThreadExecutionState`.

## Accessibility (must be preserved)

The UI targets screen-reader (Narrator/NVDA/JAWS) and keyboard-only use. When changing the UI:
- Use `Controls/AccessibleLabel`, `AccessibleButton`, `AccessibleToggleButton` for controls with access keys (`_Sala`) — they hide the duplicate underscored text child WPF otherwise exposes.
- Every input needs an associated label, automation name and `HelpText`; mute/screen/record are toggle buttons (announce pressed state).
- Status and participant count are live regions — announce changes without moving focus.
- Focus must never be lost: on error to the offending field, on join to "Silenciar microfone", on leave to "Entrar na sala".
- Keep the existing access keys (Alt+V/S/C/Q/E/M/T/G/R, Ctrl+M mute) documented in README in sync.
