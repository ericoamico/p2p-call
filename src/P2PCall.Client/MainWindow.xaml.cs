using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _syncingToggles;

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
        // Em caso de erro, o foco vai para o campo com problema e o erro é anunciado.
        if (room.Length == 0)
        {
            SetStatus("Por favor, digite um nome para a sala!");
            RoomInput.Focus();
            return;
        }
        if (MicSelect.SelectedItem is not MicItem mic)
        {
            SetStatus("Selecione um microfone.");
            MicSelect.Focus();
            return;
        }
        if (!Uri.TryCreate(ServerInput.Text.Trim(), UriKind.Absolute, out var server))
        {
            SetStatus("Endereço do servidor inválido.");
            ServerInput.Focus();
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
            _call.ParticipantsChanged += n => Dispatcher.BeginInvoke(() => SetParticipants(n));
            _call.RemoteScreenFrame += OnRemoteScreenFrame;
            _call.PeerLeft += id => Dispatcher.BeginInvoke(() => RemoveScreen(id));

            await _call.JoinAsync(server.ToString(), room);

            KeepAwake(true);
            _screenWatchdog.Start();
            SetInCallUi(joining: false, inCall: true);
            // "Entrar" fica desabilitado: leva o foco para o próximo controle útil.
            BtnMute.Focus();
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
        BtnStart.Focus();
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
        SetParticipants(0);
        SetInCallUi(joining: false, inCall: false);
    }

    // Os botões de alternância reagem a Checked/Unchecked (e não a Click) porque leitores de tela
    // os acionam pelo padrão Toggle da automação, que muda o estado sem disparar Click.
    private void BtnMute_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_syncingToggles) SetMuted(BtnMute.IsChecked == true);
    }

    private void SetMuted(bool muted)
    {
        _isMuted = muted;
        SetToggle(BtnMute, muted);
        if (_audio is not null) _audio.IsMuted = muted;
        SetStatus(muted ? "Microfone silenciado." : "Microfone ativado.");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+M silencia/ativa o microfone de qualquer lugar da janela.
        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control && BtnMute.IsEnabled)
        {
            SetMuted(!_isMuted);
            e.Handled = true;
        }
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

    private async void BtnShare_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles || _call is null) return;
        bool wantShare = BtnShare.IsChecked == true;
        if (wantShare == _call.IsSharingScreen) return;

        if (!wantShare)
        {
            _call.StopScreenShare();
            SetStatus("Compartilhamento de tela encerrado.");
        }
        else
        {
            var call = _call;
            bool withAudio = await call.StartScreenShareAsync();
            // Pode ter parado (ou saído da sala) enquanto o áudio do PC era ativado.
            if (_call != call || !call.IsSharingScreen) return;
            SetStatus(withAudio
                ? "Compartilhando sua tela principal e o áudio do PC."
                : "Compartilhando sua tela principal, sem o áudio do PC (requer Windows 10 versão 2004 ou mais recente).");
        }
        SetToggle(BtnShare, _call.IsSharingScreen);
    }

    private void BtnRecord_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles || _audio is null || _call is null) return;
        bool wantRecord = BtnRecord.IsChecked == true;
        if (wantRecord == _audio.IsRecording) return;

        if (wantRecord)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "P2PCall");
            var safeRoom = string.Concat(_call.Room.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(folder, $"Gravacao_{safeRoom}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.wav");
            _audio.StartRecording(path);
            SetStatus("Gravando chamada.");
        }
        else
        {
            FinishRecording();
        }
    }

    private void FinishRecording()
    {
        var path = _audio?.StopRecording();
        SetToggle(BtnRecord, false);
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
                SetStatus($"{screen.Label} começou a ser compartilhada.");
            }
            screen.Update(width, height, copy);
        });
    }

    private void RemoveStaleScreens()
    {
        foreach (var s in _screens.Where(s => DateTime.UtcNow - s.LastFrameUtc > ScreenTimeout).ToList())
        {
            _screens.Remove(s);
            SetStatus($"{s.Label} deixou de ser compartilhada.");
        }
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
        SetToggle(BtnMute, _isMuted);
        if (!inCall)
        {
            SetToggle(BtnShare, false);
            SetToggle(BtnRecord, false);
        }
    }

    /// <summary>Atualiza o estado visual/anunciado sem disparar a ação do botão.</summary>
    private void SetToggle(ToggleButton button, bool value)
    {
        _syncingToggles = true;
        try { button.IsChecked = value; }
        finally { _syncingToggles = false; }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        Announce(StatusText);
    }

    private void SetParticipants(int count)
    {
        var text = $"Participantes conectados: {count}";
        if (ParticipantsText.Text == text) return;
        ParticipantsText.Text = text;
        Announce(ParticipantsText);
    }

    /// <summary>Avisa o leitor de tela (Narrador, NVDA, JAWS) que uma região "ao vivo" mudou.</summary>
    private static void Announce(UIElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

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

    // ToString é o que o leitor de tela lê para cada item da lista.
    private sealed record MicItem(int DeviceNumber, string Name)
    {
        public override string ToString() => Name;
    }
}
