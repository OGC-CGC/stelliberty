using System.Text.Json;
using SBproxy.Application.Settings;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.DataManagement;

public sealed class AgentDataManagementService(string agentPipeName) :
    IDataManagementService,
    IWebDavDataBackupService,
    IDisposable
{
    private readonly JsonRpcPipeClient _client = new(agentPipeName);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DataManagementOperationResult CreateBackup()
        => RequestResult("backups.create", new { });

    public DataManagementOperationResult CreateBackup(string backupPath)
        => RequestResult("backups.create", new { path = backupPath });

    public DataManagementOperationResult RestoreBackup(DataRestoreMode mode)
        => RequestResult("backups.restore", new { mode = mode.ToString() });

    public DataManagementOperationResult RestoreBackup(
        string backupPath,
        DataRestoreMode mode)
        => RequestResult(
            "backups.restore",
            new
            {
                path = backupPath,
                mode = mode.ToString(),
            });

    public Task<DataManagementOperationResult> TestConnectionAsync(
        WebDavBackupSettings settings,
        CancellationToken cancellationToken)
        => RequestResultAsync(
            "backups.webdav.test",
            new { settings = SerializeSettings(settings) },
            cancellationToken);

    public Task<DataManagementOperationResult> CreateBackupAsync(
        WebDavBackupSettings settings,
        CancellationToken cancellationToken)
        => RequestResultAsync(
            "backups.webdav.create",
            new { settings = SerializeSettings(settings) },
            cancellationToken);

    public async Task<IReadOnlyList<RemoteBackupEntry>> ListBackupsAsync(
        WebDavBackupSettings settings,
        CancellationToken cancellationToken)
    {
        var result = await RequestAsync(
            "backups.webdav.list",
            new { settings = SerializeSettings(settings) },
            cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return entries.EnumerateArray()
            .Select(entry => new RemoteBackupEntry(
                entry.GetProperty("file_name").GetString() ?? string.Empty,
                entry.TryGetProperty("size", out var size) && size.TryGetInt64(out var length)
                    ? length
                    : null,
                entry.TryGetProperty("last_modified", out var modified)
                    && DateTimeOffset.TryParse(modified.GetString(), out var timestamp)
                        ? timestamp
                        : null))
            .Where(entry => !string.IsNullOrEmpty(entry.FileName))
            .ToList();
    }

    public Task<DataManagementOperationResult> RestoreBackupAsync(
        WebDavBackupSettings settings,
        string fileName,
        DataRestoreMode mode,
        CancellationToken cancellationToken)
        => RequestResultAsync(
            "backups.webdav.restore",
            new
            {
                settings = SerializeSettings(settings),
                file_name = fileName,
                mode = mode.ToString(),
            },
            cancellationToken);

    public Task<DataManagementOperationResult> RestoreLatestBackupAsync(
        WebDavBackupSettings settings,
        DataRestoreMode mode,
        CancellationToken cancellationToken)
        => RequestResultAsync(
            "backups.webdav.restore",
            new
            {
                settings = SerializeSettings(settings),
                mode = mode.ToString(),
            },
            cancellationToken);

    public Task<DataManagementOperationResult> DeleteBackupAsync(
        WebDavBackupSettings settings,
        string fileName,
        CancellationToken cancellationToken)
        => RequestResultAsync(
            "backups.webdav.delete",
            new
            {
                settings = SerializeSettings(settings),
                file_name = fileName,
            },
            cancellationToken);

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    private DataManagementOperationResult RequestResult(string method, object parameters)
    {
        try
        {
            return RequestResultAsync(method, parameters, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            return new DataManagementOperationResult(false, exception.Message);
        }
    }

    private async Task<DataManagementOperationResult> RequestResultAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RequestAsync(method, parameters, cancellationToken)
                .ConfigureAwait(false);
            return new DataManagementOperationResult(
                result.TryGetProperty("success", out var success) && success.GetBoolean(),
                result.TryGetProperty("message", out var message)
                    ? message.GetString() ?? string.Empty
                    : string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DataManagementOperationResult(false, exception.Message);
        }
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            return await _client.RequestAsync(
                    method,
                    JsonSerializer.SerializeToElement(parameters),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static object SerializeSettings(WebDavBackupSettings settings)
    {
        return new
        {
            url = settings.Url.Trim(),
            remote_directory = settings.RemoteDirectory.Trim(),
            user_name = settings.UserName.Trim(),
            password = settings.Password,
            retention_count = Math.Max(1, settings.RetentionCount),
        };
    }
}
