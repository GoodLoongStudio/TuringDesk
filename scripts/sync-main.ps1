$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Destination = (Get-Location).Path
$RepoZip = "https://github.com/GoodLoongStudio/TuringDesk/archive/refs/heads/main.zip"
$TempRoot = Join-Path $env:TEMP ("TuringDesk-sync-" + [guid]::NewGuid().ToString("N"))
$ZipPath = Join-Path $TempRoot "main.zip"
$ExtractPath = Join-Path $TempRoot "extract"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

try {
    if (-not (Test-Path $Destination)) {
        throw "Destination does not exist: $Destination"
    }

    New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $ExtractPath | Out-Null

    Step "Downloading latest main"
    Invoke-WebRequest -UseBasicParsing -Uri $RepoZip -OutFile $ZipPath

    Step "Extracting"
    Expand-Archive -Path $ZipPath -DestinationPath $ExtractPath -Force

    $Source = Join-Path $ExtractPath "TuringDesk-main"
    if (-not (Test-Path $Source)) {
        throw "Downloaded archive did not contain TuringDesk-main"
    }

    Step "Syncing files to $Destination"
    Write-Host "Preserved local paths: .git, build, .vs, SYNC-MAIN.cmd" -ForegroundColor DarkGray

    $RoboArgs = @(
        $Source,
        $Destination,
        "/MIR",
        "/R:2",
        "/W:1",
        "/NFL",
        "/NDL",
        "/NJH",
        "/NJS",
        "/NP",
        "/XD",
        ".git",
        "build",
        ".vs",
        "/XF",
        "SYNC-MAIN.cmd"
    )

    & robocopy @RoboArgs
    $Code = $LASTEXITCODE
    if ($Code -ge 8) {
        throw "robocopy failed with exit code $Code"
    }

    Step "Sync complete"
    Write-Host "Local source now mirrors GitHub main." -ForegroundColor Green
    Write-Host "Remote deletions are deleted locally except preserved paths."
    Write-Host "Next: run DEPLOY-NATIVE-ARM64.cmd"
    exit 0
}
finally {
    Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
