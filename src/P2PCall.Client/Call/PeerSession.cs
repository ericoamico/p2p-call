using P2PCall.Client.Audio;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace P2PCall.Client.Call;

/// <summary>Estado de uma conexão P2P com um participante.</summary>
internal sealed class PeerSession : IDisposable
{
    private const string OpusFmtp = "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1";
    private static readonly AudioFormat OpusFormat = new(AudioCodecsEnum.OPUS, 111, AudioEngine.SampleRate, 2, OpusFmtp);

    private readonly AudioEngine _engine;
    private readonly Dictionary<int, AudioEngine.RemoteAudioSink> _audioSinks = [];
    private readonly List<RTCIceCandidateInit> _pendingCandidates = [];
    private readonly object _videoLock = new();
    private VpxVideoEncoder? _videoDecoder;

    public string Id { get; }
    public RTCPeerConnection Pc { get; }

    /// <summary>Nosso papel DTLS ("active"/"passive") definido na primeira negociação.</summary>
    public string? DtlsSetup { get; set; }

    /// <summary>Nome de exibição informado pelo participante (null para clientes web).</summary>
    public string? DisplayName { get; set; }

    public bool IsConnected => Pc.connectionState == RTCPeerConnectionState.connected;

    public PeerSession(string id, RTCPeerConnection pc, AudioEngine engine)
    {
        Id = id;
        Pc = pc;
        _engine = engine;
    }

    /// <summary>
    /// Garante uma trilha de áudio local por m-line de áudio da oferta. A primeira envia o
    /// microfone; as extras (ex.: áudio do PC do participante web) só recebem.
    /// </summary>
    public void EnsureAudioStreams(int count)
    {
        int existing = Pc.AudioStreamList.Count(s => s.LocalTrack is not null);
        while (existing < count)
        {
            bool first = existing == 0;
            Pc.addTrack(new MediaStreamTrack(OpusFormat, first ? MediaStreamStatusEnum.SendRecv : MediaStreamStatusEnum.RecvOnly));

            int now = Pc.AudioStreamList.Count(s => s.LocalTrack is not null);
            if (now <= existing) break; // a biblioteca não criou outro stream
            existing = now;
        }
    }

    public AudioEngine.RemoteAudioSink GetAudioSink(int streamIndex)
    {
        lock (_audioSinks)
        {
            if (!_audioSinks.TryGetValue(streamIndex, out var sink))
                _audioSinks[streamIndex] = sink = _engine.CreateRemoteSink();
            return sink;
        }
    }

    public void AddRemoteCandidate(RTCIceCandidateInit candidate)
    {
        if (Pc.remoteDescription is null)
            _pendingCandidates.Add(candidate);
        else
            Pc.addIceCandidate(candidate);
    }

    public void FlushPendingCandidates()
    {
        if (Pc.remoteDescription is null) return;
        foreach (var c in _pendingCandidates) Pc.addIceCandidate(c);
        _pendingCandidates.Clear();
    }

    public IReadOnlyList<VideoSample> DecodeVideo(byte[] frame)
    {
        lock (_videoLock)
        {
            _videoDecoder ??= new VpxVideoEncoder();
            try
            {
                return _videoDecoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8)?.ToList() ?? [];
            }
            catch
            {
                return []; // frame incompleto
            }
        }
    }

    public void Dispose()
    {
        try { Pc.close(); } catch { /* já fechada */ }
        lock (_audioSinks)
        {
            foreach (var sink in _audioSinks.Values) sink.Dispose();
            _audioSinks.Clear();
        }
        lock (_videoLock)
        {
            _videoDecoder?.Dispose();
            _videoDecoder = null;
        }
    }
}
