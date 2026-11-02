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
    private byte[] _stereo = new byte[AudioEngine.FrameSamples * 4];
    private readonly byte[] _read = new byte[AudioEngine.FrameSamples * 4];

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
        _buffer = new BufferedWaveProvider(AudioEngine.Pcm16Stereo, TimeSpan.FromSeconds(2))
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

    /// <summary>Soma 20 ms de áudio do PC ao quadro do microfone (PCM 16 bits estéreo intercalado).</summary>
    public void MixInto(Span<short> dst)
    {
        // Evita latência crescente quando o relógio da placa é mais rápido que o do microfone.
        if (_buffer.BufferedDuration > MaxLatency) _buffer.ClearBuffer();

        var read = _read.AsSpan(0, dst.Length * 2);
        _buffer.Read(read);

        var src = MemoryMarshal.Cast<byte, short>(read);
        for (int i = 0; i < dst.Length; i++)
            dst[i] = (short)Math.Clamp(dst[i] + src[i], short.MinValue, short.MaxValue);
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        int bytesPerFrame = _channels * (_isFloat ? 4 : 2);
        int frames = data.Length / bytesPerFrame;
        if (frames == 0) return;
        if (_stereo.Length < frames * 4) _stereo = new byte[frames * 4];
        var stereo = MemoryMarshal.Cast<byte, short>(_stereo.AsSpan(0, frames * 4));
        // Pedimos estéreo ao Windows; se vier outro layout, usa os dois primeiros canais (E/D).
        int right = _channels > 1 ? 1 : 0;

        // Com a flag Silent o conteúdo do buffer não vale nada: é silêncio.
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            stereo.Clear();
        }
        else if (_isFloat)
        {
            var f = MemoryMarshal.Cast<byte, float>(data[..(frames * bytesPerFrame)]);
            for (int i = 0; i < frames; i++)
            {
                stereo[2 * i] = ToPcm16(f[i * _channels]);
                stereo[2 * i + 1] = ToPcm16(f[i * _channels + right]);
            }
        }
        else
        {
            var s = MemoryMarshal.Cast<byte, short>(data[..(frames * bytesPerFrame)]);
            for (int i = 0; i < frames; i++)
            {
                stereo[2 * i] = s[i * _channels];
                stereo[2 * i + 1] = s[i * _channels + right];
            }
        }
        _buffer.AddSamples(_stereo, 0, frames * 4);
    }

    private static short ToPcm16(float sample) =>
        (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);

    public void Dispose()
    {
        _recorder.DataAvailable -= OnData;
        try { _recorder.StopRecording(); } catch { /* dispositivo removido */ }
        _recorder.Dispose();
    }
}
