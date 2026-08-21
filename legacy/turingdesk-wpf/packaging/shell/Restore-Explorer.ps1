$ErrorActionPreference = "Stop"

$PolicyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System"
$StatePath = "HKCU:\Software\TuringDesk\Shell"

New-Item -Path $PolicyPath -Force | Out-Null
$current = (Get-ItemProperty -Path $PolicyPath -Name Shell -ErrorAction SilentlyContinue).Shell
$previous = (Get-ItemProperty -Path $StatePath -Name PreviousShell -ErrorAction SilentlyContinue).PreviousShell

if ($current -and $current.ToString().Contains("TuringDesk.ShellHost.exe", [System.StringComparison]::OrdinalIgnoreCase)) {
    if ($previous) {
        New-ItemProperty -Path $PolicyPath -Name Shell -PropertyType String -Value $previous.ToString() -Force | Out-Null
    }
    else {
        Remove-ItemProperty -Path $PolicyPath -Name Shell -ErrorAction SilentlyContinue
    }
}

if (Test-Path $StatePath) {
    New-ItemProperty -Path $StatePath -Name Enabled -PropertyType DWord -Value 0 -Force | Out-Null
}

Get-Process TuringDesk.ShellHost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process TuringDesk.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process "$env:WINDIR\explorer.exe"

Write-Host "Explorer has been restored for the current user." -ForegroundColor Green
Write-Host "The change is fully applied at the next sign-in."
