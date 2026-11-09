# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

WPF (.NET 9, `net9.0-windows`) desktop client for P2P voice calls with screen sharing. It must stay wire-compatible with an existing **web client** (`client.js`, not in this repo) that uses the same Socket.IO signaling server — web and desktop participants mix in the same room. Code comments, UI strings and commit messages are in Brazilian Portuguese; keep that convention.

## Commands

```bash
dotnet build P2PCall.sln
dotnet run --project src/P2PCall.Client -- --sala minhaSala [--server http://localhost:3000]
dotnet publish src/P2PCall.Client -c Release -o publish   # P2PCall.exe único, self-contained
```

`.github/workflows/build.yml` publishes that exe and creates a GitHub release (`v1.0.<run>`) on every push to `main` of `ericoamico/p2p-call`.

Default server is `https://voz.tempsharing.com.br`. Local signaling server for testing:

```bash
cd server && npm install && npm start   # listens on PORT or 3000
```

There are no test projects and no linter configured.

## Architecture

- **Signaling** (`Call/CallManager.cs`): Socket.IO events `join`, `user-joined`, `offer`, `answer`, `ice-candidate`, `user-disconnected`. Payload shapes must match `server/server.js` and the web client (e.g. `ice-candidate` sends both `target` and `to`). The existing participant offers to each newcomer (`OnUserJoined`). All signaling handlers are serialized through `_signalingLock`; ICE candidates arriving before the peer exists are buffered in `_earlyCandidates`, and per-peer ones before the remote description in `PeerSession`.
- **Display names**: sent as an extra `name` field inside the `offer`/`answer` objects (`{ type, sdp, name }`) — the server forwards those objects untouched and the browser's `RTCSessionDescription` ignores unknown fields, so no server change is needed. `CallManager.LearnName` stores it in `PeerSession.DisplayName` (cleaned by `Participant.CleanName`); web clients and old versions send none and show as "Participante xxxx". A re-offer without `name` keeps the known name. `ParticipantsChanged` carries the full `Participant` list.
- **One `RTCPeerConnection` (SIPSorcery) per remote participant**, wrapped in `PeerSession` (per-track Opus decoders/audio sinks, VP8 decoder, DTLS role, pending candidates).
- **Web-compatibility rules in SDP negotiation** (the source of most past bugs):
  - The answer must mirror the offer's m-lines exactly (count, order). `OnOffer` counts `m=audio` lines and only adds video if the offer has `m=video`. Web clients offer audio only (sometimes two audio tracks when sharing PC audio), so they never receive the screen.
  - `SdpDirections.MatchOffer` rewrites answer directions because SIPSorcery answers `sendrecv` even when the browser offered `recvonly`.
  - Renegotiations from the web reuse the existing connection; the DTLS `a=setup` role from the first negotiation is forced on later answers.
  - Between desktop clients the VP8 video track is created with the connection up front, so toggling screen share needs no renegotiation; video is only sent to peers with a remote video track.
- **Audio** (`Audio/AudioEngine.cs`): NAudio `WaveIn` mono capture, duplicated to stereo → Concentus stereo Opus encoder (48 kHz, 20 ms / 960-sample frames, FEC) → `EncodedFrameReady` → `CallManager.SendAudioToPeers`. Remote tracks each get a `RemoteAudioSink` feeding a `MixingSampleProvider` → `WaveOut`. `RecordingTap` sits on the mixer and also takes mic samples to write the call to WAV. No echo cancellation/noise suppression/AGC by design (matches web raw audio).
- **System audio** (`Audio/SystemAudioCapture.cs`): while screen sharing, WASAPI process loopback with `ExcludeTargetProcessTree` on our own PID captures all PC audio *except* this app, so remote voices we play are never sent back (viewers don't hear themselves twice). It stays stereo (L/R) and is summed into the stereo frame (mic duplicated on both channels) after the mute is applied, so muting only silences the mic; the same frame is encoded and recorded — one audio track, no SDP change. Never fall back to whole-system loopback (it would echo); unsupported Windows just shares the screen without audio.
- **Video** (`Video/`): `ScreenSharer` captures the primary screen via GDI and encodes VP8 (max 1600 px, 12 fps, cursor drawn); `RemoteScreen` displays decoded frames. `MainWindow` removes a remote screen ~3 s after frames stop (`_screenWatchdog`).
- **UI** (`MainWindow.xaml(.cs)`): `CallManager` events fire on background threads and are marshalled with `Dispatcher.BeginInvoke`. Also keeps the PC awake during a call via `SetThreadExecutionState`. Room links (`Call/RoomLink.cs`) use the web format `https://server/?sala=<name>`: the copy-link button builds one, and pasting one into the room field (`RoomInput_TextChanged`) keeps only the room name and updates the server field if it differs.
- **Auto-update** (`Update/UpdateService.cs`): only in the published single-file exe (detected by the absence of `P2PCall.dll` next to it — `Assembly.Location` is not empty because of `IncludeAllContentForSelfExtract`). The installed version lives in `latest.json` next to the exe (fallback `%LOCALAPPDATA%\P2PCall`), kept at max(json, assembly version); CI sets the assembly version with `-p:Version=1.0.<run>` to match the release tag. On startup it queries the GitHub releases API (override with env `P2PCALL_RELEASES_API` for local tests), downloads `P2PCall.exe.new` beside the exe verifying size and the asset `digest` (SHA-256), then a temp PowerShell script waits for this process to exit (force-kills after 30 s), moves the new file over the exe and relaunches it with `--atualizado` (or `--falha-atualizacao`); log in `%TEMP%\P2PCall-atualizacao.log`. The old app owns the whole procedure, so the new exe needs no special mode.

## Accessibility (must be preserved)

The UI targets screen-reader (Narrator/NVDA/JAWS) and keyboard-only use. When changing the UI:
- Use `Controls/AccessibleLabel`, `AccessibleButton`, `AccessibleToggleButton` for controls with access keys (`_Sala`) — they hide the duplicate underscored text child WPF otherwise exposes.
- Every input needs an associated label, automation name and `HelpText`; mute/screen/record are toggle buttons (announce pressed state).
- Status and participant count are live regions — announce changes without moving focus.
- Focus must never be lost: on error to the offending field, on join to "Silenciar microfone", on leave to "Entrar na sala".
- Keep the existing access keys (Alt+V/S/N/C/Q/E/M/T/G/L/R/B, Ctrl+M mute) documented in README in sync.
