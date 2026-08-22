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

function Resolve-Dsh {
    Refresh-ProcessPath
    $command = Get-Command dsh.cmd -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    $appDataBin = Join-Path $env:APPDATA "npm\dsh.cmd"
    if (Test-Path $appDataBin -PathType Leaf) { return $appDataBin }

    return $null
}

function Ensure-NpmGlobalBinOnUserPath {
    $globalBin = Join-Path $env:APPDATA "npm"
    if (-not (Test-Path $globalBin -PathType Container)) { return }

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $parts = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $exists = $false
    foreach ($part in $parts) {
        if ($part.TrimEnd('\').Equals($globalBin.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $exists = $true
            break
        }
    }

    if (-not $exists) {
        $newPath = (($parts + $globalBin) -join ';')
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "Added official npm global bin to user PATH: $globalBin" -ForegroundColor DarkGray
    }

    if (($env:PATH -split ';') -notcontains $globalBin) {
        $env:PATH = "$globalBin;$env:PATH"
    }
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

$dsh = Resolve-Dsh
if ($dsh) {
    Write-Host "Official DeepSeek Harness already installed: $dsh" -ForegroundColor Green
    exit 0
}

$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $npm) {
    throw "npm.cmd was not found. The prerequisite step should install official Node.js first."
}

Step "Installing official DeepSeek Harness"
Write-Host "Official project: deepseek-ai/deepseek-harness" -ForegroundColor DarkGray
Write-Host "Official npm CLI package: $Package" -ForegroundColor DarkGray
Write-Host "Official command after installation: dsh" -ForegroundColor DarkGray
Write-Host "This is a one-time installation of the official upstream package. TuringDesk will not create a private HarnessRuntime." -ForegroundColor DarkGray

$npmCache = Join-Path $env:LOCALAPPDATA "TuringDesk\RuntimeCache\npm-official"
New-Item -ItemType Directory -Force -Path $npmCache | Out-Null

# The upstream package currently has a large dependency tree. On a 4 GB Windows
# machine npm can hit V8's ~2 GB default heap while resolving it, so give this
# one installation process a bounded 3 GB ceiling. Downloaded package data is
# persisted in npm-official and reused by retries and later installs.
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

Ensure-NpmGlobalBinOnUserPath
$dsh = Resolve-Dsh
if (-not $dsh) {
    throw "Official DeepSeek Harness installation completed, but dsh.cmd was not found."
}

Write-Host "Official DeepSeek Harness ready: $dsh" -ForegroundColor Green
