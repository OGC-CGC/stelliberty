using System.Windows.Input;
using Stelliberty.Domain.Connections;

namespace Stelliberty.Presentation.ViewModels;

public sealed record ConnectionSortMenuItemViewModel(
    ConnectionSortMode Mode,
    string DisplayName,
    bool IsSelected,
    ICommand SelectCommand);
