param(
    [string]$Package = "@deepseek-ai/dsh"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Refresh-ProcessPath {
    $machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $user = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:PATH = (($machine, $user) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ";"
}

function Get-NpmGlobalBin([string]$NpmPath) {
    if (-not [string]::IsNullOrWhiteSpace($NpmPath)) {
        try {
            $prefix = (& $NpmPath prefix -g 2>$null | Select-Object -First 1)
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($prefix)) {
                return ([string]$prefix).Trim()
            }
        }
        catch { }

        try {
            $prefix = (& $NpmPath config get prefix 2>$null | Select-Object -First 1)
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($prefix)) {
                return ([string]$prefix).Trim()
            }
        }
        catch { }
    }

    return (Join-Path $env:APPDATA "npm")
}

function Test-OfficialDshInstall([string]$DshPath) {
    if ([string]::IsNullOrWhiteSpace($DshPath) -or -not (Test-Path $DshPath -PathType Leaf)) { return $false }
    try {
        $binDir = Split-Path $DshPath -Parent
        $manifest = Join-Path $binDir "node_modules\@deepseek-ai\dsh\package.json"
        if (-not (Test-Path $manifest -PathType Leaf)) { return $false }
        $packageJson = Get-Content $manifest -Raw | ConvertFrom-Json
        return ([string]$packageJson.name -eq "@deepseek-ai/dsh")
    }
    catch { return $false }
}

function Resolve-Dsh([string]$GlobalBin) {
    Refresh-ProcessPath

    $command = Get-Command dsh.cmd -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and (Test-OfficialDshInstall -DshPath $command.Source)) { return $command.Source }

    if (-not [string]::IsNullOrWhiteSpace($GlobalBin)) {
        $candidate = Join-Path $GlobalBin "dsh.cmd"
        if (Test-OfficialDshInstall -DshPath $candidate) { return $candidate }
    }

    $fallback = Join-Path $env:APPDATA "npm\dsh.cmd"
    if (Test-OfficialDshInstall -DshPath $fallback) { return $fallback }

    return $null
}

function Ensure-NpmGlobalBinOnUserPath([string]$GlobalBin) {
    if ([string]::IsNullOrWhiteSpace($GlobalBin) -or -not (Test-Path $GlobalBin -PathType Container)) { return }

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $parts = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $exists = $false
    foreach ($part in $parts) {
        if ($part.TrimEnd('\').Equals($GlobalBin.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $exists = $true
            break
        }
    }

    if (-not $exists) {
        $newPath = (($parts + $GlobalBin) -join ';')
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "Added npm global bin to user PATH: $GlobalBin" -ForegroundColor DarkGray
    }

    $currentParts = @($env:PATH -split ';')
    $inCurrentPath = $false
    foreach ($part in $currentParts) {
        if (-not [string]::IsNullOrWhiteSpace($part) -and $part.TrimEnd('\').Equals($GlobalBin.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $inCurrentPath = $true
            break
        }
    }
    if (-not $inCurrentPath) { $env:PATH = "$GlobalBin;$env:PATH" }
}

function Invoke-OfficialDshInstall([string]$NpmPath, [string]$CachePath, [switch]$PreferOnline) {
    $mode = if ($PreferOnline) { "prefer-online retry" } else { "prefer-offline cache-first" }
    Write-Host "npm mode: $mode" -ForegroundColor DarkGray
    Write-Host "npm registry: https://registry.npmjs.org/" -ForegroundColor DarkGray
    Write-Host "npm cache: $CachePath" -ForegroundColor DarkGray

    $args = @(
        "install", "-g", $Package,
        "--registry=https://registry.npmjs.org/",
        "--cache=$CachePath",
        $(if ($PreferOnline) { "--prefer-online" } else { "--prefer-offline" }),
        "--fetch-retries=4",
        "--fetch-retry-factor=2",
        "--fetch-retry-mintimeout=1000",
        "--fetch-retry-maxtimeout=15000",
        "--fetch-timeout=60000",
        "--foreground-scripts",
        "--no-audit",
        "--no-fund",
        "--loglevel=http",
        "--timing"
    )

    & $NpmPath @args
    return $LASTEXITCODE
}

Refresh-ProcessPath
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $npm) {
    throw "npm.cmd was not found. The prerequisite step should install official Node.js first."
}

$globalBin = Get-NpmGlobalBin -NpmPath $npm.Source
Write-Host "npm global prefix: $globalBin" -ForegroundColor DarkGray
Ensure-NpmGlobalBinOnUserPath -GlobalBin $globalBin

$dsh = Resolve-Dsh -GlobalBin $globalBin
if ($dsh) {
    Write-Host "Official DeepSeek Harness already installed: $dsh" -ForegroundColor Green
    exit 0
}

Step "Installing official DeepSeek Harness"
Write-Host "Official project: deepseek-ai/deepseek-harness" -ForegroundColor DarkGray
Write-Host "Official npm CLI package: $Package" -ForegroundColor DarkGray
Write-Host "Official command after installation: dsh" -ForegroundColor DarkGray
Write-Host "This runs only when a complete official global dsh installation is not found." -ForegroundColor DarkGray

$npmCache = Join-Path $env:LOCALAPPDATA "TuringDesk\RuntimeCache\npm-official"
New-Item -ItemType Directory -Force -Path $npmCache | Out-Null

# The upstream package has a large dependency tree. On a 4 GB Windows machine
# npm can hit V8's ~2 GB default heap while resolving it, so give this one
# installation process a bounded 3 GB ceiling. Downloaded package data is kept
# in npm-official and reused by retries and future official updates.
$oldNodeOptions = $env:NODE_OPTIONS
try {
    $env:NODE_OPTIONS = "--max-old-space-size=3072"

    $exitCode = Invoke-OfficialDshInstall -NpmPath $npm.Source -CachePath $npmCache
    if ($exitCode -ne 0) {
        Write-Host "First npm attempt failed with exit code $exitCode. Retrying against the same official registry while reusing the cache..." -ForegroundColor Yellow
        $exitCode = Invoke-OfficialDshInstall -NpmPath $npm.Source -CachePath $npmCache -PreferOnline
    }

    if ($exitCode -ne 0) {
        throw "Official DeepSeek Harness installation failed with exit code $exitCode"
    }
}
finally {
    $env:NODE_OPTIONS = $oldNodeOptions
}

$globalBin = Get-NpmGlobalBin -NpmPath $npm.Source
Ensure-NpmGlobalBinOnUserPath -GlobalBin $globalBin
$dsh = Resolve-Dsh -GlobalBin $globalBin
if (-not $dsh) {
    throw "Official DeepSeek Harness installation completed, but a complete dsh installation was not found under npm global prefix: $globalBin"
}

Write-Host "Official DeepSeek Harness ready: $dsh" -ForegroundColor Green
