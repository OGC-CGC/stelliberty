# Agent command-line control

[English](CLI.md) | [简体中文](CLI.zh-CN.md)

`stelliberty-cli` ships with every build and is disabled by default. Enable it manually in the application under Settings → App behavior; importing a backup does not change the enabled state on this machine. Once disabled, new commands are rejected while operations that already started may still finish.

## Windows
The executable is located at `tweaks/cli/stelliberty-cli.exe`
## MacOS
The executable is located at `Contents/MacOS/tweaks/cli/stelliberty-cli`. When command-line control is enabled, the application adds the CLI path to `~/.profile`.
## Linux
The executable is located at `tweaks/cli/stelliberty-cli`.

## CLI usage
Below, `stelliberty-cli` stands for that executable. Command and argument names are case-sensitive; enum values are not. Every command and every configuration field has offline help:

```sh
stelliberty-cli --help
stelliberty-cli help proxy
stelliberty-cli proxy select --help
stelliberty-cli settings set --help
stelliberty-cli help settings set theme
stelliberty-cli help proxy config set mixed-port
stelliberty-cli help dns set nameservers
stelliberty-cli subscription delete --help
stelliberty-cli --version
```

Help contains purpose, syntax, examples, prerequisites and return-value notes; field help also contains the type, default value, allowed values or range. Help and version queries need neither a running application nor authorization; all other commands require the CLI to be enabled and the matching application version running under the same system user.

## Configuration and pre-checks

```sh
stelliberty-cli settings schema
stelliberty-cli settings get
stelliberty-cli settings set theme dark --dry-run
stelliberty-cli settings set theme dark
stelliberty-cli settings set title-bar-fps false
stelliberty-cli settings set minimize-to-tray true
stelliberty-cli settings set auto-start true
stelliberty-cli settings set webdav-url 'https://dav.example.com/remote.php/dav/files/user'
stelliberty-cli proxy config set external-controller-address 127.0.0.1:9090
stelliberty-cli proxy config schema
stelliberty-cli proxy config set mixed-port 7890
stelliberty-cli proxy config set socks-port 0
stelliberty-cli dns schema
stelliberty-cli dns set override true
stelliberty-cli dns set nameservers '["1.1.1.1","https://dns.google/dns-query"]'
```

`settings` covers language, theme, accent color, window effect, background, tray behavior, start at login, lightweight mode, update source, WebDAV backups and update settings. `proxy config` covers ports, LAN, authentication, IPv6, delay tests, TUN and the persistent TUN switch, the default outbound mode, system proxy host/bypass/PAC, the external controller and core performance and log-level parameters; `dns` covers the application's DNS settings. Run `schema` for the complete field catalog. Optional SOCKS/HTTP/redirect ports are disabled with `0`; the mixed port must be 1–65535; WebDAV backups run every 1–24 hours and keep 1–5 copies. Window effects are limited by the current platform; enabling TUN requires the installed privileged service or administrator mode and takes effect while the core runs. Start at login writes or removes the system login item. While DNS override is off, saved DNS parameters do not override the subscription configuration.

Write commands support `--dry-run`, which validates arguments and returns the intended values without saving configuration, downloading subscriptions or running delay tests. A pre-check cannot guarantee that network requests, the system proxy or the core will actually apply the change. Configuration changes reuse the application's validation and save path; core-related changes wait for the existing runtime refresh. One field per call, with no batch transaction and no automatic rollback.

Sensitive values can be passed through standard input so they stay out of the shell history:

```sh
stelliberty-cli proxy config set lan-password --stdin
stelliberty-cli dns set nameservers --stdin --dry-run
```

Write the content to stdin and end the input; a trailing newline is removed. Arrays use JSON string arrays where each element represents one row in the UI. Files must be absolute paths. Passwords, authentication user names, background paths, test URLs and DNS array queries are redacted; subscription queries do not return source addresses or paths.

## Proxy and subscriptions

```sh
stelliberty-cli proxy groups
stelliberty-cli proxy nodes --group '节点选择'
stelliberty-cli proxy select --group '节点选择' --node '香港 01' --dry-run
stelliberty-cli proxy select --group '节点选择' --node '香港 01'
stelliberty-cli proxy delay --group '节点选择' --node '香港 01'
stelliberty-cli proxy mode get
stelliberty-cli proxy mode set rule
stelliberty-cli proxy system set true
stelliberty-cli proxy tun set true
stelliberty-cli subscription list
stelliberty-cli subscription add --name Example --file '/absolute/path/config.yaml'
stelliberty-cli subscription add --name Example --url 'https://example.com/subscription'
stelliberty-cli subscription update SUBSCRIPTION_ID
stelliberty-cli subscription select SUBSCRIPTION_ID
stelliberty-cli subscription delete SUBSCRIPTION_ID --dry-run
```

Group and node names must match exactly and may contain non-ASCII characters; only groups that support manual selection can be switched. The node list is not filtered by the UI search box. Group and node operations, mode switching and enabling the system proxy require an interactive running core; TUN additionally requires the application's existing service/administrator conditions. The CLI does not install services or elevate privileges. Delay results are returned per node as `delay_ms` and `delay_state`; a finished test does not mean every node is reachable.

Subscription add follows the application's rules: when there is no current subscription the new one is selected automatically, otherwise the current selection is kept. Scheduled updates and automatic delay tests are disabled by default for new subscriptions. The update command only applies to remote subscriptions. Deleting the current subscription clears its runtime and does not select another subscription. All changes use the application's existing flows.

## Agent response contract

Except for help and version, stdout returns JSON: `success`, `status`, `data`, `error`. Status `ok` means a query completed, `dry_run` means a pre-check completed, `saved` means the value was saved, `applied` means a runtime operation completed, and `saved_not_applied` means the value was saved but runtime application failed. Without a running core, saved configuration does not mean the network behavior actually changed.

| Exit code | Meaning |
| --- | --- |
| 0 | Success, help or version |
| 2 | Invalid arguments |
| 3 | The user has not enabled the CLI |
| 4 | Application/core unavailable, version mismatch or unsupported operation conditions |
| 5 | Operation failed, including a failed runtime apply after saving |
| 6 | Timeout, final outcome unknown |

The startup connection waits about 30 seconds and an operation waits up to about 180 seconds. After a timeout the operation may still complete; query the state before deciding to retry. The CLI never retries write operations automatically. It communicates over the current user's local named pipe or Unix socket and never listens on a network port; enabling it means other processes under the same user can also call these commands.
