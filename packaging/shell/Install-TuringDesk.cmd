@echo off
setlocal
cd /d "%~dp0"
title TuringDesk Shell Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Enable-TuringDeskShell.ps1"
if errorlevel 1 (
  echo.
  echo TuringDesk installation failed. Press any key to close.
  pause >nul
  exit /b 1
)
exit /b 0
