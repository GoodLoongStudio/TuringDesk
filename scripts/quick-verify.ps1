param(
    [switch]$SkipPull,
    [switch]$SkipRuntimeInstall,
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

function Resolve-CommandPath([string]$Name, [string[]]$Candidates = @()) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if (Test-Path $expanded) { return (Resolve-Path $expanded).Path }
    }

    return $null
}

function Resolve-Git {
    $direct = Resolve-CommandPath "git" @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "$env:ProgramFiles\Git\bin\git.exe",
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    if ($direct) { return $direct }

    # GitHub Desktop ships its own Git, but does not always add it to PATH.
    $desktopRoot = Join-Path $env:LOCALAPPDATA "GitHubDesktop"
    if (Test-Path $desktopRoot) {
        $desktopGit = Get-ChildItem $desktopRoot -Directory -Filter "app-*" -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                @(
                    (Join-Path $_.FullName "resources\app\git\cmd\git.exe"),
                    (Join-Path $_.FullName "resources\app\git\bin\git.exe")
                )
            } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($desktopGit) { return $desktopGit }
    }

    return $null
}

function Require-Command([string]$Name, [string[]]$Candidates = @()) {
    $resolved = Resolve-CommandPath $Name $Candidates
    if (-not $resolved) {
        throw "Missing required command: $Name"
    }
    return $resolved
}

Write-Host "=== TuringDesk quick verification (main) ===" -ForegroundColor Cyan
$git = Resolve-Git
$dotnet = Require-Command "dotnet" @(
    "$env:ProgramFiles\dotnet\dotnet.exe"
)
$node = Require-Command "node" @(
    "$env:ProgramFiles\nodejs\node.exe",
    "$env:LOCALAPPDATA\Programs\nodejs\node.exe"
)
$corepack = Resolve-CommandPath "corepack" @(
    "$env:ProgramFiles\nodejs\corepack.cmd",
    "$env:ProgramFiles\nodejs\corepack.ps1"
)
if (-not $corepack) { throw "Missing required command: corepack (install Node.js 22.19+ with Corepack)" }

if ($git) {
    Write-Host "Git: $git" -ForegroundColor DarkGray
    if (-not $SkipPull) {
        Write-Host "Updating main..." -ForegroundColor Cyan
        & $git fetch origin main
        if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }
        & $git switch main
        if ($LASTEXITCODE -ne 0) { throw "git switch main failed" }
        & $git pull --ff-only origin main
        if ($LASTEXITCODE -ne 0) { throw "git pull failed" }
    }

    $branch = (& $git branch --show-current).Trim()
    if ($branch -and $branch -ne "main") { throw "Quick verification only runs from main. Current branch: $branch" }
    $commit = (& $git rev-parse --short HEAD).Trim()
}
else {
    if (-not $SkipPull) {
        Write-Host "Git was not found in PATH, Git for Windows, or GitHub Desktop. Using the current checkout without pulling." -ForegroundColor Yellow
        Write-Host "If this folder came from GitHub Desktop, use Repository -> Pull origin first, then run QUICK-VERIFY.cmd again." -ForegroundColor Yellow
    }
    $commit = "current-checkout"
}

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
    & $corepack enable
    if ($LASTEXITCODE -ne 0) { throw "corepack enable failed" }

    if (-not $SkipRuntimeInstall) {
        & $corepack pnpm install --no-frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
    }

    & $corepack pnpm build
    if ($LASTEXITCODE -ne 0) { throw "Runtime build failed" }
}
finally {
    Pop-Location
}

Write-Host "Building Desktop Release..." -ForegroundColor Cyan
& $dotnet build "src/TuringDesk.Desktop/TuringDesk.Desktop.csproj" --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed" }

$exe = Join-Path $Root "src\TuringDesk.Desktop\bin\Release\net8.0-windows\TuringDesk.Desktop.exe"
if (-not (Test-Path $exe)) {
    $exe = Get-ChildItem (Join-Path $Root "src\TuringDesk.Desktop\bin\Release") -Filter "TuringDesk.Desktop.exe" -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $exe -or -not (Test-Path $exe)) { throw "Desktop executable was not found after build." }

if ($BuildOnly) {
    Write-Host "Quick verification build-only check passed." -ForegroundColor Green
    Write-Host "Desktop executable: $exe"
    exit 0
}

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
Write-Host "To relaunch after another pull, just double-click QUICK-VERIFY.cmd again." -ForegroundColor DarkGray
