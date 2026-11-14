# ADR 0001: Native filesystem access through libc

**Status:** Accepted, 2026-11-14
**Decision issue:** [jav76/hyperdu#11](https://github.com/jav76/hyperdu/issues/11) (D4 in [the du compatibility plan](../du-compatibility-plan.md))

## Context

The scanner used .NET's `FileSystemEnumerator`. On Linux and macOS that has two costs:

- It stats every entry by its full path, so the kernel walks every path component again for each file.
- Names are decoded to UTF-16. A name that is not valid UTF-8 cannot be opened again: such a directory shows as "Directory Not Found" and its contents are missing from the totals.

A Rust rewrite was considered. Toy walkers on a warm-cache `/usr` (363k files, 29k dirs) showed that C# calling libc directly matches Rust std (343 ms vs 349 ms on 1 thread, 32 ms vs 34 ms on 24 threads), so the gap was the access pattern, not the language.

## Decision

On Linux and macOS (x64 and arm64), read directories with libc through `LibraryImport`:

- `open` + `fdopendir` + `readdir`, keeping names as raw bytes.
- Per-entry `statx` (Linux) or `fstatat` (macOS) relative to the open directory's fd.
- Intel macOS uses the `$INODE64` entry points; arm64 macOS uses the plain ones.
- A startup probe binds every entry point and checks the `dirent` and `stat` layouts against `/`. Any failure falls back to the managed enumerator, as does every other platform (Windows) and `HYPERDU_NATIVE_IO=0`.

`DirectoryNode` keeps its string `Path` and `Name` for display and stores raw name bytes only for names that are not valid UTF-8. The exact byte path is rebuilt from the parent chain when needed, so valid trees pay no memory cost.

## Consequences

- The real scanner is faster on warm-cache `/usr`: 695 ms to 436 ms on 1 worker, 75 ms to 55 ms on 24 workers, with no change in peak memory.
- Directories whose names are not valid UTF-8 are scanned, listed and rescanned correctly.
- Directory symlink sizes now use the link's own `st_size` (length of the link text) on Linux and macOS, instead of the length of the resolved absolute target path. Windows keeps the old behavior.
- hyperdu now owns the `dirent`/`stat` struct offsets for Linux and macOS. The probe turns a layout mismatch into a slower fallback rather than wrong numbers.
- Remaining overhead versus a bare walker is per-directory bookkeeping in `ParallelScanner` (async state machines, queue locking, delta flushing), which is independent of this decision.
- Not covered yet: directories deeper than `PATH_MAX` (still opened by full path) and `getdents64` batching (option B), both left for later.
