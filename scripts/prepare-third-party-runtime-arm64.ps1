param(
    [string]$DeployDir = (Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"),
    [string]$EverythingVersion = "1.5.0.1422b",
    [string]$CodexRelease = "rust-v0.146.0"
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

# Codex app-server backs the richer L3 path. Keep it persistent exactly like the
# Harness runtime so UI-only deploys never download it again.
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
        # Codex is not required for local Search/Wallpaper/Harness validation. Keep
        # the one-click deploy usable while making the missing optional runtime clear.
        Write-Host "WARNING: Codex Runtime preparation failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
} else {
    Write-Host "Codex Agent Runtime already installed." -ForegroundColor DarkGray
}
