@echo off
setlocal
cd /d "%~dp0"

echo TuringDesk update and run
echo =========================

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $root=(Get-Location).Path; $gitOk=$false; if (Get-Command git -ErrorAction SilentlyContinue) { for ($i=1; $i -le 3; $i++) { Write-Host ''; Write-Host ('==> Updating repository to origin/main (attempt ' + $i + '/3)') -ForegroundColor Cyan; git -C $root fetch origin main; if ($LASTEXITCODE -eq 0) { $gitOk=$true; break }; if ($i -lt 3) { Start-Sleep -Seconds 2 } } }; if ($gitOk) { git -C $root checkout -f -B main origin/main; if ($LASTEXITCODE -ne 0) { throw 'git checkout failed' }; git -C $root reset --hard origin/main; if ($LASTEXITCODE -ne 0) { throw 'git reset failed' }; git -C $root clean -fd -e build -e .vs; if ($LASTEXITCODE -ne 0) { throw 'git clean failed' }; $sha=(git -C $root rev-parse HEAD).Trim(); Write-Host ('Updated to main: ' + $sha) -ForegroundColor Green } else { Write-Host ''; Write-Host '==> Git endpoint unavailable; using ZIP mirror fallback' -ForegroundColor Yellow; $temp=Join-Path $env:TEMP ('TuringDesk-sync-' + [guid]::NewGuid().ToString('N')); $zip=Join-Path $temp 'main.zip'; $extract=Join-Path $temp 'extract'; New-Item -ItemType Directory -Force -Path $extract | Out-Null; try { Invoke-WebRequest -UseBasicParsing -Uri 'https://codeload.github.com/GoodLoongStudio/TuringDesk/zip/refs/heads/main' -OutFile $zip; Expand-Archive -Path $zip -DestinationPath $extract -Force; $src=Get-ChildItem -Path $extract -Directory | Select-Object -First 1; if (-not $src) { throw 'ZIP fallback did not contain repository files' }; & robocopy $src.FullName $root /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP /XD .git build .vs /XF SYNC-MAIN.cmd; $rc=$LASTEXITCODE; if ($rc -ge 8) { throw ('robocopy fallback failed with exit code ' + $rc) }; Write-Host 'ZIP mirror sync complete.' -ForegroundColor Green } finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue } }; $deploy=Join-Path $root 'scripts\deploy-native-arm64.ps1'; if (-not (Test-Path $deploy)) { throw ('Deploy script not found: ' + $deploy) }; Write-Host ''; Write-Host '==> Deploying and starting ARM64 build' -ForegroundColor Cyan; & $deploy"
set "CODE=%ERRORLEVEL%"

if not "%CODE%"=="0" (
  echo.
  echo Update or launch failed with exit code %CODE%.
  pause
  exit /b %CODE%
)

echo.
echo Update and launch complete.
exit /b 0
