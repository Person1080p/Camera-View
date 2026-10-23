#!/usr/bin/env bash
# Builds the embedded Tailscale helper (tsproxy) for every platform Camera Viewer ships on.
# Output: tailscale/bin/<rid>/tsproxy[.exe], which the server project copies next to its own binaries.
# Requires Go 1.23+. Pure Go, so cross-compiling needs nothing else.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
cd "$here/tsproxy"

build() {
  local rid=$1 goos=$2 goarch=$3 exe=tsproxy
  [[ $goos == windows ]] && exe=tsproxy.exe
  echo "Building $rid..."
  CGO_ENABLED=0 GOOS=$goos GOARCH=$goarch go build -trimpath -ldflags '-s -w' -o "$here/bin/$rid/$exe" .
}

build win-x64 windows amd64
build linux-x64 linux amd64
build linux-arm64 linux arm64
