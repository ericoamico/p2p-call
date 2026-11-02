using System.Text.RegularExpressions;

namespace P2PCall.Client.Call;

/// <summary>
/// Ajusta a direção (sendrecv/sendonly/recvonly/inactive) de cada m-line da resposta para ser
/// compatível com a oferta. O SIPSorcery responde "sendrecv" em renegociações mesmo quando o
/// navegador ofereceu "recvonly", e o navegador rejeita a resposta inteira.
/// </summary>
internal static partial class SdpDirections
{
    public static string MatchOffer(string offerSdp, string answerSdp)
    {
        var offerDirections = SplitMedia(offerSdp).Skip(1).Select(DirectionOf).ToList();
        var answerParts = SplitMedia(answerSdp);

        for (int i = 1; i < answerParts.Count && i - 1 < offerDirections.Count; i++)
        {
            var current = DirectionOf(answerParts[i]);
            var allowed = Restrict(current, offerDirections[i - 1]);
            if (allowed == current) continue;

            answerParts[i] = DirectionRegex().IsMatch(answerParts[i])
                ? DirectionRegex().Replace(answerParts[i], "a=" + allowed, 1)
                : answerParts[i].TrimEnd('\r', '\n') + "\r\na=" + allowed + "\r\n";
        }
        return string.Concat(answerParts);
    }

    public static string? GetDtlsSetup(string sdp) =>
        SetupRegex().Match(sdp) is { Success: true } m ? m.Groups[1].Value : null;

    public static string SetDtlsSetup(string sdp, string setup) => SetupRegex().Replace(sdp, "a=setup:" + setup);

    /// <summary>Parte 0 é a sessão; as demais são as seções m=, na ordem.</summary>
    private static List<string> SplitMedia(string sdp) => MediaSplitRegex().Split(sdp).ToList();

    private static string DirectionOf(string section) =>
        DirectionRegex().Match(section) is { Success: true } m ? m.Groups[1].Value : "sendrecv";

    private static string Restrict(string answer, string offer)
    {
        bool send = answer is "sendrecv" or "sendonly";
        bool recv = answer is "sendrecv" or "recvonly";
        // Só posso enviar se o outro lado recebe, e só recebo se o outro lado envia.
        send &= offer is "sendrecv" or "recvonly";
        recv &= offer is "sendrecv" or "sendonly";
        return (send, recv) switch
        {
            (true, true) => "sendrecv",
            (true, false) => "sendonly",
            (false, true) => "recvonly",
            _ => "inactive",
        };
    }

    [GeneratedRegex(@"(?=^m=)", RegexOptions.Multiline)]
    private static partial Regex MediaSplitRegex();

    [GeneratedRegex(@"^a=setup:(\w+)", RegexOptions.Multiline)]
    private static partial Regex SetupRegex();

    [GeneratedRegex(@"^a=(sendrecv|sendonly|recvonly|inactive)\b", RegexOptions.Multiline)]
    private static partial Regex DirectionRegex();
}
