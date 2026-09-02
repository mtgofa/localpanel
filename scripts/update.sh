#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "=========================================="
echo "      LocalPanel One-Command Updater      "
echo "=========================================="

cd "$ROOT_DIR"

if [ -d ".git" ]; then
    echo "Pulling latest changes from repository..."
    git pull --rebase || git pull
fi

echo "Building updated version..."
"$ROOT_DIR/scripts/build.sh"

echo ""
echo "Update completed successfully!"
echo "You can now run: ./dist/localpanel status"
