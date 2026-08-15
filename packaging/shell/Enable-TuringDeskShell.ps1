$ErrorActionPreference = "Stop"

$SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallRoot = Join-Path $env:LOCALAPPDATA "TuringDesk\Shell"
$PolicyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System"
$StatePath = "HKCU:\Software\TuringDesk\Shell"

function Normalize-Path([string]$PathValue) {
    return [System.IO.Path]::GetFullPath($PathValue).TrimEnd('\')
}

$SourceRoot = Normalize-Path $SourceRoot
$InstallRoot = Normalize-Path $InstallRoot

if ($SourceRoot -ne $InstallRoot) {
    Write-Host "Installing TuringDesk Shell to $InstallRoot" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    robocopy $SourceRoot $InstallRoot /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Failed to copy TuringDesk Shell package. robocopy exit code: $LASTEXITCODE"
    }
}

$ShellHost = Join-Path $InstallRoot "shellhost\TuringDesk.ShellHost.exe"
if (-not (Test-Path $ShellHost)) {
    throw "TuringDesk ShellHost is missing: $ShellHost"
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
        Set-ItemProperty -Path $StatePath -Name PreviousShell -Value $current.ToString()
    }
}

Set-ItemProperty -Path $StatePath -Name InstallRoot -Value $InstallRoot
Set-ItemProperty -Path $StatePath -Name Enabled -Type DWord -Value 1
Set-ItemProperty -Path $PolicyPath -Name Shell -Value ('"' + $ShellHost + '"')

Write-Host ""
Write-Host "TuringDesk is now configured as the Custom User Interface for the CURRENT USER." -ForegroundColor Green
Write-Host "Explorer will be replaced after the next sign-out/sign-in." -ForegroundColor Yellow
Write-Host ""
Write-Host "Recovery:" -ForegroundColor Cyan
Write-Host "  Ctrl+Shift+Esc -> Run new task -> powershell"
Write-Host "  Then run: & '$InstallRoot\Restore-Explorer.ps1'"
Write-Host ""
Write-Host "For a safe test before signing out, run Preview-TuringDeskShell.ps1."
