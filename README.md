<div align="center">

# SBproxy

[![English](https://img.shields.io/badge/English-red?style=flat-square)](README.md)
&nbsp;
[![简体中文](https://img.shields.io/badge/简体中文-blue?style=flat-square)](.github/docs/README.zh-CN.md)

<br>

A fork of a fork of [Stelliberty](https://github.com/Kindness-Kismet/stelliberty). Information identifying the source fork has been removed at its request.

[![Avalonia](https://img.shields.io/badge/Avalonia-UI-9b4fdb?logo=avaloniaui&logoColor=white&style=flat-square)](https://avaloniaui.net)
[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white&style=flat-square)](https://dotnet.microsoft.com)
[![Windows](https://img.shields.io/badge/Windows-0078D6?logo=windows&logoColor=white&style=flat-square)](#)
[![Linux](https://img.shields.io/badge/Linux-FCC624?logo=linux&logoColor=black&style=flat-square)](#)
[![macOS](https://img.shields.io/badge/macOS-000000?logo=apple&logoColor=white&style=flat-square)](#)

</div>

<br>

| Home · Light | Home · Dark |
|---|---|
| ![Home light](.github/images/home-light.png) | ![Home dark](.github/images/home-dark.png) |

| Settings · Light | Settings · Dark |
|---|---|
| ![Settings light](.github/images/settings-light.png) | ![Settings dark](.github/images/settings-dark.png) |

SBproxy is a cross-platform desktop proxy client for Windows, Linux, and macOS.

Import Clash-standard and Base64 subscriptions. It starts quickly and uses little resources. On Windows and macOS, platform-level frosted window effects are available, with a simple native look.

<br>

---

## Navigation

- [SBproxy](#sbproxy)
  - [Navigation](#navigation)
  - [📦 Installation](#-installation)
    - [Download](#download)
    - [System Requirements](#system-requirements)
  - [🚀 Quick Start](#-quick-start)
  - [❓ FAQ](#-faq)
    - [App will not start or appears unresponsive after installation?](#app-will-not-start-or-appears-unresponsive-after-installation)
    - [UWP Loopback \& Administrator Privileges](#uwp-loopback--administrator-privileges)
    - [Do system proxy / virtual network require admin?](#do-system-proxy--virtual-network-require-admin)
  - [🛠 Development Guide](#-development-guide)
    - [Prerequisites](#prerequisites)
    - [Architecture](#architecture)
    - [C# Conventions](#c-conventions)
    - [Rust Conventions](#rust-conventions)
    - [Avalonia / MVVM](#avalonia--mvvm)
    - [Control Test IDs](#control-test-ids)
    - [Build \& Test](#build--test)
      - [1. Prebuild](#1-prebuild)
      - [2. Pre-build Tests](#2-pre-build-tests)
      - [3. Build](#3-build)
      - [4. Formatting](#4-formatting)
  - [PR](#pr)
  - [📄 License](#-license)
    - [Open Source Commitment](#open-source-commitment)
    - [Commercial Use Restrictions](#commercial-use-restrictions)
  - [Thanks](#thanks)

<br>

---

## 📦 Installation

<sub>[↑ Back to Navigation](#navigation)</sub>

### Download

Grab the package for your platform from the **[Releases page](https://github.com/OGC-CGC/stelliberty/releases/latest)**:

| Platform | Recommended | Alternative |
|---|---|---|
| Windows · x64 / arm64 | `*-setup.exe` (installer) | `*.zip` (portable) |
| Linux · x64 / arm64 | `*.AppImage` | |
| macOS · x64 / arm64 | `*.dmg` | |

### System Requirements

| Platform | Minimum |
|---|---|
| Windows | Windows 10 1809 or later |
| Linux | X11/Wayland-based desktop environment |
| macOS | macOS 13+ |

<br>

---

## 🚀 Quick Start

<sub>[↑ Back to Navigation](#navigation)</sub>

1. Launch the app and import your subscription or config file.
2. Select nodes on the Nodes page; set an outbound mode on the Home page (Rule / Global / Direct).
3. Enable system proxy or virtual network mode for full-device traffic coverage.

Config format is fully compatible with Clash Meta — see the [mihomo documentation](https://wiki.metacubex.one/en/config/) for details.

Agents can control the app through the CLI: [Agent CLI Help](.github/docs/CLI.md).

<br>

---

## ❓ FAQ

<sub>[↑ Back to Navigation](#navigation)</sub>

### App will not start or appears unresponsive after installation?

Install the .NET 11 Runtime, then restart the app.

- General: [Microsoft download](https://dotnet.microsoft.com/download/dotnet/11.0)
- Arch Linux and derivatives: AUR package [`dotnet-core-preview-bin`](https://aur.archlinux.org/packages/dotnet-core-preview-bin)

### UWP Loopback & Administrator Privileges

UWP apps on Windows (e.g. Microsoft Store apps) are blocked from accessing the local proxy loopback address by default. Enable **UWP loopback exemption** to remove this restriction.

Notes:

- Administrator privileges are usually not required; the system only prompts for elevation when permission is insufficient.
- Virtual network mode is unaffected — it takes over traffic at the adapter level and never goes through loopback.
- If you use system proxy mode and want UWP apps (such as Microsoft Store apps) to go through the proxy, turn this option on.

### Do system proxy / virtual network require admin?

- **System proxy**: No administrator privileges required.
- **Virtual network**: Creating a virtual network adapter requires administrator privileges. Install service mode on first use to avoid repeated UAC prompts.
- **UWP loopback exemption** (Windows): usually no administrator privileges required; elevate only if permission is insufficient.

<br>

---

## 🛠 Development Guide

<sub>[↑ Back to Navigation](#navigation)</sub>

### Prerequisites

| Tool | Version | Get it |
|---|---|---|
| .NET SDK | `11.0.x` | https://dotnet.microsoft.com/download/dotnet/11.0 |
| Rust | stable (rustup) | https://rustup.rs |
| Python | `3.10+` | https://www.python.org/downloads/ |

### Architecture

Modular layers + Clean Architecture + MVVM, with separate tray and UI host processes.

```
src/Stelliberty.Tray            Persistent tray host, core lifecycle, background tasks
src/Stelliberty.Desktop         On-demand Avalonia UI host, windows, UI platform services
src/Stelliberty.Presentation    ViewModels, UI state, command bindings
src/Stelliberty.Application     Use cases, service & capability interfaces
src/Stelliberty.Domain          Entities, value objects, domain rules
src/Stelliberty.Infrastructure  File system, persistence, external services, IPC transport and clients
src/Stelliberty.Native          C# wrappers over the native FFI layer
src/Stelliberty.Cli             CLI program for controlling the app
native/hub                      Native library: config override, parsing, capabilities
native/service                  Service mode
scripts/                        build.py · prebuild.py · test.py
```

Dependency directions: `Desktop → Presentation → Application → Domain` and `Tray → Application → Domain`.

`Infrastructure` and `Native` implement interfaces defined by `Application`; `Application` has no dependency on desktop, Avalonia, or FFI details.

The public entry point, `stelliberty.exe`, handles the tray, core runtime, system proxy, service mode, global hotkeys, and background scheduling. When a window is needed, it launches `data/deps/stelliberty_ui.exe` through an authorized session. Other desktop platforms use the corresponding executables without filename extensions.

Opening the window again reuses the active UI session. By default, hiding to the tray keeps the UI process alive. Lightweight Mode releases the UI process when hidden and creates a new session when reopened; the background runtime continues in both modes. Full application exit is coordinated by the tray. Startup integration, installers, and elevated restarts all use the tray entry point, and `scripts/build.py` builds and packages both hosts together.

Prohibited:

- Views accessing databases, file system, or Rust FFI directly
- ViewModels holding platform APIs, file paths, or window lifecycle details
- Domain depending on Avalonia, databases, networking, logging, or config files
- Rust crates being aware of C#, Avalonia, or window lifecycle

### C# Conventions

**Naming**

- Types, enums, interfaces, properties, methods: `PascalCase`
- Local variables, parameters, private instance fields: `camelCase`; private readonly instance fields: `_camelCase`
- Interfaces use `I` prefix only when expressing an abstract capability
- Async methods end with `Async`; cancellable operations take `CancellationToken`
- Booleans use affirmative semantics: `IsEnabled`, `CanSave`, `HasSelection`

**Practices**

- Use `var` when the type is obvious from the right side; explicit types otherwise
- Use result objects for complex state (`SaveResult`, `ParseResult`), not bare `bool`
- Guard clauses at system boundaries; no excessive defensiveness for impossible internal states
- Never use `.Result` or `.Wait()` to block async tasks
- Comments state intent, constraints, and pitfalls in Simplified Chinese; concise, max 2 lines per block

### Rust Conventions

**Naming**

- crate, module, function, variable: `snake_case`
- type, trait, enum, struct: `PascalCase`
- const, static: `SCREAMING_SNAKE_CASE`

**Practices**

- Immutable by default, prefer borrowing
- Use `Result<T, E>` and `?` for error propagation; no `unwrap()` for recoverable errors
- `unsafe` is prohibited by default; when necessary, minimize scope and comment the safety precondition
- No `mod.rs` — use Rust 2018+ same-name file style
- FFI function naming: `hub_<capability>_<action>`

**Capability module structure**

```
native/hub/src/
├── lib.rs              // module declarations only
├── ffi.rs              // root FFI aggregation
├── capabilities/       // one file per capability
├── infra/              // HTTP client, runtime, etc.
└── util/               // pure utility functions
```

### Avalonia / MVVM

- Views handle display and binding only — no business logic
- ViewModels expose immutable or observable state; never manipulate control instances directly
- UI thread handles UI updates only; heavy work goes to background threads
- Platform capabilities (windows, tray, permissions) are implemented in the host layer and exposed via `Application` interfaces

### Control Test IDs

All interactive controls must have `AutomationProperties.AutomationId` set.

- Format: `PageOrArea.SemanticName`, e.g. `Main.SaveButton`, `Library.SearchBox`
- IDs are stable — they don't change with display text, language, or layout
- No duplicates within the same View
- No random numbers, indices, or visual-position naming

### Build & Test

Development workflow: **prebuild → test → build**.

#### 1. Prebuild

Downloads the core binary, GeoIP data, and fonts; builds the service-mode binary.

```bash
python scripts/prebuild.py
```

| Flag | Effect |
|---|---|
| *(default)* | Release service binary |
| `--dev` | Debug service binary |
| `--all` | Both Debug and Release |
| `--platform <rid>` | `current` · `win-x64` · `win-arm64` · `linux-x64` · `linux-arm64` · `macos-x64` · `macos-arm64` |
| `--clean` | Clean `build/` and `bin/obj/` before fetching |

#### 2. Pre-build Tests

```bash
python scripts/test.py --all
python scripts/test.py <name>
```

| Flag | Effect |
|---|---|
| `--all` | Run every pre-build test |
| `<name>` | Run a specific test (see the table below) |

<details>
<summary>Available tests</summary>

| Name | Description |
|---|---|
| `monitoring-rules` | Connection and log parsing and reduction; rule parsing and classification |
| `settings-rules` | TUN permission correction, system proxy requests, update release selection |
| `subscription-rules` | Update planning, provider parsing, content normalization |
| `proxy-selection-rules` | Group semantics, normalization, selection, visibility |
| `runtime-config-rules` | Settings normalization and deterministic YAML generation |
| `chain-proxy-rules` | Analysis and deterministic runtime config transformation |

</details>

#### 3. Build

```bash
python scripts/build.py
```

| Flag | Effect |
|---|---|
| *(default)* | Release build |
| `--dev` | Debug build |
| `--all` | Both Debug and Release |
| `--platform <rid>` | Same as prebuild, plus `desktop` |
| `--pack <format>` | `app`, `zip`, `installer`, `all` |
| `--version` | Specify the version number for this release |
| `--clean` | Clean target output directory before building |

Packaging during the build requires Inno Setup on Windows, the relevant system packaging tools on Linux, and DMG Python on macOS.

**Full release build:**

```bash
python scripts/prebuild.py
python scripts/build.py --pack all
```

#### 4. Formatting

```bash
dotnet format
cargo fmt
```

<br>

---

## PR

<sub>[↑ Back to Navigation](#navigation)</sub>

Pull Requests are not accepted. Fork the repository to make changes.

---

## 📄 License

<sub>[↑ Back to Navigation](#navigation)</sub>

### Open Source Commitment

The modifications in this project are published as source code under the Wushidrm License. No warranty is provided for any code it contains.

### Commercial Use Restrictions

This project and its derivative works must not be used for commercial purposes.

<br>

## Thanks

<sub>[↑ Back to Navigation](#navigation)</sub>

- [Original author](https://t.me/MaterialDesign3).
