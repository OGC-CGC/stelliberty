using Stelliberty.Domain.Connections;

namespace Stelliberty.Presentation.ViewModels;

public sealed record ConnectionFilterMenuItemViewModel(
    ConnectionFilterLevel Level,
    string DisplayName,
    bool IsSelected) : ICardMenuItemViewModel
{
    public string IconType => IsSelected ? "CheckCircleLine" : Level switch
    {
        ConnectionFilterLevel.Direct => "ArrowRightLine",
        ConnectionFilterLevel.Proxy => "SendPlaneLine",
        _ => "ListOrderedLine"
    };

    public string AutomationId => $"Connections.RouteFilter.{Level}";

    public bool IsDanger => false;
}
