#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test IL2.slnx -c Release --nologo "$@"
