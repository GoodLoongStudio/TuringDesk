@echo off
setlocal EnableExtensions

if /I "%~1"=="--local" goto :verify
if /I "%~1"=="--refresh" goto :refresh

set "TARGET=%~dp0"
set "BOOTSTRAP=%TEMP%\TuringDesk-quick-verify-bootstrap-%RANDOM%-%RANDOM%.cmd"
copy /y "%~f0" "%BOOTSTRAP%" >nul
if errorlevel 1 goto :failed

call "%BOOTSTRAP%" --refresh "%TARGET%"
set "RC=%ERRORLEVEL%"
del /q "%BOOTSTRAP%" >nul 2>&1
exit /b %RC%

:refresh
set "TARGET=%~2"
if not defined TARGET goto :failed
set "TURINGDESK_TARGET_DIR=%TARGET%"

echo Downloading latest TuringDesk main into:
echo   %TARGET%
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $target=$env:TURINGDESK_TARGET_DIR; $work=Join-Path $env:TEMP ('TuringDesk-main-'+[guid]::NewGuid().ToString('N')); $zip=$work+'.zip'; try { Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/GoodLoongStudio/TuringDesk/archive/refs/heads/main.zip' -OutFile $zip; New-Item -ItemType Directory -Force -Path $work | Out-Null; Expand-Archive -LiteralPath $zip -DestinationPath $work -Force; $src=Join-Path $work 'TuringDesk-main'; if (-not (Test-Path -LiteralPath $src)) { throw 'Downloaded archive did not contain TuringDesk-main.' }; Write-Host 'Updating current script directory from GitHub main...' -ForegroundColor Cyan; & robocopy.exe $src $target /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Host; $rc=$LASTEXITCODE; if ($rc -gt 7) { throw ('robocopy failed with exit code '+$rc) }; Write-Host 'TuringDesk main is up to date.' -ForegroundColor Green } finally { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }"
if errorlevel 1 goto :failed

call "%TARGET%QUICK-VERIFY.cmd" --local
exit /b %ERRORLEVEL%

:verify
cd /d "%~dp0"

echo Preparing pinned Everything file search backend...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\bootstrap-everything.ps1"
if errorlevel 1 goto :failed

echo Checking installed .NET SDK before bootstrap...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\adopt-installed-dotnet.ps1"
if errorlevel 1 goto :failed

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\quick-verify.ps1" -SkipPull
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
exit /b 1

:done
echo.
echo TuringDesk quick verification completed successfully.
endlocal
exit /b 0
