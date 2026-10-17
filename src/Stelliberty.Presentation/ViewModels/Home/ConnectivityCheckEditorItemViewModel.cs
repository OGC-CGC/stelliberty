using Stelliberty.Application.Platform;

namespace Stelliberty.Presentation.ViewModels;

public sealed class ConnectivityCheckEditorItemViewModel(NetworkDiagnosticCheck check) : ViewModelBase
{
    private string _name = check.Name;
    private string _url = check.Url;
    private bool _isEnabled = check.IsEnabled;
    private NetworkDiagnosticTestType _testType = check.TestType;
    private static readonly IReadOnlyList<SelectionOption<NetworkDiagnosticTestType>> TypeOptions =
    [
        new(NetworkDiagnosticTestType.Http, "HTTP(S)"),
        new(NetworkDiagnosticTestType.Tcp, "TCP"),
        new(NetworkDiagnosticTestType.Ssh, "SSH")
    ];

    public string Id { get; } = check.Id;
    public NetworkDiagnosticTestType TestType => _testType;
    public IReadOnlyList<SelectionOption<NetworkDiagnosticTestType>> TestTypeOptions => TypeOptions;
    public SelectionOption<NetworkDiagnosticTestType> SelectedTestTypeOption
    {
        get => TypeOptions.First(option => option.Value == _testType);
        set
        {
            if (SetProperty(ref _testType, value.Value, nameof(SelectedTestTypeOption)))
            {
                OnPropertyChanged(nameof(TestType));
                OnPropertyChanged(nameof(AddressWatermark));
            }
        }
    }
    public string AddressWatermark => _testType == NetworkDiagnosticTestType.Http ? "https://example.com" : "example.com:22";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Url { get => _url; set => SetProperty(ref _url, value); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    public NetworkDiagnosticCheck ToCheck() => new(Id, Name.Trim(), Url.Trim(), IsEnabled, TestType);
}
