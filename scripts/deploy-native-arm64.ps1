param(
    [string]$Repo = "GoodLoongStudio/TuringDesk"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$BuildDir = Join-Path $Root "build\arm64-local"
$DeployDir = Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"
$ArtifactName = "TuringDesk-Native-Search-ARM64"
$Workflow = "native-search-windows.yml"
$ExeName = "TuringDesk.exe"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Stop-DeployedInstance {
    $Target = (Join-Path $DeployDir $ExeName).ToLowerInvariant()
    Get-CimInstance Win32_Process -Filter "Name='TuringDesk.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant() -eq $Target } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Test-Binary([string]$Exe) {
    Step "Running Native Search self-test"
    & $Exe --self-test
    if ($LASTEXITCODE -ne 0) {
        throw "Self-test failed with exit code $LASTEXITCODE"
    }
}

function Try-LocalBuild {
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
        return $null
    }

    try {
        Step "CMake found; trying local ARM64 incremental build"
        & cmake -S $Root -B $BuildDir -A ARM64
        if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

        & cmake --build $BuildDir --config Release --parallel
        if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }

        $Exe = Join-Path $BuildDir "src\native\Release\$ExeName"
        if (-not (Test-Path $Exe)) {
            throw "Build succeeded but $ExeName was not found"
        }
        return $Exe
    }
    catch {
        Write-Warning "Local ARM64 build unavailable: $($_.Exception.Message)"
        Write-Host "Falling back to GitHub Actions ARM64 artifact." -ForegroundColor Yellow
        return $null
    }
}

function Get-LatestSuccessfulRunId {
    $Json = & gh run list --repo $Repo --workflow $Workflow --branch main --status success --limit 1 --json databaseId
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query GitHub Actions runs"
    }

    $Runs = @($Json | ConvertFrom-Json)
    if ($Runs.Count -eq 0) {
        return $null
    }
    return $Runs[0].databaseId
}

function Start-And-WaitForCiRun {
    Step "No usable artifact found; starting GitHub Actions build"
    & gh workflow run $Workflow --repo $Repo --ref main
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start GitHub Actions workflow"
    }

    Start-Sleep -Seconds 4
    $Json = & gh run list --repo $Repo --workflow $Workflow --branch main --limit 1 --json databaseId,status,conclusion
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to find newly started workflow run"
    }

    $Runs = @($Json | ConvertFrom-Json)
    if ($Runs.Count -eq 0) {
        throw "Workflow was started but no run was found"
    }

    $RunId = $Runs[0].databaseId
    Step "Waiting for GitHub Actions run $RunId"
    & gh run watch $RunId --repo $Repo --exit-status
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Actions ARM64 build failed"
    }
    return $RunId
}

function Download-ArtifactFromRun([long]$RunId) {
    $Temp = Join-Path $env:TEMP ("TuringDesk-ARM64-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $Temp | Out-Null

    try {
        Step "Downloading ARM64 artifact from run $RunId"
        & gh run download $RunId --repo $Repo --name $ArtifactName --dir $Temp
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        $Found = Get-ChildItem -Path $Temp -Filter $ExeName -Recurse | Select-Object -First 1
        if (-not $Found) {
            return $null
        }

        $Cache = Join-Path $Root "build\arm64-artifact"
        Remove-Item $Cache -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $Cache | Out-Null
        $CachedExe = Join-Path $Cache $ExeName
        Copy-Item $Found.FullName $CachedExe -Force
        return $CachedExe
    }
    finally {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Download-LatestArtifact {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) was not found in PATH"
    }

    Step "Checking GitHub CLI authentication"
    & gh auth status 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not authenticated. Run: gh auth login"
    }

    $RunId = Get-LatestSuccessfulRunId
    if ($RunId) {
        $Exe = Download-ArtifactFromRun $RunId
        if ($Exe) {
            return $Exe
        }
        Write-Host "Latest successful run has no usable ARM64 artifact." -ForegroundColor Yellow
    }

    $RunId = Start-And-WaitForCiRun
    $Exe = Download-ArtifactFromRun $RunId
    if (-not $Exe) {
        throw "ARM64 artifact could not be downloaded after successful CI"
    }
    return $Exe
}

Step "Preparing source tree"
$GitDir = Join-Path $Root ".git"
if ((Test-Path $GitDir) -and (Get-Command git -ErrorAction SilentlyContinue)) {
    & git -C $Root fetch origin main
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }

    $Branch = (& git -C $Root branch --show-current).Trim()
    if ($Branch -eq "main") {
        & git -C $Root pull --ff-only origin main
        if ($LASTEXITCODE -ne 0) {
            throw "git pull --ff-only failed; resolve local changes first"
        }
    }
    else {
        Write-Host "Current branch is $Branch; building current working tree." -ForegroundColor Yellow
    }
}
else {
    Write-Host "Git checkout not detected; using current extracted files." -ForegroundColor DarkGray
}

$Exe = Try-LocalBuild
if (-not $Exe) {
    $Exe = Download-LatestArtifact
}

Test-Binary $Exe

Step "Deploying to $DeployDir"
Stop-DeployedInstance
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
$DeployedExe = Join-Path $DeployDir $ExeName
Copy-Item $Exe $DeployedExe -Force

$Everything = Get-Process Everything -ErrorAction SilentlyContinue
if (-not $Everything) {
    Write-Host "NOTE: Everything is not running. L2 file search will be unavailable; L1 and L3 still work." -ForegroundColor Yellow
}

Step "Starting TuringDesk Native Search"
Start-Process $DeployedExe
Write-Host "`nDeployment complete. Press Alt+Space to open Search." -ForegroundColor Green
Write-Host "Path: $DeployDir"
