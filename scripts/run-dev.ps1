$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

Write-Host "Starting TuringDesk Runtime..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$Root\runtime'; corepack enable; pnpm install; pnpm dev"

Write-Host "Starting TuringDesk Desktop..." -ForegroundColor Cyan
Set-Location $Root
dotnet run --project src/TuringDesk.Desktop/TuringDesk.Desktop.csproj
