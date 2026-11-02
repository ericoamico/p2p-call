using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace P2PCall.Client.Controls;

// Com tecla de acesso ("_Sala"), o WPF expõe ao leitor de tela, além do nome limpo do controle,
// um filho de texto com o sublinhado ("_Sala"), que acaba sendo lido em duplicidade.
// Estes controles escondem esse filho; o nome do controle continua vindo do conteúdo.

public class AccessibleLabel : Label
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(Label owner) : LabelAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

public class AccessibleButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(Button owner) : ButtonAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

public class AccessibleToggleButton : ToggleButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(ToggleButton owner) : ToggleButtonAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
