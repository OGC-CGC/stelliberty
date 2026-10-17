using System.Collections.ObjectModel;
using Stelliberty.Application.Platform;
using Stelliberty.Presentation.Commands;

namespace Stelliberty.Presentation.ViewModels;

public sealed partial class HomePageViewModel
{
    private INetworkDiagnosticsService? _networkDiagnostics;
    private bool _isTestingConnectivity;
    private bool _isConnectivityEditorOpen;
    private string? _connectivityEditorErrorKey;
    private IReadOnlyList<NetworkDiagnosticCheck> _connectivityConfiguration = [];
    private Action<IReadOnlyList<NetworkDiagnosticCheck>>? _saveConnectivityConfiguration;

    public IReadOnlyList<NetworkDiagnosticItemViewModel> ConnectivityChecks { get; private set; } = [];
    public IReadOnlyList<NetworkDiagnosticItemViewModel> PublicIpChecks { get; private set; } = [];
    public RelayCommand TestConnectivityCommand { get; private set; } = null!;
    public RelayCommand EditConnectivityCommand { get; private set; } = null!;
    public RelayCommand SaveConnectivityCommand { get; private set; } = null!;
    public RelayCommand CancelConnectivityEditCommand { get; private set; } = null!;
    public RelayCommand AddConnectivityCheckCommand { get; private set; } = null!;
    public RelayCommand<ConnectivityCheckEditorItemViewModel> RemoveConnectivityCheckCommand { get; private set; } = null!;
    public RelayCommand<ConnectivityCheckEditorItemViewModel> MoveConnectivityCheckUpCommand { get; private set; } = null!;
    public RelayCommand<ConnectivityCheckEditorItemViewModel> MoveConnectivityCheckDownCommand { get; private set; } = null!;
    public ObservableCollection<ConnectivityCheckEditorItemViewModel> ConnectivityEditorItems { get; } = [];
    public bool IsConnectivityEditorOpen => _isConnectivityEditorOpen;
    public string ConnectivityEditorError => _connectivityEditorErrorKey is { } key ? Localize(key) : string.Empty;
    public bool HasConnectivityEditorError => _connectivityEditorErrorKey is not null;
    public bool CanTestConnectivity => !_isDisposed && !_isTestingConnectivity && !IsConnectivityEditorOpen && _networkDiagnostics is not null;

    private void InitializeConnectivity(
        INetworkDiagnosticsService? networkDiagnostics,
        IReadOnlyList<NetworkDiagnosticCheck>? checks,
        Action<IReadOnlyList<NetworkDiagnosticCheck>>? saveConfiguration)
    {
        _networkDiagnostics = networkDiagnostics;
        _saveConnectivityConfiguration = saveConfiguration;
        ApplyConnectivityConfiguration(checks ?? NetworkDiagnosticCheck.CreateDefaults());
        PublicIpChecks =
        [
            new(NetworkDiagnosticTarget.Ipinfo, "IPinfo.io", "IP", _localization),
            new(NetworkDiagnosticTarget.CloudflareIp, "Cloudflare", "IP", _localization)
        ];
        TestConnectivityCommand = new RelayCommand(() => _ = TestConnectivityAsync(), () => CanTestConnectivity);
        EditConnectivityCommand = new RelayCommand(OpenConnectivityEditor, () => !_isDisposed && !_isTestingConnectivity && !IsConnectivityEditorOpen);
        SaveConnectivityCommand = new RelayCommand(SaveConnectivityEditor);
        CancelConnectivityEditCommand = new RelayCommand(() => SetConnectivityEditorOpen(false));
        AddConnectivityCheckCommand = new RelayCommand(() => ConnectivityEditorItems.Add(new(
            new(Guid.NewGuid().ToString("N"), string.Empty, "https://"))));
        RemoveConnectivityCheckCommand = new RelayCommand<ConnectivityCheckEditorItemViewModel>(item =>
        {
            if (item is not null)
            {
                ConnectivityEditorItems.Remove(item);
            }
        });
        MoveConnectivityCheckUpCommand = new RelayCommand<ConnectivityCheckEditorItemViewModel>(item => MoveConnectivityCheck(item, -1));
        MoveConnectivityCheckDownCommand = new RelayCommand<ConnectivityCheckEditorItemViewModel>(item => MoveConnectivityCheck(item, 1));
    }

