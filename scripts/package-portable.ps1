param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version = "v0.1",
    [string]$NodeVersion = "22.19.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$PackageName = "TuringDesk-$Version-$RuntimeIdentifier"
$ArtifactsRoot = Join-Path $Root "artifacts"
$PackageRoot = Join-Path $ArtifactsRoot $PackageName
$DesktopDir = Join-Path $PackageRoot "desktop"
$RuntimeAppDir = Join-Path $PackageRoot "runtime\app"
$RuntimeNodeDir = Join-Path $PackageRoot "runtime\node"

Write-Host "Building $PackageName" -ForegroundColor Cyan

if (Test-Path $PackageRoot) {
    Remove-Item $PackageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $DesktopDir, $RuntimeAppDir, $RuntimeNodeDir | Out-Null

Write-Host "Building TypeScript runtime..." -ForegroundColor Cyan
Push-Location (Join-Path $Root "runtime")
try {
    corepack enable
    pnpm install --no-frozen-lockfile
    pnpm build
}
finally {
    Pop-Location
}

Write-Host "Publishing self-contained Windows desktop..." -ForegroundColor Cyan
$Project = Join-Path $Root "src\TuringDesk.Desktop\TuringDesk.Desktop.csproj"
dotnet publish $Project `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $DesktopDir

Write-Host "Copying runtime..." -ForegroundColor Cyan
Copy-Item (Join-Path $Root "runtime\dist\*") $RuntimeAppDir -Recurse -Force

Write-Host "Downloading embedded Node.js $NodeVersion..." -ForegroundColor Cyan
$TempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$NodeZip = Join-Path $TempRoot "node-$NodeVersion-win-x64.zip"
$NodeExtract = Join-Path $TempRoot "node-$NodeVersion-win-x64"
if (Test-Path $NodeExtract) { Remove-Item $NodeExtract -Recurse -Force }
Invoke-WebRequest "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-x64.zip" -OutFile $NodeZip
Expand-Archive $NodeZip -DestinationPath $NodeExtract -Force
$NodeSource = Join-Path $NodeExtract "node-v$NodeVersion-win-x64"
Copy-Item (Join-Path $NodeSource "node.exe") $RuntimeNodeDir -Force
if (Test-Path (Join-Path $NodeSource "LICENSE")) {
    Copy-Item (Join-Path $NodeSource "LICENSE") (Join-Path $RuntimeNodeDir "NODE-LICENSE.txt") -Force
}

Copy-Item (Join-Path $PSScriptRoot "Start-TuringDesk.cmd") $PackageRoot -Force
Copy-Item (Join-Path $PSScriptRoot "Start-TuringDesk.ps1") $PackageRoot -Force
Copy-Item (Join-Path $Root "packaging\PORTABLE-README.txt") (Join-Path $PackageRoot "README.txt") -Force

$BuildInfo = @"
TuringDesk $Version
Runtime: $RuntimeIdentifier
Node: $NodeVersion
Build commit: $env:GITHUB_SHA
Build time (UTC): $([DateTime]::UtcNow.ToString("o"))
"@
Set-Content -Path (Join-Path $PackageRoot "BUILD-INFO.txt") -Value $BuildInfo -Encoding UTF8

Write-Host "Portable package ready: $PackageRoot" -ForegroundColor Green
