using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using P2PCall.Client.Audio;
using P2PCall.Client.Call;
using P2PCall.Client.Video;

namespace P2PCall.Client;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ScreenTimeout = TimeSpan.FromSeconds(3);

    private readonly ObservableCollection<RemoteScreen> _screens = [];
    private readonly HashSet<string> _pendingScreenUpdates = [];
    private readonly DispatcherTimer _screenWatchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private AudioEngine? _audio;
    private CallManager? _call;
    private bool _isMuted;

    public MainWindow()
    {
        InitializeComponent();
        ScreensList.ItemsSource = _screens;
        _screens.CollectionChanged += (_, _) =>
            NoScreensText.Visibility = _screens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _screenWatchdog.Tick += (_, _) => RemoveStaleScreens();

        ApplyCommandLine(Environment.GetCommandLineArgs());
        LoadMicrophones();
    }

    /// <summary>Equivalente ao ?sala= da versão web: P2PCall.Client.exe --sala nome [--server url].</summary>
    private void ApplyCommandLine(string[] args)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] is "--sala" or "--room") RoomInput.Text = args[i + 1];
            if (args[i] is "--server") ServerInput.Text = args[i + 1];
        }
    }

    private void LoadMicrophones()
    {
        try
        {
            var mics = AudioEngine.GetMicrophones().Select(m => new MicItem(m.DeviceNumber, m.Name)).ToList();
            MicSelect.ItemsSource = mics;
            MicSelect.SelectedIndex = mics.Count > 0 ? 0 : -1;
            SetStatus(mics.Count > 0 ? "Pronto para entrar." : "Nenhum microfone encontrado.");
        }
        catch (Exception ex)
        {
            SetStatus("Erro ao listar dispositivos: " + ex.Message);
        }
    }

    private int SelectedBitrate =>
        QualitySelect.SelectedItem is ComboBoxItem { Tag: string tag } ? int.Parse(tag) : 128000;

    private async void BtnStart_Click(object sender, RoutedEventArgs e) => await JoinCallAsync();

    private async void RoomInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnStart.IsEnabled) await JoinCallAsync();
    }

    private async Task JoinCallAsync()
    {
        var room = RoomInput.Text.Trim();
        if (room.Length == 0)
        {
            SetStatus("Por favor, digite um nome para a sala!");
            return;
        }
        if (MicSelect.SelectedItem is not MicItem mic)
        {
            SetStatus("Selecione um microfone.");
            return;
        }
        if (!Uri.TryCreate(ServerInput.Text.Trim(), UriKind.Absolute, out var server))
        {
            SetStatus("Endereço do servidor inválido.");
            return;
        }

        SetInCallUi(joining: true);
        SetStatus("Conectando...");

        try
        {
            _audio = new AudioEngine(SelectedBitrate) { IsMuted = _isMuted };
            _audio.StartCapture(mic.DeviceNumber);
            _audio.StartPlayback();

            _call = new CallManager(_audio);
            _call.StatusChanged += msg => Dispatcher.BeginInvoke(() => SetStatus(msg));
            _call.ParticipantsChanged += n => Dispatcher.BeginInvoke(() => ParticipantsText.Text = $"Participantes conectados: {n}");
            _call.RemoteScreenFrame += OnRemoteScreenFrame;
            _call.PeerLeft += id => Dispatcher.BeginInvoke(() => RemoveScreen(id));

            await _call.JoinAsync(server.ToString(), room);

            KeepAwake(true);
            _screenWatchdog.Start();
            SetInCallUi(joining: false, inCall: true);
        }
        catch (Exception ex)
        {
            SetStatus("Erro ao entrar: " + ex.Message);
            await LeaveCallAsync();
        }
    }

    private async void BtnLeave_Click(object sender, RoutedEventArgs e)
    {
        await LeaveCallAsync();
        SetStatus("Você saiu da sala.");
    }

    private async Task LeaveCallAsync()
    {
        _screenWatchdog.Stop();
        if (_audio?.IsRecording == true) FinishRecording();

        if (_call is not null)
        {
            await _call.DisposeAsync();
            _call = null;
        }
        _audio?.Dispose();
        _audio = null;

        _screens.Clear();
        KeepAwake(false);
        ParticipantsText.Text = "Participantes conectados: 0";
        SetInCallUi(joining: false, inCall: false);
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        if (_audio is not null) _audio.IsMuted = _isMuted;
        BtnMute.Content = _isMuted ? "Ativar Microfone" : "Silenciar Microfone";
    }

    private void MicSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audio is null || MicSelect.SelectedItem is not MicItem mic) return;
        try
        {
            _audio.StartCapture(mic.DeviceNumber);
        }
        catch (Exception ex)
        {
            SetStatus("Erro no hardware: " + ex.Message);
        }
    }

    private void QualitySelect_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _audio?.SetBitrate(SelectedBitrate);

    private void BtnShare_Click(object sender, RoutedEventArgs e)
    {
        if (_call is null) return;
        if (_call.IsSharingScreen)
        {
            _call.StopScreenShare();
            BtnShare.Content = "Compartilhar Tela";
            SetStatus($"Conectado à sala: {_call.Room}.");
        }
        else
        {
            _call.StartScreenShare();
            BtnShare.Content = "Parar Compartilhamento";
            SetStatus("Compartilhando sua tela principal...");
        }
    }

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null || _call is null) return;

        if (!_audio.IsRecording)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "P2PCall");
            var safeRoom = string.Concat(_call.Room.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(folder, $"Gravacao_{safeRoom}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.wav");
            _audio.StartRecording(path);
            BtnRecord.Content = "Parar e Salvar Gravação";
            BtnRecord.Background = System.Windows.Media.Brushes.DarkRed;
            SetStatus("Gravando chamada...");
        }
        else
        {
            FinishRecording();
        }
    }

    private void FinishRecording()
    {
        var path = _audio?.StopRecording();
        BtnRecord.Content = "Gravar Chamada";
        BtnRecord.ClearValue(BackgroundProperty);
        if (path is not null) SetStatus($"Gravação salva em: {path}");
    }

    // ---------- Telas remotas ----------

    private void OnRemoteScreenFrame(string peerId, int width, int height, byte[] bgr)
    {
        // Descarta frames enquanto a UI ainda não desenhou o anterior desse participante.
        lock (_pendingScreenUpdates)
        {
            if (!_pendingScreenUpdates.Add(peerId)) return;
        }

        var copy = bgr.ToArray();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            lock (_pendingScreenUpdates) _pendingScreenUpdates.Remove(peerId);
            if (_call is null) return;

            var screen = _screens.FirstOrDefault(s => s.PeerId == peerId);
            if (screen is null)
            {
                screen = new RemoteScreen(peerId);
                _screens.Add(screen);
            }
            screen.Update(width, height, copy);
        });
    }

    private void RemoveStaleScreens()
    {
        foreach (var s in _screens.Where(s => DateTime.UtcNow - s.LastFrameUtc > ScreenTimeout).ToList())
            _screens.Remove(s);
    }

    private void RemoveScreen(string peerId)
    {
        var screen = _screens.FirstOrDefault(s => s.PeerId == peerId);
        if (screen is not null) _screens.Remove(screen);
    }

    // ---------- UI ----------

    private void SetInCallUi(bool joining, bool inCall = false)
    {
        BtnStart.IsEnabled = !joining && !inCall;
        RoomInput.IsEnabled = !joining && !inCall;
        ServerInput.IsEnabled = !joining && !inCall;
        BtnMute.IsEnabled = inCall;
        BtnShare.IsEnabled = inCall;
        BtnRecord.IsEnabled = inCall;
        BtnLeave.IsEnabled = inCall;
        if (!inCall) BtnShare.Content = "Compartilhar Tela";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_call is null && _audio is null) return;
        e.Cancel = true;
        await LeaveCallAsync();
        Close();
    }

    // Equivalente ao Wake Lock da versão web: impede que a tela/PC durma durante a chamada.
    private static void KeepAwake(bool enable)
    {
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        SetThreadExecutionState(enable ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private sealed record MicItem(int DeviceNumber, string Name);
}
