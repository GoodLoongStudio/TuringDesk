param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$targetFramework = "net8.0-windows10.0.19041.0"
$exe = Join-Path $Root "src\TuringDesk.Desktop\bin\$Configuration\$targetFramework\TuringDesk.Desktop.exe"

if (-not (Test-Path $exe)) {
    $releaseRoot = Join-Path $Root "src\TuringDesk.Desktop\bin\$Configuration"
    if (Test-Path $releaseRoot) {
        $exe = Get-ChildItem $releaseRoot -Filter "TuringDesk.Desktop.exe" -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

if (-not $exe -or -not (Test-Path $exe)) {
    throw "Desktop executable was not found. Build TuringDesk.Desktop first."
}

Write-Host "Verifying L3 native HttpClient path..." -ForegroundColor Cyan
Write-Host "  executable: $exe" -ForegroundColor DarkGray
Write-Host "  network: 127.0.0.1 loopback only; no Harness, Node, API key, or external provider" -ForegroundColor DarkGray

$process = Start-Process $exe `
    -ArgumentList "--verify-l3-http" `
    -WorkingDirectory (Split-Path $exe -Parent) `
    -PassThru `
    -Wait

if ($process.ExitCode -ne 0) {
    $log = Join-Path $env:LOCALAPPDATA "TuringDesk\logs\desktop.log"
    if (Test-Path $log) {
        Write-Host "--- desktop.log tail ---" -ForegroundColor Yellow
        Get-Content $log -Tail 80 | Write-Host
    }
    throw "L3 native HttpClient verification failed with exit code $($process.ExitCode)."
}

Write-Host "L3 native HttpClient verification passed." -ForegroundColor Green
