@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\quick-verify.ps1"
if errorlevel 1 (
  echo.
  echo TuringDesk quick verification failed. See the error above.
  pause
)
endlocal
