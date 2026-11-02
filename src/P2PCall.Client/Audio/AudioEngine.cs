using System.IO;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace P2PCall.Client.Audio;

/// <summary>
/// "Mesa de som" da chamada: captura o microfone (NAudio), codifica em Opus,
/// soma o áudio do PC durante o compartilhamento de tela, mistura as vozes remotas para
/// reprodução e grava a chamada (mic + áudio do PC + remotos) em WAV.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960; // 20 ms a 48 kHz
    private const int MaxOpusPacket = 4000;

    public static readonly WaveFormat Pcm16Stereo = new(SampleRate, 16, 2);
    private static readonly WaveFormat Pcm16Mono = new(SampleRate, 16, 1);
    private static readonly WaveFormat FloatStereo = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    private readonly MixingSampleProvider _mixer = new(FloatStereo) { ReadFully = true };
    private readonly RecordingTap _tap;
    private readonly object _encoderLock = new();
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _captureAccumulator = new byte[FrameSamples * 2];
    private readonly byte[] _opusOutput = new byte[MaxOpusPacket];
    private int _accumulated;

    private WaveIn? _capture;
    private WaveOut? _output;
    private SystemAudioCapture? _systemAudio;
    private bool _systemAudioWanted;

    /// <summary>Disparado a cada 20 ms com um pacote Opus pronto para envio.</summary>
    public event Action<byte[]>? EncodedFrameReady;

    public bool IsMuted { get; set; }
    public bool IsRecording => _tap.IsRecording;
    public bool IsSharingSystemAudio => _systemAudio is not null;

    public AudioEngine(int bitrateBps)
    {
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = bitrateBps;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
        _encoder.Complexity = 10;
        _tap = new RecordingTap(_mixer);
    }

    public static IReadOnlyList<(int DeviceNumber, string Name)> GetMicrophones()
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            list.Add((i, string.IsNullOrWhiteSpace(caps.ProductName) ? $"Microfone {i + 1}" : caps.ProductName));
        }
        return list;
    }

    /// <summary>Equivalente ao maxBitrate do sender no WebRTC do navegador.</summary>
    public void SetBitrate(int bitrateBps)
    {
        lock (_encoderLock) _encoder.Bitrate = bitrateBps;
    }

    /// <summary>Inicia (ou troca) o microfone sem derrubar as conexões.</summary>
    public void StartCapture(int deviceNumber)
    {
        StopCapture();

        // Sem cancelamento de eco / supressão de ruído / AGC: o WinMM entrega o áudio cru.
        var capture = new WaveIn
        {
            DeviceNumber = deviceNumber,
            WaveFormat = Pcm16Mono,
            BufferMilliseconds = 20,
            NumberOfBuffers = 4,
        };
        capture.DataAvailable += OnCaptureData;
        capture.StartRecording();
        _capture = capture;
    }

    public void StopCapture()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnCaptureData;
        try { _capture.StopRecording(); } catch { /* dispositivo removido */ }
        _capture.Dispose();
        _capture = null;
        _accumulated = 0;
    }

    /// <summary>
    /// Começa a enviar o áudio do PC (tudo menos este aplicativo) junto com o microfone.
    /// Retorna false se o Windows não suporta o loopback por processo.
    /// </summary>
    public async Task<bool> StartSystemAudioAsync()
    {
        if (_systemAudio is not null) return true;
        _systemAudioWanted = true;

        SystemAudioCapture capture;
        try
        {
            capture = await SystemAudioCapture.StartAsync();
        }
        catch (Exception ex)
        {
            // Nunca cai para o loopback do sistema inteiro: ele recapturaria as vozes remotas.
            System.Diagnostics.Debug.WriteLine($"Áudio do PC indisponível: {ex.Message}");
            return false;
        }

        // O compartilhamento pode ter sido encerrado enquanto a captura era ativada.
        if (!_systemAudioWanted || _systemAudio is not null)
        {
            capture.Dispose();
            return _systemAudioWanted;
        }
        _systemAudio = capture;
        return true;
    }

    public void StopSystemAudio()
    {
        _systemAudioWanted = false;
        Interlocked.Exchange(ref _systemAudio, null)?.Dispose();
    }

    public void StartPlayback()
    {
        if (_output is not null) return;
        _output = new WaveOut { BufferMilliseconds = 40, NumberOfBuffers = 3 };
        _output.Init(_tap);
        _output.Play();
    }

    public void StopPlayback()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }

    /// <summary>Cria a "entrada da mesa" de um participante remoto.</summary>
    public RemoteAudioSink CreateRemoteSink()
    {
        var sink = new RemoteAudioSink(this);
        _mixer.AddMixerInput(sink.SampleProvider);
        return sink;
    }

    internal void RemoveSink(RemoteAudioSink sink) => _mixer.RemoveMixerInput(sink.SampleProvider);

    public void StartRecording(string path) => _tap.Start(path);

    public string? StopRecording() => _tap.Stop();

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        var data = e.BufferSpan[..e.BytesRecorded];
        const int frameBytes = FrameSamples * 2;

        while (!data.IsEmpty)
        {
            int toCopy = Math.Min(frameBytes - _accumulated, data.Length);
            data[..toCopy].CopyTo(_captureAccumulator.AsSpan(_accumulated));
            _accumulated += toCopy;
            data = data[toCopy..];

            if (_accumulated < frameBytes) continue;
            _accumulated = 0;

            var frame = _captureAccumulator.AsSpan(0, frameBytes);
            if (IsMuted) frame.Clear();
            // Silenciar corta só o microfone: o áudio do PC compartilhado continua indo.
            _systemAudio?.MixInto(frame);

            _tap.AddMicSamples(frame);

            byte[] packet;
            lock (_encoderLock)
            {
                int len = _encoder.Encode(MemoryMarshal.Cast<byte, short>(frame), FrameSamples, _opusOutput, MaxOpusPacket);
                if (len <= 0) continue;
                packet = _opusOutput.AsSpan(0, len).ToArray();
            }
            EncodedFrameReady?.Invoke(packet);
        }
    }

    public void Dispose()
    {
        StopRecording();
        StopSystemAudio();
        StopCapture();
        StopPlayback();
        _mixer.RemoveAllMixerInputs();
    }

    /// <summary>Voz de um participante remoto: decodifica Opus e entrega PCM para a mesa.</summary>
    public sealed class RemoteAudioSink : IDisposable
    {
        private static readonly TimeSpan MaxLatency = TimeSpan.FromMilliseconds(300);

        private readonly AudioEngine _engine;
        private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(SampleRate, 2);
        private readonly BufferedWaveProvider _buffer;
        private readonly short[] _pcm = new short[5760 * 2]; // até 120 ms estéreo

        internal ISampleProvider SampleProvider { get; }

        internal RemoteAudioSink(AudioEngine engine)
        {
            _engine = engine;
            _buffer = new BufferedWaveProvider(Pcm16Stereo, TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            SampleProvider = _buffer.ToSampleProvider();
        }

        public void PushOpus(byte[] packet)
        {
            int samplesPerChannel;
            try
            {
                samplesPerChannel = _decoder.Decode(packet, _pcm, 5760, false);
            }
            catch
            {
                return; // pacote corrompido
            }
            if (samplesPerChannel <= 0) return;

            // Evita latência crescente quando o relógio do remetente é mais rápido que o nosso.
            if (_buffer.BufferedDuration > MaxLatency) _buffer.ClearBuffer();

            _buffer.AddSamples(MemoryMarshal.AsBytes(_pcm.AsSpan(0, samplesPerChannel * 2)));
        }

        public void Dispose() => _engine.RemoveSink(this);
    }

    /// <summary>
    /// Fica entre a mesa e a saída de áudio: tudo que toca nos alto-falantes (vozes remotas)
    /// é somado ao microfone local e gravado no arquivo, usando o relógio da placa de som.
    /// </summary>
    private sealed class RecordingTap : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly BufferedWaveProvider _micBuffer;
        private readonly ISampleProvider _mic;
        private readonly object _lock = new();
        private WaveFileWriter? _writer;
        private float[] _micScratch = new float[4096];
        private float[] _mixScratch = new float[4096];

        public WaveFormat WaveFormat => _source.WaveFormat;
        public bool IsRecording => _writer is not null;

        public RecordingTap(ISampleProvider source)
        {
            _source = source;
            _micBuffer = new BufferedWaveProvider(Pcm16Mono, TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            _mic = _micBuffer.ToSampleProvider().ToStereo();
        }

        public void Start(string path)
        {
            lock (_lock)
            {
                if (_writer is not null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _micBuffer.ClearBuffer();
                _writer = new WaveFileWriter(path, Pcm16Stereo);
            }
        }

        public string? Stop()
        {
            lock (_lock)
            {
                if (_writer is null) return null;
                var path = _writer.Filename;
                _writer.Dispose();
                _writer = null;
                return path;
            }
        }

        public void AddMicSamples(ReadOnlySpan<byte> pcm16Mono)
        {
            if (_writer is null) return;
            _micBuffer.AddSamples(pcm16Mono);
        }

        public int Read(Span<float> buffer)
        {
            int read = _source.Read(buffer);
            if (_writer is null || read == 0) return read;

            lock (_lock)
            {
                if (_writer is null) return read;

                if (_micScratch.Length < read)
                {
                    _micScratch = new float[read];
                    _mixScratch = new float[read];
                }

                var mic = _micScratch.AsSpan(0, read);
                _mic.Read(mic);
                for (int i = 0; i < read; i++)
                    _mixScratch[i] = Math.Clamp(buffer[i] + mic[i], -1f, 1f);

                _writer.WriteSamples(_mixScratch, 0, read);
            }
            return read;
        }
    }
}
