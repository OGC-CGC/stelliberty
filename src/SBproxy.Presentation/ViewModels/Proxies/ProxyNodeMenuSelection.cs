namespace SBproxy.Presentation.ViewModels;

public enum ProxyNodeMenuAction
{
    Information,
    Share,
}

public sealed record ProxyNodeMenuSelection(
    string NodeName,
    ProxyNodeMenuAction Action,
    string DisplayName) : ICardMenuItemViewModel
{
    public string IconType => Action switch
    {
        ProxyNodeMenuAction.Information => "InformationLine",
        ProxyNodeMenuAction.Share => "Share3Line",
        _ => "More2Line",
    };

    public string AutomationId => $"Proxy.Node.{NodeName}.Menu.{Action}";

    public bool IsDanger => false;
}
