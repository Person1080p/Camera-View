# Builds the embedded Tailscale helper (tsproxy) for every platform Camera Viewer ships on.
# Output: tailscale/bin/<rid>/tsproxy[.exe], which the server project copies next to its own binaries.
# Requires Go (winget install GoLang.Go). Pure Go, so cross-compiling needs nothing else.
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'tsproxy'
$bin = Join-Path $PSScriptRoot 'bin'

if (-not (Get-Command go -ErrorAction SilentlyContinue) -and (Test-Path 'C:\Program Files\Go\bin\go.exe')) {
    $env:PATH = "C:\Program Files\Go\bin;$env:PATH"
}

$targets = @(
    @{ Rid = 'win-x64';     GOOS = 'windows'; GOARCH = 'amd64' },
    @{ Rid = 'linux-x64';   GOOS = 'linux';   GOARCH = 'amd64' },
    @{ Rid = 'linux-arm64'; GOOS = 'linux';   GOARCH = 'arm64' }
)

Push-Location $src
try {
    $env:CGO_ENABLED = '0'
    foreach ($t in $targets) {
        $env:GOOS = $t.GOOS
        $env:GOARCH = $t.GOARCH
        $exe = if ($t.GOOS -eq 'windows') { 'tsproxy.exe' } else { 'tsproxy' }
        $out = Join-Path (Join-Path $bin $t.Rid) $exe
        Write-Host "Building $($t.Rid)..."
        go build -trimpath -ldflags '-s -w' -o $out .
        if ($LASTEXITCODE -ne 0) { throw "go build failed for $($t.Rid)" }
    }
}
finally {
    Remove-Item Env:GOOS, Env:GOARCH, Env:CGO_ENABLED -ErrorAction SilentlyContinue
    Pop-Location
}
