using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace P2PCall.Client.Video;

/// <summary>Tela compartilhada por um participante, exibida na UI.</summary>
public sealed class RemoteScreen : INotifyPropertyChanged
{
    private WriteableBitmap? _bitmap;

    public string PeerId { get; }
    public string Label => $"Tela de {PeerId[..Math.Min(6, PeerId.Length)]}";
    public DateTime LastFrameUtc { get; private set; }

    public WriteableBitmap? Bitmap
    {
        get => _bitmap;
        private set
        {
            _bitmap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bitmap)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RemoteScreen(string peerId) => PeerId = peerId;

    /// <summary>Copia um frame BGR24 para o bitmap (chamar na thread da UI).</summary>
    public void Update(int width, int height, byte[] bgr)
    {
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);

        int stride = width * 3;
        if (bgr.Length >= stride * height)
            _bitmap!.WritePixels(new Int32Rect(0, 0, width, height), bgr, stride, 0);
        LastFrameUtc = DateTime.UtcNow;
    }
}
