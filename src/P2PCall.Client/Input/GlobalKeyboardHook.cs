using System.ComponentModel;
using System.Runtime.InteropServices;

namespace P2PCall.Client.Input;

/// <summary>
/// Hook de teclado global (WH_KEYBOARD_LL): recebe as teclas de qualquer janela, para o
/// "aperte para falar" funcionar mesmo com o aplicativo em segundo plano.
/// Roda numa thread própria com laço de mensagens: se a thread da UI travar, o teclado
/// do Windows não fica lento (o sistema espera o hook a cada tecla).
/// Os eventos são disparados nessa thread — quem assina precisa ser rápido e despachar para a UI.
/// </summary>
internal sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint WM_QUIT = 0x12;
    private const int VK_LCONTROL = 0xA2;
    // O AltGr (teclado ABNT2) gera um Ctrl esquerdo "falso" com este código de varredura.
    private const uint AltGrFakeCtrlScanCode = 0x21D;

    private readonly HookProc _proc; // referência mantida para o GC não coletar o delegate
    private readonly Thread _thread;
    private readonly HashSet<int> _down = [];
    private uint _threadId;
    private int _error;

    /// <summary>Tecla pressionada (código virtual, é repetição automática?). Retornar true engole a tecla.</summary>
    public Func<int, bool, bool>? KeyDown { get; set; }

    /// <summary>Tecla solta (código virtual).</summary>
    public Action<int>? KeyUp { get; set; }

    public GlobalKeyboardHook()
    {
        _proc = HookCallback;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Hook de teclado" };
        _thread.Start();
        ready.Wait();
        if (_error != 0) throw new Win32Exception(_error, "Não foi possível monitorar o teclado.");
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        // Garante a fila de mensagens antes de liberar o construtor (Dispose posta WM_QUIT nela).
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) _error = Marshal.GetLastWin32Error();
        ready.Set();
        if (hook == IntPtr.Zero) return;

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnhookWindowsHookEx(hook);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            int vk = (int)info.VkCode;
            int message = (int)wParam;
            bool fakeCtrl = vk == VK_LCONTROL && info.ScanCode == AltGrFakeCtrlScanCode;

            if (!fakeCtrl)
            {
                try
                {
                    if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                    {
                        bool repeat = !_down.Add(vk);
                        if (KeyDown?.Invoke(vk, repeat) == true) return 1;
                    }
                    else if (message is WM_KEYUP or WM_SYSKEYUP)
                    {
                        _down.Remove(vk);
                        KeyUp?.Invoke(vk);
                    }
                }
                catch
                {
                    // Uma exceção aqui derrubaria o processo sem passar a tecla adiante.
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
