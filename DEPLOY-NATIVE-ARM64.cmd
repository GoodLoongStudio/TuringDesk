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

echo [1/3] Updating main...
git pull --ff-only
if errorlevel 1 goto :fail

echo.
echo [2/3] Preparing repository-vendored ARM64 runtime (no third-party download)...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-third-party-runtime-arm64.ps1"
if errorlevel 1 goto :fail

echo.
echo [3/3] Fetching the verified ARM64 build, validating, and launching TuringDesk...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-native-arm64.ps1"
if errorlevel 1 goto :fail

echo.
echo DeepSeek Harness: pinned official package from this repository RuntimeBundle.
echo Node / Harness / Everything / Codex are deployed from local repository files.
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
