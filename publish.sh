#!/usr/bin/env bash
set -e

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT="$DIR/hyperdu.Cli/hyperdu.Cli.csproj"

echo "Building cross-platform self-contained executables..."

platforms=("linux-x64" "linux-arm64" "win-x64" "win-x86" "osx-x64" "osx-arm64")

for platform in "${platforms[@]}"; do
    echo "Publishing for $platform..."
    dotnet publish "$PROJECT" \
        -c Release \
        -r "$platform" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true \
        -o "$DIR/publish/$platform"
done

echo "All targets published successfully in the './publish/' folder!"
