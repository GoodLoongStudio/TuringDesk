param(
    [switch]$SkipPull,
    [switch]$SkipRuntimeInstall,
    [switch]$BuildOnly,
    [switch]$ResetEnvironment
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ToolsRoot = Join-Path $Root ".tools\quick-verify"
$ForceBootstrap = $env:TURINGDESK_FORCE_BOOTSTRAP -eq "1"
Set-Location $Root

# The quick verification environment is repository-pinned. Do not silently use
# whatever Node/.NET happens to be installed globally on the target machine.
$NodeVersion = (Get-Content (Join-Path $Root ".node-version") -Raw).Trim()
$DotnetConfig = Get-Content (Join-Path $Root "global.json") -Raw | ConvertFrom-Json
$DotnetVersion = [string]$DotnetConfig.sdk.version
$RuntimePackage = Get-Content (Join-Path $Root "runtime\package.json") -Raw | ConvertFrom-Json
$PnpmVersion = ([string]$RuntimePackage.packageManager) -replace '^pnpm@', ''

if ([string]::IsNullOrWhiteSpace($NodeVersion)) { throw ".node-version is empty." }
if ([string]::IsNullOrWhiteSpace($DotnetVersion)) { throw "global.json does not pin a .NET SDK version." }
if ([string]::IsNullOrWhiteSpace($PnpmVersion)) { throw "runtime/package.json does not pin pnpm." }

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

function Test-ExactNode([string]$NodePath) {
    if (-not $NodePath -or -not (Test-Path $NodePath)) { return $false }
    try { return ((& $NodePath -p "process.versions.node").Trim() -eq $NodeVersion) }
    catch { return $false }
}

function Ensure-LocalNode([string]$Architecture) {
    $nodeRoot = Join-Path $ToolsRoot "node-v$NodeVersion-win-$Architecture"
    $nodeExe = Join-Path $nodeRoot "node.exe"
    if (Test-ExactNode $nodeExe) {
        Write-Host "Using cached Node.js $NodeVersion." -ForegroundColor DarkGray
        return $nodeExe
    }

    New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
    $zipPath = Join-Path $ToolsRoot "node-v$NodeVersion-win-$Architecture.zip"
    $download = "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-$Architecture.zip"
    Write-Host "Initializing pinned Node.js $NodeVersion ($Architecture) once..." -ForegroundColor Yellow
    Invoke-WebRequest $download -OutFile $zipPath -UseBasicParsing | Out-Null
    if (Test-Path $nodeRoot) { Remove-Item $nodeRoot -Recurse -Force }
    Expand-Archive $zipPath -DestinationPath $ToolsRoot -Force
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    if (-not (Test-ExactNode $nodeExe)) { throw "Pinned Node.js bootstrap failed: $nodeExe" }
    return $nodeExe
}

function Resolve-Npm([string]$NodePath) {
    $nodeDir = Split-Path $NodePath -Parent
    $candidate = Join-Path $nodeDir "npm.cmd"
    if (Test-Path $candidate) { return $candidate }
    throw "npm was not found next to pinned Node.js: $NodePath"
}

function Ensure-LocalPnpm([string]$NpmPath) {
    $pnpmRoot = Join-Path $ToolsRoot "pnpm-$PnpmVersion"
    $pnpmCmd = Join-Path $pnpmRoot "pnpm.cmd"
    if (Test-Path $pnpmCmd) {
        try {
            if ((& $pnpmCmd --version).Trim() -eq $PnpmVersion) {
                Write-Host "Using cached pnpm $PnpmVersion." -ForegroundColor DarkGray
                return $pnpmCmd
            }
        }
        catch { }
    }

    Write-Host "Initializing pinned pnpm $PnpmVersion once..." -ForegroundColor Yellow
    if (Test-Path $pnpmRoot) { Remove-Item $pnpmRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $pnpmRoot | Out-Null
    & $NpmPath install --global --prefix $pnpmRoot "pnpm@$PnpmVersion" --no-audit --no-fund | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "pnpm bootstrap failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path $pnpmCmd)) { throw "pnpm bootstrap did not produce $pnpmCmd" }
    return $pnpmCmd
}

function Test-ExactDotnet([string]$DotnetPath) {
    if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) { return $false }
    try { return ((& $DotnetPath --version).Trim() -eq $DotnetVersion) }
    catch { return $false }
}

function Ensure-LocalDotnet([string]$Architecture) {
    # Keep the historical folder name so already-bootstrapped target machines can
    # reuse their existing SDK without another ~280 MB download.
    $dotnetRoot = Join-Path $ToolsRoot "dotnet8-$Architecture"
    $dotnetExe = Join-Path $dotnetRoot "dotnet.exe"
    if (Test-ExactDotnet $dotnetExe) {
        Write-Host "Using cached .NET SDK $DotnetVersion." -ForegroundColor DarkGray
        return $dotnetExe
    }

    New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
    $installer = Join-Path $ToolsRoot "dotnet-install.ps1"
    Write-Host "Initializing pinned .NET SDK $DotnetVersion ($Architecture) once..." -ForegroundColor Yellow
    Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer -UseBasicParsing | Out-Null
    if (Test-Path $dotnetRoot) { Remove-Item $dotnetRoot -Recurse -Force }
    $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installer -Version $DotnetVersion -Architecture $Architecture -InstallDir $dotnetRoot -NoPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ".NET SDK bootstrap failed with exit code $LASTEXITCODE" }
    if (-not (Test-ExactDotnet $dotnetExe)) { throw "Pinned .NET SDK bootstrap did not produce $DotnetVersion." }
    return $dotnetExe
}

