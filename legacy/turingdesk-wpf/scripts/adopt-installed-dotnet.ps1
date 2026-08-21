param()

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ToolsRoot = Join-Path $Root ".tools\quick-verify"
$DotnetConfig = Get-Content (Join-Path $Root "global.json") -Raw | ConvertFrom-Json
$DotnetVersion = [string]$DotnetConfig.sdk.version

if ([string]::IsNullOrWhiteSpace($DotnetVersion)) {
    throw "global.json does not pin a .NET SDK version."
}

$runtimeChannel = (($DotnetVersion -split '\.')[0..1] -join '.')
$osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$architecture = switch ($osArch) {
    "Arm64" { "arm64" }
    "X64" { "x64" }
    default { throw "Unsupported Windows architecture for .NET bootstrap: $osArch" }
}

$cacheRoot = Join-Path $ToolsRoot "dotnet8-$architecture"
$cacheExe = Join-Path $cacheRoot "dotnet.exe"
$installer = Join-Path $ToolsRoot "dotnet-install.ps1"
$powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

function Test-ExactSdk([string]$DotnetPath) {
    if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) { return $false }
    try {
        $sdks = & $DotnetPath --list-sdks 2>$null
        return [bool]($sdks | Where-Object { ($_ -split '\s+')[0] -eq $DotnetVersion } | Select-Object -First 1)
    }
    catch { return $false }
}

function Test-WindowsDesktopRuntime([string]$DotnetPath) {
    if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) { return $false }
    try {
        $runtimes = & $DotnetPath --list-runtimes 2>$null
        return [bool]($runtimes | Where-Object {
            $_ -match '^Microsoft\.WindowsDesktop\.App\s+' -and
            (($_ -split '\s+')[1]).StartsWith($runtimeChannel + '.', [StringComparison]::Ordinal)
        } | Select-Object -First 1)
    }
    catch { return $false }
}

function Ensure-Installer {
    New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
    if (-not (Test-Path $installer)) {
        Write-Host "Downloading Microsoft dotnet-install helper once..." -ForegroundColor DarkGray
        Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer -UseBasicParsing | Out-Null
    }
}

# A complete repository-local toolchain always wins.
if ((Test-ExactSdk $cacheExe) -and (Test-WindowsDesktopRuntime $cacheExe)) {
    Write-Host "Found cached .NET SDK $DotnetVersion + Windows Desktop Runtime $runtimeChannel; no install needed." -ForegroundColor DarkGray
    exit 0
}

# Reuse a complete system installation only when it contains BOTH the pinned SDK
# and the WPF/WinForms desktop runtime required to launch TuringDesk.Desktop.
$command = Get-Command dotnet -ErrorAction SilentlyContinue
if ($command) {
    $systemDotnet = $command.Source
    $systemRoot = Split-Path $systemDotnet -Parent
    if ((Test-ExactSdk $systemDotnet) -and (Test-WindowsDesktopRuntime $systemDotnet)) {
        New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
        if (Test-Path $cacheRoot) { Remove-Item $cacheRoot -Recurse -Force }
        New-Item -ItemType Junction -Path $cacheRoot -Target $systemRoot | Out-Null

        if ((Test-ExactSdk $cacheExe) -and (Test-WindowsDesktopRuntime $cacheExe)) {
            Write-Host "Using installed .NET SDK $DotnetVersion + Windows Desktop Runtime $runtimeChannel; download skipped." -ForegroundColor Green
            exit 0
        }

        Remove-Item $cacheRoot -Force -ErrorAction SilentlyContinue
    }
}

# The system installation is missing either the exact SDK or WindowsDesktop.
# Build a private, non-admin toolchain under the repository instead of sending the
# user to the browser/runtime download page.
Ensure-Installer
if (Test-Path $cacheRoot) { Remove-Item $cacheRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

Write-Host "Preparing repository-local .NET SDK $DotnetVersion ($architecture)..." -ForegroundColor Yellow
& $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installer -Version $DotnetVersion -Architecture $architecture -InstallDir $cacheRoot -NoPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw ".NET SDK bootstrap failed with exit code $LASTEXITCODE" }
if (-not (Test-ExactSdk $cacheExe)) { throw "Pinned .NET SDK bootstrap did not produce $DotnetVersion." }

if (-not (Test-WindowsDesktopRuntime $cacheExe)) {
    Write-Host "Preparing repository-local Windows Desktop Runtime $runtimeChannel ($architecture)..." -ForegroundColor Yellow
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installer -Channel $runtimeChannel -Runtime windowsdesktop -Architecture $architecture -InstallDir $cacheRoot -NoPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Windows Desktop Runtime bootstrap failed with exit code $LASTEXITCODE" }
}

if (-not (Test-WindowsDesktopRuntime $cacheExe)) {
    throw "Microsoft.WindowsDesktop.App $runtimeChannel was not available after bootstrap."
}

Write-Host "Repository-local .NET toolchain is ready: SDK $DotnetVersion + Windows Desktop Runtime $runtimeChannel." -ForegroundColor Green
