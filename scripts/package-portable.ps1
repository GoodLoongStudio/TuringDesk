param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-arm64",
    [string]$Version = "v0.9",
    [string]$NodeVersion = "22.19.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$PackageName = "TuringDesk-$Version-$RuntimeIdentifier"
$ArtifactsRoot = Join-Path $Root "artifacts"
$PackageRoot = Join-Path $ArtifactsRoot $PackageName
$DesktopDir = Join-Path $PackageRoot "desktop"
$ShellHostDir = Join-Path $PackageRoot "shellhost"
$RuntimeAppDir = Join-Path $PackageRoot "runtime\app"
$RuntimeNodeDir = Join-Path $PackageRoot "runtime\node"

$NodeArchitecture = switch ($RuntimeIdentifier) {
    "win-arm64" { "arm64" }
    "win-x64" { "x64" }
    default { throw "Unsupported Windows runtime identifier: $RuntimeIdentifier" }
}

Write-Host "Building $PackageName" -ForegroundColor Cyan
Write-Host "Runtime architecture: $RuntimeIdentifier / Node $NodeArchitecture" -ForegroundColor Cyan

if (Test-Path $PackageRoot) {
    Remove-Item $PackageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $DesktopDir, $ShellHostDir, $RuntimeAppDir, $RuntimeNodeDir | Out-Null

Write-Host "Building TypeScript runtime..." -ForegroundColor Cyan
Push-Location (Join-Path $Root "runtime")
try {
    corepack enable
    pnpm install --no-frozen-lockfile
    pnpm build
    pnpm test:harness
}
finally {
    Pop-Location
}

Write-Host "Publishing self-contained Windows desktop for $RuntimeIdentifier..." -ForegroundColor Cyan
$DesktopProject = Join-Path $Root "src\TuringDesk.Desktop\TuringDesk.Desktop.csproj"
dotnet publish $DesktopProject `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $DesktopDir

Write-Host "Publishing resilient replacement ShellHost for $RuntimeIdentifier..." -ForegroundColor Cyan
$ShellHostProject = Join-Path $Root "src\TuringDesk.ShellHost\TuringDesk.ShellHost.csproj"
dotnet publish $ShellHostProject `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $ShellHostDir

Write-Host "Copying runtime and Harness profile..." -ForegroundColor Cyan
Copy-Item (Join-Path $Root "runtime\dist\*") $RuntimeAppDir -Recurse -Force
Copy-Item (Join-Path $Root "runtime\package.json") $RuntimeAppDir -Force
Copy-Item (Join-Path $Root "runtime\harness") (Join-Path $RuntimeAppDir "harness") -Recurse -Force

Write-Host "Installing portable Runtime + pinned DeepSeek Harness production dependencies..." -ForegroundColor Cyan
Push-Location $RuntimeAppDir
try {
    npm install --omit=dev --ignore-scripts --package-lock=false
}
finally {
    Pop-Location
}

Write-Host "Downloading embedded Node.js $NodeVersion for Windows $NodeArchitecture..." -ForegroundColor Cyan
$TempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$NodeZip = Join-Path $TempRoot "node-$NodeVersion-win-$NodeArchitecture.zip"
$NodeExtract = Join-Path $TempRoot "node-$NodeVersion-win-$NodeArchitecture"
if (Test-Path $NodeExtract) { Remove-Item $NodeExtract -Recurse -Force }
Invoke-WebRequest "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-$NodeArchitecture.zip" -OutFile $NodeZip
Expand-Archive $NodeZip -DestinationPath $NodeExtract -Force
$NodeSource = Join-Path $NodeExtract "node-v$NodeVersion-win-$NodeArchitecture"
$EmbeddedNode = Join-Path $RuntimeNodeDir "node.exe"
Copy-Item (Join-Path $NodeSource "node.exe") $EmbeddedNode -Force
if (Test-Path (Join-Path $NodeSource "LICENSE")) {
    Copy-Item (Join-Path $NodeSource "LICENSE") (Join-Path $RuntimeNodeDir "NODE-LICENSE.txt") -Force
}

Write-Host "Verifying Harness from the final replacement-shell layout..." -ForegroundColor Cyan
& $EmbeddedNode (Join-Path $RuntimeAppDir "harness-integration-smoke.js")
if ($LASTEXITCODE -ne 0) {
    throw "Packaged DeepSeek Harness integration smoke failed with exit code $LASTEXITCODE"
}

# v0.9 is a direct replacement-shell package. The normal-app/Preview entry points
# are deliberately not shipped in the user-facing archive.
Copy-Item (Join-Path $Root "packaging\shell\Install-TuringDesk.cmd") $PackageRoot -Force
Copy-Item (Join-Path $Root "packaging\shell\Enable-TuringDeskShell.ps1") $PackageRoot -Force
Copy-Item (Join-Path $Root "packaging\shell\Restore-Explorer.ps1") $PackageRoot -Force
Copy-Item (Join-Path $Root "packaging\shell\Restore-Explorer.cmd") $PackageRoot -Force
Copy-Item (Join-Path $Root "packaging\PORTABLE-README.txt") (Join-Path $PackageRoot "README.txt") -Force
if (Test-Path (Join-Path $Root "packaging\THIRD-PARTY-NOTICES.txt")) {
    Copy-Item (Join-Path $Root "packaging\THIRD-PARTY-NOTICES.txt") (Join-Path $PackageRoot "THIRD-PARTY-NOTICES.txt") -Force
}

foreach ($Required in @(
    (Join-Path $DesktopDir "TuringDesk.Desktop.exe"),
    (Join-Path $ShellHostDir "TuringDesk.ShellHost.exe"),
    $EmbeddedNode,
    (Join-Path $RuntimeAppDir "harness\turingdesk.cordis.yml"),
    (Join-Path $PackageRoot "Install-TuringDesk.cmd"),
    (Join-Path $PackageRoot "Enable-TuringDeskShell.ps1"),
    (Join-Path $PackageRoot "Restore-Explorer.ps1")
)) {
    if (-not (Test-Path $Required)) {
        throw "Shell replacement package is incomplete: $Required"
    }
}

foreach ($Forbidden in @(
    (Join-Path $PackageRoot "Preview-TuringDeskShell.ps1"),
    (Join-Path $PackageRoot "Start-TuringDesk.cmd"),
    (Join-Path $PackageRoot "Start-TuringDesk.ps1")
)) {
    if (Test-Path $Forbidden) {
        throw "v0.9 direct Shell package must not contain Preview/normal-mode entry points: $Forbidden"
    }
}

$DetectedNodeArch = (& $EmbeddedNode -p "process.arch").Trim()
if ($DetectedNodeArch -ne $NodeArchitecture) {
    throw "Embedded Node architecture mismatch. Expected $NodeArchitecture, got $DetectedNodeArch"
}

$BuildInfo = @"
TuringDesk $Version
Runtime: $RuntimeIdentifier
Architecture: $NodeArchitecture
Node: $NodeVersion ($DetectedNodeArch)
DeepSeek Harness: 0.1.0-rc.6
Install flow: direct current-user replacement Shell
Installer: Install-TuringDesk.cmd
Shell mode: Windows Custom User Interface (current-user policy)
Shell host: shellhost/TuringDesk.ShellHost.exe
Recovery: Restore-Explorer.ps1
Preview mode: not shipped
Harness profile: runtime/app/harness/turingdesk.cordis.yml
Build commit: $env:GITHUB_SHA
Build time (UTC): $([DateTime]::UtcNow.ToString("o"))
"@
Set-Content -Path (Join-Path $PackageRoot "BUILD-INFO.txt") -Value $BuildInfo -Encoding UTF8

Write-Host "Direct replacement-shell package ready: $PackageRoot" -ForegroundColor Green