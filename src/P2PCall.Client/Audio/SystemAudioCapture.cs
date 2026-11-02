using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace P2PCall.Client.Audio;

/// <summary>
/// Áudio do PC que acompanha o compartilhamento de tela. Usa o loopback por processo do Windows
/// excluindo este aplicativo: as vozes dos participantes que tocamos não são recapturadas,
/// então quem está vendo a tela não se ouve de novo (eco). Requer Windows 10 2004 ou mais recente.
/// </summary>
internal sealed class SystemAudioCapture : IDisposable
{
    private static readonly TimeSpan MaxLatency = TimeSpan.FromMilliseconds(200);

    private readonly WasapiRecorder _recorder;
    private readonly BufferedWaveProvider _buffer;
    private readonly int _channels;
    private readonly bool _isFloat;
    private byte[] _mono = new byte[AudioEngine.FrameSamples * 2];
    private readonly byte[] _read = new byte[AudioEngine.FrameSamples * 2];

    private SystemAudioCapture(WasapiRecorder recorder)
    {
        var format = recorder.WaveFormat;
        if (format.SampleRate != AudioEngine.SampleRate)
            throw new NotSupportedException($"Taxa de amostragem não suportada: {format.SampleRate} Hz.");
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (!_isFloat && format.BitsPerSample != 16)
            throw new NotSupportedException($"Formato de áudio não suportado: {format}.");

        _recorder = recorder;
        _channels = format.Channels;
        _buffer = new BufferedWaveProvider(new WaveFormat(AudioEngine.SampleRate, 16, 1), TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _recorder.DataAvailable += OnData;
    }

    public static async Task<SystemAudioCapture> StartAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Loopback por processo requer Windows 10 2004.");

        var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
            .WithFormat(new WaveFormat(AudioEngine.SampleRate, 16, 2))
            .BuildAsync();
        try
        {
            var capture = new SystemAudioCapture(recorder);
            recorder.StartRecording();
            return capture;
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    /// <summary>Soma 20 ms de áudio do PC ao quadro do microfone (PCM 16 bits mono).</summary>
    public void MixInto(Span<byte> pcm16Mono)
    {
        // Evita latência crescente quando o relógio da placa é mais rápido que o do microfone.
        if (_buffer.BufferedDuration > MaxLatency) _buffer.ClearBuffer();

        var read = _read.AsSpan(0, pcm16Mono.Length);
        _buffer.Read(read);

        var dst = MemoryMarshal.Cast<byte, short>(pcm16Mono);
        var src = MemoryMarshal.Cast<byte, short>(read);
        for (int i = 0; i < dst.Length; i++)
            dst[i] = (short)Math.Clamp(dst[i] + src[i], short.MinValue, short.MaxValue);
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        int bytesPerFrame = _channels * (_isFloat ? 4 : 2);
        int frames = data.Length / bytesPerFrame;
        if (frames == 0) return;
        if (_mono.Length < frames * 2) _mono = new byte[frames * 2];
        var mono = MemoryMarshal.Cast<byte, short>(_mono.AsSpan(0, frames * 2));

        // Com a flag Silent o conteúdo do buffer não vale nada: é silêncio.
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            mono.Clear();
        }
        else if (_isFloat)
        {
            var f = MemoryMarshal.Cast<byte, float>(data[..(frames * bytesPerFrame)]);
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += f[i * _channels + c];
                mono[i] = (short)Math.Clamp(sum / _channels * short.MaxValue, short.MinValue, short.MaxValue);
            }
        }
        else
        {
            var s = MemoryMarshal.Cast<byte, short>(data[..(frames * bytesPerFrame)]);
            for (int i = 0; i < frames; i++)
            {
                int sum = 0;
                for (int c = 0; c < _channels; c++) sum += s[i * _channels + c];
                mono[i] = (short)(sum / _channels);
            }
        }
        _buffer.AddSamples(_mono, 0, frames * 2);
    }

    public void Dispose()
    {
        _recorder.DataAvailable -= OnData;
        try { _recorder.StopRecording(); } catch { /* dispositivo removido */ }
        _recorder.Dispose();
    }
}
