#!/usr/bin/env bash
# Serve the Bot Manager from this folder on http://localhost:5030 (Contracts' BotManagerApi.DefaultUrl);
# Swagger UI is at /swagger. Its bots, assignments and run history live in ./data (the Data setting).
# Agents dial out to it; agents on other PCs need it to listen beyond localhost, with a key:
#   ApiKey=<secret> ./run.sh --urls=http://0.0.0.0:5030
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --configuration Release -- "$@"
