using Stelliberty.Domain.Connections;

namespace Stelliberty.Application.Connections;

public sealed class ConnectionSorter
{
    public IReadOnlyList<ConnectionInfo> Apply(
        IReadOnlyList<ConnectionInfo> connections,
        ConnectionSortMode mode,
        bool descending,
        DateTimeOffset sampledAt)
    {
        if (mode == ConnectionSortMode.Default)
        {
            return descending ? connections.Reverse().ToList() : connections;
        }

        Func<ConnectionInfo, long> key = mode switch
        {
            ConnectionSortMode.StartTime => connection => connection.Start.UtcTicks,
            ConnectionSortMode.Duration => connection => connection.Start == default
                ? 0
                : Math.Max(0, ((connection.DisconnectedAt ?? sampledAt) - connection.Start).Ticks),
            ConnectionSortMode.UploadSpeed => connection => connection.IsActive ? connection.UploadSpeed : 0,
            ConnectionSortMode.DownloadSpeed => connection => connection.IsActive ? connection.DownloadSpeed : 0,
            ConnectionSortMode.UploadAmount => connection => connection.Upload,
            ConnectionSortMode.DownloadAmount => connection => connection.Download,
            _ => connection => connection.Start.UtcTicks
        };

        var ordered = descending ? connections.OrderByDescending(key) : connections.OrderBy(key);
        return ordered.ThenBy(connection => connection.Id, StringComparer.Ordinal).ToList();
    }
}
