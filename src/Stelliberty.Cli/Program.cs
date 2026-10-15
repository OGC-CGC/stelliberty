using System.Net.Sockets;
using System.Text.Json;
using Stelliberty.Application.Cli;
using Stelliberty.Application.Platform;
using Stelliberty.Infrastructure.Core;
using Stelliberty.Infrastructure.Tray;

namespace Stelliberty.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--version"])
            {
                Console.WriteLine(AppMetadata.Version);
                return 0;
            }
            if (args.Length == 0 || args[0] == "help" || args.Contains("--help") || args.Contains("-h"))
            {
                var topic = args.Where(arg => arg is not "--help" and not "-h").ToArray();
                if (topic.Length > 0 && topic[0] == "help") topic = topic[1..];
                Console.WriteLine(CliCommands.Help(topic));
                return 0;
            }
            if (args.Contains("--stdin"))
            {
                if (args.Count(arg => arg == "--stdin") != 1) throw new ArgumentException("Duplicate --stdin.");
                var index = Array.IndexOf(args, "--stdin");
                // --stdin occupies the value position of settings/dns/proxy config set.
                if (!(index == 3 && (args[0] is "settings" or "dns") && args[1] == "set")
                    && !(index == 4 && args.Take(3).SequenceEqual(new[] { "proxy", "config", "set" })))
                    throw new ArgumentException("--stdin must replace the value of a setting command.");
                var input = (await Console.In.ReadToEndAsync()).TrimEnd('\r', '\n');
                args = [.. args[..index], .. args[(index + 1)..], "--", input];
            }
            var command = CliCommands.Parse(args);
            // Resolve local paths before sending; the UI's working directory may differ.
            if (command.Command == "settings set" && command.Values[0] == "background-image"
                && command.Values[1].Length > 0 && command.Values[1] != "builtin:default")
                RequireAbsolutePath(command.Values[1]);
            if (command.Command == "subscription add" && command.Options.TryGetValue("file", out var file)) RequireAbsolutePath(file);

            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var tray = new TrayIpcClient();
            try
            {
                await tray.ConnectAsync(startup.Token);
                await tray.HelloAsync(Environment.ProcessId, startup.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                return Fail(4, "unavailable", "The background tray is unavailable. Start the matching installed app first.");
            }
            using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var result = await tray.ExecuteCliAsync(new CliRequest(args), operation.Token);
            var response = result.Deserialize<CliResponse>(CliCatalog.JsonOptions)
                ?? throw new JsonException("Empty CLI response.");
            Console.WriteLine(result.GetRawText());
            return response.Success ? 0 : response.Status switch
            {
                "invalid_arguments" => 2,
                "disabled" => 3,
                "unavailable" => 4,
                "timeout" => 6,
                _ => 5
            };
        }
        catch (ArgumentException exception) { return Fail(2, "invalid_arguments", exception.Message); }
        catch (IpcRemoteException exception) { return Fail(exception.Code == "cli.disabled" ? 3 : 4, exception.Code, exception.Code == "cli.disabled" ? CliCatalog.Prerequisites : "IPC rejected the request. Check that CLI and application versions match."); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        { return Fail(6, "timeout", "Timed out. The operation may still complete; inspect current state before retrying."); }
        catch (Exception exception) when (exception is IOException or SocketException or TrayProtocolMismatchException)
        { return Fail(4, "unavailable", "Start the matching installed app under the same user, then enable command-line control. If a request was already sent, inspect state before retrying."); }
        catch (Exception) { return Fail(5, "operation_failed", "CLI failed. No automatic retry was performed."); }
    }

    private static void RequireAbsolutePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("File paths must be absolute.");
    }

    private static int Fail(int exitCode, string status, string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new CliResponse(false, status, Error: message), CliCatalog.JsonOptions));
        return exitCode;
    }
}
