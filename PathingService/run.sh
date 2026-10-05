#!/usr/bin/env bash
# Serve the Pathing Service from this folder: data_config.json is resolved from the
# working directory. Listens on http://localhost:5010 (Contracts' PathingApi.DefaultUrl);
# Swagger UI is at /swagger. Extra arguments pass through, e.g. ./run.sh --exp=forever
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --configuration Release -- "$@"
