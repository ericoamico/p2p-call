using P2PCall.Client.Audio;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace P2PCall.Client.Call;

/// <summary>Estado de uma conexão P2P com um participante.</summary>
internal sealed class PeerSession : IDisposable
{
    private readonly List<RTCIceCandidateInit> _pendingCandidates = [];
    private readonly object _videoLock = new();
    private VpxVideoEncoder? _videoDecoder;

    public string Id { get; }
    public RTCPeerConnection Pc { get; }
    public AudioEngine.RemoteAudioSink Audio { get; }

    public bool IsConnected => Pc.connectionState == RTCPeerConnectionState.connected;

    public PeerSession(string id, RTCPeerConnection pc, AudioEngine.RemoteAudioSink audio)
    {
        Id = id;
        Pc = pc;
        Audio = audio;
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
        Audio.Dispose();
        lock (_videoLock)
        {
            _videoDecoder?.Dispose();
            _videoDecoder = null;
        }
    }
}
