using System.Text;
using SBproxy.Application.Diagnostics;
using SBproxy.Desktop.Services;
using SBproxy.Infrastructure.Diagnostics;

namespace SBproxy.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppLogger.Configure(new CapturedAppLogger(DesktopApplicationLayout.RunningLogFilePath));
        DependencyDirectoryService.Configure();
        AppRuntime.Run(args);
    }
}