    private void ApplyConnectivityConfiguration(IReadOnlyList<NetworkDiagnosticCheck> checks)
    {
        _connectivityConfiguration = checks;
        ConnectivityChecks = checks.Where(check => check.IsEnabled).Select(check => new NetworkDiagnosticItemViewModel(
            NetworkDiagnosticTarget.Custom, check.Name,
            check.TestType switch
            {
                NetworkDiagnosticTestType.Tcp => "TCP",
                NetworkDiagnosticTestType.Ssh => "SSH",
                _ => check.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "HTTP" : "HTTPS"
            },
            _localization, check.Url, check.Id, check.TestType)).ToArray();
        OnPropertyChanged(nameof(ConnectivityChecks));
    }

    private void OpenConnectivityEditor()
    {
        ConnectivityEditorItems.Clear();
        foreach (var check in _connectivityConfiguration)
        {
            ConnectivityEditorItems.Add(new(check));
        }

        SetConnectivityEditorError(null);
        SetConnectivityEditorOpen(true);
    }

    private void SetConnectivityEditorOpen(bool isOpen)
    {
        if (SetProperty(ref _isConnectivityEditorOpen, isOpen, nameof(IsConnectivityEditorOpen)))
        {
            TestConnectivityCommand.RaiseCanExecuteChanged();
            EditConnectivityCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanTestConnectivity));
        }
    }

    private void SetConnectivityEditorError(string? key)
    {
        _connectivityEditorErrorKey = key;
        OnPropertyChanged(nameof(ConnectivityEditorError));
        OnPropertyChanged(nameof(HasConnectivityEditorError));
    }

    private void MoveConnectivityCheck(ConnectivityCheckEditorItemViewModel? item, int direction)
    {
        var index = item is null ? -1 : ConnectivityEditorItems.IndexOf(item);
        var destination = index + direction;
        if (index >= 0 && destination >= 0 && destination < ConnectivityEditorItems.Count)
        {
            ConnectivityEditorItems.Move(index, destination);
        }
    }

    private void SaveConnectivityEditor()
    {
        if (ConnectivityEditorItems.Any(item => string.IsNullOrWhiteSpace(item.Name)))
        {
            SetConnectivityEditorError("Home.Connectivity.NameRequired");
            return;
        }

        if (ConnectivityEditorItems.Any(item => item.TestType == NetworkDiagnosticTestType.Http
            && !NetworkDiagnosticAddress.IsHttpUrl(item.Url)))
        {
            SetConnectivityEditorError("Home.Connectivity.InvalidUrl");
            return;
        }

        if (ConnectivityEditorItems.Any(item => item.TestType is NetworkDiagnosticTestType.Tcp or NetworkDiagnosticTestType.Ssh
            && !NetworkDiagnosticAddress.TryParseSocketAddress(item.Url, out _, out _)))
        {
            SetConnectivityEditorError("Home.Connectivity.InvalidSocketAddress");
            return;
        }

        var checks = ConnectivityEditorItems.Select(item => item.ToCheck()).ToArray();
        try
        {
            _saveConnectivityConfiguration?.Invoke(checks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetConnectivityEditorError("Home.Connectivity.SaveFailed");
            return;
        }

        ApplyConnectivityConfiguration(checks);
        SetConnectivityEditorOpen(false);
    }

    private async Task TestConnectivityAsync()
    {
        if (!CanTestConnectivity || _refreshCancellation is null)
        {
            return;
        }

        _isTestingConnectivity = true;
        TestConnectivityCommand.RaiseCanExecuteChanged();
        EditConnectivityCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanTestConnectivity));
        var items = ConnectivityChecks.Concat(PublicIpChecks).ToArray();
        foreach (var item in items)
        {
            item.BeginTest();
        }

        var token = _refreshCancellation.Token;
        try
        {
            await Task.WhenAll(items.Select(async item =>
            {
                var result = await _networkDiagnostics!.TestAsync(item.Target, token, item.Url, item.TestType).ConfigureAwait(false);
                Post(() =>
                {
                    if (!_isDisposed)
                    {
                        item.CompleteTest(result);
                    }
                });
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 关闭时取消测试，不再更新已释放的页面。
        }
        finally
        {
            Post(() =>
            {
                if (!_isDisposed)
                {
                    _isTestingConnectivity = false;
                    TestConnectivityCommand.RaiseCanExecuteChanged();
                    EditConnectivityCommand.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(CanTestConnectivity));
                }
            });
        }
    }
}
