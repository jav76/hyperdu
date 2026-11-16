#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 || $# -gt 4 ]]; then
  echo "Usage: $0 <hyperdu-binary> <output-tar-gz> [license-file] [readme-file]" >&2
  exit 2
fi

BINARY_PATH="$1"
OUTPUT_TAR_GZ="$2"
LICENSE_PATH="${3:-LICENSE}"
README_PATH="${4:-README.md}"

if [[ ! -f "$BINARY_PATH" ]]; then
  echo "Error: binary not found: $BINARY_PATH" >&2
  exit 1
fi
if [[ ! -f "$LICENSE_PATH" ]]; then
  echo "Error: LICENSE not found: $LICENSE_PATH" >&2
  exit 1
fi
if [[ ! -f "$README_PATH" ]]; then
  echo "Error: README.md not found: $README_PATH" >&2
  exit 1
fi

STAGE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/hyperdu-pkg-stage.XXXXXX")"
cleanup() {
  rm -rf "$STAGE_ROOT" || true
}
trap cleanup EXIT

mkdir -p "$(dirname "$OUTPUT_TAR_GZ")"

cp "$BINARY_PATH" "$STAGE_ROOT/hyperdu"
chmod 0755 "$STAGE_ROOT/hyperdu"
cp "$LICENSE_PATH" "$STAGE_ROOT/LICENSE"
cp "$README_PATH" "$STAGE_ROOT/README.md"
chmod 0644 "$STAGE_ROOT/LICENSE" "$STAGE_ROOT/README.md"

(
  cd "$STAGE_ROOT"
  ln -sf hyperdu du
  ln -sf hyperdu gdu
)

COPYFILE_DISABLE=1 tar -czf "$OUTPUT_TAR_GZ" -C "$STAGE_ROOT" hyperdu du gdu LICENSE README.md
echo "Packaged Unix release archive: $OUTPUT_TAR_GZ"
