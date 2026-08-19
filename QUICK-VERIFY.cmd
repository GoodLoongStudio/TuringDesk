@echo off
setlocal
cd /d "%~dp0"

echo Preparing pinned Everything file search backend...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\bootstrap-everything.ps1"
if errorlevel 1 goto :failed

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\quick-verify.ps1"
if errorlevel 1 goto :failed

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify-app-search.ps1"
if errorlevel 1 goto :failed

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify-lazy-runtime.ps1"
if errorlevel 1 goto :failed

goto :done

:failed
echo.
echo TuringDesk quick verification failed. See the error above.
pause

:done
endlocal