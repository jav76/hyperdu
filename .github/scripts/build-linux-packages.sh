#!/usr/bin/env bash
# Builds .deb and .rpm packages from the Linux release tarballs with nFPM.
# Usage: build-linux-packages.sh <version> <tarball-dir> <out-dir>
# Expects hyperdu-linux-{x64,arm64}.tar.gz in <tarball-dir> and nfpm on PATH.
set -euo pipefail

version="$1"
tarballs="$(cd "$2" && pwd)"
mkdir -p "$3"
out="$(cd "$3" && pwd)"
config="$(cd "$(dirname "$0")/../../packaging" && pwd)/nfpm.yaml"

# Both deb and rpm sort "2.0.0~rc.1" before "2.0.0".
pkg_version="${version/-/\~}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for rid in x64 arm64; do
    case "$rid" in
        x64) arch=amd64 ;;
        arm64) arch=arm64 ;;
    esac
    src="$work/$rid"
    mkdir -p "$src"
    tar -xzf "$tarballs/hyperdu-linux-$rid.tar.gz" -C "$src"
    for packager in deb rpm; do
        (cd "$src" && NFPM_ARCH="$arch" VERSION="$pkg_version" \
            nfpm package --config "$config" --packager "$packager" --target "$out/")
    done
done

ls -l "$out"
