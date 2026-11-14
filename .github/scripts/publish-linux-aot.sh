#!/usr/bin/env bash
# Native AOT publish for Linux, run inside almalinux:8 (glibc 2.28).
# Linking against an old glibc keeps the binary's floor at GLIBC_2.17; building on
# Ubuntu 22.04/24.04 binds pthread/dl symbols to GLIBC_2.34 instead.
#
# Usage (from the repo root, as root inside the container):
#   bash .github/scripts/publish-linux-aot.sh <rid> <output-dir>
set -euo pipefail

rid="$1"
out="$2"

dnf install -y -q --setopt=install_weak_deps=False clang zlib-devel libicu git

curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /opt/dotnet
export PATH="/opt/dotnet:$PATH" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet publish hyperdu.Cli/hyperdu.Cli.csproj -c Release -r "$rid" -p:PublishAot=true -o "$out"
