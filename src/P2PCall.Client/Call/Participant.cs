namespace P2PCall.Client.Call;

/// <summary>Participante remoto da sala.</summary>
/// <param name="Name">Nome de exibição; null quando o participante não informou (cliente web ou versão antiga).</param>
public sealed record Participant(string Id, string? Name, bool Connected)
{
    public const int MaxNameLength = 40;

    /// <summary>Nome para mostrar na lista e nos avisos.</summary>
    public string DisplayLabel => Name ?? $"Participante {Id[..Math.Min(4, Id.Length)]}";

    /// <summary>
    /// Normaliza um nome digitado ou recebido de outro participante: sem caracteres de controle
    /// nem espaços repetidos, com no máximo <see cref="MaxNameLength"/> caracteres.
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var cleaned = string.Join(' ', new string(name.Where(c => !char.IsControl(c)).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..MaxNameLength].TrimEnd();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
