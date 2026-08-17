param(
    [switch]$SkipPull,
    [switch]$SkipRuntimeInstall,
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ToolsRoot = Join-Path $Root ".tools\quick-verify"
$NodeVersion = "22.19.0"
$PnpmVersion = "11.7.0"
$ForceBootstrap = $env:TURINGDESK_FORCE_BOOTSTRAP -eq "1"
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
                    (Join-Path $_.FullName "resources\app\git\bin\git.exe"),
                    (Join-Path $_.FullName "resources\app\git\mingw64\bin\git.exe")
                )
            } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($desktopGit) { return $desktopGit }
    }

    return $null
}

function Get-WindowsArchitecture {
    $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    switch ($osArch) {
        "Arm64" { return "arm64" }
        "X64" { return "x64" }
        default { throw "Unsupported Windows architecture for quick verification: $osArch" }
    }
}

function Test-NodeVersion([string]$NodePath) {
    if (-not $NodePath -or -not (Test-Path $NodePath)) { return $false }
    try {
        $versionText = (& $NodePath -p "process.versions.node").Trim()
        return ([version]$versionText -ge [version]$NodeVersion)
    }
    catch {
        return $false
    }
}

function Install-LocalNode([string]$Architecture) {
    $nodeRoot = Join-Path $ToolsRoot "node-v$NodeVersion-win-$Architecture"
    $nodeExe = Join-Path $nodeRoot "node.exe"
    if ((Test-Path $nodeExe) -and (Test-NodeVersion $nodeExe)) {
        return $nodeExe
    }

    New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
    $zipPath = Join-Path $ToolsRoot "node-v$NodeVersion-win-$Architecture.zip"
    $download = "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-$Architecture.zip"

    Write-Host "Node.js $NodeVersion was not found. Bootstrapping local Node.js $Architecture..." -ForegroundColor Yellow
    Write-Host "  $download" -ForegroundColor DarkGray
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Invoke-WebRequest $download -OutFile $zipPath -UseBasicParsing | Out-Null

    if (Test-Path $nodeRoot) { Remove-Item $nodeRoot -Recurse -Force }
    Expand-Archive $zipPath -DestinationPath $ToolsRoot -Force
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $nodeExe)) { throw "Local Node.js bootstrap did not produce $nodeExe" }
    return $nodeExe
}

function Resolve-Node([string]$Architecture) {
    if (-not $ForceBootstrap) {
        $candidate = Resolve-CommandPath "node" @(
            "$env:ProgramFiles\nodejs\node.exe",
            "$env:LOCALAPPDATA\Programs\nodejs\node.exe"
        )
        if ($candidate -and (Test-NodeVersion $candidate)) {
            return $candidate
        }
    }

    return Install-LocalNode $Architecture
}

function Resolve-Npm([string]$NodePath) {
    $nodeDir = Split-Path $NodePath -Parent
    foreach ($candidate in @(
        (Join-Path $nodeDir "npm.cmd"),
        (Resolve-CommandPath "npm" @("$env:ProgramFiles\nodejs\npm.cmd"))
    )) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    throw "npm was not found next to Node.js: $NodePath"
}

function Install-LocalPnpm([string]$NpmPath) {
    $pnpmRoot = Join-Path $ToolsRoot "pnpm-$PnpmVersion"
    $pnpmCmd = Join-Path $pnpmRoot "pnpm.cmd"

    if (-not $ForceBootstrap -and (Test-Path $pnpmCmd)) {
        try {
            if ((& $pnpmCmd --version).Trim() -eq $PnpmVersion) { return $pnpmCmd }
        }
        catch {
            # Reinstall below.
        }
    }

    Write-Host "pnpm $PnpmVersion was not found. Bootstrapping local pnpm..." -ForegroundColor Yellow
    if (Test-Path $pnpmRoot) { Remove-Item $pnpmRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $pnpmRoot | Out-Null
    & $NpmPath install --global --prefix $pnpmRoot "pnpm@$PnpmVersion" --no-audit --no-fund | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "pnpm bootstrap failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path $pnpmCmd)) { throw "pnpm bootstrap did not produce $pnpmCmd" }
    return $pnpmCmd
}

function Test-Dotnet8Sdk([string]$DotnetPath) {
    if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) { return $false }
    try {
        $sdks = & $DotnetPath --list-sdks
        return [bool]($sdks | Where-Object { $_ -match '^8\.' } | Select-Object -First 1)
    }
    catch {
        return $false
    }
}

function Install-LocalDotnet8([string]$Architecture) {
    $dotnetRoot = Join-Path $ToolsRoot "dotnet8-$Architecture"
    $dotnetExe = Join-Path $dotnetRoot "dotnet.exe"
    if ((Test-Path $dotnetExe) -and (Test-Dotnet8Sdk $dotnetExe)) {
        return $dotnetExe
    }

    New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
    $installer = Join-Path $ToolsRoot "dotnet-install.ps1"
    Write-Host ".NET 8 SDK was not found. Bootstrapping a local SDK ($Architecture)..." -ForegroundColor Yellow
    Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer -UseBasicParsing | Out-Null

    if (Test-Path $dotnetRoot) { Remove-Item $dotnetRoot -Recurse -Force }
    $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 8.0 -Architecture $Architecture -InstallDir $dotnetRoot -NoPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ".NET 8 SDK bootstrap failed with exit code $LASTEXITCODE" }
    if (-not (Test-Dotnet8Sdk $dotnetExe)) { throw "Local .NET bootstrap did not produce a usable .NET 8 SDK." }
    return $dotnetExe
}

function Resolve-Dotnet8([string]$Architecture) {
    if (-not $ForceBootstrap) {
        $candidate = Resolve-CommandPath "dotnet" @(
            "$env:ProgramFiles\dotnet\dotnet.exe"
        )
        if ($candidate -and (Test-Dotnet8Sdk $candidate)) {
            return $candidate
        }
    }

    return Install-LocalDotnet8 $Architecture
}

Write-Host "=== TuringDesk quick verification (main) ===" -ForegroundColor Cyan
$architecture = Get-WindowsArchitecture
$git = Resolve-Git

# Update source first when Git is available. Dependency bootstrap happens after
# the pull so the script itself can evolve without requiring local prerequisites.
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
        Write-Host "Git was not found. Continuing with the current checkout instead of failing." -ForegroundColor Yellow
        Write-Host "If you use GitHub Desktop, press Fetch origin / Pull origin before running this launcher." -ForegroundColor Yellow
    }
    $commit = "current-checkout"
}

Write-Host "Commit: $commit" -ForegroundColor Green
Write-Host "Checking build prerequisites..." -ForegroundColor Cyan
$node = Resolve-Node $architecture
$npm = Resolve-Npm $node
$pnpm = Install-LocalPnpm $npm
$dotnet = Resolve-Dotnet8 $architecture
Write-Host "Node: $((& $node -p 'process.versions.node').Trim()) [$node]" -ForegroundColor DarkGray
Write-Host "pnpm: $((& $pnpm --version).Trim()) [$pnpm]" -ForegroundColor DarkGray
Write-Host ".NET: $((& $dotnet --version).Trim()) [$dotnet]" -ForegroundColor DarkGray

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
    if (-not $SkipRuntimeInstall) {
        & $pnpm install --no-frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
    }

    & $pnpm build
    if ($LASTEXITCODE -ne 0) { throw "Runtime build failed" }
}
finally {
    Pop-Location
}

Write-Host "Building Desktop Release..." -ForegroundColor Cyan
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
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