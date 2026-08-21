@echo off
setlocal
cd /d "%~dp0"

echo TuringDesk one-click main sync
echo ===============================

set "SYNC_PS1=%TEMP%\TuringDesk-sync-main-%RANDOM%-%RANDOM%.ps1"

powershell.exe -NoLogo -NoProfile -Command "$ProgressPreference='SilentlyContinue'; Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/GoodLoongStudio/TuringDesk/main/scripts/sync-main.ps1' -OutFile '%SYNC_PS1%'"
if errorlevel 1 (
  echo.
  echo Failed to download latest sync script.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SYNC_PS1%" -Destination "%~dp0"
set "CODE=%ERRORLEVEL%"
del /q "%SYNC_PS1%" >nul 2>nul

if not "%CODE%"=="0" (
  echo.
  echo Sync failed with exit code %CODE%.
  pause
  exit /b %CODE%
)

echo.
echo Sync finished.
pause
exit /b 0
