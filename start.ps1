# Builds the React client (if needed) and starts the server on http://localhost:5080.
#   .\start.ps1           build client only when it hasn't been built yet
#   .\start.ps1 -Rebuild  always rebuild the client
param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$wwwroot = Join-Path $root 'server\CameraViewer.Api\wwwroot\index.html'

if (-not (Get-Command npm -ErrorAction SilentlyContinue) -and (Test-Path 'C:\Program Files\nodejs')) {
    $env:PATH = "C:\Program Files\nodejs;$env:PATH"
}

if ($Rebuild -or -not (Test-Path $wwwroot)) {
    Push-Location (Join-Path $root 'client')
    try {
        if (-not (Test-Path 'node_modules')) { npm install --no-fund --no-audit }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Client build failed' }
    }
    finally { Pop-Location }
}

dotnet run --project (Join-Path $root 'server\CameraViewer.Api') --launch-profile http
