$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DesktopExe = Join-Path $Root "desktop\TuringDesk.Desktop.exe"

if (-not (Test-Path $DesktopExe)) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("TuringDesk portable package is incomplete.`nMissing: $DesktopExe", "TuringDesk", "OK", "Error") | Out-Null
    exit 1
}

try {
    $Desktop = Start-Process -FilePath $DesktopExe -WorkingDirectory (Join-Path $Root "desktop") -PassThru
    $Desktop.WaitForExit()
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, "TuringDesk startup failed", "OK", "Error") | Out-Null
    exit 1
}
