<div align="center">

# SBproxy

[![English](https://img.shields.io/badge/English-red?style=flat-square)](../../README.md)
&nbsp;
[![简体中文](https://img.shields.io/badge/简体中文-blue?style=flat-square)](README.zh-CN.md)

<br>

[Stelliberty](https://github.com/Kindness-Kismet/stelliberty) 的一个二次分叉。根据分叉来源的要求，抹去了其信息。

[![Avalonia](https://img.shields.io/badge/Avalonia-UI-9b4fdb?logo=avaloniaui&logoColor=white&style=flat-square)](https://avaloniaui.net)
[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white&style=flat-square)](https://dotnet.microsoft.com)
[![Windows](https://img.shields.io/badge/Windows-0078D6?logo=windows&logoColor=white&style=flat-square)](#)
[![Linux](https://img.shields.io/badge/Linux-FCC624?logo=linux&logoColor=black&style=flat-square)](#)
[![macOS](https://img.shields.io/badge/macOS-000000?logo=apple&logoColor=white&style=flat-square)](#)

</div>

<br>

| 主页 · 浅色 | 主页 · 深色 |
|---|---|
| ![主页浅色](../../.github/images/home-light.png) | ![主页深色](../../.github/images/home-dark.png) |

| 设置 · 浅色 | 设置 · 深色 |
|---|---|
| ![设置浅色](../../.github/images/settings-light.png) | ![设置深色](../../.github/images/settings-dark.png) |

SBproxy 是跨平台桌面代理客户端，覆盖 Windows、Linux 与 macOS。

支持 Clash 标准订阅和 Base64 订阅导入，启动快、占用低。Windows 和 macOS 上还做了平台级的模糊窗口效果，界面偏简约原生风格。

<br>

---

## 导航

- [SBproxy](#sbproxy)
  - [导航](#导航)
  - [📦 安装](#-安装)
    - [下载](#下载)
    - [系统要求](#系统要求)
  - [🚀 快速上手](#-快速上手)
  - [❓ 常见问题](#-常见问题)
    - [安装后无法启动或启动无响应？](#安装后无法启动或启动无响应)
    - [UWP 回环与管理员权限](#uwp-回环与管理员权限)
    - [系统代理 / 虚拟网卡需要管理员权限吗？](#系统代理--虚拟网卡需要管理员权限吗)
  - [🛠 开发指南](#-开发指南)
    - [前置依赖](#前置依赖)
    - [架构](#架构)
    - [C# 规范](#c-规范)
    - [Rust 规范](#rust-规范)
    - [Avalonia / MVVM](#avalonia--mvvm)
    - [控件测试定位](#控件测试定位)
    - [编译与测试](#编译与测试)
      - [1. 预构建](#1-预构建)
      - [2. 编译前测试](#2-编译前测试)
      - [3. 构建](#3-构建)
      - [4. 格式化](#4-格式化)
  - [PR](#pr)
  - [📄 许可证](#-许可证)
    - [开源承诺](#开源承诺)
    - [商业使用限制](#商业使用限制)
  - [🤝 友情链接](#-友情链接)

<br>

---

## 📦 安装

<sub>[↑ 回到导航](#导航)</sub>

### 下载

从 **[Releases 页面](https://github.com/OGC-CGC/stelliberty/releases/latest)** 下载对应平台的安装包：

| 平台 | 推荐 | 其他可选 |
|---|---|---|
| Windows · x64 / arm64 | `*-setup.exe`（安装版） | `*.zip`（便携版） |
| Linux · x64 / arm64 | `*.AppImage` | |
| macOS · x64 / arm64 | `*.dmg` | |

### 系统要求

| 平台 | 最低要求 |
|---|---|
| Windows | Windows 10 1809 及以上 |
| Linux | 基于 X11/Wayland 的桌面环境 |
| macOS | macOS 13+ |

<br>

---

## 🚀 快速上手

<sub>[↑ 回到导航](#导航)</sub>

1. 启动应用并导入订阅或配置文件。
2. 在节点页选择节点，在主页设置出站模式（规则 / 全局 / 直连）。
3. 启用系统代理或虚拟网卡模式以覆盖整机流量。

配置格式与 Clash Meta 完全兼容，详细参考 [mihomo 官方文档](https://wiki.metacubex.one/en/config/)。

可使用 Agent 通过 cli 操作：[Agent 命令行帮助](CLI.zh-CN.md)

<br>

---

## ❓ 常见问题

<sub>[↑ 回到导航](#导航)</sub>

### 安装后无法启动或启动无响应？

请先安装 .NET 11 运行时，然后重新启动。

- 通用：[微软官方下载](https://dotnet.microsoft.com/download/dotnet/11.0)
- Arch Linux 及其衍生发行版：AUR 包 [`dotnet-core-preview-bin`](https://aur.archlinux.org/packages/dotnet-core-preview-bin)

### UWP 回环与管理员权限

Windows 上的 UWP 应用（如微软商店应用）默认禁止访问本地代理回环地址。可使用 **UWP 回环豁免** 功能来解除此限制。

注意事项：

- 一般情况下不需要管理员权限，只有权限不够的时候系统才会提示你提权。
- 如果用的是虚拟网卡模式，这个限制其实不影响你——它直接接管了网卡流量，压根不走回环地址。
- 但如果你用的是系统代理模式，又想让 UWP 应用（比如微软商店里的软件）也走代理，那这个选项就得开。

### 系统代理 / 虚拟网卡需要管理员权限吗？

- **系统代理**：不需要管理员权限。
- **虚拟网卡**：创建虚拟网卡需要管理员权限。首次使用时安装服务模式可避免每次启动都弹出 UAC。
- **UWP 回环豁免**（Windows）：通常不需要管理员权限；权限不足时才需要提权。

<br>

---

## 🛠 开发指南

<sub>[↑ 回到导航](#导航)</sub>

### 前置依赖

| 工具 | 版本 | 获取方式 |
|---|---|---|
| .NET SDK | `11.0.x` | https://dotnet.microsoft.com/download/dotnet/11.0 |
| Rust | stable（rustup） | https://rustup.rs |
| Python | `3.10+` | https://www.python.org/downloads/ |

### 架构

采用模块化分层 + Clean Architecture + MVVM，托盘和界面分别运行在独立宿主进程中。

```
src/Stelliberty.Tray            常驻托盘宿主、核心生命周期、后台任务
src/Stelliberty.Desktop         按需启动的 Avalonia 界面宿主、窗口、界面平台服务
src/Stelliberty.Presentation    ViewModel、UI 状态、命令绑定
src/Stelliberty.Application     用例、服务与平台能力接口
src/Stelliberty.Domain          实体、值对象、领域规则
src/Stelliberty.Infrastructure  文件系统、持久化、外部服务、进程通信传输与客户端
src/Stelliberty.Native          C# 到原生 FFI 层的包装
src/Stelliberty.Cli             用于控制软件的 Cli 程序
native/hub                      原生库：配置覆写、解析、能力模块
native/service                  服务模式
scripts/                        build.py · prebuild.py · test.py
```

依赖方向：`Desktop → Presentation → Application → Domain`，以及 `Tray → Application → Domain`。

`Infrastructure` 和 `Native` 实现 `Application` 定义的接口；`Application` 不依赖桌面、Avalonia 或 FFI 细节。

公共入口 `stelliberty.exe` 负责托盘、核心运行、系统代理、服务模式、全局快捷键与后台调度，需要窗口时通过授权会话启动 `data/deps/stelliberty_ui.exe`。其他桌面平台使用对应的无扩展名程序入口。

重复打开窗口时复用当前界面会话；默认隐藏到托盘时保留界面进程，开启轻量模式后才在隐藏时退出界面进程，再次打开时创建新会话。两种模式下后台均继续运行，完整退出由托盘统一清理。自启动、安装和提权重启均指向托盘入口，`scripts/build.py` 统一构建并打包两个宿主。

禁止：

- View 直接访问数据库、文件系统或 Rust FFI
- ViewModel 持有平台 API、文件路径、窗口生命周期细节
- Domain 依赖 Avalonia、数据库、网络、日志或配置文件
- Rust crate 感知 C#、Avalonia 或窗口生命周期

### C# 规范

**命名**

- 类型、枚举、接口、属性、方法：`PascalCase`
- 局部变量、参数、私有实例字段：`camelCase`；私有只读实例字段：`_camelCase`
- 接口只在表达抽象能力时使用 `I` 前缀
- 异步方法以 `Async` 结尾；可取消操作传递 `CancellationToken`
- Boolean 使用肯定语义：`IsEnabled`、`CanSave`、`HasSelection`

**实践**

- 能从右侧明显推断时使用 `var`，否则写显式类型
- 用结果对象表达复杂状态（`SaveResult`、`ParseResult`），不用裸 `bool`
- 系统边界使用卫语句验证；内部不可能状态不过度防御
- 不使用 `.Result`、`.Wait()` 阻塞异步任务
- 注释写意图、约束、坑点；简体中文，极致精炼，同段不超过 2 行

### Rust 规范

**命名**

- crate、module、function、variable：`snake_case`
- type、trait、enum、struct：`PascalCase`
- const、static：`SCREAMING_SNAKE_CASE`

**实践**

- 默认不可变，优先借用
- 使用 `Result<T, E>` 和 `?` 传播错误；不用 `unwrap()` 处理可恢复错误
- 默认禁止 `unsafe`；必须使用时限制最小范围并注释安全前提
- 不使用 `mod.rs`，采用 Rust 2018+ 同名文件风格
- FFI 函数命名：`hub_<能力名>_<动作>`

**能力模块结构**

```
native/hub/src/
├── lib.rs              // 仅模块声明
├── ffi.rs              // 根 FFI 聚合
├── capabilities/       // 每个能力一个文件
├── infra/              // HTTP 客户端、runtime 等基础设施
└── util/               // 纯工具函数
```

### Avalonia / MVVM

- View 只负责展示与绑定，不含业务逻辑
- ViewModel 暴露不可变或可观察状态，不直接操作控件实例
- UI 线程只处理 UI 更新；耗时任务放后台
- 平台能力（窗口、托盘、权限）放宿主层实现，经 `Application` 接口暴露

### 控件测试定位

所有可交互控件必须设置 `AutomationProperties.AutomationId`。

- 格式：`页面或区域.语义名称`，例如 `Main.SaveButton`、`Library.SearchBox`
- ID 稳定，不随显示文本、语言、布局变化
- 同一 View 内不得重复
- 禁止随机数、索引、视觉位置命名

### 编译与测试

开发流程：预构建 → 测试 → 构建。

#### 1. 预构建

下载核心二进制、GeoIP 数据与字体，构建服务模式二进制。

```bash
python scripts/prebuild.py
```

| 参数 | 作用 |
|---|---|
| *（默认）* | Release 服务二进制 |
| `--dev` | Debug 服务二进制 |
| `--all` | 同时构建 Debug 与 Release |
| `--platform <rid>` | 目标平台：`current` · `win-x64` · `win-arm64` · `linux-x64` · `linux-arm64` · `macos-x64` · `macos-arm64` |
| `--clean` | 准备前清理 `build/` 与项目 `bin/obj/` 目录 |

#### 2. 编译前测试

```bash
python scripts/test.py --all
python scripts/test.py <测试名>
```

| 参数 | 作用 |
|---|---|
| `--all` | 运行所有编译前测试 |
| `<测试名>` | 运行指定测试（见下表） |

<details>
<summary>可用测试列表</summary>

| 名称 | 说明 |
|---|---|
| `monitoring-rules` | 监控规则：连接与日志解析和归约、规则解析与分类 |
| `settings-rules` | 设置规则：TUN 权限修正、系统代理请求、更新版本选择 |
| `subscription-rules` | 订阅规则：更新计划、提供器解析、内容规范化 |
| `proxy-selection-rules` | 节点选择规则：组语义、规范化、选择、可见性 |
| `runtime-config-rules` | 运行时配置规则：设置规范化、确定性 YAML 生成 |
| `chain-proxy-rules` | 链式代理规则：分析、确定性运行时配置转换 |

</details>

#### 3. 构建

```bash
python scripts/build.py
```

| 参数 | 作用 |
|---|---|
| *（默认）* | Release 构建 |
| `--dev` | Debug 构建 |
| `--all` | 同时构建 Debug 与 Release |
| `--platform <rid>` | 目标平台（同预构建，额外支持 `desktop`） |
| `--pack <格式>` | `app`, `zip`, `installer`, `all` |
| `--version` | 指定此次发布使用的版本号 |
| `--clean` | 构建前清理输出目录 |

构建过程中若需要打包，则在 Windows 上依赖 Inno Setup，在 Linux 上依赖各系统打包工具，在 macOS 上依赖 DMG Python。

**完整发布构建：**

```bash
python scripts/prebuild.py
python scripts/build.py --pack all
```

#### 4. 格式化

```bash
dotnet format
cargo fmt
```

<br>

---

## PR

<sub>[↑ 回到导航](#导航)</sub>

不接受任何 Pull Request。要改任何东西请分叉。

---

## 📄 许可证

<sub>[↑ 回到导航](#导航)</sub>

### 开源承诺

本项目的修改基于 Wushidrm 协议公开源代码。不对其内含的任何代码作任何保证。

### 商业使用限制

本项目及其衍生作品均禁止用于商业用途。

<br>

## 🤝 友情链接

<sub>[↑ 回到导航](#导航)</sub>

- [原作者](https://t.me/MaterialDesign3)。
