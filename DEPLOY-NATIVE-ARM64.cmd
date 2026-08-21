@echo off
setlocal
cd /d "%~dp0"

echo TuringDesk Native ARM64 deploy
echo ==============================

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-native-arm64.ps1"
set "CODE=%ERRORLEVEL%"

if not "%CODE%"=="0" (
  echo.
  echo Deployment failed with exit code %CODE%.
  pause
  exit /b %CODE%
)

echo.
echo Deployment finished.
pause
exit /b 0
