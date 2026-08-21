@echo off
setlocal
cd /d "%~dp0"

echo TuringDesk one-click main sync
echo ===============================

where git >nul 2>nul
if errorlevel 1 (
  echo Git was not found in PATH.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $root=(Get-Location).Path; Write-Host 'Fetching origin/main...'; git -C $root fetch origin main; if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }; Write-Host 'Switching to main...'; git -C $root checkout main; if ($LASTEXITCODE -ne 0) { throw 'git checkout main failed' }; Write-Host 'Resetting local tree to origin/main...'; git -C $root reset --hard origin/main; if ($LASTEXITCODE -ne 0) { throw 'git reset failed' }; Write-Host 'Removing untracked source files...'; git -C $root clean -fd -e build -e .vs; if ($LASTEXITCODE -ne 0) { throw 'git clean failed' }; Write-Host ''; Write-Host 'Sync complete. Local source matches origin/main.' -ForegroundColor Green"
set "CODE=%ERRORLEVEL%"

if not "%CODE%"=="0" (
  echo.
  echo Sync failed with exit code %CODE%.
  pause
  exit /b %CODE%
)

echo.
pause
exit /b 0
