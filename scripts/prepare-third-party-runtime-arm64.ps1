param(
    [string]$DeployDir = (Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest")
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$RepoRoot = Split-Path $PSScriptRoot -Parent
$BundleRoot = Join-Path $RepoRoot "runtime\arm64"
$ManifestPath = Join-Path $BundleRoot "runtime-manifest.json"
$CompleteMarker = Join-Path $BundleRoot ".complete"

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

function Resolve-BundleFile([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath)) { throw "Runtime manifest contains an empty archive path" }
    $candidate = Join-Path $BundleRoot ($RelativePath -replace '/', '\')
    if (-not (Test-Path $candidate -PathType Leaf)) { throw "Vendored runtime file is missing: $candidate" }
    return $candidate
}

function Assert-BundleHash([string]$Path, [string]$Expected) {
    if ([string]::IsNullOrWhiteSpace($Expected)) { throw "Runtime manifest is missing SHA256 for $Path" }
    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "Vendored runtime integrity check failed: $Path`nExpected: $Expected`nActual:   $actual"
    }
}

function Test-PathInsideRoot([string]$Candidate, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Candidate) -or [string]::IsNullOrWhiteSpace($Root)) { return $false }
    try {
        $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\')
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
        return $candidateFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase) -or
               $candidateFull.StartsWith($rootFull + "\", [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Stop-BundledHarnessProcesses {
    $runtimeNode = Join-Path $DeployDir "Runtime\Node"
    $expectedHarness = [System.IO.Path]::GetFullPath((Join-Path $DeployDir "TuringDeskHarness.exe"))

    foreach ($process in @(Get-Process -Name "TuringDeskHarness" -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -and ([System.IO.Path]::GetFullPath($process.Path) -eq $expectedHarness)) {
                Write-Host "Stopping TuringDeskHarness PID $($process.Id)" -ForegroundColor DarkGray
                & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            }
        }
        catch { }
    }

    try {
        foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop)) {
            $exe = [string]$process.ExecutablePath
            if ($exe -and (Test-PathInsideRoot -Candidate $exe -Root $runtimeNode)) {
                Write-Host "Stopping TuringDesk bundled Node PID $($process.ProcessId)" -ForegroundColor DarkGray
                & taskkill.exe /PID $process.ProcessId /T /F 2>$null | Out-Null
            }
        }
    }
    catch { }
    Start-Sleep -Milliseconds 500
}

function Remove-WithRetry([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $last = $null
    for ($i = 1; $i -le 20; $i++) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $last = $_
            if (($i % 5) -eq 0) { Stop-BundledHarnessProcesses }
            Start-Sleep -Milliseconds 250
        }
    }
    throw "Unable to replace TuringDesk runtime path: $Path : $($last.Exception.Message)"
}

function Expand-ArchiveWithTar([string]$Archive, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & tar.exe -xf $Archive -C $Destination
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract vendored archive: $Archive" }
}

if (-not (Test-Path $CompleteMarker -PathType Leaf) -or -not (Test-Path $ManifestPath -PathType Leaf)) {
    throw "TuringDesk ARM64 RuntimeBundle is not vendored yet. Pull the latest main after the runtime vendoring workflow completes."
}

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.architecture -ne "arm64") { throw "RuntimeBundle architecture is not ARM64" }

$nodeArchive = Resolve-BundleFile ([string]$manifest.node.archive)
$harnessArchive = Resolve-BundleFile ([string]$manifest.deepseekHarness.archive)
$everythingArchive = Resolve-BundleFile ([string]$manifest.everything.archive)
$codexArchive = Resolve-BundleFile ([string]$manifest.codex.archive)
Assert-BundleHash $nodeArchive ([string]$manifest.node.sha256)
Assert-BundleHash $harnessArchive ([string]$manifest.deepseekHarness.sha256)
Assert-BundleHash $everythingArchive ([string]$manifest.everything.sha256)
Assert-BundleHash $codexArchive ([string]$manifest.codex.sha256)

$RuntimeDir = Join-Path $DeployDir "Runtime"
$NodeDir = Join-Path $RuntimeDir "Node"
$EverythingDir = Join-Path $DeployDir "Everything"
$CodexDir = Join-Path $DeployDir "Codex"
$NodeExe = Join-Path $NodeDir "node.exe"
$DshBin = Join-Path $NodeDir "node_modules\@deepseek-ai\dsh\lib\bin.js"
$EverythingExe = Join-Path $EverythingDir "Everything.exe"
$CodexExe = Join-Path $CodexDir "codex-app-server.exe"
$DeployManifestHash = Join-Path $RuntimeDir "runtime-manifest.sha256"
$sourceManifestHash = Get-Sha256 $ManifestPath