function Get-RuntimeDependencyFingerprint {
    param([string]$Architecture)
    $inputs = @(
        (Join-Path $Root "runtime\package.json"),
        (Join-Path $Root "runtime\pnpm-workspace.yaml")
    )

    $parts = @("node=$NodeVersion", "pnpm=$PnpmVersion", "arch=$Architecture")
    foreach ($input in $inputs) {
        $parts += "$(Split-Path $input -Leaf)=$((Get-FileHash $input -Algorithm SHA256).Hash)"
    }
    return ($parts -join "|")
}

Write-Host "=== TuringDesk quick verification (main) ===" -ForegroundColor Cyan
$architecture = Get-WindowsArchitecture
$git = Resolve-Git

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
        Write-Host "Git was not found. Continuing with the current checkout." -ForegroundColor Yellow
        Write-Host "Use GitHub Desktop -> Fetch origin / Pull origin before verification." -ForegroundColor Yellow
    }
    $commit = "current-checkout"
}

if ($ResetEnvironment -or $ForceBootstrap) {
    if (Test-Path $ToolsRoot) {
        Write-Host "Resetting repository-local verified toolchain..." -ForegroundColor Yellow
        Remove-Item $ToolsRoot -Recurse -Force
    }
}

Write-Host "Commit: $commit" -ForegroundColor Green
Write-Host "Pinned environment: Node $NodeVersion | pnpm $PnpmVersion | .NET SDK $DotnetVersion | $architecture" -ForegroundColor Cyan
Write-Host "Toolchain cache: $ToolsRoot" -ForegroundColor DarkGray

$node = Ensure-LocalNode $architecture
$nodeDir = Split-Path $node -Parent
$env:PATH = "$nodeDir;$env:PATH"
$npm = Resolve-Npm $node
$pnpm = Ensure-LocalPnpm $npm
$pnpmDir = Split-Path $pnpm -Parent
$env:PATH = "$nodeDir;$pnpmDir;$env:PATH"
$dotnet = Ensure-LocalDotnet $architecture
$dotnetDir = Split-Path $dotnet -Parent
$env:DOTNET_ROOT = $dotnetDir
$env:PATH = "$nodeDir;$pnpmDir;$dotnetDir;$env:PATH"

Write-Host "Node: $((& $node -p 'process.versions.node').Trim()) [$node]" -ForegroundColor DarkGray
Write-Host "pnpm: $((& $pnpm --version).Trim()) [$pnpm]" -ForegroundColor DarkGray
Write-Host ".NET: $((& $dotnet --version).Trim()) [$dotnet]" -ForegroundColor DarkGray

