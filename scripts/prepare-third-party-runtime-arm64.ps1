param(
    [string]$DeployDir = (Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"),
    [string]$EverythingVersion = "1.5.0.1422b",
    [string]$CodexRelease = "rust-v0.146.0",
    [string]$NodeVersion = "24.19.0"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$SmartDownload = Join-Path $PSScriptRoot "smart-download.ps1"
if (-not (Test-Path $SmartDownload -PathType Leaf)) { throw "Smart download helper is missing: $SmartDownload" }
. $SmartDownload

$TuringDeskRoot = Join-Path $env:LOCALAPPDATA "TuringDesk"
$RuntimeCacheRoot = Join-Path $TuringDeskRoot "RuntimeCache"
$CacheRoot = Join-Path $RuntimeCacheRoot "downloads"
New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Test-PathInsideRoot([string]$Candidate, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Candidate) -or [string]::IsNullOrWhiteSpace($Root)) { return $false }
    try {
        $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\')
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
        if ($candidateFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
        return $candidateFull.StartsWith($rootFull + "\", [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Stop-LegacyPrivateHarnessProcesses {
    $legacyHarnessRoot = Join-Path $DeployDir "HarnessRuntime"
    $legacyNodeCacheRoot = Join-Path $RuntimeCacheRoot "Node"
    $legacyRoots = @($legacyHarnessRoot, $legacyNodeCacheRoot)

    # Stop the old TuringDesk Harness shell first. Older builds may have left a
    # child Node process alive, so this is followed by path-scoped Node cleanup.
    $expectedHarness = [System.IO.Path]::GetFullPath((Join-Path $DeployDir "TuringDeskHarness.exe"))
    foreach ($process in @(Get-Process -Name "TuringDeskHarness" -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -and ([System.IO.Path]::GetFullPath($process.Path) -eq $expectedHarness)) {
                Write-Host "Stopping legacy TuringDeskHarness PID $($process.Id)" -ForegroundColor DarkGray
                & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            }
        }
        catch { }
    }

    # Only terminate node.exe instances whose executable itself lives inside a
    # TuringDesk-owned legacy private runtime. Never terminate system Node or a
    # Node executable owned by another project.
    $nodeProcesses = @()
    try {
        $nodeProcesses = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop)
    }
    catch {
        foreach ($process in @(Get-Process -Name "node" -ErrorAction SilentlyContinue)) {
            try {
                $nodeProcesses += [pscustomobject]@{ ProcessId = $process.Id; ExecutablePath = $process.Path }
            }
            catch { }
        }
    }

    foreach ($process in $nodeProcesses) {
        $exePath = [string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($exePath)) { continue }
        $ownedByLegacyRuntime = $false
        foreach ($root in $legacyRoots) {
            if (Test-PathInsideRoot -Candidate $exePath -Root $root) {
                $ownedByLegacyRuntime = $true
                break
            }
        }
        if (-not $ownedByLegacyRuntime) { continue }

        try {
            Write-Host "Stopping legacy private Node PID $($process.ProcessId): $exePath" -ForegroundColor Yellow
            & taskkill.exe /PID $process.ProcessId /T /F 2>$null | Out-Null
        }
        catch { }
    }

    # Give Windows a moment to release executable image mappings and inherited
    # handles before deleting the old runtime tree.
    Start-Sleep -Milliseconds 1200
}

function Remove-TuringDeskPathSafely([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $lastError = $null
    for ($i = 1; $i -le 30; $i++) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            Write-Host "Removed legacy TuringDesk path: $Path" -ForegroundColor DarkGray
            return
        }
        catch {
            $lastError = $_
            if (($i % 5) -eq 0) {
                # A late-exiting child from the abandoned bootstrap may still hold
                # node.exe briefly. Re-scan only TuringDesk-owned private runtimes.
                Stop-LegacyPrivateHarnessProcesses
            }
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Unable to remove obsolete TuringDesk Harness data at $Path : $($lastError.Exception.Message)"
}

function Remove-LegacyPrivateHarnessEnvironment {
    Step "Cleaning obsolete private Harness environment"
    Stop-LegacyPrivateHarnessProcesses

    # These are only directories/files created by TuringDesk's abandoned private
    # Harness bootstrap. Never touch system Node, global npm cache, user profiles,
    # or another project's node_modules.
    $legacyPaths = @(
        (Join-Path $DeployDir "HarnessRuntime"),
        (Join-Path $RuntimeCacheRoot "Node"),
        (Join-Path $RuntimeCacheRoot "npm"),
        (Join-Path $RuntimeCacheRoot "logs")
    )
    foreach ($path in $legacyPaths) { Remove-TuringDeskPathSafely $path }

    $legacyFiles = @(
        (Join-Path $TuringDeskRoot "Logs\harness-system-install.log")
    )
    foreach ($path in $legacyFiles) {
        if (Test-Path $path -PathType Leaf) {
            Remove-Item $path -Force -ErrorAction SilentlyContinue
            Write-Host "Removed legacy TuringDesk file: $path" -ForegroundColor DarkGray
        }
    }

    Write-Host "Legacy private Harness environment is clean." -ForegroundColor Green
}

function Refresh-ProcessPath {
    $machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $user = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:PATH = (($machine, $user) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ";"
}

function Find-Npx {
    Refresh-ProcessPath
    $command = Get-Command npx.cmd -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    $candidate = Join-Path $env:ProgramFiles "nodejs\npx.cmd"
    if (Test-Path $candidate -PathType Leaf) { return $candidate }
    return $null
}

Remove-LegacyPrivateHarnessEnvironment

# DeepSeek's official Windows Web UI path is `npx @deepseek-ai/dsh web`.
# TuringDesk only guarantees official Node.js/npx exists; it does not build or
# maintain a private HarnessRuntime/node_modules tree anymore.
$npx = Find-Npx
if (-not $npx) {
    Step "Installing official Node.js $NodeVersion ARM64 for DeepSeek Harness"
    $fileName = "node-v$NodeVersion-arm64.msi"
    $msi = Join-Path $CacheRoot $fileName
    $sums = Join-Path $CacheRoot "node-v$NodeVersion-SHASUMS256.txt"

    Invoke-TuringDeskSmartDownload `
        -Url "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt" `
        -Destination $sums `
        -Name "Node.js checksums" `
        -FileName "SHASUMS256.txt" `
        -TimeoutSeconds 120 | Out-Null

    $sumLine = Get-Content $sums | Where-Object { $_ -match "\s+$([regex]::Escape($fileName))$" } | Select-Object -First 1
    if (-not $sumLine) { throw "Node checksum entry not found for $fileName" }
    $expectedHash = ($sumLine -split '\s+')[0].ToLowerInvariant()

    Invoke-TuringDeskSmartDownload `
        -Url "https://nodejs.org/dist/v$NodeVersion/$fileName" `
        -Destination $msi `
        -Name "Node.js $NodeVersion ARM64 MSI" `
        -ExpectedSha256 $expectedHash `
        -FileName $fileName `
        -TimeoutSeconds 300 | Out-Null

    Write-Host "Installing official Node.js. Windows may show one administrator approval prompt." -ForegroundColor Yellow
    $installer = Start-Process -FilePath "msiexec.exe" `
        -ArgumentList @("/i", "`"$msi`"", "/passive", "/norestart") `
        -Verb RunAs -Wait -PassThru
    if ($installer.ExitCode -notin @(0, 3010)) {
        throw "Official Node.js installer failed with exit code $($installer.ExitCode)"
    }

    $npx = Find-Npx
    if (-not $npx) { throw "Node.js installation completed but npx.cmd was not found" }
    Write-Host "Official Node.js/npx ready: $npx" -ForegroundColor Green
} else {
    Write-Host "Official/system npx already available: $npx" -ForegroundColor DarkGray
}

# Everything is a small but important search runtime. Install it once and keep it
# outside fast native EXE replacements.
$EverythingDir = Join-Path $DeployDir "Everything"
$EverythingExe = Join-Path $EverythingDir "Everything.exe"
if (-not (Test-Path $EverythingExe -PathType Leaf)) {
    Step "Preparing Everything ARM64 runtime"
    $zipName = "Everything-$EverythingVersion.ARM64.zip"
    $zip = Join-Path $CacheRoot $zipName
    Invoke-TuringDeskSmartDownload `
        -Url "https://www.voidtools.com/$zipName" `
        -Destination $zip `
        -Name "Everything $EverythingVersion ARM64" `
        -FileName $zipName `
        -TimeoutSeconds 180 | Out-Null

    if (Test-Path $EverythingDir) { Remove-Item $EverythingDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $EverythingDir | Out-Null
    Expand-Archive $zip -DestinationPath $EverythingDir -Force
    if (-not (Test-Path $EverythingExe -PathType Leaf)) { throw "Everything archive did not contain Everything.exe" }

    $license = Join-Path $EverythingDir "LICENSE.txt"
    Invoke-TuringDeskSmartDownload `
        -Url "https://www.voidtools.com/License.txt" `
        -Destination $license `
        -Name "Everything license" `
        -FileName "LICENSE.txt" `
        -TimeoutSeconds 60 | Out-Null

    @"
[Everything]
run_in_background=1
show_tray_icon=0
check_for_updates_on_startup=0
"@ | Set-Content -Path (Join-Path $EverythingDir "TuringDesk-Everything.ini") -Encoding UTF8
    Write-Host "Everything runtime ready." -ForegroundColor Green
} else {
    Write-Host "Everything runtime already installed." -ForegroundColor DarkGray
}

# Codex app-server backs the richer L3 path and is independent from DeepSeek Harness.
$CodexDir = Join-Path $DeployDir "Codex"
$CodexExe = Join-Path $CodexDir "codex-app-server.exe"
if (-not (Test-Path $CodexExe -PathType Leaf)) {
    Step "Preparing Codex Agent Runtime ARM64"
    $asset = "codex-app-server-aarch64-pc-windows-msvc.exe.zip"
    $zip = Join-Path $CacheRoot "$CodexRelease-$asset"
    $url = "https://github.com/openai/codex/releases/download/$CodexRelease/$asset"
    try {
        Invoke-TuringDeskSmartDownload `
            -Url $url `
            -Destination $zip `
            -Name "Codex Agent Runtime $CodexRelease ARM64" `
            -FileName $asset `
            -TimeoutSeconds 300 | Out-Null

        $expanded = Join-Path $env:TEMP ("TuringDesk-Codex-" + [guid]::NewGuid().ToString("N"))
        try {
            Expand-Archive $zip -DestinationPath $expanded -Force
            $found = Get-ChildItem -Path $expanded -Filter "codex-app-server-aarch64-pc-windows-msvc.exe" -Recurse | Select-Object -First 1
            if (-not $found) { throw "Codex archive did not contain the expected ARM64 executable" }
            New-Item -ItemType Directory -Force -Path $CodexDir | Out-Null
            Copy-Item $found.FullName $CodexExe -Force
            Write-Host "Codex Agent Runtime ready." -ForegroundColor Green
        }
        finally { Remove-Item $expanded -Recurse -Force -ErrorAction SilentlyContinue }
    }
    catch {
        Write-Host "WARNING: Codex Runtime preparation failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
} else {
    Write-Host "Codex Agent Runtime already installed." -ForegroundColor DarkGray
}
