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

# The upstream package currently has a large dependency tree. On a 4 GB Windows
# machine npm can hit V8's ~2 GB default heap while resolving it, so give the
# installer a bounded 3 GB ceiling. This affects only this one installation
# process; the installed Harness does not reserve 3 GB at runtime.
$oldNodeOptions = $env:NODE_OPTIONS
try {
    $env:NODE_OPTIONS = "--max-old-space-size=3072"
    & $npm.Source install -g $Package --prefer-offline --no-audit --no-fund --loglevel=notice
    if ($LASTEXITCODE -ne 0) {
        throw "Official DeepSeek Harness installation failed with exit code $LASTEXITCODE"
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
