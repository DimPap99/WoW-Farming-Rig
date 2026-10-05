#!/usr/bin/env bash
# Serve the Control Center from this folder on http://localhost:5020 (Contracts'
# ControlCenterDefaults.DefaultUrl). It is a client of the Pathing Service at
# PathingService:Url (appsettings.json, default http://localhost:5010): start that first with
# ../PathingService/run.sh. Extra arguments pass through, e.g.
#   ./run.sh --PathingService:Url=http://other-host:5010
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --configuration Release -- "$@"
