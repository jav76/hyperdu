#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/PINNED_VERSION"

COREUTILS_DIR=""
CANDIDATE_DU=""
HYPERDU_BIN=""
BASELINE=0
TIMEOUT_SEC=5
JSON_OUT=""
MD_OUT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --coreutils-dir)
      COREUTILS_DIR="$2"
      shift 2
      ;;
    --candidate-du)
      CANDIDATE_DU="$2"
      shift 2
      ;;
    --hyperdu)
      HYPERDU_BIN="$2"
      shift 2
      ;;
    --timeout)
      TIMEOUT_SEC="$2"
      shift 2
      ;;
    --json)
      JSON_OUT="$2"
      shift 2
      ;;
    --markdown)
      MD_OUT="$2"
      shift 2
      ;;
    --baseline)
      BASELINE=1
      shift
      ;;
    -h|--help)
      echo "Usage: $0 [--candidate-du <path> | --hyperdu <path>] [--coreutils-dir <dir>] [--timeout <sec>] [--json <file>] [--markdown <file>] [--baseline]"
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 2
      ;;
  esac
done

SCRATCH_DIRS=()
cleanup() {
  for d in "${SCRATCH_DIRS[@]}"; do
    rm -rf "$d" || true
  done
}
trap cleanup EXIT

if [[ -z "$CANDIDATE_DU" ]]; then
  if [[ -z "$HYPERDU_BIN" ]]; then
    REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
    for cand in \
      "$REPO_ROOT/artifacts/compat-bin/du" \
      "$REPO_ROOT/artifacts/bench-bin/du" \
      "$REPO_ROOT/artifacts/compat-bin/hyperdu" \
      "$REPO_ROOT/artifacts/bench-bin/hyperdu"; do
      if [[ -e "$cand" ]]; then
        if [[ "$(basename "$cand")" == "du" ]]; then
          CANDIDATE_DU="$cand"
        else
          HYPERDU_BIN="$cand"
        fi
        break
      fi
    done
  fi
  if [[ -z "$CANDIDATE_DU" ]]; then
    if [[ -z "$HYPERDU_BIN" ]]; then
      echo "Error: pass --candidate-du or --hyperdu." >&2
      exit 2
    fi
    TMP_BIN="$(mktemp -d -t hyperdu-gnu-bin-XXXXXX)"
    SCRATCH_DIRS+=("$TMP_BIN")
    ln -sf "$(cd "$(dirname "$HYPERDU_BIN")" && pwd)/$(basename "$HYPERDU_BIN")" "$TMP_BIN/du"
    CANDIDATE_DU="$TMP_BIN/du"
  fi
fi

CANDIDATE_DU="$(cd "$(dirname "$CANDIDATE_DU")" && pwd)/$(basename "$CANDIDATE_DU")"

if [[ -z "$COREUTILS_DIR" ]]; then
  COREUTILS_DIR="$(mktemp -d -t coreutils-tests-XXXXXX)"
  SCRATCH_DIRS+=("$COREUTILS_DIR")
  echo "Fetching GNU coreutils tests/du at tag $PINNED_COREUTILS_TAG..."
  git clone --quiet --depth 1 --filter=blob:none --sparse --branch "$PINNED_COREUTILS_TAG" \
    https://github.com/coreutils/coreutils.git "$COREUTILS_DIR"
  git -C "$COREUTILS_DIR" sparse-checkout set --no-cone \
    /tests/du /tests/init.sh /init.cfg /tests/Coreutils.pm /tests/CuTmpdir.pm /tests/other-fs-tmpdir
fi

mkdir -p "$COREUTILS_DIR/src"
ln -sf "$CANDIDATE_DU" "$COREUTILS_DIR/src/du"
printf '#define HAVE_OPENAT 1\n' > "$COREUTILS_DIR/config.h"

TOTAL=0
PASSED=0
SKIPPED=0
FAILED=0
TIMED_OUT=0
RESULTS_LINES=()

for test_script in "$COREUTILS_DIR"/tests/du/*.sh; do
  name="tests/du/$(basename "$test_script")"
  TOTAL=$((TOTAL + 1))

  set +e
  (
    cd "$COREUTILS_DIR"
    exec 9>&2
    AWK=awk \
    CONFIG_HEADER="$COREUTILS_DIR/config.h" \
    abs_srcdir="$COREUTILS_DIR" \
    abs_top_srcdir="$COREUTILS_DIR" \
    srcdir="$COREUTILS_DIR" \
    built_programs="du" \
    VERSION="$PINNED_GNU_DU_VERSION" \
    timeout "${TIMEOUT_SEC}s" bash "$test_script" >/dev/null 2>&1
  )
  rc=$?
  set -e

  if [[ $rc -eq 0 ]]; then
    PASSED=$((PASSED + 1))
    status="PASS"
  elif [[ $rc -eq 77 ]]; then
    SKIPPED=$((SKIPPED + 1))
    status="SKIP"
  elif [[ $rc -eq 124 ]]; then
    TIMED_OUT=$((TIMED_OUT + 1))
    FAILED=$((FAILED + 1))
    status="TIMEOUT"
  else
    FAILED=$((FAILED + 1))
    status="FAIL($rc)"
  fi

  RESULTS_LINES+=("$name|$status")
done

echo "GNU coreutils ($PINNED_COREUTILS_TAG) tests/du/*.sh results:"
echo "  Passed: $PASSED / $TOTAL (Skipped: $SKIPPED, Failed: $FAILED, Timed out: $TIMED_OUT)"

if [[ -n "$JSON_OUT" ]]; then
  mkdir -p "$(dirname "$JSON_OUT")"
  python3 - "$JSON_OUT" "$PINNED_COREUTILS_TAG" "$CANDIDATE_DU" "$TOTAL" "$PASSED" "$SKIPPED" "$FAILED" "$TIMED_OUT" "${RESULTS_LINES[@]}" <<'PY'
import json, sys
out_path, tag, cand, total, passed, skipped, failed, timed_out = sys.argv[1:9]
rows = [item.split("|", 1) for item in sys.argv[9:]]
with open(out_path, "w", encoding="utf-8") as f:
    json.dump({
        "coreutils_tag": tag,
        "candidate_du": cand,
        "total": int(total),
        "passed": int(passed),
        "skipped": int(skipped),
        "failed": int(failed),
        "timed_out": int(timed_out),
        "tests": [{"name": n, "status": s} for n, s in rows],
    }, f, indent=2)
PY
fi

if [[ -n "$MD_OUT" ]]; then
  mkdir -p "$(dirname "$MD_OUT")"
  {
    echo "## GNU coreutils \`tests/du/\` Suite ($PINNED_COREUTILS_TAG)"
    echo ""
    echo "- **Candidate**: \`$CANDIDATE_DU\`"
    echo "- **Passed**: **$PASSED / $TOTAL** (Skipped: $SKIPPED, Failed: $FAILED, Timed out: $TIMED_OUT)"
    echo ""
    echo "| Test | Status |"
    echo "| :--- | :--- |"
    for item in "${RESULTS_LINES[@]}"; do
      t_name="${item%%|*}"
      t_status="${item##*|}"
      echo "| \`$t_name\` | $t_status |"
    done
  } > "$MD_OUT"
fi

if [[ "$BASELINE" -eq 0 ]] && [[ "$FAILED" -gt 0 ]]; then
  exit 1
fi
exit 0
