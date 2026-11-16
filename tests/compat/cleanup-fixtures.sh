#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <fixture-root>" >&2
  exit 2
fi

FIXTURE_ROOT="$1"
if [[ ! -d "$FIXTURE_ROOT" ]]; then
  exit 0
fi

FIXTURE_ROOT="$(cd "$FIXTURE_ROOT" && pwd)"

if [[ -d "$FIXTURE_ROOT/err1/locked" ]]; then
  chmod u+rwx "$FIXTURE_ROOT/err1/locked" 2>/dev/null || true
fi

if [[ "$(uname -s)" == "Linux" ]] && [[ -d "$FIXTURE_ROOT/mnt/submount" ]]; then
  if command -v mountpoint >/dev/null 2>&1 && mountpoint -q "$FIXTURE_ROOT/mnt/submount"; then
    sudo -n umount "$FIXTURE_ROOT/mnt/submount" 2>/dev/null || umount "$FIXTURE_ROOT/mnt/submount" 2>/dev/null || true
  fi
fi

# Delete tree using fd-relative traversal so deep/ (>PATH_MAX) removes cleanly.
python3 - "$FIXTURE_ROOT" <<'PY'
import os
import stat
import sys

root = sys.argv[1]

def delete_dir_contents(dir_fd: int):
    with os.scandir(dir_fd) as it:
        entries = [(entry.name, entry.is_dir(follow_symlinks=False)) for entry in it]
    for name, is_dir in entries:
        if is_dir:
            try:
                child_fd = os.open(name, os.O_RDONLY, dir_fd=dir_fd)
            except PermissionError:
                os.chmod(name, 0o700, dir_fd=dir_fd)
                child_fd = os.open(name, os.O_RDONLY, dir_fd=dir_fd)
            try:
                delete_dir_contents(child_fd)
            finally:
                os.close(child_fd)
            os.rmdir(name, dir_fd=dir_fd)
        else:
            os.unlink(name, dir_fd=dir_fd)

parent = os.path.dirname(root)
base = os.path.basename(root)
parent_fd = os.open(parent, os.O_RDONLY)
try:
    root_fd = os.open(base, os.O_RDONLY, dir_fd=parent_fd)
    try:
        delete_dir_contents(root_fd)
    finally:
        os.close(root_fd)
    os.rmdir(base, dir_fd=parent_fd)
finally:
    os.close(parent_fd)
PY
