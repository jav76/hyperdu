#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <fixture-root> [--with-mounts]" >&2
  exit 2
fi

FIXTURE_ROOT="$1"
WITH_MOUNTS=0
if [[ "${2:-}" == "--with-mounts" ]]; then
  WITH_MOUNTS=1
fi

mkdir -p "$FIXTURE_ROOT"
FIXTURE_ROOT="$(cd "$FIXTURE_ROOT" && pwd)"

python3 - "$FIXTURE_ROOT" <<'PY'
import os
import sys

root = sys.argv[1]

def write_bytes(path, data: bytes):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)

# 1. Hard links, symlinks, empty dirs, and Section 7 scenarios under d/
write_bytes(os.path.join(root, "d", "a", "file1.bin"), b"A" * 4096)
os.link(os.path.join(root, "d", "a", "file1.bin"), os.path.join(root, "d", "a", "hl_sub.bin"))
os.makedirs(os.path.join(root, "d", "c"), exist_ok=True)
os.link(os.path.join(root, "d", "a", "file1.bin"), os.path.join(root, "d", "c", "hl_cross.bin"))
write_bytes(os.path.join(root, "d", "c", "unique.bin"), b"C" * 8192)
write_bytes(os.path.join(root, "d", "tmp_ignored.tmp"), b"T" * 1024)
os.makedirs(os.path.join(root, "d", "empty_dir"), exist_ok=True)

os.symlink("a", os.path.join(root, "d", "sl"))
os.symlink(os.path.join("c", "unique.bin"), os.path.join(root, "d", "file_sl"))
os.symlink("missing_target", os.path.join(root, "d", "dangling_sl"))
os.symlink("loop_b", os.path.join(root, "d", "loop_a"))
os.symlink("loop_a", os.path.join(root, "d", "loop_b"))

# 2. Sparse files
sparse_dir = os.path.join(root, "sparse")
os.makedirs(sparse_dir, exist_ok=True)
with open(os.path.join(sparse_dir, "sparse_10m.bin"), "wb") as f:
    f.write(b"S" * 4096)
    f.truncate(10 * 1024 * 1024)
with open(os.path.join(sparse_dir, "hole_only.bin"), "wb") as f:
    f.truncate(1024 * 1024)

# 3. Unreadable directory (err1/locked)
write_bytes(os.path.join(root, "err1", "ok.txt"), b"ok\n" * 50)
locked_dir = os.path.join(root, "err1", "locked")
write_bytes(os.path.join(locked_dir, "secret.txt"), b"X" * 500)
os.chmod(locked_dir, 0o000)

# 4. Special names: raw non-UTF-8 bytes, newlines, tabs, quotes
names_dir = os.path.join(root, "names").encode("utf-8")
os.makedirs(names_dir, exist_ok=True)
write_bytes(names_dir + b"/has\nnewline.txt", b"N" * 32)
write_bytes(names_dir + b"/has\ttab.txt", b"T" * 32)
write_bytes(names_dir + b"/has 'single' and \"double\".txt", b"Q" * 32)

# APFS/NTFS may reject raw invalid UTF-8 bytes; create when filesystem allows it.
try:
    non_utf8_dir = names_dir + b"/dir\xfe"
    os.makedirs(non_utf8_dir, exist_ok=True)
    with open(non_utf8_dir + b"/payload.txt", "wb") as f:
        f.write(b"U" * 128)
    with open(names_dir + b"/bad\xffname", "wb") as f:
        f.write(b"B" * 64)
except OSError:
    pass

# 5. Directory with >10,000 entries (gnulib fts inode-sort threshold)
large_dir = os.path.join(root, "large-dir")
os.makedirs(large_dir, exist_ok=True)
for i in range(10_005):
    with open(os.path.join(large_dir, f"f_{i:05d}"), "wb"):
        pass

# 6. Depth beyond PATH_MAX (4096 bytes) created fd-relatively
deep_root = os.path.join(root, "deep")
os.makedirs(deep_root, exist_ok=True)
cur_fd = os.open(deep_root, os.O_RDONLY)
try:
    for level in range(260):
        seg = f"d_{level:03d}_segment12345"
        os.mkdir(seg, mode=0o755, dir_fd=cur_fd)
        next_fd = os.open(seg, os.O_RDONLY, dir_fd=cur_fd)
        os.close(cur_fd)
        cur_fd = next_fd
        if level in (0, 130, 259):
            leaf_fd = os.open(f"leaf_{level:03d}.dat", os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o644, dir_fd=cur_fd)
            try:
                os.write(leaf_fd, b"D" * 256)
            finally:
                os.close(leaf_fd)
finally:
    os.close(cur_fd)

# 7. Mount point target, fixed DST timestamps, & helper metadata files
write_bytes(os.path.join(root, "mnt", "outside.bin"), b"O" * 4096)
os.makedirs(os.path.join(root, "mnt", "submount"), exist_ok=True)
write_bytes(os.path.join(root, "exclude_patterns.txt"), b"*.tmp\n")
write_bytes(os.path.join(root, "files0.list"), b"d/a\x00d/c\x00sparse\x00")

times_dir = os.path.join(root, "times_dst")
times_sub = os.path.join(times_dir, "sub")
summer = os.path.join(times_dir, "summer.bin")
winter = os.path.join(times_sub, "winter.bin")
write_bytes(summer, b"S" * 4096)
write_bytes(winter, b"W" * 4096)
os.utime(summer, ns=(1751373296_987654321, 1751373296_987654321)) # 2025-07-01 12:34:56.987654321 UTC (EDT -0400)
os.utime(winter, ns=(1764592496_123456789, 1764592496_123456789)) # 2025-12-01 12:34:56.123456789 UTC (EST -0500)
os.utime(times_sub, ns=(1735689600_000000000, 1735689600_000000000))
os.utime(times_dir, ns=(1735689600_000000000, 1735689600_000000000))
PY

if [[ "$WITH_MOUNTS" -eq 1 ]] && [[ "$(uname -s)" == "Linux" ]]; then
  if sudo -n mount -t tmpfs -o size=2M tmpfs "$FIXTURE_ROOT/mnt/submount" 2>/dev/null; then
    mkdir -p "$FIXTURE_ROOT/mnt/submount/sub"
    python3 -c 'import os,sys; open(sys.argv[1],"wb").write(b"M"*8192); open(sys.argv[2],"wb").write(b"S"*4096)' \
      "$FIXTURE_ROOT/mnt/submount/inside.bin" "$FIXTURE_ROOT/mnt/submount/sub/inner.bin"
    echo "Mounted tmpfs at $FIXTURE_ROOT/mnt/submount"
  else
    echo "Warning: --with-mounts requested but passwordless sudo mount failed; using plain submount directory." >&2
    python3 -c 'import os,sys; os.makedirs(os.path.dirname(sys.argv[1]),exist_ok=True); open(sys.argv[1],"wb").write(b"M"*8192)' \
      "$FIXTURE_ROOT/mnt/submount/inside.bin"
  fi
else
  python3 -c 'import os,sys; os.makedirs(os.path.dirname(sys.argv[1]),exist_ok=True); open(sys.argv[1],"wb").write(b"M"*8192)' \
    "$FIXTURE_ROOT/mnt/submount/inside.bin"
fi

echo "Created compatibility fixtures at $FIXTURE_ROOT"
