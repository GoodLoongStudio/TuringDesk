param(
    [switch]$Logoff,
    [switch]$NoLogoff,
    [switch]$InstalledMode
)

$ErrorActionPreference = "Stop"

if ($Logoff -and $NoLogoff) {
    throw "Use only one of -Logoff or -NoLogoff."
}

$SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$TuringRoot = Join-Path $env:LOCALAPPDATA "TuringDesk"
$VersionsRoot = Join-Path $TuringRoot "Versions"
$PolicyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System"
$StatePath = "HKCU:\Software\TuringDesk\Shell"
$Quote = '"'

function Normalize-Path([string]$PathValue) {
    return [System.IO.Path]::GetFullPath($PathValue).TrimEnd('\')
}

function Get-PackageVersionTag {
    $buildInfo = Join-Path $SourceRoot "BUILD-INFO.txt"
    if (Test-Path $buildInfo) {
        $firstLine = (Get-Content $buildInfo -TotalCount 1 -ErrorAction SilentlyContinue)
        if ($firstLine -match '^TuringDesk\s+([A-Za-z0-9._-]+)$') {
            return $Matches[1]
        }
    }
    return "dev"
}

function Confirm-TuringDeskLogoff {
    if ($Logoff) { return $true }
    if ($NoLogoff) { return $false }

    # Keep this script ASCII-only because the MSI launches it with the built-in
    # Windows PowerShell 5.1. PowerShell 5.1 may decode UTF-8-without-BOM source
    # using the active ANSI code page, which can corrupt non-ASCII string literals
    # and turn otherwise valid source into a parser error.
    try {
        $popup = New-Object -ComObject WScript.Shell
        $message = "TuringDesk desktop has been enabled or updated. Sign out of the current Windows user to switch to the new desktop.`r`n`r`nSign out now?`r`n`r`nChoose No to sign out later."
        # 4 = Yes/No, 64 = information icon. Popup returns 6 for Yes and 7 for No.
        $result = $popup.Popup($message, 0, "TuringDesk - Sign out required", 68)
        return $result -eq 6
    }
    catch {
        $choice = Read-Host "TuringDesk Shell needs sign-out/sign-in. Sign out now? [y/N]"
        return $choice -match '^(y|yes)$'
    }
}

$SourceRoot = Normalize-Path $SourceRoot
$TuringRoot = Normalize-Path $TuringRoot
$VersionsRoot = Normalize-Path $VersionsRoot
New-Item -ItemType Directory -Force -Path $TuringRoot | Out-Null

$VersionTag = Get-PackageVersionTag
$currentInstallRoot = $null

if ($InstalledMode) {
    # Standard Windows Installer mode: run directly from the Program Files installation.
    # Do not duplicate the application into LocalAppData; MSI owns the installed files.
    $InstallRoot = $SourceRoot
    Write-Host "Enabling installed TuringDesk Shell $VersionTag from $InstallRoot" -ForegroundColor Cyan
}
else {
    # Portable/development fallback retained for engineering builds.
    New-Item -ItemType Directory -Force -Path $VersionsRoot | Out-Null
    $InstallId = "$VersionTag-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID"
    $InstallRoot = Normalize-Path (Join-Path $VersionsRoot $InstallId)

    Write-Host "Staging portable TuringDesk Shell $VersionTag to $InstallRoot" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    robocopy $SourceRoot $InstallRoot /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Failed to stage TuringDesk Shell package. robocopy exit code: $LASTEXITCODE"
    }
}

$ShellHost = Join-Path $InstallRoot "shellhost\TuringDesk.ShellHost.exe"
if (-not (Test-Path $ShellHost)) {
    throw "TuringDesk ShellHost is missing: $ShellHost"
}

# Keep a stable emergency recovery script under LocalAppData. It remains usable even
# if the installer is being repaired or an application update is incomplete.
Copy-Item (Join-Path $SourceRoot "Restore-Explorer.ps1") (Join-Path $TuringRoot "Restore-Explorer.ps1") -Force
if (Test-Path (Join-Path $SourceRoot "Restore-Explorer.cmd")) {
    Copy-Item (Join-Path $SourceRoot "Restore-Explorer.cmd") (Join-Path $TuringRoot "Restore-Explorer.cmd") -Force
}

New-Item -Path $PolicyPath -Force | Out-Null
New-Item -Path $StatePath -Force | Out-Null

$current = (Get-ItemProperty -Path $PolicyPath -Name Shell -ErrorAction SilentlyContinue).Shell
$alreadyTuringDesk = $current -and $current.ToString().Contains("TuringDesk.ShellHost.exe", [System.StringComparison]::OrdinalIgnoreCase)

if (-not $alreadyTuringDesk) {
    if ($null -eq $current) {
        Remove-ItemProperty -Path $StatePath -Name PreviousShell -ErrorAction SilentlyContinue
    }
    else {
        New-ItemProperty -Path $StatePath -Name PreviousShell -PropertyType String -Value $current.ToString() -Force | Out-Null
    }
}
else {
    try {
        $currentShellPath = $current.ToString().Trim($Quote)
        $currentInstallRoot = Split-Path -Parent (Split-Path -Parent $currentShellPath)
        $currentInstallRoot = Normalize-Path $currentInstallRoot
    }
    catch { $currentInstallRoot = $null }
}

New-ItemProperty -Path $StatePath -Name InstallRoot -PropertyType String -Value $InstallRoot -Force | Out-Null
New-ItemProperty -Path $StatePath -Name Version -PropertyType String -Value $VersionTag -Force | Out-Null
New-ItemProperty -Path $StatePath -Name InstalledMode -PropertyType DWord -Value ([int]$InstalledMode.IsPresent) -Force | Out-Null
New-ItemProperty -Path $StatePath -Name Enabled -PropertyType DWord -Value 1 -Force | Out-Null
New-ItemProperty -Path $PolicyPath -Name Shell -PropertyType String -Value ($Quote + $ShellHost + $Quote) -Force | Out-Null

if (-not $InstalledMode) {
    # Best-effort cleanup for legacy portable versions only. MSI-installed files are
    # owned exclusively by Windows Installer and are never mutated here.
    Get-ChildItem $VersionsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            (Normalize-Path $_.FullName) -ne $InstallRoot -and
            (-not $currentInstallRoot -or (Normalize-Path $_.FullName) -ne $currentInstallRoot)
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip 2 |
        ForEach-Object {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}

Write-Host ""
Write-Host "TuringDesk $VersionTag is now configured as the Custom User Interface for the CURRENT USER." -ForegroundColor Green
Write-Host "The new desktop takes effect after sign-out/sign-in." -ForegroundColor Yellow
Write-Host ""
Write-Host "Emergency recovery:" -ForegroundColor Cyan
Write-Host "  Ctrl+Shift+Esc -> Run new task -> powershell"
Write-Host "  Then run: & '$TuringRoot\Restore-Explorer.ps1'"
Write-Host ""

if (Confirm-TuringDeskLogoff) {
    Write-Host "Signing out current Windows user..." -ForegroundColor Yellow
    Start-Process -FilePath "$env:SystemRoot\System32\shutdown.exe" -ArgumentList "/l"
    exit 0
}

Write-Host "Sign-out was postponed. The new shell will take effect the next time you sign out and sign in." -ForegroundColor Cyan
Write-Host "Automation options: -Logoff to sign out immediately, or -NoLogoff to suppress the prompt."
