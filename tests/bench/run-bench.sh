#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

USE_AOT=1
PASSTHROUGH_ARGS=()

for arg in "$@"; do
  if [[ "$arg" == "--no-aot" ]]; then
    USE_AOT=0
  else
    PASSTHROUGH_ARGS+=("$arg")
  fi
done

BENCH_BIN_DIR="$REPO_ROOT/artifacts/bench-bin"
mkdir -p "$BENCH_BIN_DIR"

resolve_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os:$arch" in
    Linux:x86_64) echo "linux-x64" ;;
    Linux:aarch64|Linux:arm64) echo "linux-arm64" ;;
    Darwin:x86_64) echo "osx-x64" ;;
    Darwin:arm64) echo "osx-arm64" ;;
    *) echo "" ;;
  esac
}

find_cpp_compiler() {
  for candidate in clang clang-18 clang-17 clang-16 gcc; do
    if command -v "$candidate" >/dev/null 2>&1; then
      echo "$candidate"
      return 0
    fi
  done
  return 1
}

if ! command -v hyperfine >/dev/null 2>&1; then
  echo "Note: 'hyperfine' not found on PATH; using built-in C# Stopwatch fallback timing engine."
  echo "      Install hyperfine for statistical outlier analysis (e.g. 'cargo install hyperfine' or apt/brew)."
fi

RID="$(resolve_rid)"
CPP_COMPILER=""
if [[ "$USE_AOT" -eq 1 ]] && [[ -n "$RID" ]] && CPP_COMPILER="$(find_cpp_compiler)"; then
  echo "Publishing Native AOT hyperdu binary (RID=$RID, CppCompilerAndLinker=$CPP_COMPILER)..."
  AOT_ARGS=(-c Release -r "$RID" -p:PublishAot=true -o "$BENCH_BIN_DIR")
  if [[ "$CPP_COMPILER" != "clang" ]]; then
    AOT_ARGS+=(-p:CppCompilerAndLinker="$CPP_COMPILER")
  fi
  dotnet publish "$REPO_ROOT/hyperdu.Cli/hyperdu.Cli.csproj" "${AOT_ARGS[@]}" --nologo -v quiet
else
  echo "Publishing framework-dependent Release hyperdu binary (use Native AOT by installing clang)..."
  dotnet publish "$REPO_ROOT/hyperdu.Cli/hyperdu.Cli.csproj" -c Release -o "$BENCH_BIN_DIR" --nologo -v quiet
fi

HYPERDU_EXE="$BENCH_BIN_DIR/hyperdu"
if [[ ! -f "$HYPERDU_EXE" && -f "$BENCH_BIN_DIR/hyperdu.exe" ]]; then
  HYPERDU_EXE="$BENCH_BIN_DIR/hyperdu.exe"
fi

if [[ -f "$HYPERDU_EXE" ]] && [[ "$HYPERDU_EXE" != *.exe ]]; then
  ln -sf "$(basename "$HYPERDU_EXE")" "$BENCH_BIN_DIR/du"
  ln -sf "$(basename "$HYPERDU_EXE")" "$BENCH_BIN_DIR/gdu"
fi

echo "Running CLI benchmark suite..."
dotnet run -c Release --project "$REPO_ROOT/hyperdu.Benchmarks/hyperdu.Benchmarks.csproj" -- \
  cli --hyperdu-bin "$HYPERDU_EXE" "${PASSTHROUGH_ARGS[@]}"
