# hyperdu

A terminal disk usage explorer for big directory trees and slow filesystems. Written in C# on .NET 10, runs on Linux, macOS and Windows.

You can browse while it scans. The folder you are looking at gets scanned first, so you don't have to wait for the whole disk before you can open a directory.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![NuGet Version](https://img.shields.io/nuget/v/hyperdu.svg)](https://www.nuget.org/packages/hyperdu/)
[![WinGet Package](https://img.shields.io/winget/v/JaretVarn.hyperdu)](https://github.com/microsoft/winget-pkgs/tree/master/manifests/j/JaretVarn/hyperdu)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20Windows%20%7C%20macOS-blue.svg)](#)
[![Framework](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Lines of Code](https://sonarqube.jav26122.net/api/project_badges/measure?project=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f&metric=ncloc&token=sqb_26e731c10e8956d411964d75c86646eb43df806e)](https://sonarqube.jav26122.net/dashboard?id=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f)
[![Maintainability Rating](https://sonarqube.jav26122.net/api/project_badges/measure?project=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f&metric=software_quality_maintainability_rating&token=sqb_26e731c10e8956d411964d75c86646eb43df806e)](https://sonarqube.jav26122.net/dashboard?id=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f)
[![Reliability Rating](https://sonarqube.jav26122.net/api/project_badges/measure?project=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f&metric=software_quality_reliability_rating&token=sqb_26e731c10e8956d411964d75c86646eb43df806e)](https://sonarqube.jav26122.net/dashboard?id=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f)
[![Security Rating](https://sonarqube.jav26122.net/api/project_badges/measure?project=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f&metric=software_quality_security_rating&token=sqb_26e731c10e8956d411964d75c86646eb43df806e)](https://sonarqube.jav26122.net/dashboard?id=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f)
[![Technical Debt](https://sonarqube.jav26122.net/api/project_badges/measure?project=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f&metric=software_quality_maintainability_remediation_effort&token=sqb_26e731c10e8956d411964d75c86646eb43df806e)](https://sonarqube.jav26122.net/dashboard?id=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f)
[![Quality Gate Status](https://sonarqube.jav26122.net/api/project_badges/measure?project=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f&metric=alert_status&token=sqb_26e731c10e8956d411964d75c86646eb43df806e)](https://sonarqube.jav26122.net/dashboard?id=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f)

![hyperdu scanning / with the var folder highlighted and prioritized](https://raw.githubusercontent.com/jav76/hyperdu/main/docs/images/scanning.png)

## Features

- **Scans in parallel** across a pool of worker threads, using direct libc calls on Linux and macOS.
- **Scans what you look at first.** The open folder and the highlighted folder jump ahead of the rest of the queue (marked with `⚡`).
- **Tunes thread count to the disk**: more threads on network mounts (NFS, SMB, SSHFS and others; Linux only) to hide latency, one thread on spinning HDDs (Linux and Windows) to avoid seek thrashing.
- **Shows live progress**: per-folder spinners, `+size` / `+files` deltas and a scan time when each folder finishes.
- **Pause, resume and rescan** without restarting.
- **Skips** Linux pseudo-filesystems (`/proc`, `/sys`, `/dev`, `/run`), and optionally hidden files or paths you exclude.

![hyperdu after finishing a scan of /usr](https://raw.githubusercontent.com/jav76/hyperdu/main/docs/images/complete.png)

## Install

**WinGet** (Windows):

```powershell
winget install JaretVarn.hyperdu
```

**.NET tool** (any platform with the .NET 10 runtime):

```bash
dotnet tool install -g hyperdu
```

### Release binaries

Self-contained Native AOT binaries for Linux and macOS (x64, arm64) and Windows (x64, x86) are attached to each [GitHub Release](https://github.com/jav76/hyperdu/releases), along with a `SHA256SUMS` file. They need no .NET runtime and no ICU library.

* Linux binaries need glibc 2.28 or newer (RHEL/Alma/Rocky 8, Debian 10, Ubuntu 20.04 and later).
* macOS binaries are not notarized. If Gatekeeper blocks a browser download, run `xattr -d com.apple.quarantine hyperdu`.

```bash
tar -xzf hyperdu-linux-x64.tar.gz
./hyperdu --version
```

## Usage

```bash
hyperdu [path] [options]
```

`path` defaults to the current directory.

| Option | Description | Default |
|---|---|---|
| `-t, --threads <N>` | Number of scanner threads. | CPU cores; `max(32, 2 x cores)` on network mounts; 1 on HDDs |
| `-e, --exclude <path>` | Skip a path. Repeat for more than one. | None |
| `--skip-hidden` | Skip hidden and system files. | Off |
| `-v, --version` | Print the version. | |
| `-h, --help` | Print help. | |

Set `HYPERDU_NATIVE_IO=0` to use the .NET file enumerator instead of direct libc calls on Linux and macOS. It is slower and cannot open names that are not valid UTF-8.

### Keys

| Key | Action |
|---|---|
| `↑` / `↓` | Move the selection |
| `Enter` | Open the selected folder |
| `Backspace` / `←` | Go up a level |
| `Space` | Pause or resume scanning |
| `R` | Rescan the selected folder (or the current one) |
| `Q` / `Esc` | Quit |

## How prioritization works

The scan queue has three priority levels:

| Priority | What gets it |
|---|---|
| High | The highlighted folder and everything under it |
| Medium | The folder you opened and everything under it |
| Low | Everything else |

Priorities update as you move, so moving the cursor onto a large folder makes the scanner work on it next.

In the list, a spinner (`⠏⠋`) means a folder is still scanning. `⚡` next to the spinner means it is prioritized. No marker means it is done.

Below, `/var` was opened while `/` was still scanning. `/var` (medium) and the highlighted `lib` (high) are marked `⚡`, and the scanner is already working inside `/var/lib`:

![hyperdu inside /var mid-scan, with lib highlighted and prioritized](https://raw.githubusercontent.com/jav76/hyperdu/main/docs/images/navigate.png)

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

```bash
git clone https://github.com/jav76/hyperdu.git
cd hyperdu
dotnet run --project hyperdu.Cli -- [path] [options]
```

`./publish.sh` builds self-contained single-file binaries for `linux-x64`, `linux-arm64`, `win-x64`, `win-x86`, `osx-x64` and `osx-arm64` into `publish/`.

Versions come from git tags via [MinVer](https://github.com/adamralph/minver). See [docs/publishing-guide.md](docs/publishing-guide.md) for the release process.

## License

[MIT](LICENSE). Copyright (c) 2026 Jaret Varn.
