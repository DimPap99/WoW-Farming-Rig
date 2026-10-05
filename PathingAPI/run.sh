#!/usr/bin/env bash
# Linux/macOS counterpart of run.bat: serve from this folder (data_config.json and
# wwwroot are resolved from the working directory) on http://localhost:5001.
# Extra arguments pass through, e.g. ./run.sh --exp=forever
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --configuration Release -- "$@"
