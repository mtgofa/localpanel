#!/usr/bin/env bash
set -e

echo "Setting up isolated .NET 8 SDK in ~/.dotnet..."
mkdir -p "$HOME/.dotnet"
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"

echo ""
echo ".NET 8 SDK installed successfully into $HOME/.dotnet"
echo "To use dotnet in terminal: export PATH=\"$HOME/.dotnet:\$PATH\""
