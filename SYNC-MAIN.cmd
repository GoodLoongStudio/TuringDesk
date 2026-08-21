@echo off
setlocal
cd /d "%~dp0"

echo TuringDesk update and run
echo =========================

where git >nul 2>nul
if errorlevel 1 (
  echo Git was not found in PATH.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $root=(Get-Location).Path; Write-Host ''; Write-Host '==> Updating repository to origin/main' -ForegroundColor Cyan; git -C $root fetch origin main; if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }; git -C $root checkout -f -B main origin/main; if ($LASTEXITCODE -ne 0) { throw 'git checkout failed' }; git -C $root reset --hard origin/main; if ($LASTEXITCODE -ne 0) { throw 'git reset failed' }; git -C $root clean -fd -e build -e .vs; if ($LASTEXITCODE -ne 0) { throw 'git clean failed' }; $sha=(git -C $root rev-parse HEAD).Trim(); Write-Host ('Updated to main: ' + $sha) -ForegroundColor Green; $deploy=Join-Path $root 'scripts\deploy-native-arm64.ps1'; if (-not (Test-Path $deploy)) { throw ('Deploy script not found: ' + $deploy) }; Write-Host ''; Write-Host '==> Deploying and starting ARM64 build' -ForegroundColor Cyan; & $deploy"
set "CODE=%ERRORLEVEL%"

if not "%CODE%"=="0" (
  echo.
  echo Update or launch failed with exit code %CODE%.
  pause
  exit /b %CODE%
)

echo.
echo Update and launch complete.
pause
exit /b 0
