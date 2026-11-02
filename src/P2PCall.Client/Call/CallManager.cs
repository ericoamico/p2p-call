using System.Collections.Concurrent;
using System.Text.Json;
using P2PCall.Client.Audio;
using P2PCall.Client.Video;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SocketIOClient;

namespace P2PCall.Client.Call;

/// <summary>
/// Sinalização via Socket.IO (mesmo protocolo do client.js) e uma conexão WebRTC P2P
/// por participante. Áudio em Opus; tela compartilhada em VP8 na trilha de vídeo.
/// </summary>
public sealed class CallManager : IAsyncDisposable
{
    private const string OpusFmtp = "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1";

    private static readonly RTCConfiguration RtcConfig = new()
    {
        iceServers =
        [
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
            new RTCIceServer { urls = "stun:stun1.l.google.com:19302" },
        ],
    };

    private readonly AudioEngine _audio;
    private readonly ScreenSharer _screen = new();
    private readonly ConcurrentDictionary<string, PeerSession> _peers = new();
    // Candidatos que chegam antes da oferta do participante.
    private readonly Dictionary<string, List<RTCIceCandidateInit>> _earlyCandidates = new();
    private readonly SemaphoreSlim _signalingLock = new(1, 1);
    private SocketIO? _socket;

    public event Action<string>? StatusChanged;
    public event Action<int>? ParticipantsChanged;
    /// <summary>Frame de tela remota decodificado (peerId, largura, altura, pixels BGR24).</summary>
    public event Action<string, int, int, byte[]>? RemoteScreenFrame;
    public event Action<string>? PeerLeft;

    public string Room { get; private set; } = "";
    public bool IsSharingScreen => _screen.IsRunning;

    public CallManager(AudioEngine audio)
    {
        _audio = audio;
        _audio.EncodedFrameReady += SendAudioToPeers;
        _screen.FrameEncoded += SendVideoToPeers;
    }

    public async Task JoinAsync(string serverUrl, string room)
    {
        Room = room;
        var socket = new SocketIO(new Uri(serverUrl));
        _socket = socket;
        bool wasConnected = false;

        socket.On("user-joined", ctx => OnUserJoined(ctx.GetValue<string>(0)!));
        socket.On("offer", ctx => OnOffer(ctx.GetValue<JsonElement>(0)));
        socket.On("answer", ctx => OnAnswer(ctx.GetValue<JsonElement>(0)));
        socket.On("ice-candidate", ctx => OnIceCandidate(ctx.GetValue<JsonElement>(0)));
        socket.On("user-disconnected", ctx => OnUserDisconnected(ctx.GetValue<string>(0)!));

        socket.OnDisconnected += (_, reason) => StatusChanged?.Invoke($"Desconectado do servidor: {reason}");
        socket.OnReconnectAttempt += (_, attempt) =>
        {
            if (wasConnected) StatusChanged?.Invoke($"Reconectando ao servidor (tentativa {attempt})...");
        };
        // Após reconectar, entra de novo na sala para receber os novos participantes.
        socket.OnConnected += async (_, _) =>
        {
            wasConnected = true;
            await socket.EmitAsync("join", [Room]);
            StatusChanged?.Invoke($"Conectado à sala: {Room}.");
        };

        await socket.ConnectAsync();
    }

    public async Task LeaveAsync()
    {
        StopScreenShare();
        foreach (var id in _peers.Keys.ToList()) RemovePeer(id);
        _earlyCandidates.Clear();
        if (_socket is not null)
        {
            try { await _socket.DisconnectAsync(); } catch { /* já caiu */ }
            _socket.Dispose();
            _socket = null;
        }
    }

    public void StartScreenShare()
    {
        _screen.Start();
        _screen.RequestKeyFrame();
    }

    public void StopScreenShare() => _screen.Stop();

    // ---------- Sinalização ----------

    private async Task OnUserJoined(string userId)
    {
        await _signalingLock.WaitAsync();
        try
        {
            var peer = CreatePeer(userId);
            var offer = peer.Pc.createOffer(null);
            await peer.Pc.setLocalDescription(offer);
            await EmitAsync("offer", new { to = userId, offer = new { type = "offer", sdp = offer.sdp } });
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Erro ao chamar participante: {ex.Message}");
        }
        finally { _signalingLock.Release(); }
    }

    private async Task OnOffer(JsonElement data)
    {
        await _signalingLock.WaitAsync();
        try
        {
            var from = data.GetProperty("from").GetString()!;
            var sdp = data.GetProperty("offer").GetProperty("sdp").GetString()!;

            var peer = CreatePeer(from);
            var result = peer.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
            if (result != SetDescriptionResultEnum.OK)
            {
                StatusChanged?.Invoke($"Oferta incompatível ({result}).");
                RemovePeer(from);
                return;
            }
            peer.FlushPendingCandidates();

            var answer = peer.Pc.createAnswer(null);
            await peer.Pc.setLocalDescription(answer);
            await EmitAsync("answer", new { to = from, answer = new { type = "answer", sdp = answer.sdp } });
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Erro ao responder oferta: {ex.Message}");
        }
        finally { _signalingLock.Release(); }
    }

    private async Task OnAnswer(JsonElement data)
    {
        await _signalingLock.WaitAsync();
        try
        {
            var from = data.GetProperty("from").GetString()!;
            if (!_peers.TryGetValue(from, out var peer)) return;

            var sdp = data.GetProperty("answer").GetProperty("sdp").GetString()!;
            var result = peer.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
            if (result != SetDescriptionResultEnum.OK)
                StatusChanged?.Invoke($"Resposta incompatível ({result}).");
            peer.FlushPendingCandidates();
        }
        finally { _signalingLock.Release(); }
    }

