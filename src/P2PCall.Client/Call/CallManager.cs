using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using P2PCall.Client.Audio;
using P2PCall.Client.Video;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SocketIOClient;

namespace P2PCall.Client.Call;

/// <summary>
/// Sinalização via Socket.IO (mesmo protocolo do client.js) e uma conexão WebRTC P2P
/// por participante. Áudio em Opus; tela compartilhada em VP8 na trilha de vídeo, com o áudio
/// do PC somado ao microfone.
/// </summary>
public sealed class CallManager : IAsyncDisposable
{

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
    /// <summary>Lista de participantes remotos (com o nome de exibição, quando informado).</summary>
    public event Action<IReadOnlyList<Participant>>? ParticipantsChanged;
    /// <summary>Frame de tela remota decodificado (peerId, largura, altura, pixels BGR24).</summary>
    public event Action<string, int, int, byte[]>? RemoteScreenFrame;
    public event Action<string>? PeerLeft;

    public string Room { get; private set; } = "";
    /// <summary>Nosso nome de exibição, enviado aos outros participantes na oferta/resposta.</summary>
    public string DisplayName { get; private set; } = "";
    public bool IsSharingScreen => _screen.IsRunning;

    public CallManager(AudioEngine audio)
    {
        _audio = audio;
        _audio.EncodedFrameReady += SendAudioToPeers;
        _screen.FrameEncoded += SendVideoToPeers;
    }

    public async Task JoinAsync(string serverUrl, string room, string displayName)
    {
        Room = room;
        DisplayName = Participant.CleanName(displayName) ?? "";
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

    /// <summary>
    /// Compartilha a tela e o áudio do PC. O áudio vai somado à trilha do microfone, que todos
    /// os participantes (web e desktop) já recebem, então não há renegociação.
    /// Retorna false se a tela foi compartilhada sem o áudio do PC.
    /// </summary>
    public async Task<bool> StartScreenShareAsync()
    {
        _screen.Start();
        _screen.RequestKeyFrame();
        return await _audio.StartSystemAudioAsync();
    }

    public void StopScreenShare()
    {
        _screen.Stop();
        _audio.StopSystemAudio();
    }

    // ---------- Sinalização ----------

    private async Task OnUserJoined(string userId)
    {
        await _signalingLock.WaitAsync();
        try
        {
            var peer = CreatePeer(userId, audioLines: 1, withVideo: true);
            var offer = peer.Pc.createOffer(null);
            await peer.Pc.setLocalDescription(offer);
            await EmitAsync("offer", new { to = userId, offer = new { type = "offer", sdp = offer.sdp, name = DisplayName } });
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
            var offerJson = data.GetProperty("offer");
            var sdp = offerJson.GetProperty("sdp").GetString()!;

            // A resposta precisa ter exatamente as mesmas m-lines da oferta, na mesma ordem:
            // o cliente web oferece só áudio (às vezes duas trilhas, com o áudio do PC).
            int audioLines = Regex.Matches(sdp, "^m=audio ", RegexOptions.Multiline).Count;
            bool withVideo = Regex.IsMatch(sdp, "^m=video ", RegexOptions.Multiline);

            // Renegociação (ex.: o participante web começou a compartilhar o áudio do PC):
            // reaproveita a conexão existente, como o client.js faz.
            bool renegotiation = false;
            if (_peers.TryGetValue(from, out var peer) && peer.Pc.remoteDescription is not null)
            {
                peer.EnsureAudioStreams(audioLines);
                renegotiation = peer.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp }) == SetDescriptionResultEnum.OK;
                if (!renegotiation) peer = null;
            }
            else
            {
                peer = null;
            }

            if (peer is null)
            {
                peer = CreatePeer(from, audioLines, withVideo);
                var result = peer.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
                if (result != SetDescriptionResultEnum.OK)
                {
                    StatusChanged?.Invoke($"Oferta incompatível ({result}).");
                    RemovePeer(from);
                    return;
                }
            }
            peer.FlushPendingCandidates();
            LearnName(peer, offerJson);

            var answer = peer.Pc.createAnswer(null);
            await peer.Pc.setLocalDescription(answer);
            var answerSdp = SdpDirections.MatchOffer(sdp, answer.sdp);
            // O papel DTLS não pode mudar numa renegociação: repete o da primeira negociação.
            if (renegotiation && peer.DtlsSetup is not null)
                answerSdp = SdpDirections.SetDtlsSetup(answerSdp, peer.DtlsSetup);
            else
                peer.DtlsSetup = SdpDirections.GetDtlsSetup(answerSdp);
            await EmitAsync("answer", new { to = from, answer = new { type = "answer", sdp = answerSdp, name = DisplayName } });
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

            var answerJson = data.GetProperty("answer");
            var sdp = answerJson.GetProperty("sdp").GetString()!;
            LearnName(peer, answerJson);
            var result = peer.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
            if (result != SetDescriptionResultEnum.OK)
                StatusChanged?.Invoke($"Resposta incompatível ({result}).");
            // Nós oferecemos: nosso papel DTLS é o oposto do que o outro lado escolheu.
            peer.DtlsSetup ??= SdpDirections.GetDtlsSetup(sdp) == "active" ? "passive" : "active";
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

    private PeerSession CreatePeer(string peerId, int audioLines, bool withVideo)
    {
        if (_peers.TryRemove(peerId, out var existing)) existing.Dispose();
        _earlyCandidates.Remove(peerId, out var early);

        var pc = new RTCPeerConnection(RtcConfig);

        var peer = new PeerSession(peerId, pc, _audio);
        peer.EnsureAudioStreams(Math.Max(1, audioLines));

        // Entre clientes desktop a trilha de vídeo existe desde o início (só envia quando a tela
        // está sendo compartilhada), assim não é preciso renegociar ao ligar/desligar.
        if (withVideo)
        {
            var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, VpxVideoEncoder.VP8_FORMATID, 90000, null);
            pc.addTrack(new MediaStreamTrack(videoFormat, MediaStreamStatusEnum.SendRecv));
        }

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
                    NotifyParticipants();
                    break;
                case RTCPeerConnectionState.failed:
                    StatusChanged?.Invoke("Falha na conexão P2P com um participante.");
                    RemovePeer(peerId);
                    break;
                case RTCPeerConnectionState.disconnected:
                case RTCPeerConnectionState.closed:
                    NotifyParticipants();
                    break;
            }
        };

        // Cada trilha de áudio remota (microfone, áudio do PC...) tem seu próprio decodificador.
        pc.OnRtpPacketReceivedByIndex += (index, _, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.audio) peer.GetAudioSink(index).PushOpus(packet.Payload);
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
        NotifyParticipants();
    }

    private void NotifyParticipants() =>
        ParticipantsChanged?.Invoke(_peers.Values.Select(p => new Participant(p.Id, p.DisplayName, p.IsConnected)).ToList());

    /// <summary>
    /// O nome vem como campo extra ("name") no objeto da oferta/resposta, que o servidor repassa
    /// sem alterar e o navegador ignora. Clientes web não mandam nome; renegociações sem nome
    /// mantêm o que já se sabia.
    /// </summary>
    private void LearnName(PeerSession peer, JsonElement description)
    {
        if (!description.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return;
        var name = Participant.CleanName(n.GetString());
        if (name is null || name == peer.DisplayName) return;
        peer.DisplayName = name;
        NotifyParticipants();
    }

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
