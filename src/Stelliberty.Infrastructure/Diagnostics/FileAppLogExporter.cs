using System.Text;
using Stelliberty.Application.Diagnostics;

namespace Stelliberty.Infrastructure.Diagnostics;

public sealed class FileAppLogExporter(string[] logFilePaths, string[] optionalLogFilePaths) : IAppLogExporter
{
    public FileAppLogExporter(params string[] logFilePaths) : this(logFilePaths, [])
    {
    }

    public async Task ExportAsync(string exportPath, CancellationToken cancellationToken = default)
    {
        var fullExportPath = Path.GetFullPath(exportPath);
        // 导出目标不能占用任何日志路径，包括尚未创建的服务日志。
        if (logFilePaths.Concat(optionalLogFilePaths)
            .Any(path => string.Equals(Path.GetFullPath(path), fullExportPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("The export path must be different from the running log paths.");
        }

        await using var target = new FileStream(
            exportPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        foreach (var logFilePath in logFilePaths)
        {
            await using var source = OpenLog(logFilePath);
            await CopyLogAsync(source, target, cancellationToken).ConfigureAwait(false);
        }

        foreach (var logFilePath in optionalLogFilePaths)
        {
            await using var source = OpenOptionalLog(logFilePath);
            if (source is not null)
            {
                await CopyLogAsync(source, target, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static FileStream OpenLog(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 81920,
        useAsync: true);

    private static FileStream? OpenOptionalLog(string path)
    {
        try
        {
            return OpenLog(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task CopyLogAsync(FileStream source, Stream target, CancellationToken cancellationToken)
    {
        var header = Encoding.UTF8.GetBytes($"===== {Path.GetFileName(source.Name)} ====={Environment.NewLine}");
        await target.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        await target.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine), cancellationToken).ConfigureAwait(false);
    }
}
