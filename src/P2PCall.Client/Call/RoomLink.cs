using System.Web;

namespace P2PCall.Client.Call;

/// <summary>
/// Links de sala no mesmo formato da versão web (<c>https://servidor/?sala=nome</c>), assim um link
/// copiado aqui abre no navegador e um link copiado do navegador funciona aqui.
/// </summary>
public static class RoomLink
{
    private const string RoomParameter = "sala";

    public static string Build(Uri server, string room) =>
        $"{server.GetLeftPart(UriPartial.Authority)}/?{RoomParameter}={Uri.EscapeDataString(room)}";

    /// <summary>Reconhece um link de sala (com ou sem "https://") e devolve o servidor e a sala.</summary>
    public static bool TryParse(string text, out string server, out string room)
    {
        server = room = "";
        text = text.Trim();
        if (!text.Contains(RoomParameter + "=", StringComparison.OrdinalIgnoreCase)) return false;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            // Link colado sem o esquema, ex.: voz.tempsharing.com.br/?sala=amigos
            if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out uri)) return false;
        }
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return false;

        // Mesmo comportamento do URLSearchParams.get("sala") do client.js ("+" vira espaço).
        var value = HttpUtility.ParseQueryString(uri.Query)[RoomParameter]?.Trim();
        if (string.IsNullOrEmpty(value)) return false;

        server = uri.GetLeftPart(UriPartial.Authority);
        room = value;
        return true;
    }
}
