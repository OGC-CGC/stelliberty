using System.Text.Json;
using Stelliberty.Application.Cli;
using Stelliberty.Application.Platform;
using Stelliberty.Infrastructure.Tray;
using Xunit;

namespace Stelliberty.Settings.Tests;

public sealed class CliIpcTests
{
    [Fact]
    public async Task ExecutesOnTrayConnectionWithoutUiPreparation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "st-cli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var endpoint = OperatingSystem.IsWindows() ? "st-cli-" + Guid.NewGuid().ToString("N") : Path.Combine(directory, "ipc.sock");
        var methods = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var server = new TrayIpcServer(endpoint, (_, request, _) =>
            {
                methods.Add(request.Method);
                return Task.FromResult(request.Method switch
                {
                    TrayProtocol.HelloMethod => TrayIpcResult.Success(new TrayHello(
                        TrayProtocol.Version, AppMetadata.Version, 1, "test", [], 0)),
                    TrayProtocol.CliExecuteMethod => TrayIpcResult.Success(JsonSerializer.SerializeToElement(
                        new CliResponse(true, "ok", new { mode = "rule" }), CliCatalog.JsonOptions)),
                    _ => TrayIpcResult.Error("unexpected", "No UI methods are supported by this server.")
                });
            }, _ => Task.CompletedTask);
            server.Start(timeout.Token);
            await using var client = new TrayIpcClient(endpoint);
            await client.ConnectAsync(timeout.Token);
            await client.HelloAsync(Environment.ProcessId, timeout.Token);
            var response = await client.ExecuteCliAsync(new(["proxy", "mode", "get"]), timeout.Token);
            Assert.True(response.GetProperty("success").GetBoolean());
            Assert.Equal(new[] { TrayProtocol.HelloMethod, TrayProtocol.CliExecuteMethod }, methods);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
