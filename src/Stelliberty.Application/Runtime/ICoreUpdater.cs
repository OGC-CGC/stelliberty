namespace Stelliberty.Application.Runtime;

public enum CoreUpdateStatus
{
    Updated,
    UpToDate,
    Failed,
}

public sealed record CoreUpdateResult(CoreUpdateStatus Status, string? Version, string Message);

public enum CoreUpdateStage
{
    Checking,
    Downloading,
    Installing,
    Restarting,
}

public readonly record struct CoreUpdateProgress(CoreUpdateStage Stage, int? DownloadPercent = null);

public interface ICoreUpdater
{
    Task<CoreUpdateResult> UpdateAsync(CancellationToken cancellationToken = default);

    Task<CoreUpdateResult> UpdateAsync(CancellationToken cancellationToken, IProgress<CoreUpdateProgress>? progress)
        => UpdateAsync(cancellationToken);
}
