#!/usr/bin/env bash
# Builds the React client (if needed) and starts the server on http://localhost:5080.
#   ./start.sh            build the client only when it hasn't been built yet
#   ./start.sh --rebuild  always rebuild the client
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"

if [[ "${1:-}" == "--rebuild" || ! -f "$root/server/CameraViewer.Api/wwwroot/index.html" ]]; then
  cd "$root/client"
  [[ -d node_modules ]] || npm install --no-fund --no-audit
  npm run build
fi

exec dotnet run --project "$root/server/CameraViewer.Api" --launch-profile http
