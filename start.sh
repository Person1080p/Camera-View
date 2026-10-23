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

# The embedded Tailscale helper is optional; build it when Go is available and it's missing.
case "$(uname -m)" in aarch64|arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac
if [[ ! -x "$root/tailscale/bin/$rid/tsproxy" ]]; then
  if command -v go > /dev/null; then
    bash "$root/tailscale/build.sh"
  else
    echo "warning: Go is not installed, so the embedded Tailscale helper was not built; Tailscale devices will not work." >&2
  fi
fi

exec dotnet run --project "$root/server/CameraViewer.Api" --launch-profile http
