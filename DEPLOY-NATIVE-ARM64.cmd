@echo off
setlocal

echo TuringDesk Native ARM64 one-click deploy
echo =========================================

set "SCRIPT=%TEMP%\TuringDesk-deploy-native-arm64.ps1"

echo.
echo ==^> Downloading latest deploy script
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/GoodLoongStudio/TuringDesk/main/scripts/deploy-native-arm64.ps1' -OutFile '%SCRIPT%'"
if errorlevel 1 (
  echo Failed to download latest deploy script.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
set "CODE=%ERRORLEVEL%"
del /q "%SCRIPT%" >nul 2>nul

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