# ── Prune stale files that robocopy / git pull may leave behind ──────────────
# QUICK-VERIFY.cmd downloads a zip and robocopies it over the working tree.
# robocopy adds and overwrites but never deletes files that no longer exist
# upstream. SDK-style .csproj auto-includes every .cs/.ts/.xaml in the tree,
# so stale copies of deleted files will break the build.
#
# This list is the single source of truth for files that have been permanently
# removed from the repository. Add to it whenever a file is deleted in a commit.
# This does NOT touch .tools/, node_modules, user data, or the toolchain cache.
$legacyFiles = @(
    # Old 4317 Runtime TS files
    "runtime\src\server.ts",
    "runtime\src\agent-activity.ts",
    "runtime\src\mock-agent.ts",
    "runtime\src\model-gateway.ts",
    "runtime\src\model-config.ts",
    "runtime\src\harness-gateway.ts",
    "runtime\src\harness-runtime.ts",
    "runtime\src\harness-integration-smoke.ts",
    "runtime\src\openai-compatible-gateway.ts",
    "runtime\src\capability-client.ts",
    "runtime\src\windows-mcp-server.ts",
    "runtime\src\windows-mcp-smoke.ts",
    # Old Cordis profile
    "runtime\harness\turingdesk.cordis.yml",
    # Old Agent UI WPF files
    "src\TuringDesk.Desktop\AgentActivityWindow.xaml",
    "src\TuringDesk.Desktop\AgentActivityWindow.xaml.cs",
    "src\TuringDesk.Desktop\AgentConversationCardWindow.xaml",
    "src\TuringDesk.Desktop\AgentConversationCardWindow.xaml.cs",
    "src\TuringDesk.Desktop\AgentTraceCardWindow.xaml",
    "src\TuringDesk.Desktop\AgentTraceCardWindow.xaml.cs",
    "src\TuringDesk.Desktop\AgentStatusBadge.xaml",
    "src\TuringDesk.Desktop\AgentStatusBadge.xaml.cs",
    # Old Runtime services
    "src\TuringDesk.Desktop\Services\RuntimeClient.cs",
    "src\TuringDesk.Desktop\Services\RuntimeHostService.cs",
    "src\TuringDesk.Desktop\Services\CapabilityServer.cs",
    "src\TuringDesk.Desktop\Services\AgentFloatingCardsService.cs",
    # Old settings center
    "src\TuringDesk.Desktop\DesktopDiyCenterWindow.xaml",
    "src\TuringDesk.Desktop\DesktopDiyCenterWindow.xaml.cs",
    # Old dev script
    "scripts\verify-lazy-runtime.ps1"
)

$pruned = 0
foreach ($file in $legacyFiles) {
    $full = Join-Path $Root $file
    if (Test-Path $full) {
        Remove-Item $full -Force
        $pruned++
        Write-Host "  Pruned stale file: $file" -ForegroundColor DarkYellow
    }
}
if ($pruned -gt 0) {
    Write-Host "Removed $pruned stale file(s) left by previous versions." -ForegroundColor Yellow
}
else {
    Write-Host "No stale legacy files found." -ForegroundColor DarkGray
}

# Also try git restore+clean if git is available — catches anything not in the
# explicit list above. Only affects source directories, never the toolchain.
if ($git) {
    & $git restore -- "src/" "runtime/src/" "runtime/harness/" "scripts/" ".github/" "docs/" 2>$null
    & $git clean -fd -- "src/" "runtime/src/" "runtime/harness/" "scripts/" ".github/" "docs/" 2>$null
}

Write-Host "Stopping previous TuringDesk processes..." -ForegroundColor Cyan
Get-Process TuringDesk.Desktop,TuringDesk.ShellHost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$statusPath = Join-Path $env:LOCALAPPDATA "TuringDesk\desktop-engine-status.json"
$sceneLogPath = Join-Path $env:LOCALAPPDATA "TuringDesk\logs\scene-engine.log"
if (Test-Path $statusPath) { Remove-Item $statusPath -Force }
if (Test-Path $sceneLogPath) { Remove-Item $sceneLogPath -Force }

