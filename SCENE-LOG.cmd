@echo off
setlocal
cd /d "%~dp0"
echo === TuringDesk Scene Debug ===
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$status=Join-Path $env:LOCALAPPDATA 'TuringDesk\desktop-engine-status.json'; $log=Join-Path $env:LOCALAPPDATA 'TuringDesk\logs\scene-engine.log'; Write-Host ('Status: ' + $status) -ForegroundColor Cyan; if(Test-Path $status){Get-Content $status}else{Write-Host 'Status file not created yet.' -ForegroundColor Yellow}; Write-Host ''; Write-Host ('Live log: ' + $log) -ForegroundColor Cyan; Write-Host 'Keep this window open, then apply Aurora / Neon / Orbital.' -ForegroundColor Green; Write-Host 'Press Ctrl+C to stop.' -ForegroundColor DarkGray; while(-not(Test-Path $log)){Write-Host 'Waiting for scene-engine.log ...' -ForegroundColor Yellow; Start-Sleep -Seconds 1}; Get-Content $log -Tail 160 -Wait"
endlocal
