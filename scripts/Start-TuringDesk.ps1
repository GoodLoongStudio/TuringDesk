$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$RuntimeExe = Join-Path $Root "runtime\node\node.exe"
$RuntimeEntry = Join-Path $Root "runtime\app\server.js"
$DesktopExe = Join-Path $Root "desktop\TuringDesk.Desktop.exe"

foreach ($Required in @($RuntimeExe, $RuntimeEntry, $DesktopExe)) {
    if (-not (Test-Path $Required)) {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show("TuringDesk portable package is incomplete.`nMissing: $Required", "TuringDesk", "OK", "Error") | Out-Null
        exit 1
    }
}

$Runtime = $null
try {
    $StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $StartInfo.FileName = $RuntimeExe
    $StartInfo.Arguments = '"' + $RuntimeEntry + '"'
    $StartInfo.WorkingDirectory = Join-Path $Root "runtime"
    $StartInfo.UseShellExecute = $false
    $StartInfo.CreateNoWindow = $true
    $StartInfo.EnvironmentVariables["TURINGDESK_RUNTIME_MODE"] = "mock"

    $Runtime = [System.Diagnostics.Process]::Start($StartInfo)
    Start-Sleep -Milliseconds 700

    if ($Runtime.HasExited) {
        throw "TuringDesk Runtime exited before the desktop started."
    }

    $Desktop = Start-Process -FilePath $DesktopExe -WorkingDirectory (Join-Path $Root "desktop") -PassThru
    $Desktop.WaitForExit()
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, "TuringDesk startup failed", "OK", "Error") | Out-Null
    exit 1
}
finally {
    if ($Runtime -and -not $Runtime.HasExited) {
        Stop-Process -Id $Runtime.Id -Force -ErrorAction SilentlyContinue
    }
}
