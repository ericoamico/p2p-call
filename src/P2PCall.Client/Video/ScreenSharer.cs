using System.Runtime.InteropServices;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace P2PCall.Client.Video;

/// <summary>
/// Captura a tela principal via GDI, reduz para no máximo <see cref="MaxWidth"/> px de largura
/// e codifica em VP8. Os frames codificados são entregues por <see cref="FrameEncoded"/>.
/// </summary>
public sealed class ScreenSharer : IDisposable
{
    public const int Fps = 12;
    public const uint RtpDurationPerFrame = 90000 / Fps;
    private const int MaxWidth = 1600;
    private static readonly TimeSpan KeyFrameInterval = TimeSpan.FromSeconds(4);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _keyFrameRequested;

    /// <summary>Frame VP8 pronto (duração em unidades RTP de 90 kHz, dados).</summary>
    public event Action<uint, byte[]>? FrameEncoded;

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => CaptureLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* cancelado */ }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Pede um keyframe (ex.: um novo participante acabou de conectar).</summary>
    public void RequestKeyFrame() => _keyFrameRequested = true;

    private void CaptureLoop(CancellationToken token)
    {
        int srcWidth = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        int srcHeight = Native.GetSystemMetrics(Native.SM_CYSCREEN);

        double scale = Math.Min(1.0, (double)MaxWidth / srcWidth);
        int width = (int)(srcWidth * scale) & ~1; // VP8/I420 exige dimensões pares
        int height = (int)(srcHeight * scale) & ~1;

        using var encoder = new VpxVideoEncoder { TargetKbps = 2500 };
        using var grabber = new GdiGrabber(srcWidth, srcHeight, width, height);
        var frameTime = TimeSpan.FromSeconds(1.0 / Fps);
        var lastKeyFrame = DateTime.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!token.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            try
            {
                var bgra = grabber.Grab();

                if (_keyFrameRequested || DateTime.UtcNow - lastKeyFrame > KeyFrameInterval)
                {
                    _keyFrameRequested = false;
                    lastKeyFrame = DateTime.UtcNow;
                    encoder.ForceKeyFrame();
                }

                var encoded = encoder.EncodeVideo(width, height, bgra, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);
                if (encoded is { Length: > 0 }) FrameEncoded?.Invoke(RtpDurationPerFrame, encoded);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Falha ao capturar tela: {ex}");
            }

            var wait = frameTime - (clock.Elapsed - started);
            if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
        }
    }

    public void Dispose() => Stop();

    /// <summary>Copia a tela para um DIB 32 bpp redimensionado, incluindo o cursor.</summary>
    private sealed class GdiGrabber : IDisposable
    {
        private readonly int _srcWidth, _srcHeight, _width, _height;
        private readonly IntPtr _screenDc, _memDc, _bitmap, _oldBitmap;
        private readonly byte[] _buffer;
        private Native.BITMAPINFO _info;

        public GdiGrabber(int srcWidth, int srcHeight, int width, int height)
        {
            (_srcWidth, _srcHeight, _width, _height) = (srcWidth, srcHeight, width, height);
            _screenDc = Native.GetDC(IntPtr.Zero);
            _memDc = Native.CreateCompatibleDC(_screenDc);
            _bitmap = Native.CreateCompatibleBitmap(_screenDc, width, height);
            _oldBitmap = Native.SelectObject(_memDc, _bitmap);
            Native.SetStretchBltMode(_memDc, Native.HALFTONE);
            _buffer = new byte[width * height * 4];
            _info = new Native.BITMAPINFO
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFO>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
        }

        public byte[] Grab()
        {
            Native.StretchBlt(_memDc, 0, 0, _width, _height, _screenDc, 0, 0, _srcWidth, _srcHeight,
                Native.SRCCOPY | Native.CAPTUREBLT);
            DrawCursor();
            Native.GetDIBits(_memDc, _bitmap, 0, (uint)_height, _buffer, ref _info, 0);
            return _buffer;
        }

        private void DrawCursor()
        {
            var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
            if (!Native.GetCursorInfo(ref ci) || ci.flags != Native.CURSOR_SHOWING) return;
            if (!Native.GetIconInfo(ci.hCursor, out var icon)) return;

            double sx = (double)_width / _srcWidth, sy = (double)_height / _srcHeight;
            int x = (int)((ci.ptScreenPos.X - icon.xHotspot) * sx);
            int y = (int)((ci.ptScreenPos.Y - icon.yHotspot) * sy);
            Native.DrawIconEx(_memDc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);

            if (icon.hbmMask != IntPtr.Zero) Native.DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) Native.DeleteObject(icon.hbmColor);
        }

        public void Dispose()
        {
            Native.SelectObject(_memDc, _oldBitmap);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(_memDc);
            Native.ReleaseDC(IntPtr.Zero, _screenDc);
        }
    }

    private static class Native
    {
        public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        public const int HALFTONE = 4;
        public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        public const int CURSOR_SHOWING = 1;
        public const int DI_NORMAL = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize, flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")]
        public static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);
    }
}
