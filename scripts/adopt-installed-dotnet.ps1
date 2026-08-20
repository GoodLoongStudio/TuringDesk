param()

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ToolsRoot = Join-Path $Root ".tools\quick-verify"
$DotnetConfig = Get-Content (Join-Path $Root "global.json") -Raw | ConvertFrom-Json
$DotnetVersion = [string]$DotnetConfig.sdk.version

if ([string]::IsNullOrWhiteSpace($DotnetVersion)) {
    throw "global.json does not pin a .NET SDK version."
}

$osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$architecture = switch ($osArch) {
    "Arm64" { "arm64" }
    "X64" { "x64" }
    default { return }
}

$cacheRoot = Join-Path $ToolsRoot "dotnet8-$architecture"
$cacheExe = Join-Path $cacheRoot "dotnet.exe"

# Existing repository-local verified SDK always wins.
if (Test-Path $cacheExe) {
    try {
        if ((& $cacheExe --version).Trim() -eq $DotnetVersion) {
            Write-Host "Found cached .NET SDK $DotnetVersion; no install needed." -ForegroundColor DarkGray
            exit 0
        }
    }
    catch { }
}

$command = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $command) {
    Write-Host "System .NET SDK not found; quick verify will bootstrap $DotnetVersion once." -ForegroundColor DarkGray
    exit 0
}

$systemDotnet = $command.Source
$systemRoot = Split-Path $systemDotnet -Parent
$installed = @()
try {
    $installed = & $systemDotnet --list-sdks 2>$null
}
catch {
    exit 0
}

$hasExactSdk = $installed | Where-Object {
    ($_ -split '\s+')[0] -eq $DotnetVersion
} | Select-Object -First 1

if (-not $hasExactSdk) {
    Write-Host "System .NET exists but SDK $DotnetVersion is not installed; quick verify will bootstrap the pinned SDK once." -ForegroundColor DarkGray
    exit 0
}

# Reuse the complete system dotnet installation through a directory junction.
# This keeps quick-verify.ps1 unchanged and preserves its exact-version check.
New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
if (Test-Path $cacheRoot) {
    Remove-Item $cacheRoot -Recurse -Force
}
New-Item -ItemType Junction -Path $cacheRoot -Target $systemRoot | Out-Null

if (-not (Test-Path $cacheExe)) {
    throw "Failed to expose installed .NET SDK through quick verify cache: $cacheRoot"
}

$resolvedVersion = (& $cacheExe --version).Trim()
if ($resolvedVersion -ne $DotnetVersion) {
    Remove-Item $cacheRoot -Force -ErrorAction SilentlyContinue
    Write-Host "Installed dotnet did not resolve pinned SDK $DotnetVersion from this repository; quick verify will bootstrap its local SDK." -ForegroundColor DarkGray
    exit 0
}

Write-Host "Using installed .NET SDK $DotnetVersion; download skipped." -ForegroundColor Green
