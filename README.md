# ⚡ hyperdu

`hyperdu` is a high-performance, cross-platform terminal-based directory space analyzer written in C# and built on **.NET 10**. 

Designed for massive directory trees and high-latency filesystems, `hyperdu` scans your disk using a custom multi-threaded engine that **dynamically prioritizes scan queues** based on your interactive navigation. If you focus on a subdirectory in the UI, the background scanning workers immediately deprioritize other directories and shift their CPU/disk I/O resources to scan your current view first.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20Windows%20%7C%20macOS-blue.svg)](#)
[![Framework](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Quality Gate Status](https://sonarqube.jav26122.net/api/project_badges/measure?project=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f&metric=alert_status)](https://sonarqube.jav26122.net/dashboard?id=jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f)


---

## User Interface Preview

```text
Target Directory: /
Scanner Workers:  24
Skip Hidden:      False
Excluded Paths:   None
Press Ctrl+C to cancel scan at any time.

╭─Scan Progress───────────────────────────────────────────────────────────────╮ 
│ Dirs: 933,711 Files: 2,567,182 Status: Scanning...                          │ 
│ Size: 2.97 TB Threads: 24                                                   │ 
│ Scanning Path: /media/                                                      │ 
╰─────────────────────────────────────────────────────────────────────────────╯ 
                           hyperdu Explorer - / ⚡ ⠏⠋                            
╭─────┬───────────────────────────────┬───────────┬────────────┬──────────────╮ 
│ Sel │ Name                          │ Size      │ Percentage │ Visual Usage │ 
├─────┼───────────────────────────────┼───────────┼────────────┼──────────────┤ 
│     │ 📁 mnt ⠏⠋                     │ 1.93 TB   │ 65.1%      │ ███████░░░   │ 
│ >   │ 📁 media ⚡ ⠏⠋                 │ 0.92 TB   │ 31.1%      │ ███░░░░░░░   │ 
│     │ 📁 var ⠏⠋                     │ 50.93 GB  │ 1.7%       │ ░░░░░░░░░░   │ 
│     │ 📁 home ⠏⠋                    │ 48.41 GB  │ 1.6%       │ ░░░░░░░░░░   │ 
│     │ 📁 usr ⠏⠋                     │ 11.16 GB  │ 0.4%       │ ░░░░░░░░░░   │ 
│     │ 📁 opt                        │ 6.23 GB   │ 0.2%       │ ░░░░░░░░░░   │ 
│     │ 📁 boot                       │ 233.67 MB │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 tmp                        │ 22.24 MB  │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 etc                        │ 6.17 MB   │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 lib64                      │ 9.00 B    │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 sbin                       │ 8.00 B    │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 bin                        │ 7.00 B    │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 lib                        │ 7.00 B    │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 srv                        │ 0.00 B    │ 0.0%       │ ░░░░░░░░░░   │ 
│     │ 📁 lost+found (Access Denied) │ 0.00 B    │ 0.0%       │ ░░░░░░░░░░   │ 
╰─────┴───────────────────────────────┴───────────┴────────────┴──────────────╯ 
                   Showing 1-15 of 17 items. (Scroll active)                    
 Total Size: 2.96 TB +6.01 MB | Subdirectories: 998868+35 | Files: 2567013+128  
            ↑/↓ Navigate | Enter Open | Backspace/← Up | Q/Esc Exit             
```

---

## Features

* **High-Performance Parallel Scanner**: Leverages a configurable pool of concurrent workers (`WorkerCount`) utilizing programmatically managed non-recursive directory traversal to avoid OS stack limitations.
* **Dynamic Queue Prioritization**: Uses a custom **3-tier prioritized queue** (High, Medium, Low). As you navigate or hover over folders in the interactive terminal, the queue priorities are re-evaluated instantly to focus scanning threads on your active view.
  * **Prioritization Indicator**: A yellow lightning bolt symbol (`⚡`) is displayed in the UI next to the folder currently selected and any actively prioritized directories. This visually indicates that scanning resources are actively focused on that path, yielding instantaneous response times.
* **Latency-Aware Network Mount Detection**: Automatically detects network mounts on Linux (e.g. NFS, CIFS, SMB, SSHFS) and scales up the worker thread count to hide remote network latency.
* **Visual Terminal Explorer**: Built with Spectre.Console, featuring:
  * Colorful HSL-based progress/bar graphs representing disk usage percent.
  * Real-time "+X size" / "+Y files" live animations for folders being actively scanned.
  * Graceful directory-entry transition states (e.g. access denied, missing directories).
* **Scan Time Tracking**: Recursively logs and computes the scan time of directories, highlighting bottle-necked folders.
* **Lock Contention Optimization**: Workers batch up size delta updates (`NodeDelta`) and flush them periodically to reduce lock contention across shared directory tree structures.
* **Intelligent Skipping**: Skips Linux pseudo-filesystems (`/proc`, `/sys`, `/dev`, `/run`) that do not consume physical disk space.

---

## ⚡ Dynamic Scan Prioritization (How it Works)

Unlike traditional directory space analyzers that scan your disk in a rigid, predefined order, `hyperdu` shifts its parallel scanning threads in real time based on where you are navigating in the interactive terminal.

This behavior is tracked and represented visually by the **yellow lightning symbol (`⚡`)**:

* **Entering a Directory (Medium Priority)**: 
  When you press `Enter` to descend into a directory, `hyperdu` immediately promotes that directory's path and all of its nested subdirectories/files to **Priority 1 (Medium)**. The scanner thread pool deprioritizes other parts of the disk to populate your active view first.
* **Selecting/Highlighting a Directory (High Priority)**: 
  You can dynamically steer the scanner's focus simply by moving your selection cursor with the `↑` and `↓` arrow keys. The folder you currently highlight in the explorer list, along with all of its deep descendants, is elevated to **Priority 2 (High)**.
* **Background Traversals (Low Priority)**: 
  All other unvisited directories are scanned at **Priority 0 (Low)** using any remaining background thread pool capacity.

### Visual State Summary
- **No Indicator**: The directory has finished scanning, or is not currently queued/running.
- **Spinner only (`⠏⠋`)**: The directory is currently scanning in the background at standard priority.
- **Lightning + Spinner (`⚡ ⠏⠋`)**: The directory is currently scanning and is **actively prioritized** (either because you are viewing it or because you have highlighted it with your selection cursor).

---

## Interactive Keybindings

When running `hyperdu`, you can navigate the disk structure instantly using the following controls:

| Key | Action |
|---|---|
| `↑` / `↓` | Select/highlight subdirectories or files. |
| `Enter` | Descend into the highlighted subdirectory (focuses scanner prioritization here). |
| `Backspace` / `←` | Ascend to the parent directory. |
| `Q` / `Esc` | Quit the application gracefully. |

---

## Installation & Building

### Prerequisites
* [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

### Clone & Build
```bash
git clone https://github.com/jav76/hyperdu.git
cd hyperdu
dotnet build
```

### Run Locally
To run `hyperdu` against a directory (defaulting to `/` if unspecified):
```bash
dotnet run --project hyperdu.Cli/hyperdu.Cli.csproj -- [target_path] [options]
```

### Cross-Compilation & Publishing
The project includes a `publish.sh` script to build self-contained, trimmed, single-file executables for multiple platforms:
```bash
./publish.sh
```
This generates binaries in the `publish/` directory for the following targets:
- `linux-x64`
- `win-x64`
- `osx-x64`
- `osx-arm64`

---

## CLI Command Line Options

```bash
hyperdu [path] [options]
```

| Option | Alias | Description | Default |
|---|---|---|---|
| `--help` | `-h` | Prints help and usage instructions. | - |
| `--threads <N>` | `-t <N>` | Explicitly sets the number of concurrent worker threads. | Logical core count (scales to max(32, 2*Cores) on network mounts) |
| `--exclude <path>` | `-e <path>` | Excludes specific paths/sub-paths from scanning (can specify multiple times). | `/mnt/` |
| `--skip-hidden` | - | Skips hidden and system files/folders during traversal. | `false` |

---

## License

This project is licensed under the [MIT License](LICENSE) - see the file for details.
Copyright (c) 2026 Jaret Varn.