    private async Task OnIceCandidate(JsonElement data)
    {
        await _signalingLock.WaitAsync();
        try
        {
            var from = data.GetProperty("from").GetString()!;
            if (!data.TryGetProperty("candidate", out var c) || c.ValueKind != JsonValueKind.Object) return;

            var candidate = c.TryGetProperty("candidate", out var cs) ? cs.GetString() : null;
            if (string.IsNullOrEmpty(candidate)) return; // fim dos candidatos

            var init = new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = c.TryGetProperty("sdpMid", out var mid) && mid.ValueKind == JsonValueKind.String ? mid.GetString() : null,
                sdpMLineIndex = c.TryGetProperty("sdpMLineIndex", out var idx) && idx.ValueKind == JsonValueKind.Number ? idx.GetUInt16() : (ushort)0,
                usernameFragment = c.TryGetProperty("usernameFragment", out var uf) && uf.ValueKind == JsonValueKind.String ? uf.GetString() : null,
            };

            // Candidatos podem chegar antes da oferta/resposta: guarda até ter a descrição remota.
            if (_peers.TryGetValue(from, out var peer))
                peer.AddRemoteCandidate(init);
            else if (_earlyCandidates.TryGetValue(from, out var list))
                list.Add(init);
            else
                _earlyCandidates[from] = [init];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ICE inválido: {ex.Message}");
        }
        finally { _signalingLock.Release(); }
    }

    private async Task OnUserDisconnected(string userId)
    {
        await _signalingLock.WaitAsync();
        try
        {
            _earlyCandidates.Remove(userId);
            RemovePeer(userId);
        }
        finally { _signalingLock.Release(); }
    }

    private Task EmitAsync(string ev, object payload) => _socket?.EmitAsync(ev, [payload]) ?? Task.CompletedTask;

    // ---------- Conexões P2P ----------

    private PeerSession CreatePeer(string peerId)
    {
        if (_peers.TryRemove(peerId, out var existing)) existing.Dispose();
        _earlyCandidates.Remove(peerId, out var early);

        var pc = new RTCPeerConnection(RtcConfig);

        var audioFormat = new AudioFormat(AudioCodecsEnum.OPUS, 111, AudioEngine.SampleRate, 2, OpusFmtp);
        pc.addTrack(new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendRecv));

        // A trilha de vídeo existe sempre (envia só quando a tela está sendo compartilhada),
        // assim não é preciso renegociar ao ligar/desligar o compartilhamento.
        var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, VpxVideoEncoder.VP8_FORMATID, 90000, null);
        pc.addTrack(new MediaStreamTrack(videoFormat, MediaStreamStatusEnum.SendRecv));

        var peer = new PeerSession(peerId, pc, _audio.CreateRemoteSink());
        if (early is not null) foreach (var c in early) peer.AddRemoteCandidate(c);
        _peers[peerId] = peer;

        pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            using var json = JsonDocument.Parse(candidate.toJSON());
            _ = EmitAsync("ice-candidate", new { target = peerId, to = peerId, candidate = json.RootElement.Clone() });
        };

        pc.onconnectionstatechange += state =>
        {
            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    if (_screen.IsRunning) _screen.RequestKeyFrame();
                    ParticipantsChanged?.Invoke(ConnectedCount);
                    break;
                case RTCPeerConnectionState.failed:
                    StatusChanged?.Invoke("Falha na conexão P2P com um participante.");
                    RemovePeer(peerId);
                    break;
                case RTCPeerConnectionState.disconnected:
                case RTCPeerConnectionState.closed:
                    ParticipantsChanged?.Invoke(ConnectedCount);
                    break;
            }
        };

        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.audio) peer.Audio.PushOpus(packet.Payload);
        };

        pc.OnVideoFrameReceived += (_, _, frame, format) =>
        {
            if (format.Codec != VideoCodecsEnum.VP8) return;
            foreach (var sample in peer.DecodeVideo(frame))
                RemoteScreenFrame?.Invoke(peerId, (int)sample.Width, (int)sample.Height, sample.Sample);
        };

        return peer;
    }

    private void RemovePeer(string peerId)
    {
        if (!_peers.TryRemove(peerId, out var peer)) return;
        peer.Dispose();
        PeerLeft?.Invoke(peerId);
        ParticipantsChanged?.Invoke(ConnectedCount);
    }

    private int ConnectedCount => _peers.Values.Count(p => p.IsConnected);

    private void SendAudioToPeers(byte[] opus)
    {
        foreach (var peer in _peers.Values)
        {
            if (!peer.IsConnected) continue;
            try { peer.Pc.SendAudio(AudioEngine.FrameSamples, opus); } catch { /* fechando */ }
        }
    }

    private void SendVideoToPeers(uint duration, byte[] frame)
    {
        foreach (var peer in _peers.Values)
        {
            // Participantes web (só áudio) não negociam vídeo.
            if (!peer.IsConnected || peer.Pc.VideoStream?.RemoteTrack is null) continue;
            try { peer.Pc.SendVideo(duration, frame); } catch { /* fechando */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _audio.EncodedFrameReady -= SendAudioToPeers;
        _screen.FrameEncoded -= SendVideoToPeers;
        await LeaveAsync();
        _screen.Dispose();
    }
}
