#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
for directory in src/*/bin src/*/obj tests/*/bin tests/*/obj tools/*/bin tools/*/obj artifacts/build artifacts/installer TestResults; do
  if [[ -d "$directory" ]]; then
    rm -rf -- "$directory"
    echo "Cleaned $directory"
  fi
done
