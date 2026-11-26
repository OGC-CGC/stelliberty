using Stelliberty.Application.Connections;
using Stelliberty.Domain.Connections;
using Stelliberty.Presentation.Formatting;

namespace Stelliberty.Presentation.ViewModels;

public sealed class ConnectionRowViewModel : ViewModelBase
{
    private ConnectionInfo _connection;
    private DateTimeOffset? _now;
    private bool _displayTrafficInBytes;

    public ConnectionRowViewModel(ConnectionInfo connection, DateTimeOffset? now = null, bool displayTrafficInBytes = false)
    {
        _connection = connection;
        _now = now;
        _displayTrafficInBytes = displayTrafficInBytes;
    }

    // 同 Id 刷新原地更新，保留行实例和虚拟化状态。
    public void Update(ConnectionInfo connection, DateTimeOffset? now)
    {
        _connection = connection;
        _now = now;
        OnPropertyChanged("");
    }

    internal void SetTrafficDisplayUnit(bool displayInBytes)
    {
        _displayTrafficInBytes = displayInBytes;
        OnPropertyChanged(nameof(UploadSpeedText));
        OnPropertyChanged(nameof(DownloadSpeedText));
        OnPropertyChanged(nameof(SpeedText));
    }

    public ConnectionInfo Connection => _connection;

    public string Id => _connection.Id;

    public bool IsActive => _connection.IsActive;

    public string RowAutomationId => $"Connections.Row.{Id}";

    public string CloseAutomationId => $"Connections.Row.{Id}.CloseButton";

    public string NetworkAutomationId => $"Connections.Row.{Id}.NetworkText";

    public string DurationAutomationId => $"Connections.Row.{Id}.DurationText";

    public string ChainAutomationId => $"Connections.Row.{Id}.ChainText";

    public string Description => _connection.Metadata.Description;

    public string DisplayAddress
    {
        get
        {
            var host = _connection.Metadata.DisplayHost;
            var port = _connection.Metadata.DestinationPort;
            if (string.IsNullOrWhiteSpace(host))
            {
                return port;
            }
            return string.IsNullOrWhiteSpace(port) ? host : $"{host}:{port}";
        }
    }

    public string NetworkLabel => _connection.Metadata.Network.ToUpperInvariant();

    public string NetworkPillTag => _connection.Metadata.Network?.ToLowerInvariant() switch
    {
        "tcp" => "tcp",
        "udp" => "udp",
        _ => "neutral"
    };

    public string DurationText => FormatDuration(_connection.Start, _connection.DisconnectedAt ?? _now ?? DateTimeOffset.Now);

    public string Rule => _connection.Rule;

    public string Process => _connection.Metadata.Process;

    public string ProcessPath => _connection.Metadata.ProcessPath;

    public bool IsProcessVisible => !string.IsNullOrWhiteSpace(Process);

    public string UploadSpeedText => $"↑ {TrafficSize.FormatRate(_connection.UploadSpeed, _displayTrafficInBytes)}";

    public string DownloadSpeedText => $"↓ {TrafficSize.FormatRate(_connection.DownloadSpeed, _displayTrafficInBytes)}";

    public string SpeedText => $"{UploadSpeedText}  {DownloadSpeedText}";

    public string UploadAmountText => TrafficSize.FormatAmount(_connection.Upload);

    public string DownloadAmountText => TrafficSize.FormatAmount(_connection.Download);

    public string TrafficText => $"{UploadAmountText} / {DownloadAmountText}";

    public string ChainSummaryText => string.Join(" → ", _connection.RoutePath);

    private static string FormatDuration(DateTimeOffset start, DateTimeOffset now)
    {
        if (start == default)
        {
            return string.Empty;
        }

        var duration = now - start;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.ToString(@"hh\:mm\:ss");
    }
}
