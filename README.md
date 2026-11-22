# hyperdu

A terminal disk usage explorer for big directory trees and slow filesystems. Written in C# on .NET 10, runs on Linux, macOS and Windows.

You can browse while it scans. The folder you are looking at gets scanned first, so you don't have to wait for the whole disk before you can open a directory.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![NuGet Version](https://img.shields.io/nuget/v/hyperdu.svg)](https://www.nuget.org/packages/hyperdu/)
[![WinGet Package](https://img.shields.io/winget/v/JaretVarn.hyperdu)](https://github.com/microsoft/winget-pkgs/tree/master/manifests/j/JaretVarn/hyperdu)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20Windows%20%7C%20macOS-blue.svg)](#)
[![Framework](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Quality Gate Status](https://jav76.github.io/hyperdu/badges/quality_gate.svg)](https://jav76.github.io/hyperdu/)
[![Reliability Rating](https://jav76.github.io/hyperdu/badges/reliability.svg)](https://jav76.github.io/hyperdu/)
[![Security Rating](https://jav76.github.io/hyperdu/badges/security.svg)](https://jav76.github.io/hyperdu/)
[![Maintainability Rating](https://jav76.github.io/hyperdu/badges/maintainability.svg)](https://jav76.github.io/hyperdu/)
[![Coverage](https://jav76.github.io/hyperdu/badges/coverage.svg)](https://jav76.github.io/hyperdu/)
[![Lines of Code](https://jav76.github.io/hyperdu/badges/ncloc.svg)](https://jav76.github.io/hyperdu/)
[![Technical Debt](https://jav76.github.io/hyperdu/badges/tech_debt.svg)](https://jav76.github.io/hyperdu/)

![hyperdu scanning / with the var folder highlighted and prioritized](https://raw.githubusercontent.com/jav76/hyperdu/main/docs/images/scanning.png)

## Features

- **Scans in parallel** across a pool of worker threads, using direct libc calls on Linux and macOS.
- **Scans what you look at first.** The open folder and the highlighted folder jump ahead of the rest of the queue (marked with `⚡`).
- **Tunes thread count to the disk**: more threads on network mounts (NFS, SMB, SSHFS and others; Linux only) to hide latency, one thread on spinning HDDs (Linux and Windows) to avoid seek thrashing.
- **Shows live progress**: per-folder spinners, `+size` / `+files` deltas and a scan time when each folder finishes.
- **Pause, resume and rescan** without restarting.
- **Skips** Linux pseudo-filesystems (`/proc`, `/sys`, `/dev`, `/run`), and optionally hidden files or paths you exclude.

![hyperdu after finishing a scan of /usr](https://raw.githubusercontent.com/jav76/hyperdu/main/docs/images/complete.png)

## Performance

`hyperdu du` against GNU `du` 9.4 on a 4-core Ubuntu 24.04 runner (warm cache, [benchmark suite](tests/bench/)). Output matched `du` in all 64 scenarios.

Median time for `du -s`:

| Tree | GNU `du` | `hyperdu du` | Speedup |
| :--- | ---: | ---: | ---: |
| 250,000 files in 250 dirs | 290 ms | 120 ms | 2.4x |
| 50,000 files in 500 dirs | 62 ms | 36 ms | 1.7x |
| Dev monorepo (15,000 files, `node_modules`, `.git`) | 22 ms | 16 ms | 1.4x |
| 20,000 files in one directory | 32 ms | 26 ms | 1.2x |
| 251 nested dirs, 23 files | 2.3 ms | 3.9 ms | 0.6x |

* **Overall:** 1.35x faster (geometric mean across 64 scenarios).
* **Memory:** about 8 MiB peak RSS (GNU `du`: about 3 MiB).
* **Where it's slower:** very small or deep, narrow trees, where process startup dominates (2.7 ms vs 0.7 ms on a 5-file directory).

## Install

**Homebrew** (macOS, Linux):

```bash
brew install jav76/tap/hyperdu
```

**WinGet** (Windows):

```powershell
winget install JaretVarn.hyperdu
```

**.NET tool** (any platform with the .NET 10 runtime; for `hyperdu` TUI and `hyperdu du`, not a `du` drop-in):

```bash
dotnet tool install -g hyperdu
```

### Release binaries

Self-contained Native AOT binaries for Linux and macOS (x64, arm64) and Windows (x64, x86) are attached to each [GitHub Release](https://github.com/jav76/hyperdu/releases), along with a `SHA256SUMS` file. They need no .NET runtime and no ICU library. Linux and macOS `.tar.gz` archives include `du` and `gdu` symlinks next to `hyperdu`.

* Linux binaries need glibc 2.28 or newer (RHEL/Alma/Rocky 8, Debian 10, Ubuntu 20.04 and later).
* macOS binaries are not notarized. If Gatekeeper blocks a browser download, run `xattr -d com.apple.quarantine hyperdu`.

```bash
tar -xzf hyperdu-linux-x64.tar.gz
./hyperdu --version
```

## Use as `du`

Invoked as `du` or `gdu` (a symlink to the Native AOT binary), or as `hyperdu du`, hyperdu runs as a non-interactive `du` with matching output. Plain `hyperdu` opens the explorer.

* **GNU dialect** on Linux, and for `gdu` everywhere: 1024-byte blocks, `--apparent-size`, `-b`, `--block-size`, `--exclude`, `--files0-from`, `--inodes`, `--time`, `--max-depth` and more.
* **BSD dialect** for `du` on macOS: 512-byte blocks, `BLOCKSIZE`, `-A`, `-B`, `-g`, `-I`, `-n`, `-r`, `-d` and BSD-style errors.
* Force a dialect with `HYPERDU_DU_DIALECT=gnu` or `bsd`. Set worker threads with `HYPERDU_THREADS=<N>`.

Install the binary and symlinks ahead of `/usr/bin` on `PATH`:

```bash
# System-wide (works with sudo, cron and scripts)
sudo tar -xzf hyperdu-linux-x64.tar.gz -C /usr/local/bin hyperdu du gdu

# Current user only
mkdir -p ~/.local/bin && tar -xzf hyperdu-linux-x64.tar.gz -C ~/.local/bin hyperdu du gdu
```

* Never replace `/usr/bin/du`. The OS package manager owns it. Remove hyperdu with `rm -f /usr/local/bin/{du,gdu,hyperdu}`.
* `sudo`, `cron` and `#!/bin/sh` scripts usually ignore `~/.local/bin` and shell aliases. Use `/usr/local/bin` if they should get hyperdu.
* The .NET tool can't be a `du` drop-in: it ignores the symlink name and has JIT startup cost. Use `hyperdu du ...` with it, or use the release archive.

## Usage

```bash
hyperdu [option]... [path]
```

`path` defaults to the current directory. Short flags align with GNU `du`.

| Option | Description | Default |
|---|---|---|
| `-j, --threads <N>` | Number of scanner worker threads. | CPU cores; `max(32, 2 x cores)` on network mounts; 1 on HDDs |
| `--apparent-size` | Count apparent file sizes instead of allocated 512-byte disk blocks. | Off (allocated blocks) |
| `-b, --bytes` | Count apparent sizes and display exact byte counts. | Off |
| `-h, --human-readable` | Format sizes in binary multiples of 1024. | On (in explorer UI) |
| `--si` | Format sizes in SI powers of 1000. | Off |
| `-t, --threshold <SIZE>` | Hide entries smaller than `SIZE` (`+SIZE` / `SIZE`) or greater than `SIZE` (`-SIZE`). | None |
| `-l, --count-links` | Count hard-linked files multiple times. | Off (deduplicated) |
| `-x, --one-file-system` | Skip directories and targets on different filesystems. | Off |
| `-L, --dereference` | Follow symbolic links. | Off |
| `--exclude <PATTERN>` | Skip files and directories matching glob `PATTERN`. Repeatable. | None |
| `-X, --exclude-from <FILE>` | Read exclude glob patterns from `FILE` (one per line, `-` for stdin). | None |
| `--skip-hidden` | Skip hidden and system files. | Off |
| `-V, --version` | Print the version and exit. | |
| `--help` | Print help and exit. | |

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
dotnet run --project hyperdu.Cli -- [option]... [path]
```

`./publish.sh` builds self-contained single-file binaries for `linux-x64`, `linux-arm64`, `win-x64`, `win-x86`, `osx-x64` and `osx-arm64` into `publish/`.

Versions come from git tags via [MinVer](https://github.com/adamralph/minver).

## License

[MIT](LICENSE). Copyright (c) 2026 Jaret Varn.
