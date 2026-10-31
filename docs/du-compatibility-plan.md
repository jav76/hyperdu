# du compatibility plan

**Goal:** `hyperdu` can be installed as `du` and existing scripts keep working, while `hyperdu` itself keeps the interactive explorer.

**Status:** draft, 2026-10-31. Reference implementation: GNU coreutils `du` (9.7 on the dev machine; CI version to be pinned, see Phase 0).

## 1. Definition of "drop-in"

For any argument vector and environment that GNU `du` accepts:

- stdout is byte-identical (same lines, order, separators, units)
- exit status is identical
- stderr carries the same messages (English, `du:` prefix, same quoting), compared as its own stream
- nothing extra is printed (no banner, colors, prompts) and no TTY is required

Measured by a differential harness against the reference `du` and by the GNU coreutils `tests/du/` suite.

**Non-goals for v1:** translated messages (gettext), BSD/macOS `du` flags (Phase 6), a Windows `du` persona (Windows has no native `du`; Git Bash and MSYS2 already ship GNU `du`).

## 2. Gap analysis

| Area | GNU du | hyperdu today | Severity |
| :--- | :--- | :--- | :--- |
| Invocation | Non-interactive, prints `SIZE<TAB>PATH` lines | Always prints the figlet banner, then opens the TUI (`Program.Main`) | Blocker |
| Operands | 0..N files or dirs, default `.`, printed as typed (`d/` stays `d/`) | One dir, last non-option wins, files rejected, printed as an absolute path | Blocker |
| Option parsing | getopt_long: bundling (`-sh`), attached values (`-d1`, `-BM`), `--opt=val`, unique-prefix abbreviations, permutation, `--` | Exact string match; unknown flags silently become the target path | Blocker |
| Flag conflicts | `-h` human-readable, `-t` threshold, `--exclude` glob | `-h` help, `-t` threads, `-v` version, `-e/--exclude` path prefix | Blocker |
| Size metric | Allocated blocks (`st_blocks * 512`), includes directory inodes and symlinks | `entry.Length` of regular files; dir symlinks counted as the length of the target path string; dir inodes not counted | Blocker |
| Hard links | Each (dev, ino) counted once, across operands too (`-l` disables) | Counted every time | Blocker |
| File names | Raw bytes | UTF-16 `string`. Verified: a dir named `dir\xfe` throws `DirectoryNotFoundException`, a file `bad\xffname` reports `exists=False` | Blocker |
| Errors and exit code | `du: cannot read directory 'x': Permission denied`, keeps going, exits 1 | Stored in `DirectoryNode.ErrorMessage`, exits 0 | Blocker |
| Output order | Post-order, entries in readdir order | Not applicable (TUI sorts by size) | Blocker |
| Pseudo filesystems | Traverses `/proc`, `/sys`, `/dev`, `/run` | Always skipped (`IsVirtualOrSystemPath`), and refused as a root | Major |
| Deep trees | fd-relative traversal, no `PATH_MAX` limit | Full path string per directory | Major |
| `-x`, symlink modes, inodes, times | Supported | No `st_dev`, `st_ino`, `st_nlink` or timestamps collected | Major |
| `du \| head` | Killed by SIGPIPE (status 141) | .NET ignores SIGPIPE, so the write throws `IOException` | Minor |
| Startup | Under 1 ms | AOT about 3 ms (measured); `dotnet tool` (JIT) is much slower and loses `argv[0]` | Minor |
| Tests | Large upstream suite | No test project | Blocker for confidence |

## 3. Decisions

### D1. How du mode is selected

| Option | Pros | Cons | Design Considerations |
| :--- | :--- | :--- | :--- |
| **A: Multi-call binary** (`argv[0]` is `du` or `gdu` selects the du persona; `hyperdu du ARGS` as an explicit entry) | • Same model as busybox and uutils<br>• No breaking change for current users<br>• Scripts unchanged | • Requires the AOT build for symlink dispatch<br>• Two option parsers | • Verified: under AOT a `du` symlink reports `argv0=./du`; under JIT it reports the `.dll` path<br>• Dispatch must happen before any Console or Spectre setup |
| **B: Make the default CLI du-compatible**, TUI behind `-i` | • One parser, one name | • Breaks `-h`, `-t`, `-v`, `-e`<br>• `hyperdu` with no args stops exploring | • Needs a major version bump |
| **C: Auto-select by TTY** (redirected stdout gives du output) | • Zero config | • `du` typed in a terminal would open the TUI<br>• Behavior depends on context | • Not a drop-in; reject |

