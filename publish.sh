#!/usr/bin/env bash
set -e

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT="$DIR/hyperdu.Cli/hyperdu.Cli.csproj"

echo "Building cross-platform self-contained executables..."

platforms=("linux-x64" "linux-arm64" "win-x64" "win-x86" "osx-x64" "osx-arm64")

# Detect host OS and architecture to determine where Native AOT can be used
HOST_OS="linux"
if [[ "$OSTYPE" == "darwin"* ]]; then
    HOST_OS="osx"
elif [[ "$OSTYPE" == "msys" || "$OSTYPE" == "cygwin" || "$OSTYPE" == "win32" ]]; then
    HOST_OS="win"
fi

HOST_ARCH=$(uname -m)
if [[ "$HOST_ARCH" == "x86_64" ]]; then
    HOST_ARCH="x64"
elif [[ "$HOST_ARCH" == "aarch64" || "$HOST_ARCH" == "arm64" ]]; then
    HOST_ARCH="arm64"
fi

HOST_RID="${HOST_OS}-${HOST_ARCH}"

for platform in "${platforms[@]}"; do
    # Clean obj and bin to prevent RID/AOT asset caching conflicts
    rm -rf "$DIR/hyperdu.Cli/obj" "$DIR/hyperdu.Cli/bin" "$DIR/hyperdu.Core/obj" "$DIR/hyperdu.Core/bin"

    if [[ "$platform" == "$HOST_RID" ]]; then
        echo "Publishing for $platform using Native AOT..."
        dotnet publish "$PROJECT" \
            -c Release \
            -r "$platform" \
            -p:PublishAot=true \
            -o "$DIR/publish/$platform"
    else
        echo "Publishing for $platform (Cross-OS fallback to Single File)..."
        dotnet publish "$PROJECT" \
            -c Release \
            -r "$platform" \
            --self-contained true \
            -p:PublishSingleFile=true \
            -p:PublishTrimmed=true \
            -o "$DIR/publish/$platform"
    fi
done

echo "All targets published successfully in the './publish/' folder!"
