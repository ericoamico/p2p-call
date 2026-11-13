using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using P2PCall.Client.Audio;
using P2PCall.Client.Call;
using P2PCall.Client.Input;
using P2PCall.Client.Update;
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
    private bool _syncingSpeakers;
    private Uri? _joinedServer;
    // Nome de exibição escolhido antes de entrar; vai para os outros participantes na negociação.
    private string _displayName = "";
    private readonly ObservableCollection<string> _participantItems = [];
    private Dictionary<string, Participant> _participants = [];
    private ReleaseInfo? _availableUpdate;
    private CancellationTokenSource? _updateCts;

    // "Aperte para falar": o hook global roda em outra thread, por isso os campos que ele lê são voláteis.
    private readonly Preferences _prefs = Preferences.Load();
    private GlobalKeyboardHook? _keyboardHook;
    private volatile bool _pushToTalk;
    private volatile int _pttKey;
    private volatile bool _pttHeld;
    private volatile bool _capturingPttKey;
    private static readonly Brush IdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0xBA, 0xC4)));
    private static readonly Brush TalkingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));

    public MainWindow()
    {
        InitializeComponent();
        _pttKey = _prefs.PushToTalkKey;
        UpdatePttKeyButton();
        InputModeSelect.SelectedIndex = _prefs.PushToTalk ? 1 : 0;
        Closed += (_, _) => _keyboardHook?.Dispose();
        ScreensList.ItemsSource = _screens;
        ParticipantsList.ItemsSource = _participantItems;
        _screens.CollectionChanged += (_, _) =>
            NoScreensText.Visibility = _screens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _screenWatchdog.Tick += (_, _) => RemoveStaleScreens();

        ApplyCommandLine(Environment.GetCommandLineArgs());
        LoadMicrophones();
        LoadSpeakers();
        Loaded += async (_, _) => await CheckForUpdateAsync();
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

    private void LoadSpeakers()
    {
        try
        {
            var speakers = AudioEngine.GetSpeakers().Select(s => new SpeakerItem(s.DeviceNumber, s.Name)).ToList();
            SpeakerSelect.ItemsSource = speakers;
            SpeakerSelect.SelectedIndex = speakers.Count > 0 ? 0 : -1;
            if (speakers.Count == 0) SetStatus("Nenhum dispositivo de saída de áudio encontrado.");
        }
        catch (Exception ex)
        {
            SetStatus("Erro ao listar as saídas de áudio: " + ex.Message);
        }
    }

    private int SelectedBitrate =>
        QualitySelect.SelectedItem is ComboBoxItem { Tag: string tag } ? int.Parse(tag) : 128000;

    // ---------- Atualização automática ----------

    private async Task CheckForUpdateAsync()
    {
        if (!UpdateService.IsSupported) return;

        var args = Environment.GetCommandLineArgs();
        var local = UpdateService.SyncLocalVersion();
        UpdateService.CleanupLeftovers();
        if (args.Contains("--atualizado")) SetStatus($"Atualizado para a versão {local}.");
        if (args.Contains("--falha-atualizacao"))
            SetStatus("Não foi possível substituir o arquivo durante a atualização. Feche outras cópias do aplicativo e tente de novo.");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var release = await UpdateService.CheckAsync(local, cts.Token);
            if (release is null) return;

            _availableUpdate = release;
            UpdateBanner.Visibility = Visibility.Visible;
            SetUpdateText($"Nova versão {release.Version} disponível. Você usa a versão {local}.");
        }
        catch
        {
            // Sem internet ou GitHub fora do ar: tenta de novo na próxima vez que abrir.
        }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Durante o download o mesmo botão cancela (o foco continua nele).
        if (_updateCts is not null)
        {
            _updateCts.Cancel();
            return;
        }
        if (_availableUpdate is not { } release) return;

        if (_call is not null && MessageBox.Show(this,
                "Você está em uma chamada. A atualização encerra a chamada e reinicia o aplicativo. Continuar?",
                "Atualizar P2P Call", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _updateCts = new CancellationTokenSource();
        BtnUpdate.Content = "Parar de _baixar";
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        SetUpdateText($"Baixando a versão {release.Version}...");

        string downloaded;
        try
        {
            int announcedQuarter = 0;
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p * 100;
                // Anuncia a cada 25% para não inundar o leitor de tela.
                int quarter = (int)(p * 4);
                if (quarter > announcedQuarter && quarter < 4)
                {
                    announcedQuarter = quarter;
                    SetUpdateText($"Baixando a versão {release.Version}: {quarter * 25}%.");
                }
            });
            downloaded = await UpdateService.DownloadAsync(release, progress, _updateCts.Token);
        }
        catch (OperationCanceledException)
        {
            ResetUpdateUi("Download da atualização cancelado.");
            return;
        }
        catch (Exception ex)
        {
            ResetUpdateUi("Falha ao baixar a atualização: " + ex.Message);
            return;
        }
        finally
        {
            _updateCts?.Dispose();
            _updateCts = null;
        }

        SetUpdateText("Download concluído. O aplicativo vai fechar e abrir a nova versão.");
        await LeaveCallAsync();
        try
        {
            UpdateService.ApplyAndRestart(downloaded);
        }
        catch (Exception ex)
        {
            ResetUpdateUi("Não foi possível instalar a atualização: " + ex.Message);
            return;
        }
        Application.Current.Shutdown();
    }

    private void ResetUpdateUi(string message)
    {
        BtnUpdate.Content = "_Baixar e atualizar";
        UpdateProgress.Visibility = Visibility.Collapsed;
        SetUpdateText(message);
    }

    private void SetUpdateText(string text)
    {
        UpdateText.Text = text;
        Announce(UpdateText);
    }

    // Ao colar um link de sala (o mesmo formato do navegador), fica só o nome da sala;
    // se o link for de outro servidor, o campo do servidor também é ajustado.
    private void RoomInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!RoomLink.TryParse(RoomInput.Text, out var server, out var room)) return;

        RoomInput.Text = room;
        RoomInput.CaretIndex = room.Length;

        bool sameServer = Uri.TryCreate(ServerInput.Text.Trim(), UriKind.Absolute, out var current)
            && Uri.Compare(current, new Uri(server), UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        if (sameServer)
        {
            SetStatus($"Link reconhecido: sala {room}.");
        }
        else
        {
            ServerInput.Text = server;
            SetStatus($"Link reconhecido: sala {room}, servidor {server}.");
        }
    }

    private void BtnCopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_call is null || _joinedServer is null) return;
        var link = RoomLink.Build(_joinedServer, _call.Room);

        // A área de transferência pode estar momentaneamente presa por outro programa.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(link);
                SetStatus($"Link da sala copiado: {link}");
                return;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                SetStatus("Não foi possível copiar o link: " + ex.Message);
                return;
            }
        }
    }

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
        if (SpeakerSelect.SelectedItem is not SpeakerItem speaker)
        {
            SetStatus("Selecione um dispositivo de saída de áudio.");
            SpeakerSelect.Focus();
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
            _audio = new AudioEngine(SelectedBitrate) { IsMuted = MicClosed };
            _audio.StartCapture(mic.DeviceNumber);
            _audio.StartPlayback(speaker.DeviceNumber);

            _call = new CallManager(_audio);
            _call.StatusChanged += msg => Dispatcher.BeginInvoke(() => SetStatus(msg));
            _call.ParticipantsChanged += list => Dispatcher.BeginInvoke(() => UpdateParticipants(list));
            _call.RemoteScreenFrame += OnRemoteScreenFrame;
            _call.PeerLeft += id => Dispatcher.BeginInvoke(() => RemoveScreen(id));

            _displayName = Participant.CleanName(NameInput.Text) ?? "";
            NameInput.Text = _displayName;
            await _call.JoinAsync(server.ToString(), room, _displayName);
            UpdateParticipants([]);
            _joinedServer = server;

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
        _participants = [];
        _participantItems.Clear();
        SetParticipantCount(0);
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
        ApplyMicState();
        UpdatePttIndicator();
        SetStatus(muted ? "Microfone silenciado."
            : _pushToTalk ? $"Microfone ativado. Segure {KeyNames.For(_pttKey)} para falar."
            : "Microfone ativado.");
    }

    // ---------- Modo de entrada (detecção de voz / aperte para falar) ----------

    /// <summary>Silenciado tem prioridade; no aperte para falar o microfone só abre com a tecla segurada.</summary>
    private bool MicClosed => _isMuted || (_pushToTalk && !_pttHeld);

    // Chamado também pela thread do hook: só lê campos e grava um bool volátil no AudioEngine.
    private void ApplyMicState()
    {
        var audio = _audio;
        if (audio is not null) audio.IsMuted = MicClosed;
    }

    private void InputModeSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool ptt = InputModeSelect.SelectedIndex == 1;
        if (ptt == _pushToTalk && (_keyboardHook is not null || !ptt)) return;
        // Na abertura do app (antes de carregar a janela) não anuncia nada.
        SetInputMode(ptt, announce: IsLoaded);
    }

    private void SetInputMode(bool pushToTalk, bool announce)
    {
        if (pushToTalk && !EnsureKeyboardHook())
        {
            SetStatus("Não foi possível monitorar o teclado; o aperte para falar não está disponível.");
            InputModeSelect.SelectedIndex = 0;
            return;
        }

        _pushToTalk = pushToTalk;
        _pttHeld = false;
        _capturingPttKey = false;
        if (!pushToTalk)
        {
            _keyboardHook?.Dispose();
            _keyboardHook = null;
        }
        UpdatePttKeyButton();
        BtnPttKey.Visibility = pushToTalk ? Visibility.Visible : Visibility.Collapsed;
        ApplyMicState();
        UpdatePttIndicator();

        _prefs.PushToTalk = pushToTalk;
        _prefs.Save();
        if (announce)
        {
            SetStatus(pushToTalk
                ? $"Modo aperte para falar: segure {KeyNames.For(_pttKey)} para falar, em qualquer janela."
                : "Modo detecção de voz: o microfone transmite enquanto você fala.");
        }
    }

    private bool EnsureKeyboardHook()
    {
        if (_keyboardHook is not null) return true;
        try
        {
            _keyboardHook = new GlobalKeyboardHook { KeyDown = OnGlobalKeyDown, KeyUp = OnGlobalKeyUp };
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Thread do hook: precisa responder rápido (o Windows espera a cada tecla), a UI vai por BeginInvoke.
    private bool OnGlobalKeyDown(int vk, bool repeat)
    {
        if (_capturingPttKey)
        {
            if (repeat) return true;
            _capturingPttKey = false;
            Dispatcher.BeginInvoke(() => FinishPttKeyCapture(vk));
            return true; // engole a tecla escolhida para ela não acionar nada na janela
        }

        if (_pushToTalk && vk == _pttKey && !_pttHeld)
        {
            _pttHeld = true;
            ApplyMicState();
            Dispatcher.BeginInvoke(UpdatePttIndicator);
        }
        return false; // a tecla continua chegando normalmente ao programa em foco
    }

    private void OnGlobalKeyUp(int vk)
    {
        if (vk != _pttKey || !_pttHeld) return;
        _pttHeld = false;
        ApplyMicState();
        Dispatcher.BeginInvoke(UpdatePttIndicator);
    }

    private void BtnPttKey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingPttKey || !EnsureKeyboardHook()) return;
        _pttHeld = false;
        ApplyMicState();
        BtnPttKey.Content = "Pressione a nova tecla (Esc cancela)";
        SetStatus("Pressione a tecla que você quer segurar para falar. Esc cancela.");
        _capturingPttKey = true;
    }

    private void FinishPttKeyCapture(int vk)
    {
        if (vk == KeyNames.Escape)
        {
            UpdatePttKeyButton();
            SetStatus($"Escolha cancelada. A tecla para falar continua sendo {KeyNames.For(_pttKey)}.");
            return;
        }

        _pttKey = vk;
        _prefs.PushToTalkKey = vk;
        _prefs.Save();
        UpdatePttKeyButton();
        UpdatePttIndicator();
        SetStatus($"Tecla para falar: {KeyNames.For(vk)}.");
    }

    private void UpdatePttKeyButton()
    {
        // O sublinhado é a tecla de acesso; no nome de uma tecla ele seria lido como acesso, então é dobrado.
        BtnPttKey.Content = $"Tecla para _falar: {KeyNames.For(_pttKey).Replace("_", "__")}";
    }

    private void UpdatePttIndicator()
    {
        bool show = _pushToTalk && _call is not null;
        PttIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        if (_isMuted)
        {
            PttIndicator.Text = "Microfone silenciado.";
            PttIndicator.Foreground = IdleBrush;
        }
        else if (_pttHeld)
        {
            PttIndicator.Text = "● Transmitindo";
            PttIndicator.Foreground = TalkingBrush;
        }
        else
        {
            PttIndicator.Text = $"Microfone fechado. Segure {KeyNames.For(_pttKey)} para falar.";
            PttIndicator.Foreground = IdleBrush;
        }
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

    // Troca a saída de áudio durante a chamada; se o dispositivo falhar, a seleção volta ao anterior.
    private void SpeakerSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audio is null || _syncingSpeakers || SpeakerSelect.SelectedItem is not SpeakerItem speaker) return;
        try
        {
            _audio.ChangePlaybackDevice(speaker.DeviceNumber);
            SetStatus($"Ouvindo em: {speaker.Name}.");
        }
        catch (Exception ex)
        {
            SetStatus("Erro ao trocar o dispositivo de saída: " + ex.Message);
            _syncingSpeakers = true;
            try
            {
                SpeakerSelect.SelectedItem = SpeakerSelect.Items
                    .Cast<SpeakerItem>()
                    .FirstOrDefault(s => s.DeviceNumber == _audio.PlaybackDeviceNumber) ?? SpeakerSelect.SelectedItem;
            }
            finally
            {
                _syncingSpeakers = false;
            }
            SpeakerSelect.Focus();
        }
    }

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
                screen = new RemoteScreen(peerId, LabelFor(peerId));
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
        NameInput.IsEnabled = !joining && !inCall;
        ServerInput.IsEnabled = !joining && !inCall;
        BtnMute.IsEnabled = inCall;
        BtnShare.IsEnabled = inCall;
        BtnRecord.IsEnabled = inCall;
        BtnCopyLink.IsEnabled = inCall;
        BtnLeave.IsEnabled = inCall;
        SetToggle(BtnMute, _isMuted);
        if (!inCall)
        {
            SetToggle(BtnShare, false);
            SetToggle(BtnRecord, false);
        }
        UpdatePttIndicator();
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

    /// <summary>
    /// Atualiza a lista "Na sala" (você primeiro, depois os conectados) e anuncia quem entrou ou saiu.
    /// </summary>
    private void UpdateParticipants(IReadOnlyList<Participant> list)
    {
        if (_call is null) return;
        var previous = _participants;
        _participants = list.ToDictionary(p => p.Id);

        foreach (var p in list.Where(p => p.Connected && !(previous.TryGetValue(p.Id, out var old) && old.Connected)))
            SetStatus($"{p.DisplayLabel} entrou na chamada.");
        foreach (var old in previous.Values.Where(o => o.Connected && !(_participants.TryGetValue(o.Id, out var p) && p.Connected)))
            SetStatus($"{old.DisplayLabel} saiu da chamada.");

        _participantItems.Clear();
        _participantItems.Add(_displayName.Length > 0 ? $"{_displayName} (você)" : "Você");
        foreach (var p in list.Where(p => p.Connected).OrderBy(p => p.DisplayLabel, StringComparer.CurrentCultureIgnoreCase))
            _participantItems.Add(p.DisplayLabel);

        foreach (var screen in _screens) screen.Owner = LabelFor(screen.PeerId);
        SetParticipantCount(list.Count(p => p.Connected));
    }

    private string LabelFor(string peerId) =>
        _participants.TryGetValue(peerId, out var p) ? p.DisplayLabel : new Participant(peerId, null, false).DisplayLabel;

    private void SetParticipantCount(int count)
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
    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed record MicItem(int DeviceNumber, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record SpeakerItem(int DeviceNumber, string Name)
    {
        public override string ToString() => Name;
    }
}
