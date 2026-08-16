param(
    [switch]$Logoff,
    [switch]$NoLogoff
)

$ErrorActionPreference = "Stop"

if ($Logoff -and $NoLogoff) {
    throw "Use only one of -Logoff or -NoLogoff."
}

$SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallRoot = Join-Path $env:LOCALAPPDATA "TuringDesk\Shell"
$PolicyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System"
$StatePath = "HKCU:\Software\TuringDesk\Shell"

function Normalize-Path([string]$PathValue) {
    return [System.IO.Path]::GetFullPath($PathValue).TrimEnd('\')
}

function Confirm-TuringDeskLogoff {
    if ($Logoff) { return $true }
    if ($NoLogoff) { return $false }

    try {
        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
        $result = [System.Windows.MessageBox]::Show(
            "TuringDesk Shell 已启用或更新，需要注销当前 Windows 用户后才能完全生效。`n`n现在注销吗？`n`n选择“否”可以稍后手动注销。",
            "TuringDesk · 需要重新登录",
            [System.Windows.MessageBoxButton]::YesNo,
            [System.Windows.MessageBoxImage]::Information,
            [System.Windows.MessageBoxResult]::No
        )
        return $result -eq [System.Windows.MessageBoxResult]::Yes
    }
    catch {
        $choice = Read-Host "TuringDesk Shell needs sign-out/sign-in. Sign out now? [y/N]"
        return $choice -match '^(y|yes)$'
    }
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
        New-ItemProperty -Path $StatePath -Name PreviousShell -PropertyType String -Value $current.ToString() -Force | Out-Null
    }
}

New-ItemProperty -Path $StatePath -Name InstallRoot -PropertyType String -Value $InstallRoot -Force | Out-Null
New-ItemProperty -Path $StatePath -Name Enabled -PropertyType DWord -Value 1 -Force | Out-Null
New-ItemProperty -Path $PolicyPath -Name Shell -PropertyType String -Value ('"' + $ShellHost + '"') -Force | Out-Null

Write-Host ""
Write-Host "TuringDesk is now configured as the Custom User Interface for the CURRENT USER." -ForegroundColor Green
Write-Host "Explorer will be replaced after the next sign-out/sign-in." -ForegroundColor Yellow
Write-Host ""
Write-Host "Recovery:" -ForegroundColor Cyan
Write-Host "  Ctrl+Shift+Esc -> Run new task -> powershell"
Write-Host "  Then run: & '$InstallRoot\Restore-Explorer.ps1'"
Write-Host ""

if (Confirm-TuringDeskLogoff) {
    Write-Host "Signing out current Windows user..." -ForegroundColor Yellow
    Start-Process -FilePath "$env:SystemRoot\System32\shutdown.exe" -ArgumentList "/l"
    exit 0
}

Write-Host "Sign-out was postponed. The new shell will take effect the next time you sign out and sign in." -ForegroundColor Cyan
Write-Host "Automation options: -Logoff to sign out immediately, or -NoLogoff to suppress the prompt."
