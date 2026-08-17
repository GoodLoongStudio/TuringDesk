param(
    [switch]$SkipPull,
    [switch]$SkipRuntimeInstall
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing required command: $Name"
    }
}

Write-Host "=== TuringDesk quick verification (main) ===" -ForegroundColor Cyan
Require-Command git
Require-Command dotnet
Require-Command node
Require-Command corepack

if (-not $SkipPull) {
    Write-Host "Updating main..." -ForegroundColor Cyan
    git fetch origin main
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }
    git switch main
    if ($LASTEXITCODE -ne 0) { throw "git switch main failed" }
    git pull --ff-only origin main
    if ($LASTEXITCODE -ne 0) { throw "git pull failed" }
}

$branch = (git branch --show-current).Trim()
if ($branch -ne "main") { throw "Quick verification only runs from main. Current branch: $branch" }
$commit = (git rev-parse --short HEAD).Trim()
Write-Host "Commit: $commit" -ForegroundColor Green

Write-Host "Stopping previous TuringDesk processes..." -ForegroundColor Cyan
Get-Process TuringDesk.Desktop,TuringDesk.ShellHost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$connections = Get-NetTCPConnection -LocalPort 4317 -State Listen -ErrorAction SilentlyContinue
foreach ($connection in $connections) {
    $owner = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
    if ($owner -and $owner.ProcessName -eq "node") {
        Write-Host "Stopping old TuringDesk runtime node PID $($owner.Id)..." -ForegroundColor DarkGray
        Stop-Process -Id $owner.Id -Force
    }
    elseif ($owner) {
        throw "Port 4317 is occupied by $($owner.ProcessName) PID $($owner.Id). Stop it before verification."
    }
}

$statusPath = Join-Path $env:LOCALAPPDATA "TuringDesk\desktop-engine-status.json"
if (Test-Path $statusPath) { Remove-Item $statusPath -Force }

Write-Host "Building Runtime..." -ForegroundColor Cyan
Push-Location (Join-Path $Root "runtime")
try {
    corepack enable
    if ($LASTEXITCODE -ne 0) { throw "corepack enable failed" }

    if (-not $SkipRuntimeInstall) {
        pnpm install --no-frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
    }

    pnpm build
    if ($LASTEXITCODE -ne 0) { throw "Runtime build failed" }
}
finally {
    Pop-Location
}

Write-Host "Building Desktop Release..." -ForegroundColor Cyan
dotnet build "src/TuringDesk.Desktop/TuringDesk.Desktop.csproj" --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed" }

$exe = Join-Path $Root "src\TuringDesk.Desktop\bin\Release\net8.0-windows\TuringDesk.Desktop.exe"
if (-not (Test-Path $exe)) {
    $exe = Get-ChildItem (Join-Path $Root "src\TuringDesk.Desktop\bin\Release") -Filter "TuringDesk.Desktop.exe" -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $exe -or -not (Test-Path $exe)) { throw "Desktop executable was not found after build." }

Write-Host "Starting latest Desktop from main..." -ForegroundColor Cyan
$process = Start-Process $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
Start-Sleep -Seconds 5
if ($process.HasExited) { throw "TuringDesk Desktop exited immediately with code $($process.ExitCode)." }

Write-Host "" 
Write-Host "=== LIVE CHECK ===" -ForegroundColor Green
Write-Host "Desktop PID: $($process.Id)"
Write-Host "Commit: $commit"

$runtimeReady = Get-NetTCPConnection -LocalPort 4317 -State Listen -ErrorAction SilentlyContinue
Write-Host ("Runtime 4317: " + $(if ($runtimeReady) { "LISTENING" } else { "NOT READY YET" }))

if (Test-Path $statusPath) {
    Write-Host "" 
    Write-Host "Desktop engine probe:" -ForegroundColor Cyan
    Get-Content $statusPath | Write-Host
}
else {
    Write-Host "Desktop engine probe has not appeared yet: $statusPath" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Verify these four things now:" -ForegroundColor Cyan
Write-Host "  1. Top-right button is a SETTINGS GEAR, not the old design/personalize icon."
Write-Host "  2. Settings -> Library -> apply Aurora / Neon / Orbital; PRIMARY DESKTOP must visibly change within ~1 second."
Write-Host "  3. After each scene switch rerun: Get-Content '$statusPath'"
Write-Host "     Primary monitor should show Attached=true and SceneId matching the selected scene."
Write-Host "  4. Submit an AI request. If no response arrives for 30 seconds, the search bar must collapse back to idle automatically."
Write-Host ""
Write-Host "To relaunch after another git pull, just run:" -ForegroundColor DarkGray
Write-Host "  powershell -ExecutionPolicy Bypass -File .\scripts\quick-verify.ps1"
