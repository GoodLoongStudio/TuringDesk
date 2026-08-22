param(
    [string]$DeployDir = (Join-Path $env:LOCALAPPDATA 'TuringDesk\NativeTest'),
    [switch]$SkipGozServiceInstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = Split-Path $PSScriptRoot -Parent
$BundleRoot = Join-Path $RepoRoot 'runtime\arm64'
$ManifestPath = Join-Path $BundleRoot 'runtime-manifest.json'
$CompleteMarker = Join-Path $BundleRoot '.complete'

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Sha256([string]$Path) { return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant() }

function Resolve-BundleFile([string]$RelativePath) {
    $candidate = Join-Path $BundleRoot ($RelativePath -replace '/', '\')
    if (-not (Test-Path $candidate -PathType Leaf)) { throw "Vendored runtime file is missing: $candidate" }
    return $candidate
}

function Assert-BundleHash([string]$Path, [string]$Expected) {
    $actual = Sha256 $Path
    if ([string]::IsNullOrWhiteSpace($Expected) -or $actual -ne $Expected.ToLowerInvariant()) {
        throw "Vendored runtime integrity check failed: $Path`nExpected: $Expected`nActual:   $actual"
    }
}

function Stop-OwnedProcesses {
    foreach ($name in @('TuringDesk','TuringDeskWallpaper','TuringDeskHarness','node','codex')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    Start-Sleep -Milliseconds 250
}

function Expand-Zip([string]$Archive, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & tar.exe -xf $Archive -C $Destination
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract vendored archive: $Archive" }
}

function Invoke-ElevatedGoz([string]$Exe, [string]$Arguments) {
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -Verb RunAs -Wait -PassThru
    if (-not $process -or $process.ExitCode -ne 0) { throw "Elevated goz operation failed: $Arguments" }
}

function Ensure-GozService([string]$GozExe, [string]$GozDaemon) {
    if ($SkipGozServiceInstall) { return }
    $status = (& $GozDaemon status 2>&1 | Out-String)
    if ($status -notmatch 'Running') {
        Step 'Installing/starting TuringDesk goz index service'
        Invoke-ElevatedGoz $GozDaemon 'install'
    }
    for ($i = 0; $i -lt 30; $i++) {
        & $GozExe --status *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host 'goz index service is reachable.' -ForegroundColor Green
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'goz service was installed but its named pipe did not become reachable'
}

if (-not (Test-Path $CompleteMarker -PathType Leaf) -or -not (Test-Path $ManifestPath -PathType Leaf)) {
    throw 'TuringDesk ARM64 RuntimeBundle is not vendored yet. Pull main after the vendoring workflow finishes.'
}

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.architecture -ne 'arm64' -or [int]$manifest.schema -lt 2) { throw 'RuntimeBundle is not the ARM64 goz/Codex CLI bundle' }

$nodeArchive = Resolve-BundleFile ([string]$manifest.node.archive)
$harnessArchive = Resolve-BundleFile ([string]$manifest.deepseekHarness.archive)
$gozArchive = Resolve-BundleFile ([string]$manifest.goz.archive)
$codexArchive = Resolve-BundleFile ([string]$manifest.codex.archive)
Assert-BundleHash $nodeArchive ([string]$manifest.node.sha256)
Assert-BundleHash $harnessArchive ([string]$manifest.deepseekHarness.sha256)
Assert-BundleHash $gozArchive ([string]$manifest.goz.sha256)
Assert-BundleHash $codexArchive ([string]$manifest.codex.sha256)

$RuntimeDir = Join-Path $DeployDir 'Runtime'
$NodeDir = Join-Path $RuntimeDir 'Node'
$GozDir = Join-Path $DeployDir 'Goz'
$CodexDir = Join-Path $DeployDir 'Codex'
$NodeExe = Join-Path $NodeDir 'node.exe'
$DshBin = Join-Path $NodeDir 'node_modules\@deepseek-ai\dsh\lib\bin.js'
$GozExe = Join-Path $GozDir 'goz.exe'
$GozDaemon = Join-Path $GozDir 'gozd.exe'
$CodexExe = Join-Path $CodexDir 'codex.exe'
$DeployManifestHash = Join-Path $RuntimeDir 'runtime-manifest.sha256'
$sourceManifestHash = Sha256 $ManifestPath

$alreadyReady = (Test-Path $DeployManifestHash -PathType Leaf) -and
                (Test-Path $NodeExe -PathType Leaf) -and
                (Test-Path $DshBin -PathType Leaf) -and
                (Test-Path $GozExe -PathType Leaf) -and
                (Test-Path $GozDaemon -PathType Leaf) -and
                (Test-Path $CodexExe -PathType Leaf) -and
                ((Get-Content $DeployManifestHash -Raw).Trim().ToLowerInvariant() -eq $sourceManifestHash)
if ($alreadyReady) {
    Write-Host "Vendored ARM64 RuntimeBundle already ready: $DeployDir" -ForegroundColor Green
    Ensure-GozService $GozExe $GozDaemon
    exit 0
}

Step 'Installing repository-vendored ARM64 RuntimeBundle (offline)'
Stop-OwnedProcesses
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null

# Materialize goz first and preserve an unchanged running service binary.
$gozTemp = Join-Path $env:TEMP ('TuringDesk-Goz-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Zip $gozArchive $gozTemp
    $newGoz = Get-ChildItem $gozTemp -Filter 'goz.exe' -File -Recurse | Select-Object -First 1
    $newGozd = Get-ChildItem $gozTemp -Filter 'gozd.exe' -File -Recurse | Select-Object -First 1
    if (-not $newGoz -or -not $newGozd) { throw 'Vendored goz archive is incomplete' }

    $replaceGoz = $true
    if ((Test-Path $GozExe -PathType Leaf) -and (Test-Path $GozDaemon -PathType Leaf)) {
        $replaceGoz = (Sha256 $GozExe) -ne (Sha256 $newGoz.FullName) -or (Sha256 $GozDaemon) -ne (Sha256 $newGozd.FullName)
    }
    if ($replaceGoz -and (Test-Path $GozDaemon -PathType Leaf)) {
        try { Invoke-ElevatedGoz $GozDaemon 'uninstall' } catch { Write-Host "goz uninstall warning: $($_.Exception.Message)" -ForegroundColor DarkYellow }
    }
    if ($replaceGoz) {
        Remove-Item $GozDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $GozDir | Out-Null
        Copy-Item (Join-Path $gozTemp '*') $GozDir -Recurse -Force
    } else {
        Write-Host 'Vendored goz binaries are unchanged; preserving the installed service binary.' -ForegroundColor DarkGray
    }
}
finally { Remove-Item $gozTemp -Recurse -Force -ErrorAction SilentlyContinue }

Remove-Item $NodeDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $CodexDir -Recurse -Force -ErrorAction SilentlyContinue

$nodeTemp = Join-Path $env:TEMP ('TuringDesk-Node-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Zip $nodeArchive $nodeTemp
    $nodeRoot = Get-ChildItem $nodeTemp -Directory | Select-Object -First 1
    if (-not $nodeRoot -or -not (Test-Path (Join-Path $nodeRoot.FullName 'node.exe'))) { throw 'Vendored Node archive layout is invalid' }
    New-Item -ItemType Directory -Force -Path $NodeDir | Out-Null
    Copy-Item (Join-Path $nodeRoot.FullName '*') $NodeDir -Recurse -Force
}
finally { Remove-Item $nodeTemp -Recurse -Force -ErrorAction SilentlyContinue }

Expand-Zip $harnessArchive $NodeDir
if (-not (Test-Path $NodeExe) -or -not (Test-Path $DshBin)) { throw 'Bundled DeepSeek Harness runtime is incomplete' }
& $NodeExe --version | Out-Host
& $NodeExe $DshBin --help | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Bundled DeepSeek Harness CLI failed to start' }

$codexTemp = Join-Path $env:TEMP ('TuringDesk-Codex-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Zip $codexArchive $codexTemp
    $found = Get-ChildItem $codexTemp -Filter 'codex-aarch64-pc-windows-msvc.exe' -File -Recurse | Select-Object -First 1
    if (-not $found) { throw 'Vendored full Codex CLI archive is incomplete' }
    New-Item -ItemType Directory -Force -Path $CodexDir | Out-Null
    Copy-Item $found.FullName $CodexExe -Force
}
finally { Remove-Item $codexTemp -Recurse -Force -ErrorAction SilentlyContinue }

& $CodexExe --version | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Bundled Codex CLI failed to execute' }
& $CodexExe app-server --help | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Bundled Codex CLI app-server command is unavailable' }

Copy-Item $ManifestPath (Join-Path $RuntimeDir 'runtime-manifest.json') -Force
Set-Content $DeployManifestHash -Value $sourceManifestHash -Encoding ASCII

Ensure-GozService $GozExe $GozDaemon
Write-Host 'Repository-vendored ARM64 RuntimeBundle ready.' -ForegroundColor Green
Write-Host "Node:    $NodeExe" -ForegroundColor DarkGray
Write-Host "Harness: $DshBin" -ForegroundColor DarkGray
Write-Host "goz:     $GozExe" -ForegroundColor DarkGray
Write-Host "gozd:    $GozDaemon" -ForegroundColor DarkGray
Write-Host "Codex:   $CodexExe" -ForegroundColor DarkGray
Write-Host 'No third-party network download or system Node installation was performed.' -ForegroundColor Green
