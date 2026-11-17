#!/usr/bin/env bash
set -euo pipefail

TAR_GZ=""
EXTRACT_DIR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --extract-dir)
      EXTRACT_DIR="$2"
      shift 2
      ;;
    -h|--help)
      echo "Usage: $0 <archive.tar.gz> [--extract-dir <dir>]"
      exit 0
      ;;
    -*)
      echo "Unknown option: $1" >&2
      exit 2
      ;;
    *)
      if [[ -n "$TAR_GZ" ]]; then
        echo "Unexpected extra argument: $1" >&2
        exit 2
      fi
      TAR_GZ="$1"
      shift
      ;;
  esac
done

if [[ -z "$TAR_GZ" || ! -f "$TAR_GZ" ]]; then
  echo "Error: archive not found: ${TAR_GZ:-<empty>}" >&2
  exit 1
fi

TEMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/hyperdu-pkg-verify.XXXXXX")"
cleanup() {
  rm -rf "$TEMP_ROOT" || true
}
trap cleanup EXIT

if [[ -z "$EXTRACT_DIR" ]]; then
  EXTRACT_DIR="$TEMP_ROOT/extracted"
fi
mkdir -p "$EXTRACT_DIR"

listing="$(tar -tvf "$TAR_GZ")"
names="$(tar -tzf "$TAR_GZ")"
echo "$listing"
for required in hyperdu LICENSE README.md; do
  if ! printf '%s\n' "$names" | grep -Fx "$required" >/dev/null; then
    echo "Error: missing '$required' in $TAR_GZ" >&2
    exit 1
  fi
done
if ! printf '%s\n' "$listing" | grep -F "du -> hyperdu" >/dev/null; then
  echo "Error: missing symlink 'du -> hyperdu' in $TAR_GZ" >&2
  exit 1
fi
if ! printf '%s\n' "$listing" | grep -F "gdu -> hyperdu" >/dev/null; then
  echo "Error: missing symlink 'gdu -> hyperdu' in $TAR_GZ" >&2
  exit 1
fi

tar -xzf "$TAR_GZ" -C "$EXTRACT_DIR"

test -f "$EXTRACT_DIR/hyperdu"
test -x "$EXTRACT_DIR/hyperdu"
test -L "$EXTRACT_DIR/du"
test -L "$EXTRACT_DIR/gdu"

du_target="$(readlink "$EXTRACT_DIR/du")"
gdu_target="$(readlink "$EXTRACT_DIR/gdu")"
if [[ "$du_target" != "hyperdu" || "$gdu_target" != "hyperdu" ]]; then
  echo "Error: expected relative symlinks to 'hyperdu', got du->'$du_target', gdu->'$gdu_target'" >&2
  exit 1
fi

hyper_ver="$("$EXTRACT_DIR/hyperdu" --version)"
gdu_ver="$("$EXTRACT_DIR/gdu" --version)"

echo "hyperdu --version: $hyper_ver"
echo "gdu --version:     $gdu_ver"

if [[ "$hyper_ver" != hyperdu\ * ]]; then
  echo "Error: unexpected hyperdu --version output: $hyper_ver" >&2
  exit 1
fi
if ! printf '%s\n' "$gdu_ver" | grep -E '^du \(hyperdu\) ' >/dev/null; then
  echo "Error: unexpected gdu --version output: $gdu_ver" >&2
  exit 1
fi

if [[ "$(uname -s)" == "Darwin" ]]; then
  du_gnu_ver="$(HYPERDU_DU_DIALECT=gnu "$EXTRACT_DIR/du" --version)"
  echo "du (gnu env) --version: $du_gnu_ver"
  if ! printf '%s\n' "$du_gnu_ver" | grep -E '^du \(hyperdu\) ' >/dev/null; then
    echo "Error: unexpected HYPERDU_DU_DIALECT=gnu du --version output: $du_gnu_ver" >&2
    exit 1
  fi

  set +e
  bsd_ver_err="$("$EXTRACT_DIR/du" --version 2>&1)"
  bsd_ver_rc=$?
  set -e
  if [[ $bsd_ver_rc -ne 64 ]] || ! printf '%s\n' "$bsd_ver_err" | grep -F "usage: du" >/dev/null; then
    echo "Error: expected macOS du symlink to use BSD dialect (rc=64), got rc=$bsd_ver_rc: $bsd_ver_err" >&2
    exit 1
  fi

  "$EXTRACT_DIR/du" -Ah -d 0 "$EXTRACT_DIR/README.md" | grep -E '^[[:space:]]*[0-9]+(\.[0-9]+)?[BKMGTP][[:space:]]+.*README\.md$' >/dev/null
else
  du_ver="$("$EXTRACT_DIR/du" --version)"
  echo "du --version:      $du_ver"
  if ! printf '%s\n' "$du_ver" | grep -E '^du \(hyperdu\) ' >/dev/null; then
    echo "Error: unexpected du --version output: $du_ver" >&2
    exit 1
  fi
fi

# Verify PATH lookup precedence with extracted binaries
PATH_CHECK_BIN="$TEMP_ROOT/path-bin"
mkdir -p "$PATH_CHECK_BIN"
tar -xzf "$TAR_GZ" -C "$PATH_CHECK_BIN" hyperdu du gdu
resolved_du="$(PATH="$PATH_CHECK_BIN:/usr/bin:/bin" command -v du)"
if [[ "$resolved_du" != "$PATH_CHECK_BIN/du" ]]; then
  echo "Error: expected PATH lookup to resolve $PATH_CHECK_BIN/du, got $resolved_du" >&2
  exit 1
fi

PATH="$PATH_CHECK_BIN:/usr/bin:/bin" du -s "$EXTRACT_DIR/README.md" | grep -E '^[0-9]+[[:space:]]+.*README\.md$' >/dev/null
PATH="$PATH_CHECK_BIN:/usr/bin:/bin" gdu -s "$EXTRACT_DIR/README.md" | grep -E '^[0-9]+[[:space:]]+.*README\.md$' >/dev/null

# Verify SIGPIPE exit status (141) under bash pipefail with no broken-pipe noise
PIPE_TREE="$TEMP_ROOT/pipe-tree"
mkdir -p "$PIPE_TREE"
i=0
while [ "$i" -lt 4000 ]; do
  : > "$PIPE_TREE/f_$i"
  i=$((i + 1))
done

pipe_stderr="$TEMP_ROOT/pipe.err"
set +e
bash -c 'set -o pipefail; "$1" -a "$2" | head -n 1 >/dev/null' _ "$EXTRACT_DIR/du" "$PIPE_TREE" 2>"$pipe_stderr"
pipe_rc=$?
set -e

if [[ -s "$pipe_stderr" ]]; then
  echo "Error: unexpected stderr during SIGPIPE check:" >&2
  cat "$pipe_stderr" >&2
  exit 1
fi
if [[ $pipe_rc -ne 141 ]]; then
  echo "Error: expected bash pipefail exit code 141 on broken pipe, got $pipe_rc" >&2
  exit 1
fi

echo "Verified Unix release archive successfully ($TAR_GZ, pipefail rc=141)."
