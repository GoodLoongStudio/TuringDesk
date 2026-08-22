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

$CacheRoot = Join-Path $env:LOCALAPPDATA "TuringDesk\RuntimeCache\downloads"
New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

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
