namespace Stelliberty.Application.Cli;

public sealed record CliCommand(string Path, string Usage, string Description, string Example);

public sealed record CliInvocation(string Command, string[] Values, Dictionary<string, string> Options, bool DryRun)
{
    public string RequireOption(string name) => Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"Missing --{name}.");
}

public static class CliCommands
{
    public static readonly CliCommand[] All =
    [
        new("settings schema", "", "List configuration types, defaults, ranges and examples; requires CLI enabled.", "settings schema"),
        new("settings get", "[key]", "Read supported appearance/behavior/update preferences. No key lists all supported settings.", "settings get theme"),
        new("settings set", "<key> <value> [--dry-run]", "Validate and save one preference through the background service. Use --stdin instead of a value to read stdin.", "settings set theme dark"),
        new("proxy config schema", "", "Describe supported connection and TUN parameters.", "proxy config schema"),
        new("proxy config get", "[key]", "Read connection/TUN settings; credential fields are always redacted.", "proxy config get mixed-port"),
        new("proxy config set", "<key> <value> [--dry-run]", "Save a connection/TUN parameter and await runtime regeneration. Core/OS validation can still reject a saved value.", "proxy config set mixed-port 7890 --dry-run"),
        new("dns schema", "", "Describe DNS settings; list values use JSON string arrays.", "dns schema"),
        new("dns get", "[key]", "Read DNS overrides; DNS URLs and policy/hosts values are redacted because they can contain credentials.", "dns get mode"),
        new("dns set", "<key> <value> [--dry-run]", "Save DNS settings. Set override=true to override subscription DNS. Arrays can be supplied with --stdin.", "dns set nameservers '[\"1.1.1.1\"]'"),
        new("proxy groups", "", "List visible groups and current selections. Requires a running, interactive core.", "proxy groups"),
        new("proxy nodes", "--group <name>", "List nodes and delays in an exact, case-sensitive group. Requires a running core.", "proxy nodes --group '节点选择'"),
        new("proxy select", "--group <name> --node <name> [--dry-run]", "Switch a manually selectable group. Names are exact; runtime selection is awaited and checked.", "proxy select --group '节点选择' --node '香港 01'"),
        new("proxy delay", "--group <name> [--node <name>] [--dry-run]", "Test a node or all nodes in a group. Requires a running core. Failed measurements appear in each node's state; large groups may exceed the CLI timeout.", "proxy delay --group '节点选择' --node '香港 01'"),
        new("proxy mode get", "", "Read current Rule/Global/Direct runtime mode.", "proxy mode get"),
        new("proxy mode set", "<rule|global|direct> [--dry-run]", "Change runtime routing mode. Requires an interactive core and awaits the confirmed state.", "proxy mode set rule"),
        new("proxy system get", "", "Read the current system-proxy state.", "proxy system get"),
        new("proxy system set", "<true|false> [--dry-run]", "Enable or disable system proxy through the application's OS integration. Enable requires a running core.", "proxy system set true"),
        new("proxy tun get", "", "Read TUN state and whether this platform/session permits toggling it.", "proxy tun get"),
        new("proxy tun set", "<true|false> [--dry-run]", "Toggle TUN through the existing runtime apply path. Requires a running core and an installed/running privileged service or administrator session. Does not install or elevate services.", "proxy tun set true"),
        new("subscription list", "", "List subscription IDs, names, local/remote kind and active state. Source URLs, paths and error details are omitted.", "subscription list"),
        new("subscription add", "--name <name> (--url <https-url>|--file <absolute-path>) [--dry-run]", "Import a remote or local subscription; when no subscription is selected, the app selects the new one. New subscriptions have automatic updates/delay tests disabled. --dry-run checks inputs without fetching/importing.", "subscription add --name Example --file '/absolute/path/config.yaml'"),
        new("subscription update", "<id> [--dry-run]", "Update one existing remote subscription and await any selected-subscription runtime refresh. Reports download/update failures.", "subscription update SUBSCRIPTION_ID"),
        new("subscription select", "<id> [--dry-run]", "Select a subscription and await runtime generation/application.", "subscription select SUBSCRIPTION_ID"),
        new("subscription delete", "<id> [--dry-run]", "Delete the subscription using the app's deletion flow. Deleting the active subscription clears its runtime; it does not select another subscription. Review the ID with list or --dry-run first.", "subscription delete SUBSCRIPTION_ID --dry-run")
    ];