$alreadyReady = $false
if ((Test-Path $DeployManifestHash -PathType Leaf) -and
    (Test-Path $NodeExe -PathType Leaf) -and
    (Test-Path $DshBin -PathType Leaf) -and
    (Test-Path $EverythingExe -PathType Leaf) -and
    (Test-Path $CodexExe -PathType Leaf)) {
    $installedHash = (Get-Content $DeployManifestHash -Raw).Trim().ToLowerInvariant()
    $alreadyReady = $installedHash -eq $sourceManifestHash
}

if ($alreadyReady) {
    Write-Host "Vendored ARM64 RuntimeBundle already ready: $DeployDir" -ForegroundColor Green
    exit 0
}

Step "Installing repository-vendored ARM64 RuntimeBundle (offline)"
Stop-BundledHarnessProcesses
Remove-WithRetry $NodeDir
Remove-WithRetry $EverythingDir
Remove-WithRetry $CodexDir
New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null

# Node's official portable zip has one top-level directory. Extract it to a
# temporary directory, then flatten that one root into Runtime\Node.
$nodeTemp = Join-Path $env:TEMP ("TuringDesk-Node-" + [guid]::NewGuid().ToString("N"))
try {
    Expand-ArchiveWithTar $nodeArchive $nodeTemp
    $nodeRoot = Get-ChildItem $nodeTemp -Directory | Select-Object -First 1
    if (-not $nodeRoot -or -not (Test-Path (Join-Path $nodeRoot.FullName "node.exe") -PathType Leaf)) {
        throw "Vendored Node archive has an unexpected layout"
    }
    New-Item -ItemType Directory -Force -Path $NodeDir | Out-Null
    Copy-Item (Join-Path $nodeRoot.FullName "*") $NodeDir -Recurse -Force
}
finally {
    Remove-Item $nodeTemp -Recurse -Force -ErrorAction SilentlyContinue
}

# The Harness zip contains the complete production node_modules tree. Merge it
# beside node.exe so HarnessProcessManager can resolve one deterministic root.
Expand-ArchiveWithTar $harnessArchive $NodeDir
@"
@echo off
"%~dp0node.exe" "%~dp0node_modules\@deepseek-ai\dsh\lib\bin.js" %*
"@ | Set-Content -Path (Join-Path $NodeDir "dsh.cmd") -Encoding ASCII

if (-not (Test-Path $NodeExe -PathType Leaf) -or -not (Test-Path $DshBin -PathType Leaf)) {
    throw "Bundled DeepSeek Harness runtime is incomplete after extraction"
}
& $NodeExe --version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Bundled ARM64 Node failed to start" }
& $NodeExe $DshBin --help | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Bundled DeepSeek Harness CLI failed to start" }

Expand-ArchiveWithTar $everythingArchive $EverythingDir
if (-not (Test-Path $EverythingExe -PathType Leaf)) { throw "Vendored Everything archive is incomplete" }
$everythingLicense = Join-Path $BundleRoot "everything\LICENSE.txt"
if (Test-Path $everythingLicense -PathType Leaf) { Copy-Item $everythingLicense (Join-Path $EverythingDir "LICENSE.txt") -Force }
@"
[Everything]
run_in_background=1
show_tray_icon=0
check_for_updates_on_startup=0
"@ | Set-Content -Path (Join-Path $EverythingDir "TuringDesk-Everything.ini") -Encoding UTF8

$codexTemp = Join-Path $env:TEMP ("TuringDesk-Codex-" + [guid]::NewGuid().ToString("N"))
try {
    Expand-ArchiveWithTar $codexArchive $codexTemp
    $found = Get-ChildItem $codexTemp -Filter "codex-app-server-aarch64-pc-windows-msvc.exe" -File -Recurse | Select-Object -First 1
    if (-not $found) { throw "Vendored Codex archive is incomplete" }
    New-Item -ItemType Directory -Force -Path $CodexDir | Out-Null
    Copy-Item $found.FullName $CodexExe -Force
}
finally {
    Remove-Item $codexTemp -Recurse -Force -ErrorAction SilentlyContinue
}

Copy-Item $ManifestPath (Join-Path $RuntimeDir "runtime-manifest.json") -Force
Set-Content $DeployManifestHash -Value $sourceManifestHash -Encoding ASCII

Write-Host "Repository-vendored ARM64 RuntimeBundle ready." -ForegroundColor Green
Write-Host "Node:    $NodeExe" -ForegroundColor DarkGray
Write-Host "Harness: $DshBin" -ForegroundColor DarkGray
Write-Host "Everything: $EverythingExe" -ForegroundColor DarkGray
Write-Host "Codex:   $CodexExe" -ForegroundColor DarkGray
Write-Host "No third-party network download or system Node installation was performed." -ForegroundColor Green
