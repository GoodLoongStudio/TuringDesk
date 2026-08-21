param(
    [string]$Root
)

$ErrorActionPreference = "Stop"
if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$exe = Join-Path $Root "src\TuringDesk.Desktop\bin\Release\net8.0-windows10.0.19041.0\TuringDesk.Desktop.exe"
if (-not (Test-Path $exe)) {
    $exe = Get-ChildItem (Join-Path $Root "src\TuringDesk.Desktop\bin\Release") -Filter "TuringDesk.Desktop.exe" -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $exe -or -not (Test-Path $exe)) {
    throw "Desktop executable was not found. Build TuringDesk.Desktop first."
}

$statusPath = Join-Path $env:LOCALAPPDATA "TuringDesk\app-search-self-test.json"
if (Test-Path $statusPath) { Remove-Item $statusPath -Force -ErrorAction SilentlyContinue }

Write-Host "Verifying L1 application discovery + fuzzy/pinyin search..." -ForegroundColor Cyan
$process = Start-Process $exe -ArgumentList "--verify-app-search" -WorkingDirectory (Split-Path $exe -Parent) -Wait -PassThru

if (Test-Path $statusPath) {
    Write-Host "L1 app search self-test:" -ForegroundColor DarkGray
    Get-Content $statusPath | Write-Host
}

if ($process.ExitCode -ne 0) {
    throw "L1 app search self-test failed with exit code $($process.ExitCode)."
}

Write-Host "L1 app search self-test passed." -ForegroundColor Green
