@echo off
setlocal
cd /d "%~dp0"
title Enable TuringDesk Desktop
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Enable-TuringDeskShell.ps1" -InstalledMode
if errorlevel 1 (
  echo.
  echo Failed to enable TuringDesk desktop. Press any key to close.
  pause >nul
  exit /b 1
)
exit /b 0