**Recommendation: A.** It is the only option that is both a drop-in and non-breaking.

### D2. Reference dialect

| Option | Pros | Cons | Design Considerations |
| :--- | :--- | :--- | :--- |
| **A: GNU coreutils du** (pinned version) | • Linux default<br>• POSIX `du` is a subset<br>• Upstream test suite to run against | • macOS scripts use BSD flags (`-g`, `-A`, `-I`, `-r`, `-n`) | • One option surface to freeze and test |
| **B: POSIX only** | • Small | • Linux scripts rely on `-h`, `--max-depth`, `--exclude`, `-b` | • Not a drop-in anywhere real |
| **C: GNU and BSD personas** (BSD as `du` on macOS, GNU as `gdu`, matching Homebrew coreutils naming) | • Drop-in on both platforms | • Two surfaces and output quirks<br>• Needs a macOS reference runner | • Adds a persona table, reuses the same core |

**Recommendation: A now, C as Phase 6.** GNU covers the largest install base and comes with a conformance suite.

### D3. Output ordering vs parallel scanning

| Option | Pros | Cons | Design Considerations |
| :--- | :--- | :--- | :--- |
| **A: Scan everything, then print** | • Simplest<br>• Reuses the current scanner | • No output until the scan ends<br>• Memory holds the whole tree, plus every file with `-a` | • Fine for small trees, poor for `du -a / \| head` |
| **B: Ordered streaming emitter + frontier-prioritized parallel scan** | • Output starts when the first subtree is done<br>• Memory bounded to the scanned-but-unprinted window<br>• Order-dependent accounting stays exact (emitter replays du's order on one thread) | • More moving parts<br>• Needs backpressure tuning | • Reuses `PrioritizedQueue`'s evaluator to steer workers to the emission frontier |
| **C: Sequential walker only** | • Trivially identical to du | • No speed advantage over du | • Loses the project's reason to exist |

**Recommendation: B, built on C.** Ship the sequential walker first (Phase 1), keep it as the `HYPERDU_THREADS=1` path and as the test oracle for the parallel engine.

### D4. Native filesystem access

| Option | Pros | Cons | Design Considerations |
| :--- | :--- | :--- | :--- |
| **A: libc via `LibraryImport`** (`openat`, `fdopendir`, `readdir`, `statx` on Linux, `fstatat` on macOS) | • Byte-exact names<br>• Blocks, ino, dev, nlink, ns timestamps in one call<br>• fd-relative, so no `PATH_MAX` limit<br>• AOT-friendly | • Per-OS `dirent`/`stat` layouts<br>• macOS x64 needs `$INODE64` entry points | • New `hyperdu.Core/Native` layer, also usable by the TUI later |
| **B: Raw `getdents64` on Linux**, A on macOS | • Batched directory reads | • Syscall numbers per arch<br>• More code | • Pure optimization over A |
| **C: Keep `FileSystemEnumerator`**, add a stat P/Invoke per path | • Smallest change | • Cannot represent non-UTF-8 names (verified)<br>• Still path-based, double stat | • Fails the drop-in bar |

**Recommendation: A**, with B only if profiling shows readdir overhead.

### D5. Existing hyperdu flags

| Option | Pros | Cons | Design Considerations |
| :--- | :--- | :--- | :--- |
| **A: Leave the `hyperdu` persona as is** | • No breaking change | • `--exclude` means a path prefix in one persona and a glob in the other | • Personas share only the core |
| **B: Align `hyperdu` to du flags** (`-t` threshold, `-h` human, threads move to `-j/--threads`, `--exclude` glob), one release of deprecation warnings | • One mental model<br>• TUI gains `-x`, `-L`, `--apparent-size` | • Breaking change, major version | • Easier once the shared core exists |

**Recommendation: A for this effort**, revisit B in Phase 6.

### D6. hyperdu-specific settings in the du persona (threads etc.)

| Option | Pros | Cons | Design Considerations |
| :--- | :--- | :--- | :--- |
| **A: Environment variables only** (`HYPERDU_THREADS`) | • Option surface stays identical to GNU, including abbreviation and ambiguity errors | • Less discoverable | • Document in README and `--help` footer text only if GNU's footer is not compared |
| **B: `--hyperdu-*` long options** | • Shows in `--help` | • `du --h` ambiguity message gains a third candidate | • Small but real stderr diff |
| **C: Plain names like `--threads`** | • Familiar | • `du --thr=1M` is valid today as `--threshold` (verified) and would become ambiguous | • Breaks scripts; reject |

**Recommendation: A.**

## 4. Target architecture

```
argv[0] / first arg
  ├─ "hyperdu"            → existing parser → ParallelScanner → InteractiveNavigator (unchanged)
  └─ "du" | "gdu" | "hyperdu du"
        → GetoptLong → DuOptions (+ env: DU_BLOCK_SIZE, BLOCK_SIZE, BLOCKSIZE, POSIXLY_CORRECT, TIME_STYLE)
        → DirectoryReader (fd-relative, byte names, statx/fstatat)
        → scanner: 1 worker at start, fans out as the queue grows; frontier-first priority
            per-dir record: own stat, entries in readdir order, sums for nlink==1 files,
            multi-link files with their position, errors
        → OrderedEmitter (single thread, GNU fts order)
            dedupe HashSet<(dev, ino)>, -d/-t/-S/-a/-c, error lines in order
            HumanFormat / BlockSize / TimeFormat
        → raw bytes to buffered stdout; EPIPE → restore SIGPIPE default and re-raise
```

Rules that keep this exact:

- **Scanner records facts, emitter decides.** Everything order-dependent (hard-link first-seen, multi-operand dedupe, `-L` directory dedupe, depth, threshold, totals) runs in the emitter while it walks du's order, using one `HashSet<(dev, ino)>` like GNU's hash table. Workers never race on attribution.
- **Scan by inode, print by path.** With `-L`, a directory reachable twice is scanned once; the emitter prints it at its first position and skips the second. An ancestor (dev, ino) check stops cycles and produces GNU's circular-directory warning.
- **Readdir order is preserved.** gnulib fts sorts entries by inode number for directories with more than 10,000 entries on most filesystems (not tmpfs or NFS). Replicate it; confirm the threshold and filesystem list against gnulib `fts.c` first.
- **Frontier priority maps onto the existing tiers:** High = subtree holding the emission frontier, Medium = the next few siblings, Low = the rest. Pause far-ahead work when unprinted records pass a memory budget.
- **Existing device heuristics stay:** HDD keeps 1 worker, network mounts get more.

Proposed files:

| Path | Purpose |
| :--- | :--- |
| `hyperdu.Core/Native/Libc.cs` | `LibraryImport` bindings and per-OS struct layouts |
| `hyperdu.Core/Native/DirectoryReader.cs` | fd-relative readdir and stat with byte names |
| `hyperdu.Core/Du/GetoptLong.cs` | getopt_long port: bundling, abbreviations, permutation, `POSIXLY_CORRECT` |
| `hyperdu.Core/Du/DuOptions.cs` | Parsed options and conflict checks |
| `hyperdu.Core/Du/BlockSize.cs` | SIZE parsing (`K`..`Q`, `KB`..`QB`, `KiB`, `'` grouping, `human-readable`, `si`), env precedence |
| `hyperdu.Core/Du/HumanFormat.cs` | gnulib `human.c` port (`-h`, `--si`, ceiling rounding) |
| `hyperdu.Core/Du/TimeFormat.cs` | `--time-style` styles and `+FORMAT` including `%N` |
| `hyperdu.Core/Du/ExcludeMatcher.cs` | gnulib exclude semantics for `--exclude` and `-X` |
| `hyperdu.Core/Du/OrderedEmitter.cs` | GNU-order accounting and output |
| `hyperdu.Core/Du/Messages.cs` | Error texts and gnulib quotearg quoting |
| `hyperdu.Cli/DuCommand.cs` | du persona entry point |
| `hyperdu.Cli/Program.cs` | Persona dispatch before any console setup |
| `hyperdu.Tests/` | xUnit unit tests |
| `tests/compat/` | Fixture builder, differential runner, GNU suite runner |

## 5. Roadmap

### Phase 0: compatibility harness first (M)

- `tests/compat/make-fixtures.sh`: sparse file, hard links (same dir, different dirs, across operands), symlinks (file, dir, dangling, loop), unreadable dir, non-UTF-8 names, names with newline and tab, empty dirs, a dir with 10,001+ entries, depth beyond `PATH_MAX`, nested tmpfs mount (CI with sudo).
- `tests/compat/diff-run.sh`: runs reference `du` and `hyperdu du` for each arg vector, compares stdout bytes, stderr and exit status separately. Vectors: a curated list plus random combinations drawn from section 6.
- GNU suite runner: fetch coreutils source at the pinned tag, run `tests/du/*` with hyperdu's `du` first in `PATH` (the uutils approach). Report pass count.
- Pin the reference: `ubuntu-24.04` runners ship GNU 9.4; Ubuntu 25.10+ defaults to uutils, so CI must install GNU explicitly. Pick one version and record it in the harness.
- CI job on Linux; macOS job later with `brew install coreutils` (`gdu`) as reference.

**Done when:** the harness runs in CI and reports a baseline score.

### Phase 1: du persona MVP on a sequential walker (L)

- Persona dispatch (`du`, `gdu`, `hyperdu du`) with zero extra output.
- `GetoptLong` recognizing every GNU option name; options not built yet fail with a clear error.
- `DirectoryReader` (D4) on Linux and macOS.
- Accounting: `st_blocks`, directory inode sizes, symlink own size, hard-link dedupe, multi-operand dedupe, file operands, default `.`, operand spelling preserved.
- Output: `-a -s -d -c -0 -S`, `--apparent-size -b -k -m -B -h --si`, block-size env vars and `POSIXLY_CORRECT` (512-byte units, no permutation).
- Errors, exit codes, conflict checks (`-s` with `-a`, `-s` with `-d N`, `--files0-from` with operands).
- `--help` and `--version` (`du (hyperdu) X.Y.Z`; never claims to be GNU).

**Done when:** the harness passes every Phase 1 vector; unit tests cover the parser, SIZE parsing and human formatting.

### Phase 2: traversal semantics (M)

- `-x`, `-L`, `-H`/`-D`, `-P`, cycle detection and warning.
- `--exclude` and `-X` with gnulib semantics (confirm against `exclude.c`): `fnmatch` without `FNM_PATHNAME` (so `*` matches `/`), tried on the full path and on every suffix after a `/`, applied to operands as well.
- `-t` (positive and negative), `--inodes`, `-l`, `--files0-from` (including `-` for stdin).
- fd-relative descent past `PATH_MAX`; fts inode sort for large directories.
- No pseudo-filesystem skipping in the du persona.

**Done when:** at least 90% of GNU `tests/du/` passes, with every remaining failure triaged in an issue.

### Phase 3: time and locale (S)

- `--time`, `--time=atime|access|use|ctime|status`, `--time-style=full-iso|long-iso|iso|+FORMAT`, `TIME_STYLE`, `posix-` prefix. Keep nanoseconds from `statx`: `DateTime` only has 100 ns ticks and `full-iso` prints 9 digits.
- Thousands grouping (`'` prefix) and decimal point from `LC_ALL`/`LC_NUMERIC`/`LANG` (for example `4,0K` under `de_DE`).

**Done when:** the harness passes time and locale vectors under `TZ=UTC` and a DST zone, with `LC_ALL=C` and `en_US.UTF-8`.

### Phase 4: parallel engine (L)

- Ordered emitter, frontier priority, backpressure and adaptive fan-out (D3 B).
- Harness runs every vector with `HYPERDU_THREADS=1`, `4` and the default; outputs must match.
- Benchmarks (`hyperfine`) against GNU `du`: warm cache, cold cache, NFS, `-a` over 1M+ files, peak memory.

**Done when:** output is identical across thread counts; faster than GNU on cold-cache large trees; within 10% on warm small trees; peak memory stays bounded with `-a`.

### Phase 5: packaging and install (S)

- Linux and macOS release archives include `du` and `gdu` symlinks next to `hyperdu`.
- README "Use as du": put the link in `/usr/local/bin` or `~/.local/bin` ahead of `/usr/bin`; never overwrite the package-managed `/usr/bin/du`; `sudo` uses `secure_path`, so root scripts need `/usr/local/bin`; shell aliases do not affect scripts.
- NuGet tool: document `hyperdu du` and a wrapper script, and state it is not a drop-in (JIT startup, no `argv[0]`).
- CI startup check on the AOT binary (for example under 5 ms for `du FILE`).

**Done when:** the install steps work on a fresh VM and the `du` symlink from the release archive passes the harness.

### Phase 6: later

- BSD/macOS persona (D2 C).
- Move the TUI onto `DirectoryReader` (fixes non-UTF-8 dirs and apparent-vs-disk size there too); optionally D5 B.
- Windows du persona via `NtQueryDirectoryFile` (`FILE_ID_FULL_DIR_INFORMATION` returns allocation size and file id).

## 6. Option coverage

| Option | Notes | Phase |
| :--- | :--- | :--- |
| `-0, --null` | NUL instead of newline | 1 |
| `-a, --all` | Files too; error with `-s` | 1 |
| `--apparent-size` | `st_size`; directories use their own `st_size` | 1 |
| `-B, --block-size=SIZE` | `K`..`Q`, `KB`..`QB`, `KiB`; `'` grouping; `human-readable`, `si`; `R`/`Q` exceed 64 bits, match GNU overflow errors | 1 |
| `-b, --bytes` | `--apparent-size --block-size=1` | 1 |
| `-c, --total` | Adds `SIZE<TAB>total` | 1 |
| `-D, -H, --dereference-args` | Follow symlink operands only | 2 |
| `-d, --max-depth=N` | Limits printing, not traversal (errors below the depth still print) | 1 |
| `--files0-from=F` | NUL-separated list, `-` is stdin, no operands allowed | 2 |
| `-h, --human-readable` | One decimal below 10, rounded up | 1 |
| `--inodes` | Counts inodes | 2 |
| `-k`, `-m` | 1 KiB, 1 MiB units | 1 |
| `-L, --dereference` | Follow all; directory dedupe; cycle warning | 2 |
| `-l, --count-links` | Disable inode dedupe | 2 |
| `-P, --no-dereference` | Default | 2 |
| `-S, --separate-dirs` | Directory sizes exclude subdirectories | 1 |
| `--si` | Powers of 1000, lowercase `k` | 1 |
| `-s, --summarize` | Same as `-d 0`, with GNU's warning and error rules | 1 |
| `-t, --threshold=SIZE` | Negative excludes larger entries | 2 |
| `--time[=WORD]`, `--time-style=STYLE` | Newest time in the subtree; nanoseconds | 3 |
| `--exclude=PATTERN`, `-X, --exclude-from=FILE` | gnulib semantics | 2 |
| `-x, --one-file-system` | Compare `st_dev` with the operand | 2 |
| `--help`, `--version` | hyperdu-branded | 1 |
| Env: `DU_BLOCK_SIZE`, `BLOCK_SIZE`, `BLOCKSIZE`, `POSIXLY_CORRECT`, `TIME_STYLE`, `TZ`, `LC_*` | Same precedence as GNU | 1 and 3 |

## 7. Verified reference behaviors

Captured from GNU du 9.7 on tmpfs, 2026-10-31. Seed the harness with these.

- `du d/ d` prints the root as `d/` and children as `d/c`, then prints nothing for the second operand (all inodes already seen).
- `du d/a d` does not re-list `d/a` under `d`; a hard link already counted under `d/a` adds 0 to `d/c`.
- `du -aL d` with `d/sl -> a` lists `d/sl/...` (first in readdir order) and skips `d/a` as already seen.
- `du --exclude=d d` prints nothing: exclusion applies to operands.
- `du --thr=1M` is accepted. `du --t` fails with `option '--t' is ambiguous; possibilities: '--total' '--threshold' '--time' '--time-style'`, status 1.
- `-s -a`: `cannot both summarize and show all entries`, status 1. `-s -d1`: `warning: summarizing conflicts with --max-depth=1`, status 1. `-s -d0`: warning only, status 0.
- `POSIXLY_CORRECT=1`: 512-byte units, and `du d -s` treats `-s` as a file name.
- `--si` gives `8.2k`, `-h` gives `8.0K`, `DU_BLOCK_SIZE="'1"` gives `8,192`.
- `--time` defaults to `2026-10-31 15:43`; `--time-style=full-iso` gives `2026-10-31 15:43:07.832641581 -0400`.
- Unreadable dir: `du: cannot read directory 'err1/locked': Permission denied`, the dir is still listed, status 1, and the error appears even with `--max-depth=0`.
- Quoting varies by message under `en_US.UTF-8`: `'err1/locked'` but `extra operand ‘err1’`. Port gnulib quotearg styles rather than hard-coding quotes.

## 8. Risks

- **Struct layouts:** Linux `statx` has one layout on every arch. macOS x64 needs `$INODE64` symbols (`fstatat$INODE64`, `readdir$INODE64`); arm64 does not. musl only has a `statx` wrapper in recent versions, so keep an `fstatat` fallback.
- **Reference drift:** GNU output changes between releases; the pinned version is the contract.
- **Version sniffing:** `du --version | grep GNU` will not match. This is intentional (no impersonation); document it.
- **Stream interleaving:** GNU buffers stdout but not stderr, so combined `2>&1` order depends on flush timing. The harness compares the streams separately.
- **HDD thrashing:** keep the rotational heuristic (1 worker) in the du persona.
- **Memory with `-a` on huge trees:** handled by streaming and backpressure; covered by the Phase 4 benchmark.
- **Two personas to maintain:** freeze the du surface to GNU and let the harness gate every change.
