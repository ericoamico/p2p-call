using System.Windows.Input;

namespace P2PCall.Client.Input;

/// <summary>Nome em português (lido pelo leitor de tela) de uma tecla pelo código virtual.</summary>
internal static class KeyNames
{
    public const int LeftCtrl = 0xA2;
    public const int Escape = 0x1B;

    private static readonly Dictionary<Key, string> Names = new()
    {
        [Key.LeftCtrl] = "Ctrl esquerdo",
        [Key.RightCtrl] = "Ctrl direito",
        [Key.LeftShift] = "Shift esquerdo",
        [Key.RightShift] = "Shift direito",
        [Key.LeftAlt] = "Alt esquerdo",
        [Key.RightAlt] = "Alt direito",
        [Key.LWin] = "Windows esquerdo",
        [Key.RWin] = "Windows direito",
        [Key.Apps] = "Menu de contexto",
        [Key.Space] = "Espaço",
        [Key.Enter] = "Enter",
        [Key.Tab] = "Tab",
        [Key.Back] = "Backspace",
        [Key.CapsLock] = "Caps Lock",
        [Key.NumLock] = "Num Lock",
        [Key.Scroll] = "Scroll Lock",
        [Key.Pause] = "Pause",
        [Key.Insert] = "Insert",
        [Key.Delete] = "Delete",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.PageUp] = "Page Up",
        [Key.PageDown] = "Page Down",
        [Key.Up] = "Seta para cima",
        [Key.Down] = "Seta para baixo",
        [Key.Left] = "Seta para a esquerda",
        [Key.Right] = "Seta para a direita",
        [Key.PrintScreen] = "Print Screen",
        [Key.Multiply] = "Asterisco do teclado numérico",
        [Key.Add] = "Mais do teclado numérico",
        [Key.Subtract] = "Menos do teclado numérico",
        [Key.Divide] = "Barra do teclado numérico",
        [Key.Decimal] = "Vírgula do teclado numérico",
    };

    public static string For(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        if (Names.TryGetValue(key, out var name)) return name;
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return $"{key - Key.NumPad0} do teclado numérico";
        if (key is >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F24) return key.ToString();
        return key == Key.None ? $"Tecla {virtualKey}" : key.ToString();
    }
}
