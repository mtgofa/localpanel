#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

DOTNET_CMD="dotnet"
if ! command -v dotnet &>/dev/null; then
    if [ -f "$HOME/.dotnet/dotnet" ]; then
        DOTNET_CMD="$HOME/.dotnet/dotnet"
    else
        echo "Error: dotnet SDK is not found. Run ./scripts/setup-dotnet.sh first."
        exit 1
    fi
fi

echo "Building LocalPanel CLI for Linux..."
"$DOTNET_CMD" publish "$ROOT_DIR/src/LocalPanel.App/LocalPanel.App.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$ROOT_DIR/dist"

echo "Building LocalPanel Desktop GUI (Window) for Linux..."
"$DOTNET_CMD" publish "$ROOT_DIR/src/LocalPanel.Desktop/LocalPanel.Desktop.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$ROOT_DIR/dist"

chmod +x "$ROOT_DIR/dist/localpanel"
chmod +x "$ROOT_DIR/dist/localpanel-gui"

echo ""
echo "=========================================================="
echo " Build Succeeded!"
echo " 1. Desktop GUI Window : $ROOT_DIR/dist/localpanel-gui"
echo " 2. CLI Command Tool   : $ROOT_DIR/dist/localpanel"
echo "=========================================================="