    public static CliInvocation Parse(string[] args)
    {
        var entry = All.OrderByDescending(item => item.Path.Length)
            .FirstOrDefault(item => args.Take(item.Path.Split(' ').Length).SequenceEqual(item.Path.Split(' ')))
            ?? throw new ArgumentException("Unknown command. Use --help to list commands.");
        var values = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var dryRun = false;
        var allowed = entry.Path switch
        {
            "proxy nodes" or "proxy delay" or "proxy select" => new[] { "group", "node" },
            "subscription add" => ["name", "url", "file"],
            _ => []
        };
        foreach (var token in args.Skip(entry.Path.Split(' ').Length))
        {
            values.Add(token);
        }
        var positional = new List<string>();
        var literal = false;
        for (var i = 0; i < values.Count; i++)
        {
            var token = values[i];
            if (literal) { positional.Add(token); continue; }
            if (token == "--") { literal = true; continue; }
            if (token == "--dry-run")
            {
                if (dryRun) throw new ArgumentException("Duplicate --dry-run.");
                dryRun = true;
            }
            else if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var key = token[2..];
                if (!allowed.Contains(key) || i + 1 == values.Count || !options.TryAdd(key, values[++i]))
                    throw new ArgumentException($"Unknown, duplicate or incomplete option {token}.");
            }
            else positional.Add(token);
        }
        var count = positional.Count;
        var isSetting = entry.Path.StartsWith("settings ", StringComparison.Ordinal)
            || entry.Path.StartsWith("dns ", StringComparison.Ordinal) || entry.Path.StartsWith("proxy config ", StringComparison.Ordinal);
        var isSet = entry.Path.EndsWith(" set", StringComparison.Ordinal);
        var valid = isSetting && entry.Path.EndsWith(" get", StringComparison.Ordinal) ? count <= 1
            : isSet ? count == (isSetting ? 2 : 1)
            : entry.Path is "subscription update" or "subscription select" or "subscription delete" ? count == 1 : count == 0;
        if (!valid) throw new ArgumentException($"Usage: {entry.Path} {entry.Usage}");
        if (dryRun && !(isSet || entry.Path is "proxy select" or "proxy delay" or "subscription add" or "subscription update" or "subscription select" or "subscription delete"))
            throw new ArgumentException("--dry-run is only available on operations that change state or perform a test.");
        if (entry.Path == "proxy nodes" && options.ContainsKey("node")) throw new ArgumentException("proxy nodes only accepts --group.");
        return new(entry.Path, positional.ToArray(), options, dryRun);
    }

    public static string Help(string[] path)
    {
        var query = string.Join(' ', path);
        foreach (var group in new[] { "settings", "proxy config", "dns" })
        {
            foreach (var verb in new[] { "get", "set" })
            {
                var prefix = $"{group} {verb} ";
                if (query.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var setting = CliCatalog.Find(group, query[prefix.Length..]);
                    return $"{setting.Description}\nUsage: stelliberty-cli {group} {verb} {setting.Name}"
                        + (verb == "set" ? " <value> [--dry-run] (or --stdin)" : "")
                        + "\n" + System.Text.Json.JsonSerializer.Serialize(setting.Schema, CliCatalog.JsonOptions)
                        + $"\n{CliCatalog.Prerequisites}\n{CliCatalog.Results}\n";
                }
            }
        }
        var commands = All.Where(item => query.Length == 0 || item.Path == query || item.Path.StartsWith(query + " ", StringComparison.Ordinal)).ToArray();
        if (commands.Length == 0) throw new ArgumentException("Unknown help topic.");
        var keys = CliCatalog.Settings.Where(item => query == item.Group || query == item.Group + " set" || query == item.Group + " get" || query == item.Group + " schema").ToArray();
        var keyHelp = keys.Length == 0 ? "" : "\n\nAvailable keys (help <command> <key> for type/default/example):\n"
            + string.Join("\n", keys.Select(item => $"  {item.Name}: {item.Description}"));
        return "Stelliberty CLI — optional local configuration tweak\n"
            + string.Join("\n\n", commands.Select(item => $"stelliberty-cli {item.Path} {item.Usage}\n  {item.Description}\n  Example: stelliberty-cli {item.Example}"))
            + keyHelp
            + $"\n\n{CliCatalog.Prerequisites}\n{CliCatalog.Results}\nUse help <command> or <command> --help; --version works without the app.\n";
    }
}