Write-Host "Building Runtime..." -ForegroundColor Cyan
$runtimeDir = Join-Path $Root "runtime"
$runtimeModules = Join-Path $runtimeDir "node_modules"
$runtimeMarker = Join-Path $runtimeModules ".turingdesk-verified-environment"
$runtimeLockPath = Join-Path $runtimeDir "pnpm-lock.yaml"
# Older quick-verify versions could leave an untracked lockfile behind. Remove it
# so the verification launcher never mutates or follows a local dependency config.
if (Test-Path $runtimeLockPath) { Remove-Item $runtimeLockPath -Force }
$runtimeFingerprint = Get-RuntimeDependencyFingerprint $architecture
$needRuntimeInstall = -not (Test-Path $runtimeModules)
$adoptExistingRuntime = $false
if (-not $needRuntimeInstall -and -not $SkipRuntimeInstall) {
    if (-not (Test-Path $runtimeMarker)) {
        # This is the migration path for machines that already ran the old verifier.
        # Keep the known-good node_modules and validate it before doing any download.
        $adoptExistingRuntime = $true
        $needRuntimeInstall = $false
        Write-Host "Found existing runtime node_modules; validating and adopting it without reinstalling." -ForegroundColor DarkGray
    }
    else {
        $existingFingerprint = (Get-Content $runtimeMarker -Raw).Trim()
        $needRuntimeInstall = $existingFingerprint -ne $runtimeFingerprint
    }
}

Push-Location $runtimeDir
try {
    if (-not $SkipRuntimeInstall -and $needRuntimeInstall) {
        Write-Host "Runtime dependency config changed or cache is missing; installing once..." -ForegroundColor Yellow
        & $pnpm install --no-frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
        New-Item -ItemType Directory -Force -Path $runtimeModules | Out-Null
        Set-Content -Path $runtimeMarker -Value $runtimeFingerprint -NoNewline
        if (Test-Path $runtimeLockPath) { Remove-Item $runtimeLockPath -Force }
    }
    elseif (-not $SkipRuntimeInstall -and -not $adoptExistingRuntime) {
        Write-Host "Using existing runtime node_modules; dependency fingerprint matches." -ForegroundColor DarkGray
    }

    & $pnpm build
    if ($LASTEXITCODE -ne 0) {
        if ($adoptExistingRuntime -and -not $SkipRuntimeInstall) {
            Write-Host "Existing runtime cache did not validate; repairing dependencies once..." -ForegroundColor Yellow
            & $pnpm install --no-frozen-lockfile
            if ($LASTEXITCODE -ne 0) { throw "pnpm repair install failed" }
            if (Test-Path $runtimeLockPath) { Remove-Item $runtimeLockPath -Force }
            & $pnpm build
            if ($LASTEXITCODE -ne 0) { throw "Runtime build failed after dependency repair" }
        }
        else {
            throw "Runtime build failed"
        }
    }

    if (-not $SkipRuntimeInstall -and $adoptExistingRuntime) {
        Set-Content -Path $runtimeMarker -Value $runtimeFingerprint -NoNewline
        Write-Host "Existing runtime dependency tree is now registered as the verified environment." -ForegroundColor DarkGray
    }
}
finally {
    Pop-Location
}

Write-Host "Building Desktop Release with pinned .NET SDK $DotnetVersion..." -ForegroundColor Cyan
& $dotnet build "src/TuringDesk.Desktop/TuringDesk.Desktop.csproj" --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed" }

$exe = Join-Path $Root "src\TuringDesk.Desktop\bin\Release\net8.0-windows10.0.19041.0\TuringDesk.Desktop.exe"
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
Write-Host "Pinned environment: Node $NodeVersion | pnpm $PnpmVersion | .NET $DotnetVersion"
Write-Host "Scene log: $sceneLogPath" -ForegroundColor Cyan
Write-Host "Scene status: $statusPath" -ForegroundColor Cyan

if (Test-Path $statusPath) {
    Write-Host ""
    Write-Host "Desktop engine probe:" -ForegroundColor Cyan
    Get-Content $statusPath | Write-Host
}
else {
    Write-Host "Desktop engine probe has not appeared yet: $statusPath" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Scene debugging:" -ForegroundColor Cyan
Write-Host "  Double-click SCENE-LOG.cmd and then apply Aurora / Neon / Orbital."
Write-Host "  The fixed toolchain is reused on future runs unless the pinned config changes."
Write-Host ""
Write-Host "To relaunch after another pull, just double-click QUICK-VERIFY.cmd again." -ForegroundColor DarkGray
