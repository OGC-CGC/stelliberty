namespace SBproxy.Application.Diagnostics;

public interface IAppLogExporter
{
    Task ExportAsync(string exportPath, CancellationToken cancellationToken = default);
}
