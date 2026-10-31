# Agent 命令行控制

[English](CLI.md) | [简体中文](CLI.zh-CN.md)

`stelliberty-cli` 随所有构建产物附带，默认关闭。可在应用的「设置 → 应用行为」中手动开启，导入备份也不会改变本机的启用状态。关闭后拒绝新命令，已经开始的操作可能继续完成。

## Windows
可执行文件位于 `tweaks/cli/stelliberty-cli.exe`
## MacOS
可执行文件位于 `Contents/MacOS/tweaks/cli/stelliberty-cli`。开启命令行控制时，应用会在 `~/.profile` 添加 CLI 路径。
## Linux
可执行文件位于 `tweaks/cli/stelliberty-cli`。

## cli 用法
下面以 `stelliberty-cli` 表示该可执行文件。命令名、参数名区分大小写；枚举值不区分大小写。每个命令以及每个配置字段都有离线帮助：

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

帮助包含用途、语法、示例、前置条件、返回值说明；字段帮助还包含类型、默认值、枚举或范围。帮助和版本查询无需应用运行或授权；其他命令必须启用 CLI，并在同一系统用户下运行对应版本的应用。

## 配置与预检查

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

`settings` 覆盖语言、主题、强调色、窗口效果、背景、托盘行为、开机自启、轻量模式、更新源、WebDAV 备份和更新设置。`proxy config` 覆盖端口、LAN、认证、IPv6、延迟测试、TUN 与 TUN 持久开关、出站默认模式、系统代理主机/绕过/PAC、外置控制器、内核性能与日志级别参数；`dns` 覆盖应用 DNS 设置。运行 `schema` 获取完整字段目录。可选 SOCKS/HTTP/重定向端口用 `0` 禁用；混合端口必须为 1–65535；WebDAV 备份间隔为 1–24 小时，保留 1–5 份。窗口效果受当前平台限制；开启 TUN 需要已安装的特权服务或管理员模式，并在核心运行时生效。开机自启会写入或移除系统登录项。DNS 覆盖关闭时，保存的 DNS 参数不会覆盖订阅配置。

写命令支持 `--dry-run`，检查参数并返回目标信息，不保存配置、下载订阅或执行延迟测试。预检查不能保证网络请求、系统代理和核心实际应用一定成功。配置修改复用应用校验和保存流程；核心相关修改等待现有运行时刷新。单次修改一个字段，没有批量事务或自动回滚。

敏感值可通过标准输入传入，避免写进命令历史：

```sh
stelliberty-cli proxy config set lan-password --stdin
stelliberty-cli dns set nameservers --stdin --dry-run
```

将所需内容写入 stdin 并结束输入；结尾换行会去除。数组使用 JSON 字符串数组，每个元素代表 UI 中的一行。文件必须使用绝对路径。密码、认证用户名、背景路径、测试 URL 和 DNS 数组查询会脱敏；订阅查询不返回来源地址或路径。

## 代理与订阅

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

分组和节点名称须精确匹配；只能手动切换支持选择的分组。节点列表不受 UI 搜索框过滤影响。分组、节点操作、模式切换与开启系统代理需要可交互的运行中核心；TUN 还需要应用现有的服务/管理员条件。CLI 不安装服务或自动提权。延迟结果按节点返回 `delay_ms` 和 `delay_state`，测试完成不意味着所有节点都可达。

订阅新增沿用应用规则：没有当前订阅时自动选中新订阅，否则保持当前选择。新订阅的定时更新和自动延迟测试默认关闭。更新命令仅用于远程订阅。删除当前订阅会清空其运行时，不会自动选择其他订阅。增删改均使用应用原有业务流程。

## Agent 返回约定

除帮助和版本外，stdout 返回 JSON：`success`、`status`、`data`、`error`。状态 `ok` 表示查询完成，`dry_run` 表示预检查完成，`saved` 表示已保存，`applied` 表示运行时操作完成，`saved_not_applied` 表示已保存但运行时应用失败。没有运行中核心时，保存配置不等于已实际改变网络行为。

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功、帮助或版本 |
| 2 | 参数无效 |
| 3 | 用户尚未开启 CLI |
| 4 | 应用/核心不可用、版本不匹配或操作条件不满足 |
| 5 | 操作失败，包括保存后应用失败 |
| 6 | 超时，最终结果未知 |

启动连接等待约 30 秒，操作等待最多约 180 秒。超时后操作仍可能完成；先查询状态再决定是否重试。CLI 不自动重试写操作。通过当前用户的本地命名管道或 Unix socket 通信，不监听网络端口；开启意味着同一用户下的其他进程也可以调用这些命令。
