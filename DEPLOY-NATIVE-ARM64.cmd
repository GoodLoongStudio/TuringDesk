@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo ========================================
echo   TuringDesk ARM64 One-Click Deploy
echo   Repository RuntimeBundle / Offline
echo ========================================
echo.

where git >nul 2>nul
if errorlevel 1 (
  echo [ERROR] git was not found in PATH.
  goto :fail
)

where powershell.exe >nul 2>nul
if errorlevel 1 (
  echo [ERROR] powershell.exe was not found.
  goto :fail
)

echo [1/4] Updating main...
git pull --ff-only
if errorlevel 1 goto :fail

echo.
echo [2/4] Verifying repository ARM64 RuntimeBundle integrity...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify-arm64-runtime-bundle.ps1"
if errorlevel 1 goto :fail

echo.
echo [3/4] Preparing repository-vendored ARM64 runtime (no third-party download)...
echo       First goz setup may request UAC once to install its MFT/USN index service.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-third-party-runtime-arm64.ps1"
if errorlevel 1 goto :fail

echo.
echo [4/4] Fetching the verified ARM64 build, validating, and launching TuringDesk...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-native-arm64.ps1"
if errorlevel 1 goto :fail

echo.
echo DeepSeek Harness: pinned official package from this repository RuntimeBundle.
echo Node / Harness / goz / full Codex CLI are deployed from local repository files.
echo goz provides the TuringDesk L2 MFT/USN file index service.
echo Harness smoke test passed before TuringDesk was launched.
echo ========================================
echo   SUCCESS - TuringDesk ARM64 is running
echo ========================================
timeout /t 2 /nobreak >nul
exit /b 0

:fail
echo.
echo ========================================
echo   DEPLOY FAILED
echo   See the error above. Nothing else to run.
echo ========================================
echo.
pause
exit /b 1
