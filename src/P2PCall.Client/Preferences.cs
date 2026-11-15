using System.IO;
using System.Text.Json;
using P2PCall.Client.Input;

namespace P2PCall.Client;

/// <summary>Preferências lembradas entre execuções (%APPDATA%\P2PCall\preferencias.json).</summary>
internal sealed class Preferences
{
    public bool PushToTalk { get; set; }
    public int PushToTalkKey { get; set; } = KeyNames.LeftCtrl;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "P2PCall", "preferencias.json");

    public static Preferences Load()
    {
        try
        {
            var prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            if (prefs.PushToTalkKey is <= 0 or > 0xFF or KeyNames.Escape) prefs.PushToTalkKey = KeyNames.LeftCtrl;
            return prefs;
        }
        catch
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Sem permissão de escrita: só não lembra na próxima vez.
        }
    }
}
