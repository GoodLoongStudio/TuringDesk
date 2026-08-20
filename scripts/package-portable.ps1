param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-arm64",
    [string]$Version = "v0.14.2",
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
$BrandIcon = Join-Path $PackageRoot "TuringDesk.ico"

$NodeArchitecture = switch ($RuntimeIdentifier) {
    "win-arm64" { "arm64" }
    "win-x64" { "x64" }
    default { throw "Unsupported Windows runtime identifier: $RuntimeIdentifier" }
}

Write-Host "Staging $PackageName" -ForegroundColor Cyan
Write-Host "Runtime architecture: $RuntimeIdentifier / Node $NodeArchitecture" -ForegroundColor Cyan

if (Test-Path $PackageRoot) {
    Remove-Item $PackageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $DesktopDir, $ShellHostDir, $RuntimeNodeDir | Out-Null

Write-Host "Building TypeScript runtime and reviewed Harness native dependencies..." -ForegroundColor Cyan
$RuntimeRoot = Join-Path $Root "runtime"
Push-Location $RuntimeRoot
try {
    corepack enable
    if ($LASTEXITCODE -ne 0) { throw "corepack enable failed with exit code $LASTEXITCODE" }

    pnpm install --no-frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "pnpm install failed with exit code $LASTEXITCODE" }

    pnpm build
    if ($LASTEXITCODE -ne 0) { throw "Runtime build failed with exit code $LASTEXITCODE" }

    # pnpm deploy creates an isolated, portable production node_modules tree.
    # --legacy allows deploy for this single-package workspace without requiring
    # inject-workspace-packages=true. It reuses the dependency tree already
    # installed and whose native lifecycle scripts were explicitly approved.
    pnpm --filter @turingdesk/runtime --prod deploy --legacy $RuntimeAppDir
    if ($LASTEXITCODE -ne 0) { throw "pnpm deploy failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

# The repository intentionally ignores dist/, so explicitly place the compiled
# runtime into the deployed production package after pnpm deploy.
Write-Host "Copying compiled runtime..." -ForegroundColor Cyan
Copy-Item (Join-Path $RuntimeRoot "dist\*") $RuntimeAppDir -Recurse -Force

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
if ($LASTEXITCODE -ne 0) { throw "TuringDesk.Desktop publish failed with exit code $LASTEXITCODE" }

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
if ($LASTEXITCODE -ne 0) { throw "TuringDesk.ShellHost publish failed with exit code $LASTEXITCODE" }

Write-Host "Generating installer/shortcut application icon..." -ForegroundColor Cyan
& (Join-Path $Root "scripts\generate-brand-icon.ps1") -OutputPath $BrandIcon
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $BrandIcon)) {
    throw "Failed to generate TuringDesk.ico"
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

Write-Host "Verifying official Harness WebUI from the final installed layout..." -ForegroundColor Cyan
& $EmbeddedNode (Join-Path $RuntimeAppDir "harness-web-smoke.js")
if ($LASTEXITCODE -ne 0) {
    throw "Packaged DeepSeek Harness WebUI smoke failed with exit code $LASTEXITCODE"
}

# These are application lifecycle actions used by the MSI Start menu shortcuts.
# The MSI owns application files; enabling the replacement shell is deliberately
# a separate current-user opt-in action.
Copy-Item (Join-Path $Root "packaging\shell\Enable-TuringDeskShell.cmd") $PackageRoot -Force
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
    $BrandIcon,
    $EmbeddedNode,
    (Join-Path $RuntimeAppDir "harness-web-smoke.js"),
    (Join-Path $RuntimeAppDir "node_modules\@deepseek-ai\dsh\lib\bin.js"),
    (Join-Path $PackageRoot "Enable-TuringDeskShell.cmd"),
    (Join-Path $PackageRoot "Enable-TuringDeskShell.ps1"),
    (Join-Path $PackageRoot "Restore-Explorer.ps1")
)) {
    if (-not (Test-Path $Required)) {
        throw "Installer payload is incomplete: $Required"
    }
}

foreach ($Forbidden in @(
    (Join-Path $PackageRoot "Preview-TuringDeskShell.ps1"),
    (Join-Path $PackageRoot "Start-TuringDesk.cmd"),
    (Join-Path $PackageRoot "Start-TuringDesk.ps1")
)) {
    if (Test-Path $Forbidden) {
        throw "Installer payload must not contain legacy Preview/normal-mode entry points: $Forbidden"
    }
}

$DetectedNodeArch = (& $EmbeddedNode -p "process.arch").Trim()
if ($DetectedNodeArch -ne $NodeArchitecture) {
    throw "Embedded Node architecture mismatch. Expected $NodeArchitecture, got $DetectedNodeArch"
}

# Fail the build if Windows cannot extract an associated icon from either EXE.
Add-Type -AssemblyName System.Drawing
foreach ($Exe in @(
    (Join-Path $DesktopDir "TuringDesk.Desktop.exe"),
    (Join-Path $ShellHostDir "TuringDesk.ShellHost.exe")
)) {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe)
    if ($null -eq $icon) {
        throw "Executable has no extractable application icon: $Exe"
    }
    try {
        if ($icon.Width -lt 16 -or $icon.Height -lt 16) {
            throw "Executable application icon is invalid: $Exe"
        }
    }
    finally {
        $icon.Dispose()
    }
}

$BuildInfo = @"
TuringDesk $Version
Runtime: $RuntimeIdentifier
Architecture: $NodeArchitecture
Node: $NodeVersion ($DetectedNodeArch)
DeepSeek Harness: 0.1.0-rc.6
Harness UI: official DeepSeek Harness WebUI wrapped by TuringDesk WebView2
Harness WebUI: packaged and boot-smoke verified
Harness startup: on-demand via official dsh --profile web
Harness configuration: shared official settings.yaml + .credentials.yaml stores
Harness credentials: Models page and beginner setup use the same writable credential source
Default desktop mode: Explorer Desktop Enhancement (WorkerW/Progman scene host)
Primary desktop UX: top-center AI search/conversation bar; no legacy Orb/Home dashboard
Search shortcut: Alt+Space focuses the same desktop search box
Desktop settings: search-bar design button opens Desktop Library / integrated settings
Desktop icon avoidance: reserved search-bar region with safe best-effort Explorer icon relocation
Desktop search Z-order: above Explorer desktop, below normal application windows
Advanced desktop mode: explicit current-user Replacement Shell
Install flow: standard Windows Installer (MSI)
Install ownership: Windows Installer / Program Files
Shell activation: explicit current-user action after installation
Shell mode: Windows Custom User Interface (current-user policy)
Shell host: shellhost/TuringDesk.ShellHost.exe
Recovery: Restore-Explorer.ps1
Application icon: embedded multi-size TuringDesk.ico
Build commit: $env:GITHUB_SHA
Build time (UTC): $([DateTime]::UtcNow.ToString("o"))
"@
Set-Content -Path (Join-Path $PackageRoot "BUILD-INFO.txt") -Value $BuildInfo -Encoding UTF8

Write-Host "Windows Installer payload ready: $PackageRoot" -ForegroundColor Green
