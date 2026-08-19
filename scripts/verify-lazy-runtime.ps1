$ErrorActionPreference = "Stop"

$desktop = Get-Process TuringDesk.Desktop -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $desktop) {
    throw "TuringDesk.Desktop is not running after quick verification launch."
}

$desktop.Refresh()
$workingSetMb = [Math]::Round($desktop.WorkingSet64 / 1MB, 1)
Write-Host "" 
Write-Host "=== LAZY RUNTIME CHECK ===" -ForegroundColor Cyan
Write-Host "Desktop PID: $($desktop.Id)"
Write-Host "Desktop working set: ${workingSetMb} MB" -ForegroundColor Cyan

$runtimeListener = Get-NetTCPConnection -LocalPort 4317 -State Listen -ErrorAction SilentlyContinue
if ($runtimeListener) {
    $owners = $runtimeListener | ForEach-Object {
        $owner = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        if ($owner) { "$($owner.ProcessName) PID $($owner.Id)" } else { "PID $($_.OwningProcess)" }
    }
    throw "Lazy Runtime regression: port 4317 is already listening while Desktop/Search is idle. Owner: $($owners -join ', ')"
}

Write-Host "Runtime 4317: COLD (expected before Agent/Harness use)" -ForegroundColor Green
Write-Host "Expected manual lifecycle:" -ForegroundColor DarkGray
Write-Host "  1. Calculator / app / file search -> 4317 stays closed."
Write-Host "  2. Click Deep Processing / open Harness -> 4317 starts on demand."
Write-Host "  3. Close Harness and leave Agent idle for ~2 minutes -> owned 4317 is reclaimed."
